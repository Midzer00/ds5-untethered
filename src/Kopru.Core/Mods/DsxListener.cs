// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Kopru.Core.Mods;

// Where the mod's output goes: one ModOutput per controller index (null ends it), and the controllers for the reply.
public interface IDsxTarget
{
	void SetModOutput(int index, ModOutput? output);

	IReadOnlyList<DsxDevice> Devices();

	// Called on every expiry tick: a target that could not place an output (no host for that index yet) places it now.
	void Sync()
	{
	}
}

// A controller as the reply describes it.
public readonly record struct DsxDevice(int Index, bool IsEdge, int BatteryPercent);

public enum DsxListenerState
{
	Off,
	Listening,
	// Another program (DSX itself, say) holds the port.
	PortInUse,
	// The socket could not be opened for another reason.
	Failed
}

// The listener's state for the UI: listening on Port, a mod's effects active, when its last message came.
public sealed record DsxStatus(DsxListenerState State, int Port, bool ModActive, DateTimeOffset? LastMessageAt)
{
	public static readonly DsxStatus Off = new(DsxListenerState.Off, DsxListener.DefaultPort, false, null);
}

// DSX mod support (docs/DSX-COMPAT.md): receives the UDP messages of game mods made for DSX on 127.0.0.1 only, answers
// every packet with a DSX-style status reply (before applying it), and hands the mod's trigger, light and LED requests
// to the bridge (IDsxTarget). A mod's effects end on ResetToUserSettings (for the controllers it names), when the mod
// closes its socket (it exited; checked once it is quiet for SenderCheckDelay, and only while all its messages came from
// one socket), after QuietTimeout without a message, and when the listener stops.
// Log lines (never one per packet): "dsx: listening on 127.0.0.1:6969", "dsx: port 6969 in use, not listening",
// "dsx: first message from a mod (TriggerUpdate, RGBUpdate)", "dsx: mod effects ended (reset by the mod)",
// "dsx: unknown instruction type 9 ignored: {...}" (once per type), "dsx: TriggerUpdate not understood, ignored: {...}"
// (once per type and mode), and one count line when the listener stops.
public sealed class DsxListener : IDisposable
{
	public const int DefaultPort = 6969;

	public const string ReplyStatus = "DSX Received UDP Instructions";

	public static readonly TimeSpan DefaultQuietTimeout = TimeSpan.FromMinutes(5);

	// A mod whose messages came from more than one socket cannot be watched for exiting: it ends after this much silence.
	public static readonly TimeSpan MultiSenderQuietTimeout = TimeSpan.FromSeconds(60);

	// "Logged once" lines per listener at most (their keys hold text a mod chose); the rest are only counted.
	public const int LogOnceLimit = 32;

	// A mod quiet for this long is checked for a closed socket.
	public static readonly TimeSpan SenderCheckDelay = TimeSpan.FromSeconds(2);

	public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

	// "first message" and "effects ended" lines per listener session at most (a mod that resets every frame must not
	// flood the log).
	public const int SessionLogLimit = 20;

	private readonly IDsxTarget target;
	private readonly TimeProvider time;
	private readonly Action<string> log;
	private readonly object gate = new();
	private readonly DsxModState state = new();
	private readonly HashSet<string> loggedOnce = [];

	private Socket? socket;
	private CancellationTokenSource? cancel;
	private Task? loop;
	private ITimer? timer;
	private DsxStatus status = DsxStatus.Off;

	// The mod session (guarded by gate): a message came since the last end; its socket, whether all messages came from it,
	// and the time of the last one.
	private bool session;
	private IPEndPoint? sender;
	private bool singleSender;
	private DateTimeOffset lastMessage;
	private int sessionLines;

	// Set by Stop, cleared by Start (guarded by gate): a packet that was waiting for the gate while Stop ran applies nothing.
	private bool stopped;
	private long suppressedLines;

	private long packets, instructions, malformed, oversized, unknown, ignored, replies, dropped;

	public DsxListener(IDsxTarget target, TimeProvider? time = null, Action<string>? log = null)
	{
		this.target = target;
		this.time = time ?? TimeProvider.System;
		this.log = log ?? Diagnostics.AppLog.Write;
	}

	// The port to listen on (0: any free port, for tests).
	public int Port { get; init; } = DefaultPort;

	public TimeSpan QuietTimeout { get; init; } = DefaultQuietTimeout;

	// Whether a local UDP port is bound (a mod's socket still open, the port taken before we bind). Tests replace it.
	public Func<int, bool> PortBound { get; init; } = UdpPortTable.IsBound;

