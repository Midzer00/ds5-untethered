// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// A wired Xbox One / Series controller made of software, served over USB/IP, built from Microsoft's open specification
// [MS-GIPUSB] 1.0 only: the Xbox Series pad type (BridgeHostPads; identity GipIdentity.XboxSeries with the Share button,
// a device id per controller from GipDeviceIds). Windows binds its in-box Xbox driver (dc1-controller.inf with the
// xboxgip filter) to it, and games see it through XInput and Windows.Gaming.Input. Everything the host does is written to
// the app log.
//
// USB (2.2.3-2.2.9): full speed, device class FF/47/D0, 64-byte EP0, one configuration with one interface (FF/47/D0,
// no audio) and two interrupt endpoints of 64 bytes, bInterval 4 ms: OUT 0x01 (host to pad) and IN 0x81 (pad to host).
// The Microsoft OS string descriptor (index 0xEE, "MSFT100", vendor code 0x90) and the extended compatible ID descriptor
// (vendor request 0x90, wIndex 4) name the compatible ID "XGIP10", which dc1-controller.inf matches as
// USB\MS_COMP_XGIP10 whatever the VID/PID. The device qualifier and anything else not listed is stalled, as the
// specification asks. GIP itself runs in GipDevice.
//
// To the bridge it is VirtualPadType.XboxSeries: the Xbox 360 pad's features (rumble only; no audio, light bar or speaker)
// plus the trigger motors. Rumble: the main motors go to the
// DualSense like the Xbox 360 pad's (OutputReceived); the trigger motors' levels go to the bridge through
// ITriggerRumbleSource, which plays them as adaptive-trigger vibration (ControllerBridge.PlayTriggerRumble). Each motor
// follows its command's duration/delay/repeat (GipMotors).
public sealed class VirtualXboxOne : IVirtualPad, IUnplugNotice, ITriggerRumbleSource, IChangeOnlyMotors
{
	public const string BusId = VirtualDualSense.BusId;
	public const uint InEndpoint = 1;
	public const uint OutEndpoint = 1;
	public const byte VendorCode = 0x90;
	public const byte OsStringIndex = 0xEE;
	public const long TickMs = 20;
	public const long EarlySummaryMs = 10_000;

	// Distinct motor commands written to the log per session, and at most this many motor lines per second.
	public const int MaxLoggedMotorCommands = 64;
	public const int MaxMotorLinesPerSecond = 10;

	// Microsoft OS string descriptor (2.2.6, table 5): "MSFT100" in UTF-16LE, vendor code, pad.
	public static readonly byte[] OsStringDescriptor = BuildOsString();

	// Extended compatible ID descriptor (2.2.6, table 6): 40 bytes, version 1.00, wIndex 4, one function: interface 0,
	// one interface, compatible ID "XGIP10".
	public static readonly byte[] CompatibleIdDescriptor =
	[
		0x28, 0x00, 0x00, 0x00, 0x00, 0x01, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x01, (byte)'X', (byte)'G', (byte)'I', (byte)'P', (byte)'1', (byte)'0', 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
	];

	// One configuration, one interface (2.2.8, 2.2.9): bus powered with remote wakeup (0xA0), 500 mA; interface 0
	// FF/47/D0 with interrupt OUT 0x01 and IN 0x81, 64 bytes, every 4 ms.
	public static readonly byte[] ConfigurationDescriptor =
	[
		0x09, 0x02, 0x20, 0x00, 0x01, 0x01, 0x00, 0xA0, 0xFA,
		0x09, 0x04, 0x00, 0x00, 0x02, 0xFF, 0x47, 0xD0, 0x00,
		0x07, 0x05, 0x01, 0x03, 0x40, 0x00, 0x04,
		0x07, 0x05, 0x81, 0x03, 0x40, 0x00, 0x04
	];

	private readonly object gate = new();

	private readonly object motorGate = new();

	private readonly Queue<(uint Sequence, Action<SubmitResult> Complete)> inputWaiting = new();

	private readonly GipDevice gip;

	private readonly GipMotors motors = new();

	private readonly PadRequestLog requests;

	private readonly Func<long> nowMs;

	private readonly Dictionary<byte, string> strings;

	private readonly HashSet<GipMotorCommand> loggedMotors = [];

	// Work GipDevice asked for while gate was held, run after it is released (guarded by gate).
	private readonly List<Action> deferred = [];

	private Timer? timer;

	private byte configuration;

	private bool begun, earlySummaryDone, firstTriggerLogged, motorOverflowLogged;

