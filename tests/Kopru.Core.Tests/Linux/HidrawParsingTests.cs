// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hid;
using Kopru.Platform.Linux.Hidraw;

namespace Kopru.Core.Tests.Linux;

// The Linux platform's parsing is pure: these run on every OS.
public class HidrawParsingTests
{
	// A made-up, locally administered address (never a real controller's).
	private const string FakeAddress = "02:00:00:00:00:01";

	[Theory]
	[InlineData("0005:0000054C:00000CE6", HidBus.Bluetooth, 0x0CE6)]
	[InlineData("0003:0000054C:00000CE6", HidBus.Usb, 0x0CE6)]
	[InlineData("0005:0000054C:00000DF2", HidBus.Bluetooth, 0x0DF2)]
	[InlineData(" 0003:0000054c:00000df2\n", HidBus.Usb, 0x0DF2)]
	public void HidId_ParsesBusVendorAndProduct(string text, int bus, int product)
	{
		HidId id = HidId.Parse(text)!.Value;
		Assert.Equal(bus, id.Bus);
		Assert.Equal(0x054C, id.VendorId);
		Assert.Equal(product, id.ProductId);
		Assert.True(id.IsDualSense);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("0005:0000054C")]
	[InlineData("0005:0000054C:00000CE6:1")]
	[InlineData("zz05:0000054C:00000CE6")]
	[InlineData("0005:-000054C:00000CE6")]
	[InlineData("0005:1000054C0:00000CE6")]
	public void HidId_RejectsMalformedText(string? text) => Assert.Null(HidId.Parse(text));

	[Fact]
	public void HidId_TellsTheBus()
	{
		Assert.Equal(Transport.Bluetooth, HidId.Parse("0005:0000054C:00000CE6")!.Value.Transport);
		Assert.Equal(Transport.Usb, HidId.Parse("0003:0000054C:00000CE6")!.Value.Transport);
		// I2C (0x18) and others are neither.
		Assert.Null(HidId.Parse("0018:0000054C:00000CE6")!.Value.Transport);
	}

	[Theory]
	[InlineData("0005:0000054C:000009CC")] // DualShock 4 v2
	[InlineData("0003:0000045E:00000B12")] // another vendor
	public void HidId_OnlyDualSenseAndEdgeCount(string text) => Assert.False(HidId.Parse(text)!.Value.IsDualSense);

	[Fact]
	public void Uevent_ReadsTheKeys()
	{
		HidrawUevent uevent = HidrawUevent.Parse(
			"DRIVER=playstation\nHID_ID=0005:0000054C:00000CE6\nHID_NAME=DualSense Wireless Controller\n" +
			$"HID_PHYS={FakeAddress}\nHID_UNIQ={FakeAddress}\nMODALIAS=hid:b0005g0001v0000054Cp00000CE6\n\n");

		Assert.Equal("playstation", uevent.Driver);
		Assert.Equal(new HidId(HidBus.Bluetooth, 0x054C, 0x0CE6), uevent.Id);
		Assert.Equal("DualSense Wireless Controller", uevent.Name);
		Assert.Equal(FakeAddress, uevent.Uniq);
		Assert.Equal("hid:b0005g0001v0000054Cp00000CE6", uevent.Get("MODALIAS"));
	}

	[Fact]
	public void Uevent_IgnoresJunk_AndEmptyValuesCountAsMissing()
	{
		HidrawUevent uevent = HidrawUevent.Parse("no equals sign\r\n=no key\r\nHID_UNIQ=\r\nDRIVER=hid-generic\r\nA=b=c\r\n");
		Assert.Null(uevent.Uniq);
		Assert.Equal("hid-generic", uevent.Driver);
		Assert.Equal("b=c", uevent.Get("A"));
		Assert.Null(uevent.Id);
	}

	// A small gamepad descriptor in the item format of the HID 1.11 specification: an application collection (Generic
	// Desktop / Game Pad) with input 0x31 (77 bytes), outputs 0x31 (77) and 0x32 (141, a two-byte report count), and
	// feature 0x05 (40).
	internal static byte[] GamepadDescriptor() =>
	[
		0x05, 0x01, // Usage Page (Generic Desktop)
		0x09, 0x05, // Usage (Game Pad)
		0xA1, 0x01, // Collection (Application)
		0x85, 0x31, //   Report ID 0x31
		0x75, 0x08, //   Report Size 8
		0x95, 0x4D, //   Report Count 77
		0x81, 0x02, //   Input
		0x95, 0x4D, //   Report Count 77
		0x91, 0x02, //   Output
		0x85, 0x32, //   Report ID 0x32
		0x96, 0x8D, 0x00, // Report Count 141 (two data bytes)
		0x91, 0x02, //   Output
		0x85, 0x05, //   Report ID 0x05
		0x95, 0x28, //   Report Count 40
		0xB1, 0x02, //   Feature
		0xC0        // End Collection
	];

