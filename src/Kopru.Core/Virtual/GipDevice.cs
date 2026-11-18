// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;

namespace Kopru.Core.Virtual;

// The device side of GIP ([MS-GIPUSB] 3.1.1, figure 6), without threads or USB: the pad feeds it the host's OUT data
// (Receive), its clock (Tick) and the controller state (SetInput), and takes the packets to send one per IN transfer
// (NextPacket). All times are milliseconds on the caller's clock. Not thread safe: the pad calls it under its lock.
//
//   Arrival   Hello every 500 ms until the host answers (Metadata Request, START, OFF or RESET) (2.2.1)
//   Metadata  the metadata blob goes out in fragments (ACK requested on the first and the last) (3.1.5.5.4)
//   WaitAck   the last fragment went out; the host's Protocol Control ACK -> Metadata Complete; no ACK in 1 s -> Arrival
//   Idle      waiting for Set Device State START; none within 500 ms of the metadata -> assume it (3.1.1)
//   Active    Status at once, then every 1 s for 10 s, then every 20 s; input reports when something changed; Guide
//             button messages (3.1.5.5.2, 3.1.5.6.1.1)
//   Off       after OFF: one "powering off" status, then silence
//   Resetting after RESET: one "powering off" status, 500 ms later back to Arrival
//
// Not in the specification, so not done: answering the host's ACK requests (the Protocol Control payload layout is not
// published) and the security exchange (the metadata opts out of it, 5.1).
public sealed class GipDevice
{
	public const long HelloIntervalMs = 500;
	public const long AckTimeoutMs = 1000;
	public const long StartTimeoutMs = 500;
	public const long ResetDelayMs = 500;
	public const long StatusFastMs = 1000;
	public const long StatusFastWindowMs = 10_000;
	public const long StatusSlowMs = 20_000;

	// After an OFF that came before any START this session (seen on a quick re-attach): announce again after this long,
	// at most MaxReannounces times. A real pad in Off waits for its Guide button; the PS button does that here too.
	public const long ReannounceDelayMs = 3000;
	public const int MaxReannounces = 3;

	// Distinct raw Protocol Control / security messages written to the log; later ones are counted only.
	public const int MaxLoggedRaw = 8;

	private enum Kind
	{
		Other,
		Hello,
		MetadataFragment,
		MetadataLast
	}

	private readonly Queue<(byte[] Data, Kind Kind)> outgoing = new();


	private readonly HashSet<byte> unknownTypes = [];

	private byte globalSequence, inputSequence, metadataSequence;

	private long nextHelloAt, ackDeadline = long.MaxValue, startDeadline = long.MaxValue, resetAt = long.MaxValue, nextStatusAt = long.MaxValue;

	private long activeSince, reannounceAt = long.MaxValue;

	private bool startedThisSession;

	private readonly Dictionary<byte, int> ackRequestsLogged = [];

	private byte[] input;

	private bool inputDirty, guide, guideSent, guideDirty, ledLogged, fullPowerLogged;

	private int rawControlLogged, rawSecurityLogged, quiesceLogged;

	public GipDevice(GipIdentity identity, ulong deviceId, byte[]? metadata = null)
	{
		Identity = identity;
		DeviceId = deviceId;
		MetadataBlob = metadata ?? GipMetadata.Gamepad(securityOptOut: true, share: identity.Share);
		input = new byte[identity.InputSize];
	}

	public GipIdentity Identity { get; }

	public ulong DeviceId { get; }

	public byte[] MetadataBlob { get; }

	public GipDeviceState State { get; private set; } = GipDeviceState.Detached;

	public GipCounters Counters { get; } = new();

	// One line per protocol step (for the app log).
	public event Action<string>? Note;

	// A Direct Motor Command from the host.
	public event Action<GipMotorCommand>? MotorCommand;

	// The device must clear its motors: Quiesce, Stop, Off or Reset.
	public event Action<string>? MotorsStop;

	// When the device went Active (Tick time), or null.
	public long? ActiveSince => State == GipDeviceState.Active ? activeSince : null;

