// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

public class XboxEmulationTests
{
	private static byte[] Submit(uint seq, uint direction, uint endpoint, int length, byte[]? setup = null, byte[]? data = null)
	{
		byte[] h = new byte[48];
		UsbIpWire.W32(h, 0, UsbIpCodes.CmdSubmit);
		UsbIpWire.W32(h, 4, seq);
		UsbIpWire.W32(h, 8, 0x10001);
		UsbIpWire.W32(h, 12, direction);
		UsbIpWire.W32(h, 16, endpoint);
		UsbIpWire.W32(h, 24, (uint)length);
		UsbIpWire.W32(h, 32, unchecked((uint)-1));
		(setup ?? new byte[8]).CopyTo(h, 40);
		return [.. h, .. data ?? []];
	}

	// A DualSense USB input report: sticks centred, D-pad released, plus the given changes.
	private static byte[] Ds(Action<byte[]>? change = null)
	{
		byte[] r = new byte[UsbReports.InputSize];
		r[0] = UsbReports.InputId;
		r[1] = r[2] = r[3] = r[4] = 0x80;
		r[8] = 0x08;
		change?.Invoke(r);
		return r;
	}

	[Fact]
	public void Descriptors_AreTheWiredXbox360Set()
	{
		byte[] d = VirtualXbox360.DeviceDescriptor;
		Assert.Equal((18, 1), (d[0], d[1]));
		Assert.Equal((0x045E, 0x028E), (d[8] | (d[9] << 8), d[10] | (d[11] << 8)));
		byte[] c = VirtualXbox360.ConfigurationDescriptor;
		Assert.Equal(c.Length, c[2] | (c[3] << 8));
		Assert.Equal(153, c.Length);
		int interfaces = 0, endpoints = 0, at = 0;
		while (at < c.Length)
		{
			Assert.True(c[at] > 0);
			interfaces += c[at + 1] == 4 ? 1 : 0;
			endpoints += c[at + 1] == 5 ? 1 : 0;
			at += c[at];
		}
		Assert.Equal(c.Length, at);
		Assert.Equal((4, 7), (interfaces, endpoints));
		Assert.Equal(c[4], interfaces);
		Assert.Equal(4, new VirtualXbox360().Info.Interfaces.Count);
	}