	private long motorLineSecond = -1;

	private int motorLinesThisSecond;

	private byte maxLeftTrigger, maxRightTrigger, maxLeftMain, maxRightMain;

	// identity: what the pad says it is (null: generic GIP device); nowMs: a millisecond clock (tests); autoTick: false
	// leaves the GIP timers to Tick() (tests).
	public VirtualXboxOne(string busId = BusId, GipIdentity? identity = null, Func<long>? nowMs = null, bool autoTick = true, ulong? deviceId = null)
	{
		GipIdentity id = identity ?? GipIdentity.Generic;
		AutoTick = autoTick;
		long origin = Stopwatch.GetTimestamp();
		this.nowMs = nowMs ?? (() => (Stopwatch.GetTimestamp() - origin) * 1000 / Stopwatch.Frequency);
		gip = new GipDevice(id, deviceId ?? NewDeviceId());
		DeviceDescriptor =
		[
			0x12, 0x01, 0x00, 0x02, 0xFF, 0x47, 0xD0, 0x40, (byte)id.VendorId, (byte)(id.VendorId >> 8), (byte)id.ProductId, (byte)(id.ProductId >> 8),
			(byte)id.BcdDevice, (byte)(id.BcdDevice >> 8), 0x01, 0x02, 0x03, 0x01
		];
		strings = new Dictionary<byte, string>
		{
			[1] = id.Manufacturer,
			[2] = id.Product,
			[3] = SerialFor(gip.DeviceId)
		};
		Info = new UsbIpDeviceInfo("/kopru/xboxone", busId, 1, 1, UsbIpCodes.SpeedFull, id.VendorId, id.ProductId, id.BcdDevice, 0xFF, 0x47, 0xD0, 1, 1,
			[(0xFF, 0x47, 0xD0)]);
		requests = new PadRequestLog(id.Share ? "Xbox Series" : "Xbox One", busId, -1, () => Log);
		gip.Note += line => requests.Note(() => "GIP " + line);
		// Raised under gate: the motor work runs after the lock is released (it calls the bridge).
		gip.MotorCommand += command =>
		{
			int number = gip.Counters.MotorCommands;
			deferred.Add(() => OnMotorCommand(command, number));
		};
		gip.MotorsStop += _ => deferred.Add(OnMotorsStop);
	}

	public VirtualPadType Type => VirtualPadType.XboxSeries;

	// Windows started the pad (a Set Device State START from the host in this pad's life; an assumed START does not count).
	public bool HostStarted
	{
		get
		{
			lock (gate)
			{
				return gip.Counters.Starts > 0;
			}
		}
	}

	// The pad went away the way a real one does: the host picked up its "powering off" status (BeforeUnplugAsync), or there
	// was no GIP session to end. False until BeforeUnplugAsync ran.
	public bool EndedCleanly { get; private set; }

	public UsbIpDeviceInfo Info { get; }

	public GipIdentity Identity => gip.Identity;

	public byte[] DeviceDescriptor { get; }

	public bool AutoTick { get; }

	// Where the diagnostics go (null: the app log; tests).
	public Action<string>? Log { get; init; }

	// DualSense-format output (0x02) with the game's main rumble in the classic motor bytes.
	public event Action<byte[]>? OutputReceived;

	public event Action<ReadOnlyMemory<byte>>? AudioReceived
	{
		add { }
		remove { }
	}

	// The four motor levels (percent) whenever they change: by a Direct Motor Command, when its time is over, or when the
	// host clears them (Quiesce, Stop, Off, Reset, unplug). Raised on the USB/IP or timer thread.
	public event Action<GipMotorLevels>? MotorsChanged;

	// The trigger motors' levels whenever one changes (same causes as MotorsChanged), in order.
	public event Action<TriggerRumble>? TriggerRumbleChanged;

	public GipDeviceState State
	{
		get
		{
			lock (gate)
			{
				return gip.State;
			}
		}
	}

	public GipCounters Counters => gip.Counters;

	public GipMotorLevels Motors
	{
		get
		{
			lock (motorGate)
			{
				return motors.Levels;
			}
		}
	}

	public byte[] MetadataBlob => gip.MetadataBlob;

	// 2.2.1.3: the four most significant bytes are 00 00 FF FB and the rest "MUST be Random numbers, determined on bootup";
	// two devices with the same ID both fail. A new pad (each app start, each pad swap) is a new boot, so a quick
	// re-attach never shows the host the ID of an instance it may still hold. The USB serial carries the same ID, so
	// Windows makes a new device entry per session (Device Manager, "Show hidden devices" lists the old ones).
	public static ulong NewDeviceId()
	{
		uint random;
		do
		{
			random = BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
		}
		while (random == 0);
		return 0x0000FFFB_00000000UL | random;
	}