	// The host started talking to the device (first IN poll): a new session, Arrival, Hello now.
	public void Begin(long now)
	{
		Reannounces = 0;
		startedThisSession = false;
		guideSent = false;
		reannounceAt = long.MaxValue;
		Arrive(now);
	}

	// Times the device announced itself again after an OFF that came before any START (this session).
	public int Reannounces { get; private set; }

	// True while a packet waits for an IN transfer.
	public bool HasQueuedPackets => outgoing.Count > 0;

	// The device is going away (app closing, pad swapped): "powering off" status, motors off, then silence, as a real
	// pad does before it powers off (3.1.1: a disconnect before the host processed that status is "unexpected"). False
	// when there is no GIP session to end.
	public bool PowerOff(string reason)
	{
		if (State is GipDeviceState.Detached or GipDeviceState.Off)
		{
			return false;
		}
		Say($"powering off ({reason}): powering-off status, then silence");
		outgoing.Clear();
		Enqueue(StatusPacket(Gip.StatusPoweringOff), Kind.Other);
		State = GipDeviceState.Off;
		ackDeadline = startDeadline = resetAt = nextStatusAt = reannounceAt = long.MaxValue;
		MotorsStop?.Invoke("power off");
		return true;
	}

	// Arrival (first time, after RESET, after a wake from Off).
	private void Arrive(long now)
	{
		outgoing.Clear();
		State = GipDeviceState.Arrival;
		nextHelloAt = now;
		ackDeadline = startDeadline = resetAt = nextStatusAt = long.MaxValue;
		Say($"arrival: Hello every {HelloIntervalMs} ms ({Identity}, firmware {GipIdentity.FirmwareMajor}.{GipIdentity.FirmwareMinor}." +
			$"{Identity.Build}.{GipIdentity.FirmwareRevision}, metadata {MetadataBlob.Length} bytes)");
		Tick(now);
	}

	// The host let go: nothing more to send.
	public void End()
	{
		outgoing.Clear();
		State = GipDeviceState.Detached;
		ackDeadline = startDeadline = resetAt = nextStatusAt = long.MaxValue;
	}

	public void Tick(long now)
	{
		switch (State)
		{
			case GipDeviceState.Arrival when now >= nextHelloAt:
				if (!outgoing.Any(p => p.Kind == Kind.Hello))
				{
					Enqueue(HelloPacket(), Kind.Hello);
					Counters.Hellos++;
					if (Counters.Hellos == 1 || Counters.Hellos % 20 == 0)
					{
						Say(Counters.Hellos == 1 ? "Hello queued" : $"still no answer to Hello ({Counters.Hellos} sent)");
					}
				}
				nextHelloAt = now + HelloIntervalMs;
				break;
			case GipDeviceState.WaitAck when now >= ackDeadline:
				Say($"no Protocol Control ACK within {AckTimeoutMs} ms after the last metadata fragment -> back to Arrival (Hello)");
				Counters.MetadataTimeouts++;
				State = GipDeviceState.Arrival;
				nextHelloAt = now;
				ackDeadline = long.MaxValue;
				Tick(now);
				break;
			case GipDeviceState.Idle when now >= startDeadline:
				Say($"no Set Device State START within {StartTimeoutMs} ms after the metadata -> assuming START ([MS-GIPUSB] 3.1.1)");
				Counters.AssumedStarts++;
				EnterActive(now);
				break;
			case GipDeviceState.Active when now >= nextStatusAt:
				QueueStatus(Gip.StatusWiredFullPower, now);
				break;
			case GipDeviceState.Resetting when now >= resetAt:
				Say("reset done -> Arrival (Hello)");
				Arrive(now);
				break;
			case GipDeviceState.Off when now >= reannounceAt:
				reannounceAt = long.MaxValue;
				Arrive(now);
				break;
		}
	}