	[Fact]
	public async Task OverUsbIp_Enumerates_AndDeliversInputAndRumble()
	{
		using VirtualXbox360 pad = new();
		List<byte[]> outputs = [];
		pad.OutputReceived += o => { lock (outputs) outputs.Add(o); };
		await using UsbIpServer server = new(pad);
		server.Start();

		using (TcpClient list = new())
		{
			await list.ConnectAsync("127.0.0.1", server.Port);
			NetworkStream ls = list.GetStream();
			await ls.WriteAsync(UsbIpWire.OpHeader(UsbIpCodes.OpReqDevlist, 0));
			byte[] reply = await UsbIpWire.ReadExactlyAsync(ls, 8 + 4 + 312 + 16, default);
			Assert.Equal((0x045E, 0x028E), (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(12 + 300)), BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(12 + 302))));
			Assert.Equal(4, reply[12 + 311]);
			Assert.Equal((0xFF, 0x5D, 0x01), (reply[12 + 312], reply[12 + 313], reply[12 + 314]));
		}

		using TcpClient client = new();
		await client.ConnectAsync("127.0.0.1", server.Port);
		NetworkStream s = client.GetStream();
		byte[] busid = new byte[32];
		Encoding.ASCII.GetBytes(VirtualXbox360.BusId, busid);
		await s.WriteAsync((byte[])[.. UsbIpWire.OpHeader(UsbIpCodes.OpReqImport, 0), .. busid]);
		Assert.Equal(0u, UsbIpWire.U32(await UsbIpWire.ReadExactlyAsync(s, 8 + 312, default), 4));

		// GET_DESCRIPTOR device and configuration, as the hub driver asks.
		await s.WriteAsync(Submit(1, UsbIpCodes.DirIn, 0, 18, setup: [0x80, 6, 0, 1, 0, 0, 18, 0]));
		byte[] dev = await UsbIpWire.ReadExactlyAsync(s, 48 + 18, default);
		Assert.Equal(VirtualXbox360.DeviceDescriptor, dev[48..]);
		await s.WriteAsync(Submit(2, UsbIpCodes.DirIn, 0, 255, setup: [0x80, 6, 0, 2, 0, 0, 255, 0]));
		byte[] cfg = await UsbIpWire.ReadExactlyAsync(s, 48 + 153, default);
		Assert.Equal(VirtualXbox360.ConfigurationDescriptor, cfg[48..]);
		await s.WriteAsync(Submit(3, UsbIpCodes.DirOut, 0, 0, setup: [0x00, 9, 1, 0, 0, 0, 0, 0]));
		Assert.Equal(3u, UsbIpWire.U32(await UsbIpWire.ReadExactlyAsync(s, 48, default), 4));

		// Interrupt IN waits for a change, then carries the 20-byte report.
		await s.WriteAsync(Submit(4, UsbIpCodes.DirIn, 1, 32));
		await Task.Delay(50);
		pad.PushInput(Ds(r => r[8] = 0x28));
		byte[] input = await UsbIpWire.ReadExactlyAsync(s, 48 + 20, default);
		Assert.Equal(4u, UsbIpWire.U32(input, 4));
		Assert.Equal((0x00, 0x14, XboxReports.A), (input[48], input[49], input[51]));

		// Rumble OUT becomes a DualSense output report with the classic motors.
		await s.WriteAsync(Submit(5, UsbIpCodes.DirOut, 1, 8, data: [0x00, 0x08, 0x00, 200, 50, 0, 0, 0]));
		Assert.Equal(5u, UsbIpWire.U32(await UsbIpWire.ReadExactlyAsync(s, 48, default), 4));
		Assert.True(SpinWait.SpinUntil(() => { lock (outputs) return outputs.Count == 1; }, 2000));
		byte[] o = outputs[0];
		Assert.Equal((UsbReports.OutputId, (byte)200, (byte)50), (o[0], o[1 + OutputBlock.MotorLeft], o[1 + OutputBlock.MotorRight]));
	}

	public static TheoryData<int, int, byte, byte> Buttons => new()
	{
		// DualSense report byte, bit, Xbox byte (2 low / 3 high), Xbox bit
		{ 8, 0x20, 3, XboxReports.A },
		{ 8, 0x40, 3, XboxReports.B },
		{ 8, 0x10, 3, XboxReports.X },
		{ 8, 0x80, 3, XboxReports.Y },
		{ 9, 0x01, 3, XboxReports.LB },
		{ 9, 0x02, 3, XboxReports.RB },
		{ 10, 0x01, 3, XboxReports.Guide },
		{ 9, 0x20, 2, XboxReports.Start },
		{ 9, 0x10, 2, XboxReports.Back },
		{ 10, 0x02, 2, XboxReports.Back },
		{ 9, 0x40, 2, XboxReports.LeftStick },
		{ 9, 0x80, 2, XboxReports.RightStick },
	};

	[Theory]
	[MemberData(nameof(Buttons))]
	public void Buttons_Map(int dsByte, int dsBit, byte xByte, byte xBit)
	{
		byte[] x = XboxReports.FromDualSense(Ds(r => r[dsByte] |= (byte)dsBit));
		Assert.Equal(xBit, x[xByte]);
		Assert.Equal(0, x[xByte == 2 ? 3 : 2]);
	}

	[Theory]
	[InlineData(0, XboxReports.DpadUp)]
	[InlineData(1, XboxReports.DpadUp | XboxReports.DpadRight)]
	[InlineData(2, XboxReports.DpadRight)]
	[InlineData(4, XboxReports.DpadDown)]
	[InlineData(6, XboxReports.DpadLeft)]
	[InlineData(7, XboxReports.DpadUp | XboxReports.DpadLeft)]
	[InlineData(8, 0)]
	public void Dpad_Hat_Maps(int hat, int bits)
	{
		Assert.Equal(bits, XboxReports.FromDualSense(Ds(r => r[8] = (byte)hat))[2]);
	}

	[Fact]
	public void Sticks_AreCentred_AndYIsInverted_TriggersAreAnalog()
	{
		byte[] centre = XboxReports.FromDualSense(Ds());
		Assert.All(new[] { 6, 8, 10, 12 }, i => Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(centre.AsSpan(i))));
		byte[] x = XboxReports.FromDualSense(Ds(r => { r[1] = 255; r[2] = 0; r[3] = 0; r[4] = 255; r[5] = 77; r[6] = 255; }));
		Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(x.AsSpan(6)));
		// Stick pushed up (DualSense Y 0) is positive for XInput.
		Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(x.AsSpan(8)));
		Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(x.AsSpan(10)));
		Assert.Equal(-short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(x.AsSpan(12)));
		Assert.Equal((77, 255), (x[4], x[5]));
	}

	[Fact]
	public void Rumble_Converts_LedReportIsIgnored()
	{
		byte[]? r = XboxReports.RumbleToDualSense([0x00, 0x08, 0x00, 0xFF, 0x10, 0, 0, 0]);
		Assert.NotNull(r);
		Assert.Equal(UsbReports.OutputSize, r!.Length);
		Assert.NotEqual(0, r[1 + OutputBlock.Flag0] & OutputBlock.CompatibleVibration);
		Assert.Equal((0xFF, 0x10), (r[1 + OutputBlock.MotorLeft], r[1 + OutputBlock.MotorRight]));
		Assert.Equal(0, r[1 + OutputBlock.Flag1]);
		Assert.Null(XboxReports.RumbleToDualSense([0x01, 0x03, 0x06]));
	}

	[Fact]
	public async Task GameChange_SwapsPad_DetachBeforeAttach_KeepsController()
	{
		ConcurrentQueue<string> log = new();
		QueueControllers controllers = new();
		DeviceIdentity identity = DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
			[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "Sony Interactive Entertainment" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });
		await using BridgeHost host = new(controllers, new FakeIdentity(identity), new FakeAttacher(log), new ManualTime())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		ConcurrentQueue<VirtualPadType> changed = new();
		host.PadTypeChanged += changed.Enqueue;
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));

		host.UsePad(VirtualPadType.Xbox360);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 1, 5000));
		host.UsePad(VirtualPadType.DualSense);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 2, 5000));

		Assert.Equal([VirtualPadType.Xbox360, VirtualPadType.DualSense], changed);
		Assert.Equal(["attach 1", "detach 1", "attach 2", "detach 2", "attach 3"], log.Where(e => e.StartsWith("attach") || e.StartsWith("detach")));
		Assert.DoesNotContain("dispose a", log);
		Assert.Equal(BridgeState.Running, host.State);
	}

	[Fact]
	public void PerGameChoice_FollowsTheGameInFront_NeverDefaultsToXbox()
	{
		GameCatalog catalog = new([new CatalogGame("Assetto Corsa Competizione", ["acc.exe"], null, false, true)]);
		var processes = new GameModeTests.FakeProcesses();
		var bridge = new PadBridge();
		var games = new GameModeController(catalog, new GameWatcher(catalog, processes), bridge);
		Assert.Equal([VirtualPadType.DualSense], bridge.Pads);

		processes.Show("acc.exe");
		games.Tick();
		string key = games.ActiveGame!.Key;
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor(key));
		int saves = 0;
		games.MemoryChanged += () => saves++;
		games.SetPadType(key, VirtualPadType.Xbox360);
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		Assert.Equal(1, saves);

		processes.Exit("acc.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
		processes.Show("acc.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);

		GameMemory memory = games.SnapshotMemory();
		Assert.Equal(VirtualPadType.Xbox360, memory.PadTypes![key]);
		var again = new GameModeController(catalog, new GameWatcher(catalog, new GameModeTests.FakeProcesses()), null);
		again.LoadMemory(memory);
		Assert.Equal(VirtualPadType.Xbox360, again.PadTypeFor(key));

		games.SetPadType(key, VirtualPadType.DualSense);
		Assert.Empty(games.SnapshotMemory().PadTypes!);
	}

	private sealed class PadBridge : IGameBridge
	{
		public List<VirtualPadType> Pads { get; } = [];

		public void ApplyProfile(BridgeProfile profile)
		{
		}

		public GameSignals TakeGameSignals() => default;

		public void UsePad(VirtualPadType type) => Pads.Add(type);
	}
}
