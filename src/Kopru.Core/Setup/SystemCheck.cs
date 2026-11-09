// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kopru.Core.Hid;
using Microsoft.Win32;

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

// Snapshot of everything the setup wizard needs.
public sealed record SystemStatus(
	ComponentInfo UsbIp,
	ComponentInfo HidHide,
	RadioState Radio,
	IReadOnlyList<HidDevice> Controllers,
	bool? RestartPending)
{
	public IEnumerable<HidDevice> BluetoothControllers => Controllers.Where(c => c.Transport == Transport.Bluetooth);
	public bool ControllerOnBluetooth => BluetoothControllers.Any();
	public bool EdgeOnBluetooth => BluetoothControllers.Any(c => c.IsEdge);
	public bool ControllerOnUsbOnly => Controllers.Count > 0 && !ControllerOnBluetooth;
}

// Uninstall entries (Programs and Features) and driver service registrations.
public interface IInstallRegistry
{
	// First uninstall entry whose DisplayName starts with the prefix: (version, install location).
	(string? Version, string? Location)? FindProgram(string displayNamePrefix);
	bool ServiceExists(string serviceName);
	bool FileExists(string path);
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

// Gathers the setup state from small fakeable probes; every query is read-only.
public sealed class SystemCheck(IInstallRegistry registry, IBluetoothRadio radio, IControllerFinder controllers, IRestartProbe restart)
{
	public const string UsbIpProgramPrefix = "USBip";
	public const string UsbIpExe = "usbip.exe";
	public static readonly string[] UsbIpServices = ["usbip2_ude"];
	public const string HidHideProgramPrefix = "HidHide";
	public const string HidHideService = "HidHide";

	[SupportedOSPlatform("windows")]
	public static SystemCheck CreateDefault() =>
		new(new WindowsInstallRegistry(), new WindowsBluetoothRadio(), new HidControllerFinder(), new WindowsRestartProbe());

	public ComponentInfo CheckUsbIp()
	{
		var program = registry.FindProgram(UsbIpProgramPrefix);
		bool driver = UsbIpServices.All(registry.ServiceExists);
		if (program is not { } p)
		{
			return ComponentInfo.Missing with { DriverPresent = driver };
		}
		string? exe = p.Location is { Length: > 0 } dir ? Path.Combine(dir, UsbIpExe) : null;
		bool exeFound = exe != null && registry.FileExists(exe);
		return new ComponentInfo(exeFound, p.Version, exeFound ? exe : p.Location, driver);
	}

	public ComponentInfo CheckHidHide()
	{
		var program = registry.FindProgram(HidHideProgramPrefix);
		bool driver = registry.ServiceExists(HidHideService);
		return program is { } p
			? new ComponentInfo(true, p.Version, p.Location, driver)
			: ComponentInfo.Missing with { DriverPresent = driver };
	}

	public SystemStatus Run() => new(CheckUsbIp(), CheckHidHide(), radio.Query(), controllers.Find(), restart.IsRestartPending());
}

[SupportedOSPlatform("windows")]
public sealed class WindowsInstallRegistry : IInstallRegistry
{
	private static readonly string[] UninstallRoots =
	[
		@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
		@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
	];

	public (string? Version, string? Location)? FindProgram(string displayNamePrefix)
	{
		foreach (string root in UninstallRoots)
		{
			using RegistryKey? key = Registry.LocalMachine.OpenSubKey(root);
			if (key == null)
			{
				continue;
			}
			foreach (string name in key.GetSubKeyNames())
			{
				using RegistryKey? entry = key.OpenSubKey(name);
				if (entry?.GetValue("DisplayName") is string display &&
					display.StartsWith(displayNamePrefix, StringComparison.OrdinalIgnoreCase))
				{
					return (entry.GetValue("DisplayVersion") as string, entry.GetValue("InstallLocation") as string);
				}
			}
		}
		return null;
	}

	public bool ServiceExists(string serviceName)
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
		return key != null;
	}

	public bool FileExists(string path) => File.Exists(path);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRestartProbe : IRestartProbe
{
	// Markers: servicing/update reboot keys, or pending file renames that touch our drivers.
	public bool? IsRestartPending()
	{
		try
		{
			using (RegistryKey? cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
			{
				if (cbs != null)
				{
					return true;
				}
			}
			using (RegistryKey? wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
			{
				if (wu != null)
				{
					return true;
				}
			}
			using RegistryKey? sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
			if (sm?.GetValue("PendingFileRenameOperations") is string[] renames)
			{
				return renames.Any(r => r.Contains("usbip", StringComparison.OrdinalIgnoreCase) || r.Contains("hidhide", StringComparison.OrdinalIgnoreCase));
			}
			return false;
		}
		catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
		{
			return null;
		}
	}
}

public sealed class HidControllerFinder : IControllerFinder
{
	public IReadOnlyList<HidDevice> Find() => WindowsHid.FindDualSenses();
}

// Classic Bluetooth radio via BluetoothApis. "On" = radio accepts connections (off in Windows settings = not connectable).
public sealed class WindowsBluetoothRadio : IBluetoothRadio
{
	public RadioState Query()
	{
		var p = new BluetoothFindRadioParams { Size = Marshal.SizeOf<BluetoothFindRadioParams>() };
		IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr radio);
		if (find == IntPtr.Zero)
		{
			return RadioState.Missing;
		}
		try
		{
			bool on = false;
			do
			{
				on |= BluetoothIsConnectable(radio);
				CloseHandle(radio);
			}
			while (!on && BluetoothFindNextRadio(find, out radio));
			return on ? RadioState.On : RadioState.Off;
		}
		finally
		{
			BluetoothFindRadioClose(find);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BluetoothFindRadioParams
	{
		public int Size;
	}

	[DllImport("BluetoothApis.dll", SetLastError = true)]
	private static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams p, out IntPtr radio);

	[DllImport("BluetoothApis.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);

	[DllImport("BluetoothApis.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindRadioClose(IntPtr find);

	[DllImport("BluetoothApis.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothIsConnectable(IntPtr radio);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
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

public static class SetupLinks
{
	public const string UsbIpReleases = "https://github.com/vadimgrn/usbip-win2/releases";
	public const string HidHideReleases = "https://github.com/nefarius/HidHide/releases";
	public const string BluetoothSettings = "ms-settings:bluetooth";
}