	// The controller state now: the input payload (Identity.InputSize bytes) and the Guide button. Goes out with the next IN transfer when it
	// changed (and the device is Active).
	public void SetInput(byte[] payload, bool guidePressed)
	{
		if (!payload.AsSpan().SequenceEqual(input))
		{
			input = payload;
			inputDirty = true;
		}
		if (guidePressed != guide)
		{
			guide = guidePressed;
			guideDirty = guide != guideSent;
			// 3.1.5.5.6: "When a device is off, the Guide button SHOULD wake the device when pressed."
			if (guide && State == GipDeviceState.Off && !outgoing.Any())
			{
				Say("Guide (PS) pressed while off -> waking: Hello again");
				reannounceAt = 0;
				// The press that woke the device is not reported (3.1.5.5.6: no pressed event until the first release).
				guideSent = true;
				guideDirty = false;
			}
		}
	}

	// The next packet for an IN transfer, or null when there is nothing to send.
	public byte[]? NextPacket(long now)
	{
		if (outgoing.TryDequeue(out (byte[] Data, Kind Kind) packet))
		{
			if (packet.Kind == Kind.MetadataLast && State == GipDeviceState.Metadata)
			{
				State = GipDeviceState.WaitAck;
				ackDeadline = now + AckTimeoutMs;
				Say("last metadata fragment sent, waiting for the host's ACK");
			}
			if (packet.Data[0] == Gip.Status)
			{
				Counters.Statuses++;
			}
			return packet.Data;
		}
		if (State != GipDeviceState.Active)
		{
			return null;
		}
		if (guideDirty)
		{
			guideDirty = false;
			guideSent = guide;
			Counters.GuideMessages++;
			if (Counters.GuideMessages == 1)
			{
				Say($"first Guide Button message ({(guide ? "pressed" : "released")}, payload {(guide ? 1 : 0):X2} {Gip.GuideVirtualKey:X2}; layout inferred, see the probe notes)");
			}
			return [Gip.GuideButton, Gip.FlagSystem, NextSequence(ref globalSequence), 2, (byte)(guide ? 1 : 0), Gip.GuideVirtualKey];
		}
		if (inputDirty)
		{
			inputDirty = false;
			Counters.InputReports++;
			if (Counters.InputReports == 1)
			{
				Say($"first input report sent ({Gip.Hex(input)})");
			}
			return [Gip.Input, 0x00, NextSequence(ref inputSequence), (byte)input.Length, .. input];
		}
		return null;
	}

	// The host's OUT transfer: one or more GIP messages.
	public void Receive(ReadOnlySpan<byte> data, long now)
	{
		List<GipMessage> messages = Gip.ParseAll(data, out bool truncated);
		foreach (GipMessage m in messages)
		{
			Counters.HostMessages++;
			if (m.WantsAck)
			{
				Counters.AckRequests++;
				int logged = ackRequestsLogged.GetValueOrDefault(m.Type);
				if (logged < 4)
				{
					ackRequestsLogged[m.Type] = logged + 1;
					Say($"host asked for an ACK of {Gip.TypeName(m.Type)} (flags 0x{m.Flags:X2}, seq {m.Sequence}, {m.Payload.Length} bytes" +
						$"{(m.TotalOrOffset is int t ? $", total/offset {t}" : "")}): {Gip.Hex(m.Payload, 24)}; not answered, [MS-GIPUSB] does not define the " +
						"Protocol Control payload");
				}
			}
			switch (m.Type)
			{
				case Gip.ProtocolControl:
					OnProtocolControl(m, now);
					break;
				case Gip.Metadata when !m.IsFragment:
					OnMetadataRequest(now);
					break;
				case Gip.SetDeviceState when m.Payload.Length >= 1:
					OnSetDeviceState(m.Payload, now);
					break;
				case Gip.Security:
					Counters.SecurityMessages++;
					if (rawSecurityLogged++ < MaxLoggedRaw)
					{
						Say($"SECURITY message from the host (flags 0x{m.Flags:X2}, seq {m.Sequence}, {m.Payload.Length} bytes{(m.TotalOrOffset is int t ? $", total/offset {t}" : "")}): " +
							$"{Gip.Hex(m.Payload, 16)} - the pad does not take part in the security exchange");
					}
					break;
				case Gip.DirectMotor:
					if (GipReports.ParseMotor(m.Payload) is { } command)
					{
						Counters.MotorCommands++;
						if (command.SetsTriggers)
						{
							Counters.TriggerMotorCommands++;
						}
						MotorCommand?.Invoke(command);
					}
					else
					{
						Say($"Direct Motor message not understood: {Gip.Hex(m.Payload)}");
					}
					break;
				case Gip.Led:
					if (!ledLogged)
					{
						ledLogged = true;
						Say($"Guide button LED command from the host ({Gip.Hex(m.Payload)}), ignored");
					}
					break;
				default:
					Counters.OtherHostMessages++;
					if (unknownTypes.Add(m.Type))
					{
						Say($"host message {Gip.TypeName(m.Type)} (flags 0x{m.Flags:X2}, {m.Payload.Length} bytes: {Gip.Hex(m.Payload, 16)}), ignored");
					}
					break;
			}
		}
		if (truncated)
		{
			Counters.TruncatedTransfers++;
			if (Counters.TruncatedTransfers == 1)
			{
				Say($"host transfer with a truncated GIP header or payload: {Gip.Hex(data, 16)}");
			}
		}
	}

