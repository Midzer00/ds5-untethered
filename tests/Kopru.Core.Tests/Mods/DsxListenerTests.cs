// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Mods;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The DSX listener: 127.0.0.1 only, a taken port reported (never thrown), a reply for every packet, the mod's effects
// handed to the target and ended on reset, a closed mod socket, a long silence and stop; logs once, never per packet.
public class DsxListenerTests
{
	private static readonly IPEndPoint Mod = new(IPAddress.Loopback, 50123);

	internal sealed class RecordingTarget : IDsxTarget
	{
		public ConcurrentQueue<(int Index, ModOutput? Output)> Calls { get; } = new();

		public ModOutput? Now(int index) => Calls.LastOrDefault(c => c.Index == index).Output;

		public void SetModOutput(int index, ModOutput? output) => Calls.Enqueue((index, output));

		public IReadOnlyList<DsxDevice> Devices() => [new DsxDevice(0, false, 80)];
	}

	private static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

	private const string Resistance = "{\"instructions\":[{\"type\":1,\"parameters\":[0,2,13,1,6]},{\"type\":2,\"parameters\":[0,10,20,30]}]}";
	private const string Reset = "{\"instructions\":[{\"type\":7,\"parameters\":[0]}]}";

	private static (DsxListener Listener, RecordingTarget Target, List<string> Log, ManualTime Time) Create(Func<int, bool>? portBound = null)
	{
		RecordingTarget target = new();
		List<string> log = [];
		ManualTime time = new();
		DsxListener listener = new(target, time, line => { lock (log) { log.Add(line); } })
		{
			Port = 0,
			Timer = false,
			PortBound = portBound ?? (_ => true)
		};
		return (listener, target, log, time);
	}

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	[Fact]
	public void Start_ListensOnLoopbackOnly()
	{
		(DsxListener listener, _, List<string> log, _) = Create();
		using (listener)
		{
			DsxStatus status = listener.Start();
			Assert.Equal(DsxListenerState.Listening, status.State);
			Assert.NotEqual(0, listener.BoundPort);
			Assert.Equal($"dsx: listening on 127.0.0.1:{listener.BoundPort}", log.Single());
			// Nothing listens on the other addresses of this PC for that port.
			Assert.Contains(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners(),
				e => e.Port == listener.BoundPort && e.Address.Equals(IPAddress.Loopback));
			Assert.DoesNotContain(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners(),
				e => e.Port == listener.BoundPort && !IPAddress.IsLoopback(e.Address));
		}
	}

