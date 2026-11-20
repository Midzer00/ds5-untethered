// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using Kopru.Core.Platform;

namespace Kopru.App.Services;

// Picks the platform the app runs on, once at start (Program.Main), and makes it PlatformServices.Current.
public static class PlatformSelection
{
	public static IPlatform ForThisSystem()
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsPlatform.Instance;
		}
		if (OperatingSystem.IsLinux())
		{
			return Linux();
		}
		return UnsupportedPlatform.Instance;
	}

	public static IPlatform Apply()
	{
		IPlatform platform = ForThisSystem();
		PlatformServices.Current = platform;
		return platform;
	}

	// Kept out of ForThisSystem so that only this method names a Kopru.Platform.Linux type: the JIT loads that assembly when
	// it compiles this method, which happens only on Linux. On Windows Kopru.Platform.Linux.dll is never loaded
	// (PlatformSelectionTests checks it).
	// Experimental: the desktop app itself does not start on Linux yet (App.OnFrameworkInitializationCompleted);
	// tools/Kopru.LinuxSpike runs the bridge headless. See docs/linux/README.md.
	[MethodImpl(MethodImplOptions.NoInlining)]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	private static IPlatform Linux() => new Kopru.Platform.Linux.LinuxPlatform();
}
