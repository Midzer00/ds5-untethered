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

	// CableMode.ChargeOnly: cabled and bridged -> the USB twin is wanted hidden and the bridge keeps running; the cable
	// out (zero delay) drops it again.
	[Fact]
	public void Cable_CabledAndBridged_WantsTheTwinHidden()
	{
		CableSwitch cable = new();
		Assert.Equal(CableDecision.None, cable.Next(true, [], [], [Address], true));
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Equal([Address], cable.HiddenTwins);
		Assert.False(cable.PausedForCable);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true));
		Assert.Empty(cable.HiddenTwins);
	}

	// A controller that is paired (known) but not bridged shows on USB only: never hidden while another is bridged.
	[Fact]
	public void Cable_KnownButNotBridged_OnUsb_IsNotHidden()
	{
		CableSwitch cable = new();
		Assert.Equal(CableDecision.None, cable.Next(true, [0x665544332211], [Address], [Address, 0x665544332211], true));
		Assert.Empty(cable.HiddenTwins);
	}

	// Every bridged-and-cabled controller gets its twin hidden; losing the Bluetooth link drops it from the set.
	[Fact]
	public void Cable_TwoBridged_EachTwinIsWanted_OnlyWhileBridged()
	{
		const ulong other = 0x112233445566;
		CableSwitch cable = new();
		Assert.Equal(CableDecision.None, cable.Next(true, [Address, other], [Address, other], [Address, other], true));
		Assert.Equal([other, Address], cable.HiddenTwins.OrderBy(a => a));
		Assert.Equal(CableDecision.None, cable.Next(true, [Address, other], [other], [Address, other], true));
		Assert.Equal([other], cable.HiddenTwins);
	}

	// Our own virtual pads answer 0x09 with a virtual address (one per slot): never hidden, and with no Bluetooth
	// listed they never count as "only the cable is left" either.
	[Fact]
	public void Cable_VirtualAddresses_AreNeverCabled()
	{
		ulong slot2 = PairingInfo.ParseAddress([PairingInfo.ReportId, .. Kopru.Core.Virtual.DeviceIdentity.VirtualAddressFor(2)])!.Value;
		CableSwitch cable = new();
		Assert.Equal(CableDecision.None, cable.Next(true, [PairingInfo.VirtualAddress, slot2], [PairingInfo.VirtualAddress, slot2],
			[Address, PairingInfo.VirtualAddress, slot2], true));
		Assert.Empty(cable.HiddenTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [PairingInfo.VirtualAddress, slot2], [], [PairingInfo.VirtualAddress, slot2], false));
		Assert.False(cable.PausedForCable);
	}

	// Bluetooth lost while cabled: the stop is debounced. A one-tick blip does not stop; only when Bluetooth stayed
	// gone for the whole delay does the bridge stop, and the hiding restore undoes the twin.
	[Fact]
	public void Cable_BluetoothLostWhileCabled_StopsOnlyAfterTheDelay()
	{
		TimeSpan delay = TimeSpan.FromSeconds(5);
		CableSwitch cable = new(delay);
		DateTimeOffset t = new(2026, 11, 22, 0, 31, 53, TimeSpan.Zero);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true, t));
		Assert.Equal([Address], cable.HiddenTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], false, t.AddSeconds(2)));
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true, t.AddSeconds(4)));
		Assert.False(cable.PausedForCable);
		Assert.Equal([Address], cable.HiddenTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], false, t.AddSeconds(6)));
		Assert.Equal(CableDecision.StopForCable, cable.Next(true, [Address], [], [Address], false, t.AddSeconds(11)));
		Assert.True(cable.PausedForCable);
		Assert.Empty(cable.HiddenTwins);
	}

	// While stopped: a one-tick Bluetooth appearance that goes away restarts the wait; Bluetooth back for the whole
	// delay resumes it — the cable may still be in, the next tick wants the twin hidden again.
	[Fact]
	public void Cable_WhenStopped_ResumesOnceBluetoothStayedBack_TheCableMayStillBeIn()
	{
		TimeSpan delay = TimeSpan.FromSeconds(5);
		CableSwitch cable = new(delay);
		DateTimeOffset t = new(2026, 11, 22, 0, 31, 53, TimeSpan.Zero);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], false, t));
		Assert.Equal(CableDecision.StopForCable, cable.Next(true, [Address], [], [Address], false, t.AddSeconds(5)));
		Assert.True(cable.PausedForCable);
		Assert.Equal(CableDecision.None, cable.Next(false, [Address], [], [Address], true, t.AddSeconds(7)));
		Assert.Equal(CableDecision.None, cable.Next(false, [Address], [], [Address], false, t.AddSeconds(9)));
		Assert.Equal(CableDecision.None, cable.Next(false, [Address], [], [Address], true, t.AddSeconds(11)));
		Assert.Equal(CableDecision.Resume, cable.Next(false, [Address], [], [Address], true, t.AddSeconds(16)));
		Assert.False(cable.PausedForCable);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true, t.AddSeconds(17)));
		Assert.Equal([Address], cable.HiddenTwins);
	}

	// A bridge stop of any kind restores all hiding: the wanted set is cleared, a new start hides the twin again.
	[Fact]
	public void Cable_ABridgeStop_ClearsTheWantedTwins_AndAStartHidesAgain()
	{
		CableSwitch cable = new();
		cable.Next(true, [Address], [Address], [Address], true);
		Assert.Equal([Address], cable.HiddenTwins);
		Assert.Equal(CableDecision.None, cable.Next(false, [Address], [Address], [Address], true));
		Assert.Empty(cable.HiddenTwins);
		cable.Next(true, [Address], [Address], [Address], true);
		Assert.Equal([Address], cable.HiddenTwins);
	}

	// Seen on hardware: plugged in while still on Bluetooth, the USB pad dropped out of the listing for one tick. The
	// blip keeps the twin wanted; only a cable that stays out for the whole delay drops it.
	[Fact]
	public void Cable_ABriefUsbDropout_KeepsTheTwin_OnlyACableThatStaysOutDropsIt()
	{
		TimeSpan delay = TimeSpan.FromSeconds(5);
		CableSwitch cable = new(delay);
		DateTimeOffset t = new(2026, 11, 22, 0, 31, 53, TimeSpan.Zero);
		cable.Next(true, [Address], [Address], [Address], true, t);
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true, t.AddSeconds(2)));
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true, t.AddSeconds(4)));
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true, t.AddSeconds(6)));
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true, t.AddSeconds(10)));
		Assert.Equal([Address], cable.HiddenTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true, t.AddSeconds(11)));
		Assert.Empty(cable.HiddenTwins);
	}

	[Fact]
	public void Pairing_IsVirtual_TrueForEveryVirtualSlot()
	{
		Assert.True(PairingInfo.IsVirtual(PairingInfo.VirtualAddress));
		foreach (int slot in new[] { 2, 3, 4 })
		{
			Assert.True(PairingInfo.IsVirtual(
				PairingInfo.ParseAddress([PairingInfo.ReportId, .. Kopru.Core.Virtual.DeviceIdentity.VirtualAddressFor(slot)])!.Value));
		}
		Assert.False(PairingInfo.IsVirtual(Address));
	}

	[Fact]
	public void Cable_AStartByHand_EndsThePause()
	{
		CableSwitch cable = new();
		Assert.Equal(CableDecision.StopForCable, cable.Next(true, [Address], [], [Address], false));
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], false));
		Assert.False(cable.PausedForCable);
	}

	// CableMode.Wired: a hidden twin that is cabled AND bridged this tick is also wanted wired — with a one-tick
	// listing gap, so a pad missing for one tick keeps its entry and only the second missing tick drops it while
	// the hidden want keeps the twin.
	[Fact]
	public void Cable_WiredMode_WantsTheTwinWired_OnlyWhileCabledAndBridgedThisTick()
	{
		TimeSpan delay = TimeSpan.FromSeconds(5);
		CableSwitch cable = new(delay) { Mode = CableMode.Wired };
		DateTimeOffset t = new(2026, 11, 22, 0, 31, 53, TimeSpan.Zero);
		Assert.Empty(cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true, t));
		Assert.Equal([Address], cable.HiddenTwins);
		Assert.Equal([Address], cable.WiredTwins);

		// A one-tick USB dropout keeps the wired want; the second missing tick drops it (HiddenTwins keeps the twin).
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true, t.AddSeconds(1)));
		Assert.Equal([Address], cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true, t.AddSeconds(2)));
		Assert.Empty(cable.WiredTwins);
		Assert.Equal([Address], cable.HiddenTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true, t.AddSeconds(3)));
		Assert.Equal([Address], cable.WiredTwins);

		// Back to charge-only: the wired want is empty again, the hiding want is untouched.
		cable.Mode = CableMode.ChargeOnly;
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true, t.AddSeconds(4)));
		Assert.Empty(cable.WiredTwins);
		Assert.Equal([Address], cable.HiddenTwins);
	}

	// WiredTwins is also empty when the address is not bridged (the same one-tick gap applies), on StopForCable and
	// while paused.
	[Fact]
	public void Cable_WiredMode_NotBridged_OrStoppedForCable_IsNotWantedWired()
	{
		// A delay switch: the gap applies while the twin is still hidden (kept for the delay).
		CableSwitch cable = new(TimeSpan.FromSeconds(5)) { Mode = CableMode.Wired };
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Equal([Address], cable.WiredTwins);

		// Not bridged (paired only): one missing tick keeps the want, the second drops it (the hidden want keeps
		// the twin until the delay).
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], true));
		Assert.Equal([Address], cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], true));
		Assert.Empty(cable.WiredTwins);

		// Bluetooth gone with the cable in: the stop (after its delay) is empty too, and stays so while paused.
		DateTimeOffset t = new(2026, 11, 22, 0, 31, 53, TimeSpan.Zero);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], false, t));
		Assert.Equal(CableDecision.StopForCable, cable.Next(true, [Address], [], [Address], false, t.AddSeconds(6)));
		Assert.Empty(cable.WiredTwins);
		Assert.True(cable.PausedForCable);
		Assert.Equal(CableDecision.None, cable.Next(false, [Address], [], [Address], false, t.AddSeconds(7)));
		Assert.Empty(cable.WiredTwins);
		Assert.True(cable.PausedForCable);
	}

	// A listing that drops out for one tick (seen on hardware while the cable settles) undoes nothing: the gap is
	// kept for one tick only while the twin is still hidden, and being listed again resets it — a mode change or a
	// cable stop still empties at once.
	[Fact]
	public void Cable_WiredMode_AOneTickListingGap_KeepsTheWiredWant_AndBeingListedResetsIt()
	{
		TimeSpan delay = TimeSpan.FromSeconds(5);
		CableSwitch cable = new(delay) { Mode = CableMode.Wired };
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Equal([Address], cable.WiredTwins);

		// One gap tick keeps the want; the second in a row drops it.
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true));
		Assert.Equal([Address], cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true));
		Assert.Empty(cable.WiredTwins);

		// Wired again: a gap, listed again (the count resets), then another gap is a fresh first one.
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true));
		Assert.Equal([Address], cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true));
		Assert.Equal([Address], cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true));
		Assert.Empty(cable.WiredTwins);

		// The bridge listing dropping out follows the same gap.
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], true));
		Assert.Equal([Address], cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], true));
		Assert.Empty(cable.WiredTwins);

		// A mode change empties the set at once, without a second tick.
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true));
		Assert.Equal([Address], cable.WiredTwins);
		cable.Mode = CableMode.ChargeOnly;
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true));
		Assert.Empty(cable.WiredTwins);

		// So does a cable stop (the stop itself still waits its delay).
		cable.Mode = CableMode.Wired;
		DateTimeOffset t = new(2026, 11, 22, 0, 31, 53, TimeSpan.Zero);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [Address], [Address], true, t));
		Assert.Equal([Address], cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [], [Address], [Address], true, t.AddSeconds(1)));
		Assert.Equal([Address], cable.WiredTwins);
		Assert.Equal(CableDecision.None, cable.Next(true, [Address], [], [Address], false, t.AddSeconds(6)));
		Assert.Equal(CableDecision.StopForCable, cable.Next(true, [Address], [], [Address], false, t.AddSeconds(11)));
		Assert.Empty(cable.WiredTwins);
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