	public ulong DeviceId => gip.DeviceId;

	// How long BeforeUnplugAsync waits for the host to pick up the powering-off status, then lets it process it (3.1.1:
	// "SHOULD then wait 500 ms before a power off"). Tests set them to 0.
	public int PowerOffDeliveryMs { get; init; } = 300;

	public int PowerOffSettleMs { get; init; } = 500;

	// Before the bridge unplugs the pad (app exit, pad swap): tell the host the pad is powering off, as a real pad does,
	// so the next attach starts fresh instead of looking like the return of a device that vanished.
	public async Task BeforeUnplugAsync()
	{
		bool started;
		lock (gate)
		{
			started = gip.PowerOff("unplug");
		}
		RunDeferred();
		Pump();
		if (!started)
		{
			EndedCleanly = true;
			return;
		}
		Stopwatch waited = Stopwatch.StartNew();
		bool delivered = false;
		while (true)
		{
			lock (gate)
			{
				delivered = !gip.HasQueuedPackets;
			}
			if (delivered || waited.ElapsedMilliseconds >= PowerOffDeliveryMs)
			{
				break;
			}
			await Task.Delay(10).ConfigureAwait(false);
		}
		long deliveryMs = waited.ElapsedMilliseconds;
		EndedCleanly = delivered;
		if (PowerOffSettleMs > 0)
		{
			await Task.Delay(PowerOffSettleMs).ConfigureAwait(false);
		}
		requests.Note(() => delivered
			? $"GIP powering-off status picked up by the host after {deliveryMs} ms; unplugging {PowerOffSettleMs} ms later"
			: $"GIP powering-off status NOT picked up within {PowerOffDeliveryMs} ms (host not polling); unplugging anyway");
	}

	// 32 hex digits carrying the 64-bit device ID (2.2.4).
	public static string SerialFor(ulong deviceId) => deviceId.ToString("X32", CultureInfo.InvariantCulture);

	public void PushInput(ReadOnlySpan<byte> usbInputReport)
	{
		byte[] payload = GipReports.FromDualSense(usbInputReport, out bool guide, gip.Identity.Share);
		lock (gate)
		{
			gip.SetInput(payload, guide);
		}
		Pump();
	}

	public void Submit(SubmitCommand command, Action<SubmitResult> complete)
	{
		if (command.Endpoint == 0)
		{
			SubmitResult result = Control(command.Setup, command.Data);
			complete(result);
			requests.Control(command.Setup, result);
			return;
		}
		if (command.Endpoint == InEndpoint && command.IsIn && !command.IsIsochronous)
		{
			lock (gate)
			{
				inputWaiting.Enqueue((command.Sequence, complete));
				BeginIfNeeded();
			}
			requests.InPoll(InEndpoint);
			Pump();
			return;
		}
		if (command.Endpoint == OutEndpoint && !command.IsIn && !command.IsIsochronous)
		{
			complete(SubmitResult.Ok(command.Length));
			requests.Output(command.Data, true);
			lock (gate)
			{
				BeginIfNeeded();
				gip.Receive(command.Data, nowMs());
			}
			RunDeferred();
			Pump();
			return;
		}
		complete(SubmitResult.Stall());
	}

	public bool Unlink(uint sequence)
	{
		lock (gate)
		{
			int before = inputWaiting.Count;
			(uint, Action<SubmitResult>)[] kept = [.. inputWaiting.Where(w => w.Sequence != sequence)];
			inputWaiting.Clear();
			foreach ((uint, Action<SubmitResult>) w in kept)
			{
				inputWaiting.Enqueue(w);
			}
			return inputWaiting.Count < before;
		}
	}

	public event Action? HostReleased;

	public void Released()
	{
		Stop("host released");
		HostReleased?.Invoke();
	}

	// The bridge stopped the rumble these motors played (IChangeOnlyMotors): forgotten without raising anything.
	public void ForgetMotors(bool main, bool triggers)
	{
		lock (motorGate)
		{
			motors.Forget(main, triggers);
		}
	}

	public void Dispose() => Stop("disposed");