	// Runs the expiry check every TickInterval (tests turn it off and call Tick).
	public bool Timer { get; init; } = true;

	// The port actually bound while listening.
	public int BoundPort { get; private set; }

	public DsxStatus Status
	{
		get
		{
			lock (gate)
			{
				return status;
			}
		}
	}

	// Raised on a state change, a mod's first message, the end of its effects, and once per TickInterval while its effects
	// are active (so "last message N s ago" can follow); on any thread.
	public event Action<DsxStatus>? StatusChanged;

	public long Packets => Interlocked.Read(ref packets);
	public long Malformed => Interlocked.Read(ref malformed);
	public long Oversized => Interlocked.Read(ref oversized);
	public long UnknownInstructions => Interlocked.Read(ref unknown);
	public long Replies => Interlocked.Read(ref replies);

	// Opens the socket on 127.0.0.1:Port; a taken port is logged and reported (PortInUse), never thrown.
	public DsxStatus Start()
	{
		lock (gate)
		{
			if (socket != null)
			{
				return status;
			}
			Socket s = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			try
			{
				if (Port != 0 && PortBound(Port))
				{
					throw new SocketException((int)SocketError.AddressAlreadyInUse);
				}
				s.ExclusiveAddressUse = true;
				if (OperatingSystem.IsWindows())
				{
					// A reply to a mod that closed its socket must not end the receive loop (SIO_UDP_CONNRESET off).
					s.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
				}
				s.Bind(new IPEndPoint(IPAddress.Loopback, Port));
			}
			catch (SocketException e)
			{
				s.Dispose();
				bool inUse = e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied;
				DsxStatus failed = new(inUse ? DsxListenerState.PortInUse : DsxListenerState.Failed, Port, false, null);
				// A retry that fails the same way logs and raises nothing.
				if (failed != status)
				{
					log(inUse ? $"dsx: port {Port} in use, not listening" : $"dsx: could not listen on 127.0.0.1:{Port}: {e.SocketErrorCode}");
					SetStatus(failed);
				}
				return status;
			}
			socket = s;
			stopped = false;
			BoundPort = ((IPEndPoint)s.LocalEndPoint!).Port;
			cancel = new CancellationTokenSource();
			CancellationToken token = cancel.Token;
			loop = Task.Run(() => ReceiveLoop(s, token));
			if (Timer)
			{
				timer = time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
			}
			log($"dsx: listening on 127.0.0.1:{BoundPort}");
			SetStatus(new DsxStatus(DsxListenerState.Listening, BoundPort, false, null));
			return status;
		}
	}

	// Closes the socket; the mod's effects end (reason logged) and the session's counts are logged once.
	public void Stop(string reason = "DSX mod support turned off")
	{
		Socket? s;
		Task? running;
		lock (gate)
		{
			stopped = true;
			s = socket;
			running = loop;
			socket = null;
			loop = null;
			timer?.Dispose();
			timer = null;
			cancel?.Cancel();
			cancel?.Dispose();
			cancel = null;
			if (s != null || session)
			{
				EndSession(reason);
			}
			if (s != null)
			{
				LogCounts();
			}
			SetStatus(DsxStatus.Off with { Port = Port });
		}
		s?.Dispose();
		try
		{
			running?.Wait(TimeSpan.FromSeconds(2));
		}
		catch (AggregateException)
		{
			// The loop ends with the socket.
		}
	}

	public void Dispose() => Stop("app exit");

	private async Task ReceiveLoop(Socket s, CancellationToken token)
	{
		byte[] buffer = new byte[65536];
		EndPoint any = new IPEndPoint(IPAddress.Any, 0);
		while (!token.IsCancellationRequested)
		{
			SocketReceiveFromResult r;
			try
			{
				r = await s.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize)
			{
				Interlocked.Increment(ref oversized);
				continue;
			}
			catch (SocketException)
			{
				if (token.IsCancellationRequested)
				{
					return;
				}
				continue;
			}
			if (r.RemoteEndPoint is not IPEndPoint from)
			{
				continue;
			}
			try
			{
				Handle(buffer.AsSpan(0, r.ReceivedBytes), from, s);
			}
			catch (Exception)
			{
				// Whatever a packet does, the loop keeps receiving.
				Interlocked.Increment(ref malformed);
			}
		}
	}