	[Fact]
	public void Descriptor_GivesEachReportsLength_WithTheIdByte()
	{
		HidReportDescriptor d = HidReportDescriptor.Parse(GamepadDescriptor());

		Assert.True(d.IsGamepad);
		Assert.True(d.UsesReportIds);
		Assert.Equal(78, d.ReportLength(HidReportKind.Input, 0x31));
		Assert.Equal(78, d.ReportLength(HidReportKind.Output, 0x31));
		Assert.Equal(142, d.ReportLength(HidReportKind.Output, 0x32));
		Assert.Equal(41, d.ReportLength(HidReportKind.Feature, 0x05));
		Assert.Null(d.ReportLength(HidReportKind.Output, 0x36));
		Assert.Equal(142, d.MaxLength(HidReportKind.Output));
		Assert.Equal(78, d.MaxLength(HidReportKind.Input));
		Assert.Equal([0x31, 0x32], d.ReportIds(HidReportKind.Output));
	}

	[Fact]
	public void Descriptor_AddsUpSeveralMainItemsOfOneReport_AndBitFields()
	{
		byte[] raw =
		[
			0x05, 0x01, 0x09, 0x05, 0xA1, 0x01,
			0x85, 0x01,
			0x75, 0x01, 0x95, 0x0E, 0x81, 0x02, // 14 one-bit buttons
			0x75, 0x02, 0x95, 0x01, 0x81, 0x03, // 2 bits of padding
			0x75, 0x08, 0x95, 0x04, 0x81, 0x02, // 4 axes
			0xC0
		];
		HidReportDescriptor d = HidReportDescriptor.Parse(raw);
		// 14 + 2 + 32 bits = 6 bytes, plus the ID.
		Assert.Equal(7, d.ReportLength(HidReportKind.Input, 0x01));
	}

	[Fact]
	public void Descriptor_WithoutReportIds_HasNoIdByte()
	{
		byte[] raw = [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x75, 0x08, 0x95, 0x08, 0x81, 0x02, 0xC0];
		HidReportDescriptor d = HidReportDescriptor.Parse(raw);
		Assert.False(d.UsesReportIds);
		Assert.Equal(8, d.ReportLength(HidReportKind.Input, 0));
	}

	[Fact]
	public void Descriptor_PushAndPop_RestoreTheGlobals()
	{
		byte[] raw =
		[
			0x05, 0x01, 0x09, 0x05, 0xA1, 0x01,
			0x85, 0x02, 0x75, 0x08, 0x95, 0x02,
			0xA4,                   // Push
			0x95, 0x10, 0x81, 0x02, // 16 bytes in
			0xB4,                   // Pop: back to 2
			0x91, 0x02,             // 2 bytes out
			0xC0
		];
		HidReportDescriptor d = HidReportDescriptor.Parse(raw);
		Assert.Equal(17, d.ReportLength(HidReportKind.Input, 2));
		Assert.Equal(3, d.ReportLength(HidReportKind.Output, 2));
	}

	[Fact]
	public void Descriptor_SkipsLongItems_AndTakesAFourByteUsageWithItsPage()
	{
		byte[] raw =
		[
			0xFE, 0x02, 0x10, 0xAA, 0xBB,       // a long item (2 data bytes)
			0x0B, 0x05, 0x00, 0x01, 0x00,       // Usage (page 0x0001, usage 0x0005) as one 4-byte item
			0xA1, 0x01, 0x85, 0x01, 0x75, 0x08, 0x95, 0x01, 0x81, 0x02, 0xC0
		];
		HidReportDescriptor d = HidReportDescriptor.Parse(raw);
		Assert.True(d.IsGamepad);
		Assert.Equal(2, d.ReportLength(HidReportKind.Input, 1));
	}

	[Fact]
	public void Descriptor_ThatIsCutShort_GivesWhatItHad()
	{
		byte[] raw = [.. GamepadDescriptor()[..14], 0x96, 0x8D];
		HidReportDescriptor d = HidReportDescriptor.Parse(raw);
		Assert.Equal(78, d.ReportLength(HidReportKind.Input, 0x31));
		Assert.Null(d.ReportLength(HidReportKind.Output, 0x32));
		Assert.Equal(0, HidReportDescriptor.Parse([]).MaxLength(HidReportKind.Input));
	}

	[Fact]
	public void Descriptor_OfAMouse_IsNoGamepad()
	{
		byte[] raw = [0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x75, 0x08, 0x95, 0x03, 0x81, 0x06, 0xC0];
		HidReportDescriptor d = HidReportDescriptor.Parse(raw);
		Assert.Equal(1, d.UsagePage);
		Assert.Equal(2, d.Usage);
		Assert.False(d.IsGamepad);
	}

	[Fact]
	public void Ioctl_Numbers_FollowTheGenericLayout()
	{
		// _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x07, 64) and 0x06.
		Assert.Equal(0xC0404807u, HidrawIoctl.GetFeature(64));
		Assert.Equal(0xC0404806u, HidrawIoctl.SetFeature(64));
		Assert.Equal(0xC0294807u, HidrawIoctl.GetFeature(41));
		Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.GetFeature(1 << 14));
		Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.GetFeature(-1));
	}
}