	// Runs the GIP timers once (the pad's own timer does this every TickMs; tests call it).
	public void Tick()
	{
		long now = nowMs();
		bool summary = false;
		lock (gate)
		{
			gip.Tick(now);
			if (!earlySummaryDone && gip.ActiveSince is long since && now - since >= EarlySummaryMs)
			{
				earlySummaryDone = summary = true;
			}
		}
		lock (motorGate)
		{
			GipMotorLevels before = motors.Levels;
			if (motors.Tick(now))
			{
				EmitMotors(before);
			}
		}
		RunDeferred();
		if (summary)
		{
			NoteSummary("10 s after START");
		}
		Pump();
	}

	private void Stop(string reason)
	{
		bool wasBegun;
		lock (gate)
		{
			inputWaiting.Clear();
			configuration = 0;
			wasBegun = begun;
			begun = false;
			earlySummaryDone = false;
			timer?.Dispose();
			timer = null;
			gip.End();
			deferred.Clear();
		}
		lock (motorGate)
		{
			GipMotorLevels before = motors.Levels;
			if (motors.Stop())
			{
				EmitMotors(before);
			}
		}
		if (wasBegun)
		{
			NoteSummary(reason);
		}
		requests.End(reason);
	}

	// The host started polling or sending: GIP arrival (Hello) and the timer. Under gate.
	private void BeginIfNeeded()
	{
		if (begun)
		{
			return;
		}
		begun = true;
		firstTriggerLogged = motorOverflowLogged = false;
		loggedMotors.Clear();
		maxLeftTrigger = maxRightTrigger = maxLeftMain = maxRightMain = 0;
		motors.ResetCounts();
		gip.Begin(nowMs());
		if (AutoTick)
		{
			timer = new Timer(_ => SafeTick(), null, TickMs, TickMs);
		}
	}

	private void SafeTick()
	{
		try
		{
			Tick();
		}
		catch (Exception e)
		{
			requests.Note(() => "GIP timer error: " + e.Message);
		}
	}

	// Hands queued GIP packets to waiting IN transfers (completions outside the lock).
	private void Pump()
	{
		while (true)
		{
			Action<SubmitResult>? complete;
			byte[]? packet;
			lock (gate)
			{
				if (inputWaiting.Count == 0 || gip.NextPacket(nowMs()) is not { } next)
				{
					return;
				}
				complete = inputWaiting.Dequeue().Complete;
				packet = next;
			}
			requests.InputDelivered();
			complete(SubmitResult.In(packet));
		}
	}

	private void RunDeferred()
	{
		Action[] work;
		lock (gate)
		{
			if (deferred.Count == 0)
			{
				return;
			}
			work = [.. deferred];
			deferred.Clear();
		}
		foreach (Action action in work)
		{
			action();
		}
	}

	// The motor state has its own lock (never taken with gate held).
	private void OnMotorCommand(GipMotorCommand command, int number)
	{
		long now = nowMs();
		lock (motorGate)
		{
			maxLeftTrigger = Math.Max(maxLeftTrigger, command.SetsLeftTrigger ? command.LeftTrigger : (byte)0);
			maxRightTrigger = Math.Max(maxRightTrigger, command.SetsRightTrigger ? command.RightTrigger : (byte)0);
			maxLeftMain = Math.Max(maxLeftMain, command.SetsLeftMain ? command.LeftMain : (byte)0);
			maxRightMain = Math.Max(maxRightMain, command.SetsRightMain ? command.RightMain : (byte)0);
			LogMotor(command, number, now);
			GipMotorLevels before = motors.Levels;
			if (motors.Apply(command, now))
			{
				EmitMotors(before);
			}
		}
	}

	private void OnMotorsStop()
	{
		lock (motorGate)
		{
			GipMotorLevels before = motors.Levels;
			if (motors.Stop())
			{
				EmitMotors(before);
			}
		}
	}

	// Under motorGate, so the reports leave in the order the levels changed. The main motors' report only when they
	// changed, the trigger levels only when they changed (nothing is sent twice for the same state).
	private void EmitMotors(GipMotorLevels before)
	{
		GipMotorLevels levels = motors.Levels;
		MotorsChanged?.Invoke(levels);
		if (levels.LeftTrigger != before.LeftTrigger || levels.RightTrigger != before.RightTrigger)
		{
			TriggerRumbleChanged?.Invoke(new TriggerRumble(levels.LeftTrigger, levels.RightTrigger));
		}
		if (levels.LeftMain != before.LeftMain || levels.RightMain != before.RightMain)
		{
			OutputReceived?.Invoke(GipReports.RumbleToDualSense(levels));
		}
	}