	// One datagram: dropped unless it came from this PC; too large or not a DSX message: counted and ignored. Otherwise
	// the reply goes out first (mods may wait for it), then the instructions are applied. Returns the reply (tests).
	public byte[]? Handle(ReadOnlySpan<byte> data, IPEndPoint from, Socket? replyTo = null)
	{
		if (!IPAddress.IsLoopback(from.Address))
		{
			Interlocked.Increment(ref dropped);
			return null;
		}
		(DsxPacket? packet, DsxParseError error) = DsxProtocol.Parse(data);
		if (packet == null)
		{
			if (error == DsxParseError.Oversized)
			{
				Interlocked.Increment(ref oversized);
			}
			else
			{
				Interlocked.Increment(ref malformed);
			}
			return null;
		}
		Interlocked.Increment(ref packets);
		Interlocked.Add(ref instructions, packet.Instructions.Count);
		byte[] reply = Reply(SafeDevices(), time.GetLocalNow());
		if (replyTo != null)
		{
			try
			{
				replyTo.SendTo(reply, from);
				Interlocked.Increment(ref replies);
			}
			catch (Exception e) when (e is SocketException or ObjectDisposedException)
			{
				// The mod is gone or the listener stopped.
			}
		}
		Apply(packet, from);
		return reply;
	}

	private IReadOnlyList<DsxDevice> SafeDevices()
	{
		try
		{
			return target.Devices();
		}
		catch (Exception)
		{
			return [];
		}
	}

	private void Apply(DsxPacket packet, IPEndPoint from)
	{
		DsxStatus? raise = null;
		lock (gate)
		{
			if (stopped)
			{
				return;
			}
			DateTimeOffset now = time.GetUtcNow();
			bool first = !session;
			if (!session)
			{
				session = true;
				sender = from;
				singleSender = true;
				if (sessionLines++ < SessionLogLimit)
				{
					log($"dsx: first message from a mod ({TypeList(packet)})");
				}
			}
			else if (sender == null || !sender.Equals(from))
			{
				singleSender = false;
				sender = from;
			}
			lastMessage = now;
			bool hadEffects = state.Any;
			bool reset = false;
			HashSet<int> changed = [];
			foreach (DsxInstruction instruction in packet.Instructions)
			{
				DsxApplyResult result = state.Apply(instruction, changed);
				switch (result)
				{
					case DsxApplyResult.Reset:
						reset = true;
						break;
					case DsxApplyResult.Unknown:
						Interlocked.Increment(ref unknown);
						LogOnce($"unknown {instruction.TypeText}", $"dsx: unknown instruction type {instruction.TypeText} ignored: {instruction.Sample}");
						break;
					case DsxApplyResult.Invalid or DsxApplyResult.NoController:
						Interlocked.Increment(ref ignored);
						string what = instruction.Type == DsxInstructionType.TriggerUpdate && instruction.Args.Count > 1
							? $"{instruction.Type} mode {instruction.Args[1].Name ?? instruction.Args[1].Int.ToString(CultureInfo.InvariantCulture)}"
							: $"{instruction.Type}";
						LogOnce($"{result} {what}", result == DsxApplyResult.NoController
							? $"dsx: {instruction.Type} for controller index {instruction.Index} ignored (no such controller)"
							: $"dsx: {what} not understood, ignored: {instruction.Sample}");
						break;
				}
			}
			foreach (int index in changed.Order())
			{
				SafeSet(index, state[index]);
			}
			if (reset && hadEffects && !state.Any)
			{
				LogEnded("reset by the mod");
			}
			DsxStatus next = status with { ModActive = state.Any, LastMessageAt = now };
			// Only a flip or a mod's first message: the per-tick raise follows "last message N s ago" (a racing mod sends
			// 60-360 packets a second, which must not each reach the UI).
			if (next.ModActive != status.ModActive || first)
			{
				raise = next;
			}
			status = next;
		}
		if (raise != null)
		{
			Raise(raise);
		}
	}

	// The expiry check (every TickInterval): a mod quiet for QuietTimeout, or one that closed its socket, ends; while its
	// effects stay, the UI hears about it once per tick.
	public void Tick()
	{
		try
		{
			target.Sync();
		}
		catch (Exception)
		{
			// Tried again on the next tick.
		}
		DsxStatus? raise = null;
		lock (gate)
		{
			if (!session)
			{
				return;
			}
			TimeSpan quiet = time.GetUtcNow() - lastMessage;
			TimeSpan limit = singleSender ? QuietTimeout : MultiSenderQuietTimeout < QuietTimeout ? MultiSenderQuietTimeout : QuietTimeout;
			if (quiet >= limit)
			{
				EndSession(string.Create(CultureInfo.InvariantCulture, $"no message for {limit.TotalSeconds:0} s"));
			}
			else if (singleSender && sender != null && quiet >= SenderCheckDelay && !SafePortBound(sender.Port))
			{
				EndSession("the mod closed its connection");
			}
			else if (state.Any)
			{
				raise = status;
			}
		}
		if (raise != null)
		{
			Raise(raise);
		}
	}