	private void OnProtocolControl(GipMessage m, long now)
	{
		Counters.ProtocolControls++;
		if (rawControlLogged++ < MaxLoggedRaw)
		{
			Say($"Protocol Control from the host (flags 0x{m.Flags:X2}, seq {m.Sequence}): {Gip.Hex(m.Payload)} (in state {State})");
		}
		if (State == GipDeviceState.WaitAck)
		{
			(byte low, byte high) = Gip.TwoByteNumber(MetadataBlob.Length);
			Enqueue([Gip.Metadata, Gip.FlagFragment | Gip.FlagSystem, metadataSequence, 0x00, low, high], Kind.Other);
			State = GipDeviceState.Idle;
			ackDeadline = long.MaxValue;
			startDeadline = now + StartTimeoutMs;
			Counters.MetadataCompletes++;
			Say("metadata ACKed by the host -> Metadata Complete sent, waiting for Set Device State START");
		}
	}

	private void OnMetadataRequest(long now)
	{
		Counters.MetadataRequests++;
		// The latest request wins: drop a transfer still queued, and the Hello that is no longer needed.
		DropHandshakePackets();
		List<byte[]> fragments = Fragments(MetadataBlob, metadataSequence = NextSequence(ref globalSequence));
		for (int i = 0; i < fragments.Count; i++)
		{
			Enqueue(fragments[i], i == fragments.Count - 1 ? Kind.MetadataLast : Kind.MetadataFragment);
		}
		State = GipDeviceState.Metadata;
		ackDeadline = startDeadline = long.MaxValue;
		Say($"Metadata Request #{Counters.MetadataRequests} from the host -> sending metadata ({MetadataBlob.Length} bytes in {fragments.Count} fragments, seq {metadataSequence})");
	}