	// Every distinct command once (up to MaxLoggedMotorCommands), at most MaxMotorLinesPerSecond lines a second; the
	// first command that drives a trigger motor always.
	private void LogMotor(GipMotorCommand command, int number, long now)
	{
		if (command.SetsTriggers && !command.StopsAll && (command.LeftTrigger | command.RightTrigger) != 0 && !firstTriggerLogged)
		{
			firstTriggerLogged = true;
			requests.Note(() => $"GIP FIRST TRIGGER RUMBLE (command #{number}): {command}");
			loggedMotors.Add(command);
			return;
		}
		if (loggedMotors.Contains(command))
		{
			return;
		}
		if (loggedMotors.Count >= MaxLoggedMotorCommands)
		{
			if (!motorOverflowLogged)
			{
				motorOverflowLogged = true;
				requests.Note(() => $"GIP more than {MaxLoggedMotorCommands} distinct motor commands, further ones are only counted");
			}
			return;
		}
		long second = now / 1000;
		if (second != motorLineSecond)
		{
			motorLineSecond = second;
			motorLinesThisSecond = 0;
		}
		if (motorLinesThisSecond >= MaxMotorLinesPerSecond)
		{
			return;
		}
		motorLinesThisSecond++;
		loggedMotors.Add(command);
		requests.Note(() => $"GIP motor command #{number}: {command}");
	}

	private void NoteSummary(string reason)
	{
		string counters;
		GipDeviceState state;
		lock (gate)
		{
			counters = gip.Counters.ToString();
			state = gip.State;
		}
		string motorsLine;
		lock (motorGate)
		{
			motorsLine = string.Create(CultureInfo.InvariantCulture,
				$"max levels LT {maxLeftTrigger} RT {maxRightTrigger} LM {maxLeftMain} RM {maxRightMain}, distinct logged {loggedMotors.Count}, " +
				$"stopped by their own time {motors.Expired} (trigger motors {motors.ExpiredTriggers})");
		}
		requests.Note(() => $"GIP summary ({reason}): state {state}, {counters}; motors {motorsLine}");
	}

	// A control transfer on endpoint 0 (public for tests).
	public SubmitResult Control(byte[] setup, byte[] data)
	{
		byte requestType = setup[0];
		byte request = setup[1];
		ushort value = (ushort)(setup[2] | (setup[3] << 8));
		ushort index = (ushort)(setup[4] | (setup[5] << 8));
		int length = setup[6] | (setup[7] << 8);
		int type = (requestType >> 5) & 3;
		bool isIn = (requestType & 0x80) != 0;
		if (type == 0)
		{
			switch (request)
			{
				case 6 when isIn:
					return Descriptor((byte)(value >> 8), (byte)value, length);
				case 0:
					return Answer([0, 0], length);
				case 8:
					lock (gate)
					{
						return Answer([configuration], length);
					}
				case 9:
					lock (gate)
					{
						configuration = (byte)value;
					}
					return SubmitResult.Ok();
				case 10:
					return Answer([0], length);
				case 11 when value == 0:
				case 1:
				case 3:
					return SubmitResult.Ok();
				default:
					return SubmitResult.Stall();
			}
		}
		// The extended compatible ID descriptor; the extended properties descriptor (wIndex 5) and every other vendor or
		// class request are stalled (2.2.3.3).
		if (type == 2 && isIn && request == VendorCode && index == 4)
		{
			return Answer(CompatibleIdDescriptor, length);
		}
		return SubmitResult.Stall();
	}

	private SubmitResult Descriptor(byte type, byte index, int length) => type switch
	{
		1 => Answer(DeviceDescriptor, length),
		2 => Answer(ConfigurationDescriptor, length),
		3 when index == 0 => Answer([4, 3, 0x09, 0x04], length),
		3 when index == OsStringIndex => Answer(OsStringDescriptor, length),
		3 when strings.TryGetValue(index, out string? text) => Answer(StringDescriptor(text), length),
		// Device qualifier: stalled, so Windows sees a full-speed-only device (2.2.5).
		_ => SubmitResult.Stall()
	};

	private static SubmitResult Answer(byte[] data, int length) => SubmitResult.In(data[..Math.Min(length, data.Length)]);

	private static byte[] StringDescriptor(string text)
	{
		byte[] body = Encoding.Unicode.GetBytes(text);
		return [(byte)(body.Length + 2), 3, .. body];
	}

	private static byte[] BuildOsString()
	{
		byte[] signature = Encoding.Unicode.GetBytes("MSFT100");
		return [0x12, 0x03, .. signature, VendorCode, 0x00];
	}
}
