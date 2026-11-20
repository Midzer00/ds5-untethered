// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Bluetooth;
using Kopru.Core.Hid;
using Kopru.Core.Hiding;
using Kopru.Platform.Linux.Hidraw;
using Kopru.Platform.Linux.UsbIp;

namespace Kopru.Platform.Linux.Bluetooth;

// Turns the controller off on Linux by asking BlueZ to drop its link ("bluetoothctl disconnect <address>"; the DualSense
// powers down when its host disconnects it). The logged-in user may do that without root under BlueZ's default D-Bus
// policy. The address is the hidraw node's HID_UNIQ (the kernel's copy of the controller's Bluetooth address). A call that
// takes longer than Timeout is ended (the runner kills it).
public sealed class BluezControllerPower(ICommandRunner runner, ILinuxHost host, string classDirectory = HidrawFinder.SysClassHidraw) : IControllerPower
{
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	public ulong? FindAddress(HidDevice device)
	{
		string name = Path.GetFileName(device.Path);
		try
		{
			string text = File.ReadAllText(Path.Combine(classDirectory, name, "device", "uevent"));
			return ParseAddress(HidrawUevent.Parse(text).Uniq);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
	}

	// "a0:ab:51:12:34:56" -> the 48-bit address; null for anything else.
	public static ulong? ParseAddress(string? text)
	{
		if (text == null)
		{
			return null;
		}
		string hex = text.Replace(":", "", StringComparison.Ordinal);
		return hex.Length == 12 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong a) && a != 0 ? a : null;
	}

	public async Task<bool> DisconnectAsync(ulong address, CancellationToken cancel = default)
	{
		if (host.FindExecutable("bluetoothctl") is not { } tool)
		{
			return false;
		}
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		timeout.CancelAfter(Timeout);
		try
		{
			CommandResult result = await runner.RunAsync(tool, ["disconnect", BluetoothAddress.Format(address)], timeout.Token).ConfigureAwait(false);
			return result.ExitCode == 0 && !result.Output.Contains("Failed", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}
}