	[Fact]
	public void Handle_DropsPacketsNotFromThisPc()
	{
		(DsxListener listener, RecordingTarget target, _, _) = Create();
		Assert.Null(listener.Handle(Json(Resistance), new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5000)));
		Assert.Empty(target.Calls);
		Assert.Equal(0, listener.Packets);
	}

	[Fact]
	public void PortInUse_IsReported_LoggedOnce_AndNeverThrown()
	{
		using UdpClient holder = new(new IPEndPoint(IPAddress.Loopback, 0));
		int port = ((IPEndPoint)holder.Client.LocalEndPoint!).Port;
		foreach (Func<int, bool> bound in new Func<int, bool>[] { UdpPortTable.IsBound, _ => false })
		{
			List<string> log = [];
			using DsxListener listener = new(new RecordingTarget(), log: log.Add) { Port = port, Timer = false, PortBound = bound };
			Assert.Equal(DsxListenerState.PortInUse, listener.Start().State);
			Assert.Equal(DsxListenerState.PortInUse, listener.Start().State);
			Assert.Equal([$"dsx: port {port} in use, not listening"], log);
		}
		Assert.True(UdpPortTable.IsBound(port));
	}

	[Fact]
	public void Packet_IsAnswered_AndApplied_FirstMessageLoggedOnce()
	{
		(DsxListener listener, RecordingTarget target, List<string> log, _) = Create();
		byte[]? reply = listener.Handle(Json(Resistance), Mod);
		listener.Handle(Json(Resistance), Mod);

		using JsonDocument doc = JsonDocument.Parse(reply!);
		Assert.Equal(DsxListener.ReplyStatus, doc.RootElement.GetProperty("Status").GetString());
		Assert.Equal(80, doc.RootElement.GetProperty("BatteryLevel").GetInt32());
		ModOutput now = target.Now(0)!;
		Assert.Equal(TriggerEffects.Feedback(1, 6), now.RightTrigger);
		Assert.Equal(((byte)10, (byte)20, (byte)30), now.Lightbar);
		// The same packet again changes nothing: one call.
		Assert.Single(target.Calls);
		Assert.Equal(["dsx: first message from a mod (TriggerUpdate, RGBUpdate)"], log);
		Assert.True(listener.Status.ModActive);
	}

	[Fact]
	public void StatusOnly_IsAnswered_ButAppliesNothing()
	{
		(DsxListener listener, RecordingTarget target, _, _) = Create();
		Assert.NotNull(listener.Handle(Json("{\"instructions\":[{\"type\":0,\"parameters\":null}]}"), Mod));
		Assert.Empty(target.Calls);
		Assert.False(listener.Status.ModActive);
	}

	[Fact]
	public void Malformed_Oversized_AndUnknown_AreCounted_UnknownLoggedOncePerType()
	{
		(DsxListener listener, _, List<string> log, _) = Create();
		Assert.Null(listener.Handle(Json("{nope"), Mod));
		Assert.Null(listener.Handle(new byte[DsxProtocol.MaxPacketBytes + 1], Mod));
		string unknown = "{\"instructions\":[{\"type\":42,\"parameters\":[0,1,2]}]}";
		listener.Handle(Json(unknown), Mod);
		listener.Handle(Json(unknown), Mod);
		Assert.Equal(1, listener.Malformed);
		Assert.Equal(1, listener.Oversized);
		Assert.Equal(2, listener.UnknownInstructions);
		Assert.Single(log, l => l.StartsWith("dsx: unknown instruction type 42 ignored: {\"type\":42", StringComparison.Ordinal));
	}

	[Fact]
	public void Reset_EndsTheEffects()
	{
		(DsxListener listener, RecordingTarget target, List<string> log, _) = Create();
		listener.Handle(Json(Resistance), Mod);
		listener.Handle(Json(Reset), Mod);
		Assert.Null(target.Now(0));
		Assert.Contains("dsx: mod effects ended (reset by the mod)", log);
		Assert.False(listener.Status.ModActive);
	}

	[Fact]
	public void ClosedModSocket_EndsTheEffects_OnceQuiet()
	{
		bool open = true;
		(DsxListener listener, RecordingTarget target, List<string> log, ManualTime time) = Create(p => p != Mod.Port || open);
		listener.Handle(Json(Resistance), Mod);
		open = false;
		time.Advance(TimeSpan.FromSeconds(1));
		listener.Tick();
		Assert.NotNull(target.Now(0));
		time.Advance(DsxListener.SenderCheckDelay);
		listener.Tick();
		Assert.Null(target.Now(0));
		Assert.Contains("dsx: mod effects ended (the mod closed its connection)", log);
	}

	[Fact]
	public void ModsUsingANewSocketPerMessage_AreNotEndedByTheSocketCheck_ButBySilence()
	{
		(DsxListener listener, RecordingTarget target, List<string> log, ManualTime time) = Create(_ => false);
		listener.Handle(Json(Resistance), new IPEndPoint(IPAddress.Loopback, 50001));
		listener.Handle(Json("{\"instructions\":[{\"type\":2,\"parameters\":[0,1,1,1]}]}"), new IPEndPoint(IPAddress.Loopback, 50002));
		time.Advance(TimeSpan.FromSeconds(30));
		listener.Tick();
		Assert.NotNull(target.Now(0));
		time.Advance(DsxListener.MultiSenderQuietTimeout);
		listener.Tick();
		Assert.Null(target.Now(0));
		Assert.Contains("dsx: mod effects ended (no message for 60 s)", log);
	}

	[Fact]
	public void OneSocketMod_EndsAfterTheLongSilence()
	{
		(DsxListener listener, RecordingTarget target, List<string> log, ManualTime time) = Create();
		listener.Handle(Json(Resistance), Mod);
		time.Advance(TimeSpan.FromMinutes(2));
		listener.Tick();
		Assert.NotNull(target.Now(0));
		time.Advance(DsxListener.DefaultQuietTimeout);
		listener.Tick();
		Assert.Null(target.Now(0));
		Assert.Contains("dsx: mod effects ended (no message for 300 s)", log);
	}

	[Fact]
	public void InvalidUtf8InAString_IsMalformed_AndTheLoopKeepsReceiving()
	{
		byte[] bad = [.. Json("{\"instructions\":[{\"type\":\""), 0xC3, 0x28, .. Json("\",\"parameters\":[0]}]}")];
		(DsxPacket? packet, DsxParseError error) = DsxProtocol.Parse(bad);
		Assert.Null(packet);
		Assert.Equal(DsxParseError.Malformed, error);
		byte[] badParam = [.. Json("{\"instructions\":[{\"type\":1,\"parameters\":[0,\""), 0xFF, 0xFE, .. Json("\",13]}]}")];
		Assert.Equal(DsxParseError.Malformed, DsxProtocol.Parse(badParam).Error);

		(DsxListener listener, RecordingTarget target, _, _) = Create();
		using (listener)
		{
			listener.Start();
			using UdpClient mod = new(new IPEndPoint(IPAddress.Loopback, 0));
			IPEndPoint to = new(IPAddress.Loopback, listener.BoundPort);
			mod.Send(bad, to);
			mod.Send(badParam, to);
			WaitUntil(() => listener.Malformed == 2);
			mod.Send(Json(Resistance), to);
			WaitUntil(() => target.Now(0) != null);
			Assert.Equal(DsxListenerState.Listening, listener.Status.State);
		}
	}

	[Fact]
	public void PacketWaitingDuringStop_AppliesNothing_AndStopClearsEveryController()
	{
		(DsxListener listener, RecordingTarget target, _, _) = Create();
		listener.Start();
		listener.Handle(Json(Resistance), Mod);
		listener.Stop();
		// The receive thread got the gate only after Stop: nothing starts again.
		listener.Handle(Json(Resistance), Mod);
		Assert.Null(target.Now(0));
		Assert.False(listener.Status.ModActive);
		for (int i = 0; i < DsxModState.MaxControllers; i++)
		{
			Assert.Contains(target.Calls, c => c.Index == i && c.Output == null);
		}
		// A new start takes packets again.
		listener.Start();
		listener.Handle(Json(Resistance), Mod);
		Assert.NotNull(target.Now(0));
		listener.Stop();
	}

	[Fact]
	public void ModChosenText_IsCapped_AndCannotForgeLogLines()
	{
		(DsxListener listener, _, List<string> log, _) = Create();
		for (int i = 0; i < 2000; i++)
		{
			listener.Handle(Json($"{{\"instructions\":[{{\"type\":\"x{i}\\ndsx: forged line\",\"parameters\":[0]}},{{\"type\":1,\"parameters\":[0,2,\"m{i}\"]}}]}}"), Mod);
		}
		Assert.True(log.Count <= DsxListener.LogOnceLimit + 2, $"{log.Count} lines");
		Assert.All(log, l => Assert.DoesNotContain('\n', l));
		Assert.All(log, l => Assert.True(l.Length < 300, l));
		string longType = new('a', 500);
		DsxInstruction i1 = Assert.Single(DsxProtocol.Parse(Json($"{{\"instructions\":[{{\"type\":\"{longType}\"}}]}}")).Packet!.Instructions);
		Assert.True(i1.TypeText.Length <= DsxProtocol.NameLength + 3);
	}

	[Fact]
	public void StatusChanged_NotPerPacket_OnlyOnFirstMessageAndFlips()
	{
		(DsxListener listener, _, _, _) = Create();
		int raised = 0;
		listener.StatusChanged += _ => raised++;
		listener.Handle(Json(Resistance), Mod);
		Assert.Equal(1, raised);
		for (int i = 0; i < 100; i++)
		{
			listener.Handle(Json($"{{\"instructions\":[{{\"type\":2,\"parameters\":[0,{i},0,0]}}]}}"), Mod);
		}
		Assert.Equal(1, raised);
		listener.Handle(Json(Reset), Mod);
		Assert.Equal(2, raised);
	}

	[Fact]
	public void Stop_EndsTheEffects_AndLogsTheCountsOnce()
	{
		(DsxListener listener, RecordingTarget target, List<string> log, _) = Create();
		listener.Start();
		listener.Handle(Json(Resistance), Mod);
		listener.Handle(Json("{bad"), Mod);
		listener.Stop();
		listener.Stop();
		Assert.Null(target.Now(0));
		Assert.Equal(DsxListenerState.Off, listener.Status.State);
		Assert.Contains("dsx: mod effects ended (DSX mod support turned off)", log);
		Assert.Single(log, l => l.StartsWith("dsx: 1 packets, 2 instructions,", StringComparison.Ordinal) && l.Contains("1 malformed"));
	}

	// A bridge-level target: index 0 is the bridge.
	private sealed class BridgeTarget(ControllerBridge bridge) : IDsxTarget
	{
		public void SetModOutput(int index, ModOutput? output)
		{
			if (index == 0)
			{
				bridge.SetModOutput(output);
			}
		}

		public IReadOnlyList<DsxDevice> Devices() => [new DsxDevice(0, false, 50)];
	}

	[Fact]
	public async Task Loopback_ARealUdpPacket_ReachesTheBridge_AndGetsTheReply()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = new(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration, [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
			new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] }));
		using ControllerBridge bridge = new(session, pad);
		using DsxListener listener = new(new BridgeTarget(bridge), log: _ => { }) { Port = 0, Timer = false };
		Assert.Equal(DsxListenerState.Listening, listener.Start().State);

		using UdpClient mod = new(new IPEndPoint(IPAddress.Loopback, 0));
		byte[] packet = Json("{\"instructions\":[{\"type\":1,\"parameters\":[0,1,16,2,6,8]},{\"type\":2,\"parameters\":[0,255,0,0]}]}");
		await mod.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, listener.BoundPort));
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		UdpReceiveResult reply = await mod.ReceiveAsync(timeout.Token);
		using (JsonDocument doc = JsonDocument.Parse(reply.Buffer))
		{
			Assert.Equal(DsxListener.ReplyStatus, doc.RootElement.GetProperty("Status").GetString());
			Assert.Equal(50, doc.RootElement.GetProperty("BatteryLevel").GetInt32());
		}

		// Wait for the write itself (ModOutput is set just before it goes to the link).
		byte[] weapon = TriggerEffects.Weapon(2, 6, 8);
		bool Written(byte[] w) => w[0] == 0x31 && w.AsSpan(3 + OutputBlock.LeftTrigger, TriggerEffects.Size).SequenceEqual(weapon);
		WaitUntil(() => link.Writes.Any(Written));
		byte[] sent = link.Writes.Last(Written).AsSpan(3, OutputBlock.Size).ToArray();
		Assert.Equal(TriggerEffects.Weapon(2, 6, 8), sent.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray());
		Assert.Equal(255, sent[OutputBlock.Red]);
		Assert.Equal(TriggerEffects.Weapon(2, 6, 8), bridge.ControllerTriggers().Left);

		// The mod resets: back to the game's (none: off).
		await mod.SendAsync(Json(Reset), new IPEndPoint(IPAddress.Loopback, listener.BoundPort));
		WaitUntil(() => bridge.ModOutput == null);
		Assert.Equal(TriggerEffects.Off(), bridge.ControllerTriggers().Left);
	}
}
