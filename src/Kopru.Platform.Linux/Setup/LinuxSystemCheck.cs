// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using Kopru.Core.Health;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Kopru.Platform.Linux.Hidraw;
using Kopru.Platform.Linux.UsbIp;

namespace Kopru.Platform.Linux.Setup;

// The module and udev snippets the setup page copies ("Copy Commands"; the same as docs/linux/README.md).
public static class LinuxSetupCommands
{
	public const string LoadVhci = "sudo modprobe vhci-hcd\necho vhci-hcd | sudo tee /etc/modules-load.d/vhci-hcd.conf";

	public const string UdevRule =
		"printf '%s\\n' 'KERNEL==\"hidraw*\", KERNELS==\"*054C:0CE6*\", MODE=\"0660\", TAG+=\"uaccess\"' " +
		"'KERNEL==\"hidraw*\", KERNELS==\"*054C:0DF2*\", MODE=\"0660\", TAG+=\"uaccess\"' | sudo tee /etc/udev/rules.d/70-ds5-untethered.rules\n" +
		"sudo udevadm control --reload";
}

// What the Linux setup page and the health chips check (read-only; nothing here needs root). Fakeable parts: the host
// (programs on PATH, folders), the hidraw listing and the hidraw access test.
public sealed class LinuxSystemCheck(ILinuxHost host, HidrawFinder finder, Func<string, bool> canOpen, IBluetoothRadio radio)
{
	[SupportedOSPlatform("linux")]
	public static LinuxSystemCheck CreateDefault() =>
		new(RealLinuxHost.Instance, new HidrawFinder(), path => LibC.Access(path, LibC.ReadOk | LibC.WriteOk) == 0, new LinuxBluetoothRadio());

	public IReadOnlyList<SetupRequirement> Requirements()
	{
		bool root = host.IsRoot;
		List<SetupRequirement> list =
		[
			new(SetupRequirementId.VhciModule, UsbIpLinuxCommands.VhciDirectories.Any(host.DirectoryExists), Text.SetupLinuxVhciMissing,
				LinuxSetupCommands.LoadVhci),
			new(SetupRequirementId.UsbIpTool, host.FindExecutable("usbip") != null, Text.SetupLinuxUsbIpMissing),
			new(SetupRequirementId.Pkexec, root || host.FindExecutable("pkexec") != null, Text.SetupLinuxPkexecMissing),
		];
		// Only a DualSense that is there can be checked; without one the pairing step says so.
		IReadOnlyList<HidrawNode> nodes = SafeNodes();
		bool readable = root || nodes.Count == 0 || nodes.Any(n => canOpen(n.DevicePath));
		list.Add(new(SetupRequirementId.HidrawAccess, readable, Text.SetupLinuxHidrawDenied, LinuxSetupCommands.UdevRule));
		return list;
	}

	public SystemStatus Run()
	{
		IReadOnlyList<SetupRequirement> requirements = Requirements();
		RadioState state;
		try
		{
			state = radio.Query();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			state = RadioState.On;
		}
		return new SystemStatus(HealthProbe.LinuxUsbIp(requirements), ComponentInfo.Missing, state, [.. SafeNodes().Select(n => n.ToHidDevice())],
			null, requirements);
	}

	private IReadOnlyList<HidrawNode> SafeNodes()
	{
		try
		{
			return finder.FindNodes();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}

// The Bluetooth radio through sysfs: no /sys/class/bluetooth/hci* = no adapter; every Bluetooth rfkill switch blocked
// (soft or hard) = off; else on. Whether BlueZ has the adapter powered is not read (that needs D-Bus).
public sealed class LinuxBluetoothRadio(string bluetoothClass = "/sys/class/bluetooth", string rfkillClass = "/sys/class/rfkill") : IBluetoothRadio
{
	public RadioState Query()
	{
		if (!Directory.Exists(bluetoothClass) || !Directory.EnumerateFileSystemEntries(bluetoothClass, "hci*").Any())
		{
			return RadioState.Missing;
		}
		if (!Directory.Exists(rfkillClass))
		{
			return RadioState.On;
		}
		bool any = false;
		foreach (string entry in Directory.EnumerateFileSystemEntries(rfkillClass, "rfkill*"))
		{
			if (Read(Path.Combine(entry, "type")) != "bluetooth")
			{
				continue;
			}
			any = true;
			if (Read(Path.Combine(entry, "soft")) != "1" && Read(Path.Combine(entry, "hard")) != "1")
			{
				return RadioState.On;
			}
		}
		return any ? RadioState.Off : RadioState.On;
	}

	private static string? Read(string path)
	{
		try
		{
			return File.ReadAllText(path).Trim();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}

// The paired DualSense controllers on Linux are BlueZ's to know (its pairing store is root-only and D-Bus is not wired up
// yet): this reports one unnamed paired controller, so a DualSense that is just off never blocks the bridge as "never
// paired" (the bridge then waits for it, as on Windows).
public sealed class AssumedPairedControllers : IPairedControllers
{
	public static AssumedPairedControllers Instance { get; } = new();

	private static readonly IReadOnlyList<PairedController> One = [new PairedController("DualSense", 0, false, false)];

	public IReadOnlyList<PairedController> Find() => One;
}
