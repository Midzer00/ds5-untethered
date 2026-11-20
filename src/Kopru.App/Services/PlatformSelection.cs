// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using Kopru.Core.Platform;

namespace Kopru.App.Services;

// Picks the platform the app runs on, once at start (Program.Main), and makes it PlatformServices.Current: Linux, else the
// unsupported platform (nothing found, nothing attached), so the app and its tests still start elsewhere.
public static class PlatformSelection
{
	public static IPlatform ForThisSystem() => OperatingSystem.IsLinux() ? Linux() : UnsupportedPlatform.Instance;

	public static IPlatform Apply()
	{
		IPlatform platform = ForThisSystem();
		PlatformServices.Current = platform;
		return platform;
	}

	// Only Linux code paths name a Kopru.Platform.Linux type (LinuxAppServices). Experimental: see docs/linux/README.md.
	[MethodImpl(MethodImplOptions.NoInlining)]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	private static IPlatform Linux() => LinuxAppServices.CreatePlatform();
}
