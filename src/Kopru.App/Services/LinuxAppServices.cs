// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Kopru.Core.Games;
using Kopru.Core.Health;
using Kopru.Core.Setup;
using Kopru.Platform.Linux;
using Kopru.Platform.Linux.Games;
using Kopru.Platform.Linux.Setup;
using Kopru.Platform.Linux.UsbIp;

namespace Kopru.App.Services;

// The app's Linux parts (experimental). Only this class and PlatformSelection name Kopru.Platform.Linux types, and they
// are called only on Linux, so Windows never loads that assembly.
[SupportedOSPlatform("linux")]
public static class LinuxAppServices
{
	// The desktop app plugs the virtual pad in through pkexec (polkit asks for the password graphically).
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static LinuxPlatform CreatePlatform() => new(new LinuxPlatformOptions { AttachMode = UsbIpAttachMode.Pkexec });

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static SetupWizard CreateWizard()
	{
		LinuxSystemCheck check = LinuxSystemCheck.CreateDefault();
		return new SetupWizard(check.Run, new ShellUrlLauncher());
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static HealthProbe CreateHealthProbe(string adapterResultPath)
	{
		LinuxSystemCheck check = LinuxSystemCheck.CreateDefault();
		return new HealthProbe(new LinuxBluetoothRadio(), AssumedPairedControllers.Instance, null, adapterResultPath,
			requirements: check.Requirements);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static IProcessSource CreateProcessSource() => new LinuxProcessSource();

	// "Turn On Bluetooth": bluetoothctl power on (no root needed for the logged-in user), at most 10 s.
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static async Task TurnOnBluetoothAsync()
	{
		if (RealLinuxHost.Instance.FindExecutable("bluetoothctl") is not { } tool)
		{
			Kopru.Core.Diagnostics.AppLog.Write("bluetooth: bluetoothctl not found; cannot turn the radio on");
			return;
		}
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
		try
		{
			Kopru.Core.Hiding.CommandResult result = await new Kopru.Core.Hiding.ProcessCommandRunner().RunAsync(tool, ["power", "on"], timeout.Token)
				.ConfigureAwait(false);
			Kopru.Core.Diagnostics.AppLog.Write("bluetooth: power on (exit " + result.ExitCode + ")");
		}
		catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			Kopru.Core.Diagnostics.AppLog.Write("bluetooth: power on failed (" + e.Message + ")");
		}
	}
}

// Linux has no system-wide "foreground window" an app may read (Wayland does not tell): the main window counts as in front
// while it is active, and bringing it forward is Avalonia's Activate.
public sealed class AvaloniaForegroundWindows(Window window) : IForegroundWindows
{
	private volatile bool active;
	private IntPtr handle;

	// UI thread, once the window exists.
	public void Attach()
	{
		handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
		window.Activated += (_, _) =>
		{
			handle = window.TryGetPlatformHandle()?.Handle ?? handle;
			active = true;
		};
		window.Deactivated += (_, _) => active = false;
	}

	public IntPtr Foreground => active ? handle : IntPtr.Zero;

	public string? BringToFront(IntPtr target)
	{
		if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
		{
			window.Activate();
			return "activate";
		}
		return null;
	}

	public bool Exists(IntPtr target) => false;
}
