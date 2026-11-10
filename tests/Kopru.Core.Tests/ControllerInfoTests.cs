// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Kopru.Core.Bridge;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Localization;

namespace Kopru.Core.Tests;

// A link that answers feature reports from fixtures (no input, never written to).
internal sealed class InfoFeatureLink(Transport transport, Dictionary<byte, byte[]> features, bool edge = false) : IHidLink
{
	public const string BtPath = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#info";

	public HidDevice Device { get; } = new(transport == Transport.Bluetooth ? BtPath : @"\\?\hid#vid_054c&pid_0ce6#usb",
		0x054C, edge ? HidDevice.DualSenseEdgeProductId : HidDevice.DualSenseProductId, 78, 547, 64);

	public List<byte> Asked { get; } = [];
	public int Writes { get; private set; }
	public bool Disposed { get; private set; }

	public int Read(Span<byte> buffer) => throw new IOException("no input");
	public void Write(ReadOnlySpan<byte> report) => Writes++;

	public byte[] GetFeature(byte reportId)
	{
		Asked.Add(reportId);
		return features.TryGetValue(reportId, out byte[]? r) ? (byte[])r.Clone() : throw new IOException("not answered");
	}

	public void CancelRead() { }
	public void Dispose() => Disposed = true;
}

[Collection("Localization")]
public class ControllerInfoTests
{
	public const ulong Address = 0xA0B1C2D3E4F5;

