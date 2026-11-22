// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Hiding;
using Kopru.Core.Setup;

namespace Kopru.Core.Tests;

// usbip.exe and HidHideCLI.exe are found where the setup check finds the programs (the uninstall entry), not only in
// the default folders.
public class DriverLocateTests
{
	private sealed class FakeRegistry : IInstallRegistry
	{
		public Dictionary<string, (string? Version, string? Location)> Programs { get; } = new();
		public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

		public (string? Version, string? Location)? FindProgram(string prefix) =>
			Programs.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(p => ((string?, string?)?)p.Value).FirstOrDefault();

		public bool ServiceExists(string serviceName) => true;
		public bool FileExists(string path) => Files.Contains(path);
	}

	[Fact]
	public void UsbIpInACustomFolderIsFoundLikeTheSetupCheck()
	{
		var reg = new FakeRegistry();
		reg.Programs["USBip 0.9.8.1"] = ("0.9.8.1", @"D:\Tools\USBip\");
		reg.Files.Add(@"D:\Tools\USBip\usbip.exe");
		reg.Files.Add(UsbIpExeAttacher.DefaultPath);

		Assert.Equal(@"D:\Tools\USBip\usbip.exe", UsbIpExeAttacher.Locate(reg, @"C:\Other"));
		// The setup check sees the same file.
		Assert.Equal(@"D:\Tools\USBip\usbip.exe", new SystemCheck(reg, null!, null!, null!).CheckUsbIp().Location);
	}

	[Fact]
	public void UsbIpFallsBackToTheDefaultFolderThenPath()
	{
		var reg = new FakeRegistry();
		reg.Programs["USBip 0.9.8.1"] = ("0.9.8.1", @"D:\Gone\");
		reg.Files.Add(UsbIpExeAttacher.DefaultPath);
		Assert.Equal(UsbIpExeAttacher.DefaultPath, UsbIpExeAttacher.Locate(reg, null));

		var onPath = new FakeRegistry();
		onPath.Files.Add(@"E:\bin\usbip.exe");
		Assert.Equal(@"E:\bin\usbip.exe", UsbIpExeAttacher.Locate(onPath, @"C:\Windows;E:\bin"));

		Assert.Null(UsbIpExeAttacher.Locate(new FakeRegistry(), @"C:\Windows"));
	}

	[Fact]
	public void UsbIpIgnoresMalformedEntries()
	{
		var reg = new FakeRegistry();
		reg.Programs["USBip"] = (null, "bad\0folder");
		reg.Files.Add(@"E:\bin\usbip.exe");
		Assert.Equal(@"E:\bin\usbip.exe", UsbIpExeAttacher.Locate(reg, "bad\0dir;E:\\bin"));
	}

	[Theory]
	[InlineData(true, @"D:\HidHide\x64\HidHideCLI.exe")]
	[InlineData(false, @"D:\HidHide\x86\HidHideCLI.exe")]
	public void HidHideCliInACustomFolderIsFound(bool is64Bit, string expected)
	{
		var reg = new FakeRegistry();
		reg.Programs["HidHide"] = ("1.5.230", @"D:\HidHide\");
		reg.Files.Add(@"D:\HidHide\x64\HidHideCLI.exe");
		reg.Files.Add(@"D:\HidHide\x86\HidHideCLI.exe");
		reg.Files.Add(HidHideControl.DefaultCliPath);
		Assert.Equal(expected, HidHideControl.LocateCli(reg, is64Bit));
	}

	[Fact]
	public void HidHideCliFallsBackToTheDefaultFolder()
	{
		var reg = new FakeRegistry();
		reg.Programs["HidHide"] = ("1.5.230", @"D:\Gone\");
		reg.Files.Add(HidHideControl.DefaultCliPath);
		Assert.Equal(HidHideControl.DefaultCliPath, HidHideControl.LocateCli(reg, true));
		Assert.Null(HidHideControl.LocateCli(new FakeRegistry(), true));
	}
}
