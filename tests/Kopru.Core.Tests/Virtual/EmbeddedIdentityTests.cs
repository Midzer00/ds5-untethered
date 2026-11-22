// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

internal sealed class FeatureLink(Func<byte[]> feature) : IHidLink
{
	public HidDevice Device { get; } = new("\\\\?\\hid#{00001124-0000-1000-8000-00805f9b34fb}_x", 0x054C, 0x0CE6, 78, 547, 64);

	public int Read(Span<byte> buffer) => throw new IOException();

	public void Write(ReadOnlySpan<byte> report)
	{
	}

	public byte[] GetFeature(byte reportId) => feature();

	public void CancelRead()
	{
	}

	public void Dispose()
	{
	}
}

public class EmbeddedIdentityTests
{
	private static DeviceIdentity Embedded() => new EmbeddedIdentitySource().Load()!;

	// The user's own capture (git-ignored), only to check the built-in identity leaks none of it; null when absent.
	private static DeviceIdentity? LocalCapture()
	{
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			string? file = CaptureIdentitySource.NewestCapture(Path.Combine(dir.FullName, "captures"));
			if (file != null)
			{
				return DeviceIdentity.FromCaptureFile(file);
			}
		}
		return null;
	}

	private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

	[Fact]
	public void Loads_DualSenseWithoutSerialOrCalibration()
	{
		DeviceIdentity identity = Embedded();
		Assert.Equal(new byte[] { 0x4C, 0x05, 0xE6, 0x0C }, identity.DeviceDescriptor[8..12]);
		Assert.Equal(0, identity.DeviceDescriptor[16]);
		Assert.Equal(new[] { 1, 2 }, identity.Strings.Keys.Order());
		Assert.False(identity.FeatureReports.ContainsKey(CalibrationOverlay.Feature));
		Assert.False(identity.FeatureReports.ContainsKey(0x22));
		Assert.Contains((byte)0x20, identity.FeatureReports.Keys);
		Assert.Equal(DeviceIdentity.VirtualAddress, identity.FeatureReports[DeviceIdentity.PairingFeature][1..7]);
		Assert.NotEmpty(identity.HidReportDescriptor);
	}

	[Fact]
	public void HoldsNoAddressOfTheCapturedController()
	{
		DeviceIdentity? capture = LocalCapture();
		if (capture == null || !capture.FeatureReports.TryGetValue(0x0B, out byte[]? pairing))
		{
			return; // No local capture: nothing to compare with.
		}
		// 0x0B keeps the original addresses (0x09 is rewritten on load): the controller's and the paired host's.
		byte[][] addresses = [pairing[1..7], pairing[11..17]];
		foreach (byte[] address in addresses.Where(a => a.Any(b => b != 0)))
		{
			byte[] reversed = address.Reverse().ToArray();
			foreach (byte[] report in Embedded().FeatureReports.Values)
			{
				Assert.False(Contains(report, address));
				Assert.False(Contains(report, reversed));
			}
		}
	}

	[Fact]
	public void CalibrationOverlay_UsesTheLiveReportInUsbForm()
	{
		byte[] bluetooth = new byte[78];
		bluetooth[0] = 0x05;
		for (int i = 1; i < bluetooth.Length; i++)
		{
			bluetooth[i] = (byte)i;
		}
		DeviceIdentity identity = CalibrationOverlay.Apply(Embedded(), new FeatureLink(() => bluetooth));
		byte[] usb = identity.FeatureReports[CalibrationOverlay.Feature];
		Assert.Equal(CalibrationOverlay.UsbLength, usb.Length);
		Assert.Equal(bluetooth[..37], usb[..37]);
		Assert.All(usb[37..], b => Assert.Equal(0, b));
	}

	[Fact]
	public void CalibrationOverlay_FallsBackToNeutral()
	{
		DeviceIdentity identity = CalibrationOverlay.Apply(Embedded(), new FeatureLink(() => throw new IOException()));
		byte[] usb = identity.FeatureReports[CalibrationOverlay.Feature];
		Assert.Equal(CalibrationOverlay.UsbLength, usb.Length);
		Assert.Equal(540, BitConverter.ToInt16(usb, 19));
		Assert.Null(CalibrationOverlay.FromBluetooth([0x31, 0, 0]));
	}

	[Fact]
	public void DefaultSource_PrefersAUserCapture()
	{
		DeviceIdentity mine = DeviceIdentity.Create([18, 1], [9, 2], [0], new Dictionary<int, string>(), new Dictionary<byte, byte[]>());
		Assert.Same(mine, new FirstIdentitySource(new FakeIdentity(mine), new EmbeddedIdentitySource()).Load());
		Assert.NotNull(new FirstIdentitySource(new FakeIdentity(null), new EmbeddedIdentitySource()).Load());
		FirstIdentitySource defaults = Assert.IsType<FirstIdentitySource>(IdentitySources.Default());
		Assert.IsType<EmbeddedIdentitySource>(defaults.Sources[^1]);
	}

	[Fact]
	public void VirtualDualSense_EnumeratesWithTheEmbeddedIdentity()
	{
		using VirtualDualSense pad = new(CalibrationOverlay.Apply(Embedded(), new FeatureLink(() => new byte[64])));
		Assert.Equal((0x054C, 0x0CE6), (pad.Info.VendorId, pad.Info.ProductId));
		SubmitResult Run(byte[] setup)
		{
			SubmitResult? result = null;
			pad.Submit(new SubmitCommand(1, 0, 1, 0, 0, 255, 0, -1, 1, setup, [], []), r => result = r);
			return result!;
		}
		Assert.Equal(18, Run([0x80, 6, 0, 1, 0, 0, 18, 0]).Data.Length);
		byte[] config = Run([0x80, 6, 0, 2, 0, 0, 255, 0]).Data;
		Assert.Equal(config[2] | (config[3] << 8), config.Length);
		Assert.Equal(DeviceIdentity.VirtualAddress, Run([0xA1, 1, 0x09, 3, 3, 0, 64, 0]).Data[1..7]);
		Assert.Equal(CalibrationOverlay.UsbLength, Run([0xA1, 1, 0x05, 3, 3, 0, 64, 0]).Data.Length);
		Assert.Equal(UsbIpCodes.BrokenPipe, Run([0x80, 6, 3, 3, 0x09, 0x04, 255, 0]).Status);
		Assert.NotEmpty(Run([0x81, 6, 0, 0x22, 3, 0, 255, 2]).Data);
	}
}
