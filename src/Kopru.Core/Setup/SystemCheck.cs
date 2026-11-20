// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Hid;

namespace Kopru.Core.Setup;

// One installed component as found on the system (null version = installed but version unknown).
public sealed record ComponentInfo(bool Installed, string? Version, string? Location, bool DriverPresent)
{
	public static ComponentInfo Missing { get; } = new(false, null, null, false);

	// Usable only when both the program and its kernel driver are registered.
	public bool Ready => Installed && DriverPresent;
}

public enum RadioState
{
	Missing,
	Off,
	On
}

// What the Linux setup checks look at (LinuxSystemCheck in Kopru.Platform.Linux).
public enum SetupRequirementId
{
	// The vhci_hcd kernel module (the USB/IP client's virtual host controller).
	VhciModule,
	// The usbip command line client.
	UsbIpTool,
	// pkexec (polkit), which the app runs usbip attach and detach through.
	Pkexec,
	// The DualSense hidraw node can be read and written by the user.
	HidrawAccess
}

// One Linux requirement as checked: ready or not, the problem in one or two sentences (with the fix) and the shell
// commands that fix it (copied by "Copy Commands"; null when there is nothing to run).
public sealed record SetupRequirement(SetupRequirementId Id, bool Ready, Func<Localization.Language?, string> Problem, string? FixCommands = null)
{
	// Without these the virtual controller can't be plugged in at all; hidraw access only keeps the controller unopened.
	public bool BlocksBridge => Id is not SetupRequirementId.HidrawAccess;
}

// Snapshot of everything the setup wizard needs. Requirements: the Linux checks (null on Windows, where UsbIp and
// HidHide say what is installed).
public sealed record SystemStatus(
	ComponentInfo UsbIp,
	ComponentInfo HidHide,
	RadioState Radio,
	IReadOnlyList<HidDevice> Controllers,
	bool? RestartPending,
	IReadOnlyList<SetupRequirement>? Requirements = null)
{
	public bool IsLinux => Requirements != null;

	public IEnumerable<HidDevice> BluetoothControllers => Controllers.Where(c => c.Transport == Transport.Bluetooth);
	public bool ControllerOnBluetooth => BluetoothControllers.Any();
	public bool EdgeOnBluetooth => BluetoothControllers.Any(c => c.IsEdge);
	public bool ControllerOnUsbOnly => Controllers.Count > 0 && !ControllerOnBluetooth;
}

public interface IBluetoothRadio
{
	RadioState Query();
}

public interface IControllerFinder
{
	IReadOnlyList<HidDevice> Find();
}

public interface IRestartProbe
{
	// null = could not tell.
	bool? IsRestartPending();
}

public interface IUrlLauncher
{
	void Open(string target);
}

public sealed class ShellUrlLauncher : IUrlLauncher
{
	public void Open(string target)
	{
		using Process? _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
	}
}
