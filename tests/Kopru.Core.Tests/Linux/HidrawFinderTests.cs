// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hid;
using Kopru.Platform.Linux.Hidraw;

namespace Kopru.Core.Tests.Linux;

// HidrawFinder over a fake /sys/class/hidraw tree in a temp folder (plain folders; the real one has symlinks).
public sealed class HidrawFinderTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), "kopru-hidraw-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(root))
		{
			Directory.Delete(root, true);
		}
	}

	private string ClassDirectory => Path.Combine(root, "class");

	private void AddNode(string name, string hidId, byte[]? descriptor, string? uniq = "02:00:00:00:00:01")
	{
		string device = Path.Combine(ClassDirectory, name, "device");
		Directory.CreateDirectory(device);
		File.WriteAllText(Path.Combine(device, "uevent"),
			$"DRIVER=playstation\nHID_ID={hidId}\nHID_NAME=Test pad\n" + (uniq != null ? $"HID_UNIQ={uniq}\n" : ""));
		if (descriptor != null)
		{
			File.WriteAllBytes(Path.Combine(device, "report_descriptor"), descriptor);
		}
	}

	private HidrawFinder Finder() => new(ClassDirectory, "/dev");

	[Fact]
	public void MissingClassFolder_FindsNothing()
	{
		Assert.Empty(new HidrawFinder(Path.Combine(root, "nope")).FindNodes());
	}

	[Fact]
	public void FindsDualSenses_InNodeNumberOrder_WithTheirBus()
	{
		AddNode("hidraw10", "0003:0000054C:00000CE6", HidrawParsingTests.GamepadDescriptor(), uniq: null);
		AddNode("hidraw2", "0005:0000054C:00000DF2", HidrawParsingTests.GamepadDescriptor());
		AddNode("hidraw0", "0003:0000046D:0000C52B", HidrawParsingTests.GamepadDescriptor()); // another vendor
		AddNode("hidraw1", "0005:0000054C:000009CC", HidrawParsingTests.GamepadDescriptor()); // DualShock 4
		Directory.CreateDirectory(Path.Combine(ClassDirectory, "hidraw7")); // no device folder

		IReadOnlyList<HidrawNode> nodes = Finder().FindNodes();

		Assert.Equal(["hidraw2", "hidraw10"], nodes.Select(n => n.Name));
		Assert.True(nodes[0].IsBluetooth);
		Assert.Equal(Transport.Usb, nodes[1].Transport);
		Assert.Equal(Path.Combine("/dev", "hidraw2"), nodes[0].DevicePath);
	}

	[Fact]
	public void Devices_LookLikeTheWindowsOnes()
	{
		AddNode("hidraw4", "0005:0000054C:00000CE6", HidrawParsingTests.GamepadDescriptor());

		HidDevice device = Assert.Single(Finder().Find());

		Assert.Equal(Transport.Bluetooth, device.Transport);
		Assert.False(device.IsEdge);
		Assert.Equal(HidDevice.SonyVendorId, device.VendorId);
		Assert.Equal(78, device.InputLength);
		Assert.Equal(142, device.OutputLength);
		Assert.Equal(41, device.FeatureLength);
	}

	[Fact]
	public void WithoutADescriptor_TheBluetoothSizesAreAssumed()
	{
		AddNode("hidraw5", "0005:0000054C:00000CE6", descriptor: null);

		HidrawNode node = Assert.Single(Finder().FindNodes());
		HidDevice device = node.ToHidDevice();

		Assert.Null(node.Descriptor);
		Assert.Equal(78, device.InputLength);
		Assert.Equal(78, device.OutputLength);
		Assert.Equal(HidrawNode.DefaultFeatureLength, device.FeatureLength);
	}

	[Fact]
	public void ANonGamepadCollection_IsSkipped()
	{
		AddNode("hidraw3", "0005:0000054C:00000CE6", [0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x75, 0x08, 0x95, 0x03, 0x81, 0x06, 0xC0]);
		Assert.Empty(Finder().FindNodes());
	}

	[Fact]
	public void StableId_IsTheUniqueId_ElseTheNodeName()
	{
		AddNode("hidraw2", "0005:0000054C:00000CE6", HidrawParsingTests.GamepadDescriptor(), uniq: "02:00:00:00:00:0A");
		AddNode("hidraw3", "0003:0000054C:00000CE6", HidrawParsingTests.GamepadDescriptor(), uniq: null);

		IReadOnlyList<HidrawNode> nodes = Finder().FindNodes();

		Assert.Equal("hid-uniq:02:00:00:00:00:0a", nodes[0].StableId);
		Assert.Equal("hidraw:hidraw3", nodes[1].StableId);
	}

	[Theory]
	[InlineData("hidraw0", 0)]
	[InlineData("hidraw12", 12)]
	[InlineData("hidraw", int.MaxValue)]
	[InlineData("hidrawx", int.MaxValue)]
	[InlineData("other", int.MaxValue)]
	public void NodeNumber_IsTheTrailingNumber(string name, int number) => Assert.Equal(number, HidrawFinder.NodeNumber(name));
}
