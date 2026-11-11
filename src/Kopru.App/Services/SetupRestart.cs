// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Diagnostics;

namespace Kopru.App.Services;

// What the setup page's "Restart The Computer" needs: Windows' restart and the one-time start after it.
public sealed record RestartServices(ISystemRestart Restart, RunOnceRegistration Resume)
{
	public static RestartServices CreateDefault() => new(new WindowsSystemRestart(), new RunOnceRegistration(new RegistryRunOnceStore(), () => Environment.ProcessPath));
}

// The confirmed restart: save the settings, have the app start once after the restart (unless "Start With Windows"
// already starts it), ask Windows to restart in Delay, then exit through the app's normal path (the virtual controller is
// unplugged and the hiding undone). When Windows refuses, the one-time start is taken back and the app keeps running.
public sealed class SetupRestart(RestartServices services, Func<bool> startsWithWindows, Action save, Action exit)
{
	public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

	// True: the restart is on its way and the app is exiting. False: Windows refused (nothing else changed).
	public bool Run()
	{
		save();
		bool registered = false;
		if (!startsWithWindows())
		{
			try
			{
				registered = services.Resume.Register();
			}
			catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
			{
				// The restart still helps; the app just does not come back by itself.
				AppLog.Write("restart: RunOnce not written: " + e.Message);
			}
		}
		try
		{
			services.Restart.Request(Delay);
		}
		catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException
			or UnauthorizedAccessException)
		{
			AppLog.Write("restart: refused: " + e.Message);
			if (registered)
			{
				try
				{
					services.Resume.Unregister();
				}
				catch (Exception u) when (u is UnauthorizedAccessException or System.Security.SecurityException or IOException)
				{
					// Left behind: it only starts the app once more.
				}
			}
			return false;
		}
		AppLog.Write("restart: requested");
		exit();
		return true;
	}
}