	private void OnSetDeviceState(byte[] payload, long now)
	{
		byte state = payload[0];
		string extra = payload.Length > 1 ? $", {payload.Length}-byte payload {Gip.Hex(payload, 16)}" : "";
		switch (state)
		{
			case Gip.StateStart:
				Counters.Starts++;
				startedThisSession = true;
				Say($"Set Device State START (in state {State}{extra})" +
					(Counters.MetadataRequests == 0 ? " - no Metadata Request before it: the host had our metadata cached" : ""));
				EnterActive(now);
				break;
			case Gip.StateStop:
				Say($"Set Device State STOP (in state {State}{extra}) -> Idle");
				State = GipDeviceState.Idle;
				startDeadline = long.MaxValue;
				MotorsStop?.Invoke("STOP");
				break;
			case Gip.StateFullPower:
				if (!fullPowerLogged)
				{
					fullPowerLogged = true;
					Say("Set Device State FULL POWER (ignored by wired devices)");
				}
				break;
			case Gip.StateOff:
				Counters.Offs++;
				outgoing.Clear();
				Enqueue(StatusPacket(Gip.StatusPoweringOff), Kind.Other);
				// The host turned off a device it never started. Seen right after a quick re-attach; a real pad would now
				// wait for its Guide button. Announce again after a pause (a "power on"), a few times per session.
				bool again = !startedThisSession && Reannounces < MaxReannounces;
				Say($"Set Device State OFF (in state {State}{extra}) -> powering-off status, then silence" + (again
					? $"; OFF came before any START this session, so the pad announces itself again in {ReannounceDelayMs} ms " +
						$"(attempt {Reannounces + 1} of {MaxReannounces}; or press PS to wake it)"
					: startedThisSession ? "; press PS to wake it" : $"; no more automatic re-announces ({MaxReannounces} done), press PS to wake it"));
				if (again)
				{
					Reannounces++;
					reannounceAt = now + ReannounceDelayMs;
				}
				State = GipDeviceState.Off;
				ackDeadline = startDeadline = resetAt = nextStatusAt = long.MaxValue;
				MotorsStop?.Invoke("OFF");
				break;
			case Gip.StateQuiesce:
				Counters.Quiesces++;
				if (quiesceLogged++ < 20)
				{
					Say("Set Device State QUIESCE (focus change) -> motors cleared");
				}
				MotorsStop?.Invoke("QUIESCE");
				break;
			case Gip.StateReset:
				Say($"Set Device State RESET (in state {State}) -> powering-off status, Hello again in {ResetDelayMs} ms");
				outgoing.Clear();
				Enqueue(StatusPacket(Gip.StatusPoweringOff), Kind.Other);
				State = GipDeviceState.Resetting;
				resetAt = now + ResetDelayMs;
				nextStatusAt = long.MaxValue;
				MotorsStop?.Invoke("RESET");
				break;
			default:
				// 0x02 and 0x06 are "not used" / "reserved" in table 40; nothing to do.
				Say($"Set Device State {Gip.StateName(state)} ({(state is 0x02 or 0x06 ? "reserved in [MS-GIPUSB] table 40" : "unknown")}, ignored{extra})");
				break;
		}
	}

	private void EnterActive(long now)
	{
		// Hellos and a metadata transfer still queued are no longer needed; anything else (Metadata Complete) still goes.
		DropHandshakePackets();
		State = GipDeviceState.Active;
		activeSince = now;
		startDeadline = ackDeadline = long.MaxValue;
		inputDirty = true;
		guideDirty = guide != guideSent;
		QueueStatus(Gip.StatusWiredFullPower, now);
		Say("Active: status sent, input reports follow");
	}

	private void QueueStatus(byte status, long now)
	{
		Enqueue(StatusPacket(status), Kind.Other);
		nextStatusAt = now + (now - activeSince < StatusFastWindowMs ? StatusFastMs : StatusSlowMs);
	}

	private void Enqueue(byte[] packet, Kind kind) => outgoing.Enqueue((packet, kind));

	private void DropHandshakePackets()
	{
		(byte[], Kind)[] kept = [.. outgoing.Where(p => p.Kind == Kind.Other)];
		outgoing.Clear();
		foreach ((byte[], Kind) p in kept)
		{
			outgoing.Enqueue(p);
		}
	}

	private void Say(string line) => Note?.Invoke(line);

	// Extended Status Device message (3.1.5.5.2.2): status, extended status (no events, "device active" clear as for a
	// device without IR LEDs), two reserved bytes.
	private byte[] StatusPacket(byte status) => [Gip.Status, Gip.FlagSystem, NextSequence(ref globalSequence), 4, status, 0x00, 0x00, 0x00];