	private bool SafePortBound(int port)
	{
		try
		{
			return PortBound(port);
		}
		catch (Exception)
		{
			return true;
		}
	}

	// Caller holds gate: every controller the mod changed goes back to normal; the session ends.
	private void EndSession(string reason)
	{
		bool had = state.Any;
		// Every controller, not only the ones the state holds: nothing a mod set may stay behind.
		for (int i = 0; i < DsxModState.MaxControllers; i++)
		{
			SafeSet(i, null);
		}
		state.Clear();
		if (session && had)
		{
			LogEnded(reason);
		}
		session = false;
		sender = null;
		if (status.State == DsxListenerState.Listening)
		{
			SetStatus(status with { ModActive = false });
		}
	}

	private void LogEnded(string reason)
	{
		if (sessionLines < SessionLogLimit * 2)
		{
			sessionLines++;
			log($"dsx: mod effects ended ({reason})");
		}
	}

	private void SafeSet(int index, ModOutput? output)
	{
		try
		{
			target.SetModOutput(index, output);
		}
		catch (Exception e)
		{
			LogOnce("target " + e.GetType().Name, $"dsx: could not apply a mod's output: {e.GetType().Name}");
		}
	}

	private void LogOnce(string key, string line)
	{
		if (loggedOnce.Count >= LogOnceLimit && !loggedOnce.Contains(key))
		{
			Interlocked.Increment(ref suppressedLines);
			return;
		}
		if (loggedOnce.Add(key))
		{
			log(line);
		}
	}

	private void LogCounts()
	{
		if (Packets == 0 && Malformed == 0 && Oversized == 0 && Interlocked.Read(ref dropped) == 0)
		{
			return;
		}
		log(string.Create(CultureInfo.InvariantCulture,
			$"dsx: {Packets} packets, {Interlocked.Read(ref instructions)} instructions, {Replies} replies, {Malformed} malformed, {Oversized} oversized, {UnknownInstructions} unknown, {Interlocked.Read(ref ignored)} ignored, {Interlocked.Read(ref dropped)} not from this PC, {Interlocked.Read(ref suppressedLines)} log lines left out"));
	}

	// Caller holds gate (or it is the start/stop path); raised after the change.
	private void SetStatus(DsxStatus next)
	{
		status = next;
		Raise(next);
	}

	private void Raise(DsxStatus value)
	{
		try
		{
			StatusChanged?.Invoke(value);
		}
		catch (Exception)
		{
			// A listener failing never breaks the receive loop.
		}
	}

	private static string TypeList(DsxPacket packet)
	{
		IEnumerable<string> names = packet.Instructions.Select(i => i.Type?.ToString() ?? $"type {i.TypeText}").Distinct();
		string list = string.Join(", ", names);
		return list.Length == 0 ? "no instructions" : list;
	}

	// The status reply (names and casing as DSX's): the first controller's battery, and one Devices entry per controller.
	// The MAC address is never sent (zeros).
	public static byte[] Reply(IReadOnlyList<DsxDevice> devices, DateTimeOffset now)
	{
		using MemoryStream stream = new();
		using (Utf8JsonWriter w = new(stream))
		{
			w.WriteStartObject();
			w.WriteString("Status", ReplyStatus);
			w.WriteString("TimeReceived", now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
			w.WriteBoolean("isControllerConnected", devices.Count > 0);
			w.WriteNumber("BatteryLevel", devices.Count > 0 ? devices[0].BatteryPercent : 0);
			w.WriteStartArray("Devices");
			foreach (DsxDevice d in devices)
			{
				w.WriteStartObject();
				w.WriteNumber("Index", d.Index);
				w.WriteString("MacAddress", "00:00:00:00:00:00");
				w.WriteNumber("DeviceType", d.IsEdge ? 1 : 0);
				w.WriteNumber("ConnectionType", 1);
				w.WriteNumber("BatteryLevel", d.BatteryPercent);
				w.WriteBoolean("IsSupportAT", true);
				w.WriteBoolean("IsSupportLightBar", true);
				w.WriteBoolean("IsSupportPlayerLED", true);
				w.WriteBoolean("IsSupportMicLED", true);
				w.WriteEndObject();
			}
			w.WriteEndArray();
			w.WriteEndObject();
		}
		return stream.ToArray();
	}
}
