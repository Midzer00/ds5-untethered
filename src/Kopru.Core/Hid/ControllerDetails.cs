// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.Core.Hid;

// What the controller says about itself (read once per connection): model, transport, firmware info (0x20) and its
// Bluetooth address (0x09). Firmware and Address are null when the controller didn't answer.
public sealed record ControllerDetails(string Path, bool IsEdge, Transport Transport, FirmwareInfo? Firmware, ulong? Address)
{
	public const string BluetoothName = "Bluetooth";
	public const string UsbName = "USB";

	public string ModelName => IsEdge ? "DualSense Edge" : "DualSense";

	public string TransportName => Transport == Transport.Bluetooth ? BluetoothName : UsbName;

	public string MaskedAddress => Address is { } a ? AddressText.Mask(a) : "—";

	// Reads 0x20 and 0x09 from an open link (GET_FEATURE only, never a write). Failures leave the field null.
	public static ControllerDetails Read(IHidLink link)
	{
		HidDevice device = link.Device;
		return new ControllerDetails(device.Path, device.IsEdge, device.Transport, Try(() => FirmwareInfo.Parse(link.GetFeature(FirmwareInfo.ReportId))),
			Try(() => PairingInfo.ParseAddress(link.GetFeature(PairingInfo.ReportId))));
	}

	// The text "Copy Details" puts on the clipboard (address masked).
	public string CopyText(TimeSpan? connectedFor, BatteryStatus? battery, Language? l = null)
	{
		string unknown = Text.InfoUnknown(l);
		List<string> lines =
		[
			Text.InfoTitle(l),
			$"{Text.InfoModel(l)}: {ModelName}",
			$"{Text.InfoConnection(l)}: {TransportName}",
			$"{Text.InfoFirmware(l)}: {(Firmware is { } f ? $"{f.FirmwareText} ({f.FirmwareHex})" : unknown)}",
			$"{Text.InfoHardware(l)}: {Firmware?.HardwareText ?? unknown}",
			$"{Text.InfoBuildDate(l)}: {BuildDateText(l)}",
			$"{Text.InfoAddress(l)}: {(Address is null ? unknown : MaskedAddress)}",
			$"{Text.InfoConnectedFor(l)}: {(connectedFor is { } d ? ConnectedFor.Format(d, l) : unknown)}",
			$"{Text.InfoBattery(l)}: {(battery is { } b ? Text.BatteryValue(b.Percent, l) : unknown)}",
		];
		return string.Join(Environment.NewLine, lines);
	}

	public string BuildDateText(Language? l = null) => Firmware switch
	{
		{ BuildTime: { } t } => t.ToString("d MMM yyyy HH:mm", Localizer.Culture(l ?? Localizer.Current)),
		{ BuildText.Length: > 0 } f => f.BuildText,
		_ => Text.InfoUnknown(l)
	};

	private static T? Try<T>(Func<T?> read) where T : class
	{
		try
		{
			return read();
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static ulong? Try(Func<ulong?> read)
	{
		try
		{
			return read();
		}
		catch (Exception)
		{
			return null;
		}
	}
}

// "Connected for" durations: "45 sn" / "5 dk 03 sn" / "2 sa 05 dk" / "1 g 3 sa" (TR), "45 s" / "5 min 03 s" / ... (EN).
public static class ConnectedFor
{
	public static string Format(TimeSpan duration, Language? l = null)
	{
		if (duration < TimeSpan.Zero)
		{
			duration = TimeSpan.Zero;
		}
		if (duration.TotalDays >= 1)
		{
			return Text.DurationDaysHours((int)duration.TotalDays, duration.Hours, l);
		}
		if (duration.TotalHours >= 1)
		{
			return Text.DurationHoursMinutes((int)duration.TotalHours, duration.Minutes, l);
		}
		if (duration.TotalMinutes >= 1)
		{
			return Text.DurationMinutesSeconds((int)duration.TotalMinutes, duration.Seconds, l);
		}
		return Text.DurationSeconds((int)duration.TotalSeconds, l);
	}
}