	// A 0x20 report as a DualSense sends it (64 bytes, made-up values at the public offsets).
	public static byte[] FirmwareFixture()
	{
		byte[] r = new byte[64];
		r[0] = 0x20;
		Encoding.ASCII.GetBytes("Jun  5 2023").CopyTo(r, 1);
		Encoding.ASCII.GetBytes("14:13:39").CopyTo(r, 12);
		BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), 0x00000616);
		BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(28), 0x0110002A);
		BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(44), 0x0630);
		return r;
	}

	public static byte[] PairingFixture(ulong address)
	{
		byte[] r = new byte[20];
		r[0] = 0x09;
		for (int i = 0; i < 6; i++)
		{
			r[1 + i] = (byte)(address >> (8 * i));
		}
		return r;
	}

	public static Dictionary<byte, byte[]> Features(ulong address = Address) => new() { [0x20] = FirmwareFixture(), [0x09] = PairingFixture(address) };

	[Fact]
	public void Firmware_ParsesThePublicOffsets()
	{
		FirmwareInfo info = FirmwareInfo.Parse(FirmwareFixture())!;
		Assert.Equal(new DateTime(2023, 6, 5, 14, 13, 39), info.BuildTime);
		Assert.Equal("Jun 5 2023 14:13:39", info.BuildText);
		Assert.Equal(0x00000616u, info.HardwareVersion);
		Assert.Equal(0x0110002Au, info.FirmwareVersion);
		Assert.Equal((ushort)0x0630, info.UpdateVersion);
		Assert.Equal("06.30", info.FirmwareText);
		Assert.Equal("0x0110002A", info.FirmwareHex);
		Assert.Equal("0x00000616", info.HardwareText);
	}

	[Fact]
	public void Firmware_RejectsOtherReports_AndShortOnes_KeepsUnparsableDates()
	{
		byte[] other = FirmwareFixture();
		other[0] = 0x05;
		Assert.Null(FirmwareInfo.Parse(other));
		Assert.Null(FirmwareInfo.Parse(FirmwareFixture().AsSpan(0, 40)));
		byte[] odd = FirmwareFixture();
		Encoding.ASCII.GetBytes("??? ?? ????").CopyTo(odd, 1);
		FirmwareInfo info = FirmwareInfo.Parse(odd)!;
		Assert.Null(info.BuildTime);
		Assert.StartsWith("???", info.BuildText);
		odd[44] = odd[45] = 0;
		Assert.Equal("0x0110002A", FirmwareInfo.Parse(odd)!.FirmwareText);
	}

	[Fact]
	public void Pairing_AddressIsLsbFirst_AndMatchesTheExistingParser()
	{
		byte[] report = PairingFixture(Address);
		Assert.Equal(Address, PairingInfo.ParseAddress(report));
		Assert.Equal(Kopru.Core.Bluetooth.BluetoothAddress.FromPairingFeature(report), PairingInfo.ParseAddress(report));
		Assert.Null(PairingInfo.ParseAddress(new byte[20] { 0x09, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
		Assert.Null(PairingInfo.ParseAddress([0x20, 1, 2, 3, 4, 5, 6]));
		Assert.Equal(0x015552504B02UL, PairingInfo.VirtualAddress);
	}

	[Fact]
	public void Address_FormatsAndMasksAllButTheLastTwoBytes()
	{
		Assert.Equal("A0:B1:C2:D3:E4:F5", AddressText.Format(Address));
		Assert.Equal("XX:XX:XX:XX:E4:F5", AddressText.Mask(Address));
		Assert.Equal("XX:XX:XX:XX:00:01", AddressText.Mask(1));
	}

	[Fact]
	public void Details_ReadOnlyFeatures_NeverWrites()
	{
		InfoFeatureLink link = new(Transport.Bluetooth, Features(), edge: true);
		ControllerDetails d = ControllerDetails.Read(link);
		Assert.Equal([(byte)0x20, (byte)0x09], link.Asked);
		Assert.Equal(0, link.Writes);
		Assert.Equal("DualSense Edge", d.ModelName);
		Assert.Equal("Bluetooth", d.TransportName);
		Assert.Equal(Address, d.Address);
		Assert.Equal("XX:XX:XX:XX:E4:F5", d.MaskedAddress);
		Assert.Equal("06.30", d.Firmware!.FirmwareText);

		ControllerDetails none = ControllerDetails.Read(new InfoFeatureLink(Transport.Usb, []));
		Assert.Null(none.Firmware);
		Assert.Null(none.Address);
		Assert.Equal("USB", none.TransportName);
		Assert.Equal("—", none.MaskedAddress);
	}

	[Theory]
	[InlineData(Language.Turkish, "Kumanda Bilgisi", "Yazılım Sürümü: 06.30 (0x0110002A)", "Bağlantı Süresi: 5 dk 03 sn", "Pil: %70")]
	[InlineData(Language.English, "Controller Info", "Firmware Version: 06.30 (0x0110002A)", "Connected For: 5 min 03 s", "Battery: 70%")]
	public void CopyText_HasEveryField_AndOnlyTheMaskedAddress(Language l, string title, string firmware, string duration, string battery)
	{
		ControllerDetails d = ControllerDetails.Read(new InfoFeatureLink(Transport.Bluetooth, Features()));
		string text = d.CopyText(TimeSpan.FromSeconds(303), new BatteryStatus(70, ChargeState.Discharging), l);
		string[] lines = text.Split(Environment.NewLine);
		Assert.Equal(title, lines[0]);
		Assert.Contains(firmware, lines);
		Assert.Contains(duration, lines);
		Assert.Contains(battery, lines);
		Assert.Contains("XX:XX:XX:XX:E4:F5", text);
		Assert.DoesNotContain("A0:B1", text);
		Assert.DoesNotContain("A0B1", text);
		Assert.Contains("0x00000616", text);
		Assert.Contains("2023", text);
	}

	[Fact]
	public void CopyText_UnknownFields_SayUnknown()
	{
		ControllerDetails d = ControllerDetails.Read(new InfoFeatureLink(Transport.Usb, []));
		string text = d.CopyText(null, null, Language.English);
		Assert.Equal(6, text.Split(Environment.NewLine).Count(line => line.EndsWith(": Unknown", StringComparison.Ordinal)));
	}

	[Theory]
	[InlineData(45, Language.Turkish, "45 sn")]
	[InlineData(45, Language.English, "45 s")]
	[InlineData(303, Language.Turkish, "5 dk 03 sn")]
	[InlineData(303, Language.English, "5 min 03 s")]
	[InlineData(7500, Language.Turkish, "2 sa 05 dk")]
	[InlineData(7500, Language.English, "2 h 05 min")]
	[InlineData(97200, Language.Turkish, "1 g 3 sa")]
	[InlineData(97200, Language.English, "1 d 3 h")]
	[InlineData(-5, Language.English, "0 s")]
	public void ConnectedFor_FormatsPerLanguage(int seconds, Language l, string expected) =>
		Assert.Equal(expected, ConnectedFor.Format(TimeSpan.FromSeconds(seconds), l));

	// USB <-> Bluetooth switch.

	[Fact]
	public void Cable_StopsTheBridge_ForTheSameController_ThenResumesOnBluetooth()
	{
		CableSwitch cable = new();
		ulong[] known = [Address];
		Assert.Equal(CableDecision.None, cable.Next(true, [], known, true));
		Assert.Equal(CableDecision.StopForCable, cable.Next(true, [Address], known, false));
		Assert.True(cable.PausedForCable);
		// Bridge stopped, still on the cable: nothing.
		Assert.Equal(CableDecision.None, cable.Next(false, [Address], known, false));
		// Cable out, not back on Bluetooth yet: wait.
		Assert.Equal(CableDecision.None, cable.Next(false, [], known, false));
		Assert.Equal(CableDecision.Resume, cable.Next(false, [], known, true));
		Assert.False(cable.PausedForCable);
		Assert.Equal(CableDecision.None, cable.Next(true, [], known, true));
	}

	[Fact]
	public void Cable_IgnoresOtherControllers_TheVirtualPad_AndAStoppedBridge()
	{
		CableSwitch cable = new();
		Assert.Equal(CableDecision.None, cable.Next(true, [0x112233445566], [Address], false));
		Assert.Equal(CableDecision.None, cable.Next(true, [PairingInfo.VirtualAddress], [Address, PairingInfo.VirtualAddress], true));
		Assert.Equal(CableDecision.None, cable.Next(false, [Address], [Address], false));
		Assert.False(cable.PausedForCable);
	}

	[Fact]
	public void Cable_AStartByHand_EndsThePause()
	{
		CableSwitch cable = new();
		Assert.Equal(CableDecision.StopForCable, cable.Next(true, [Address], [Address], false));
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], false));
		Assert.False(cable.PausedForCable);
	}

	// Reconnect.

	[Theory]
	[InlineData(true, "stop,reopen,start")]
	[InlineData(false, "reopen")]
	public async Task Reconnect_StopsReopensAndRestarts_InOrder(bool running, string expected)
	{
		List<string> calls = [];
		await ReconnectSequence.RunAsync(running,
			() => { calls.Add("stop"); return Task.CompletedTask; },
			async () => { await Task.Yield(); calls.Add("reopen"); },
			() => { calls.Add("start"); return Task.CompletedTask; });
		Assert.Equal(expected, string.Join(',', calls));
	}

	// Health.

	private static readonly HidDevice BtPad = new(InfoFeatureLink.BtPath, 0x054C, 0x0CE6, 78, 547, 64);

	[Theory]
	[InlineData(BridgeError.ControllerLost, true)]
	[InlineData(BridgeError.ControllerOpenFailed, true)]
	[InlineData(BridgeError.None, false)]
	public void Health_StuckConnection_OffersReconnect(BridgeError error, bool stuck)
	{
		HealthRuntime run = new([BtPad], true, BridgeState.WaitingForController, error, null);
		HealthChip chip = HealthReport.BridgeChip(run, Language.English);
		Assert.Equal(stuck, HealthReport.IsStuck(run));
		Assert.Equal(stuck ? FixAction.ReconnectController : FixAction.None, chip.Fix);
		if (stuck)
		{
			Assert.Equal(HealthLevel.Warning, chip.Level);
			Assert.Equal(Text.HealthBridgeStuck(Language.English), chip.Reason);
		}
		Assert.True(HealthReport.IsHandledByService(FixAction.ReconnectController));
	}

	[Fact]
	public void Health_NoControllerListed_IsNotStuck() =>
		Assert.False(HealthReport.IsStuck(new HealthRuntime([], true, BridgeState.WaitingForController, BridgeError.ControllerLost, null)));

	// BridgeHost: read-only details probe while stopped.

	private sealed class Source(Func<IHidLink?> open) : IReadOnlyControllerSource
	{
		public IHidLink? TryOpenReadOnly() => open();
	}

	[Fact]
	public async Task Host_ReadsDetailsInTheBatteryProbesOpen_OncePerConnection()
	{
		List<InfoFeatureLink> links = [];
		InfoFeatureLink Open()
		{
			InfoFeatureLink l = new(Transport.Bluetooth, Features());
			links.Add(l);
			return l;
		}
		BridgeHost host = new(new NoControllers(), new NoIdentity(), new NoAttacher())
		{
			BatteryProbe = new BatteryProbe(new Source(Open)) { ReadTime = TimeSpan.FromMilliseconds(50) }
		};
		ControllerDetails? seen = null;
		host.DetailsRead += d => seen = d;
		await host.ProbeBatteryAsync();
		ControllerDetails? read = host.Details;
		Assert.NotNull(read);
		Assert.Same(read, seen);
		Assert.Equal(Address, read!.Address);
		Assert.Single(links);
		Assert.True(links[0].Disposed);
		Assert.Equal(0, links[0].Writes);
		Assert.Equal([(byte)0x20, (byte)0x09], links[0].Asked);

		// Same controller: not read again; after ForgetDetails (new connection, Reconnect) it is.
		await host.ProbeBatteryAsync();
		Assert.Empty(links[1].Asked);
		host.ForgetDetails();
		Assert.Null(host.Details);
		await host.ProbeBatteryAsync();
		Assert.Equal([(byte)0x20, (byte)0x09], links[2].Asked);
		Assert.All(links, l => Assert.Equal(0, l.Writes));
	}

	private sealed class NoControllers : IControllerSource
	{
		public IHidLink? TryOpen() => null;
	}

	private sealed class NoIdentity : IIdentitySource
	{
		public Virtual.DeviceIdentity? Load() => null;
	}

	private sealed class NoAttacher : IUsbIpAttacher
	{
		public Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel) => Task.FromResult(1);
		public Task DetachAsync(int port) => Task.CompletedTask;
	}
}