	// Hello Device (3.1.5.5.1, table 27).
	public byte[] HelloPacket()
	{
		byte[] p = new byte[4 + 28];
		p[0] = Gip.Hello;
		p[1] = Gip.FlagSystem;
		p[2] = NextSequence(ref globalSequence);
		p[3] = 28;
		BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(4), DeviceId);
		BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(12), Identity.VendorId);
		BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(14), Identity.ProductId);
		BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(16), GipIdentity.FirmwareMajor);
		BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(18), GipIdentity.FirmwareMinor);
		BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(20), Identity.Build);
		BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(22), GipIdentity.FirmwareRevision);
		p[24] = 1; // hardware version 1.0
		p[25] = 0;
		p[26] = 1; // RF protocol 1.0
		p[27] = 0;
		p[28] = 1; // security protocol 1.0
		p[29] = 0;
		p[30] = 1; // GIP 1.0
		p[31] = 0;
		return p;
	}

	// The metadata response fragments (3.1.5.5.4.1-3): first (flags 0xF0, payload length, total length in two bytes),
	// middle (0xA0, payload length in two bytes, offset in one byte while below 128, else payload length in one byte and
	// offset in two), last (0xB0, payload length, offset in two bytes). 58 data bytes per fragment.
	public static List<byte[]> Fragments(byte[] blob, byte sequence)
	{
		if (blob.Length <= Gip.FragmentData || blob.Length > 0x3FFF)
		{
			throw new ArgumentException("metadata must need more than one fragment", nameof(blob));
		}
		List<byte[]> fragments = [];
		const byte system = Gip.FlagSystem;
		for (int offset = 0; offset < blob.Length; offset += Gip.FragmentData)
		{
			int length = Math.Min(Gip.FragmentData, blob.Length - offset);
			bool first = offset == 0, last = offset + length == blob.Length;
			byte[] header;
			if (first)
			{
				(byte low, byte high) = Gip.TwoByteNumber(blob.Length);
				header = [Gip.Metadata, Gip.FlagFragment | Gip.FlagInitFragment | system | Gip.FlagAckMe, sequence, (byte)length, low, high];
			}
			else if (last)
			{
				(byte low, byte high) = Gip.TwoByteNumber(offset);
				header = [Gip.Metadata, Gip.FlagFragment | system | Gip.FlagAckMe, sequence, (byte)length, low, high];
			}
			else if (offset < 128)
			{
				(byte low, byte high) = Gip.TwoByteNumber(length);
				header = [Gip.Metadata, Gip.FlagFragment | system, sequence, low, high, (byte)offset];
			}
			else
			{
				(byte low, byte high) = Gip.TwoByteNumber(offset);
				header = [Gip.Metadata, Gip.FlagFragment | system, sequence, (byte)length, low, high];
			}
			fragments.Add([.. header, .. blob.AsSpan(offset, length)]);
		}
		return fragments;
	}

	// Wrapping counter; 0 is reserved (2.2.10.3).
	private static byte NextSequence(ref byte counter)
	{
		counter = (byte)(counter == 0xFF ? 1 : counter + 1);
		return counter;
	}
}

public enum GipDeviceState
{
	Detached,
	Arrival,
	Metadata,
	WaitAck,
	Idle,
	Active,
	Off,
	Resetting
}

// What happened in a session, for the summary line and tests.
public sealed class GipCounters
{
	public int Hellos, MetadataRequests, MetadataCompletes, MetadataTimeouts, ProtocolControls, Starts, AssumedStarts, Statuses, InputReports,
		GuideMessages, MotorCommands, TriggerMotorCommands, Quiesces, Offs, SecurityMessages, OtherHostMessages, HostMessages, TruncatedTransfers, AckRequests;

	public override string ToString() => string.Create(CultureInfo.InvariantCulture,
		$"Hello {Hellos}, metadata requests {MetadataRequests} (completed {MetadataCompletes}, ACK timeouts {MetadataTimeouts}), " +
		$"Protocol Control from host {ProtocolControls}, START {Starts} (assumed {AssumedStarts}), status {Statuses}, input reports {InputReports}, " +
		$"guide {GuideMessages}, motor commands {MotorCommands} (with trigger motors {TriggerMotorCommands}), quiesce {Quiesces}, OFF {Offs}, " +
		$"security {SecurityMessages}, ACK requests from host {AckRequests}, other host messages {OtherHostMessages}, truncated transfers {TruncatedTransfers}");
}
