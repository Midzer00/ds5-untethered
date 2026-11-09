// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hid;

namespace Kopru.Core.Virtual;

// Feature report 0x05 (IMU calibration) is unit specific, so the built-in identity carries none: the virtual pad answers
// with the live controller's own calibration, read over Bluetooth.
//
// Layout relied on (41 bytes with the report id on both USB and Bluetooth; as read by Linux hid-playstation):
//   [0]      report id 0x05
//   [1..35)  17 little-endian int16: gyro pitch/yaw/roll bias; gyro pitch+/pitch-/yaw+/yaw-/roll+/roll-;
//            gyro speed+/speed-; accel x+/x-/y+/y-/z+/z-
//   [35..37) two trailing bytes, kept as read
//   [37..41) on Bluetooth the CRC-32 of the report; unused on USB, so zeroed here
public static class CalibrationOverlay
{
	public const byte Feature = 0x05;

	public const int UsbLength = 41;

	private const int CalibrationEnd = 37;

	// Generic values (typical of a DualSense, zero bias) for when the controller's own can't be read.
	private static readonly short[] Neutral = [0, 0, 0, 8850, -8850, 8850, -8850, 8850, -8850, 540, 540, 8192, -8192, 8192, -8192, 8192, -8192];

	// The USB form of a Bluetooth feature 0x05 payload; null when it is not one.
	public static byte[]? FromBluetooth(byte[]? bluetooth)
	{
		if (bluetooth == null || bluetooth.Length < CalibrationEnd || bluetooth[0] != Feature)
		{
			return null;
		}
		byte[] usb = new byte[UsbLength];
		bluetooth.AsSpan(0, CalibrationEnd).CopyTo(usb);
		return usb;
	}

	public static byte[] NeutralReport()
	{
		byte[] usb = new byte[UsbLength];
		usb[0] = Feature;
		for (int i = 0; i < Neutral.Length; i++)
		{
			BitConverter.TryWriteBytes(usb.AsSpan(1 + (2 * i), 2), Neutral[i]);
		}
		return usb;
	}

	// The identity with 0x05 replaced by the given USB report.
	public static DeviceIdentity With(DeviceIdentity identity, byte[] calibration)
	{
		Dictionary<byte, byte[]> features = identity.FeatureReports.ToDictionary(p => p.Key, p => p.Value);
		features[Feature] = calibration;
		return new DeviceIdentity
		{
			DeviceDescriptor = identity.DeviceDescriptor,
			ConfigurationDescriptor = identity.ConfigurationDescriptor,
			HidReportDescriptor = identity.HidReportDescriptor,
			Strings = identity.Strings,
			FeatureReports = features
		};
	}

	// Overlays the live controller's calibration; without one keeps the identity's own 0x05, else a neutral one.
	public static DeviceIdentity Apply(DeviceIdentity identity, IHidLink link)
	{
		byte[]? live;
		try
		{
			live = FromBluetooth(link.GetFeature(Feature));
		}
		catch (Exception)
		{
			live = null;
		}
		if (live != null)
		{
			return With(identity, live);
		}
		return identity.FeatureReports.ContainsKey(Feature) ? identity : With(identity, NeutralReport());
	}
}
