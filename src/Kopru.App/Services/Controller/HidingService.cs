// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hiding;

namespace Kopru.App.Services;

// The app's name for the platform's hiding (IControllerHiding, from PlatformServices.Current.CreateHiding; on Windows
// HidHideHidingService). Kept so the test fakes stay as they are.
public interface IHidingService : IControllerHiding
{
}

// The order the app runs the hiding steps in.
public static class HidingLifecycle
{
	// App start: undo a crashed run, then register our exe early. HidHide errors are left for the bridge start.
	public static async Task StartAsync(IControllerHiding hiding)
	{
		if (!hiding.IsInstalled)
		{
			return;
		}
		try
		{
			await hiding.RecoverAsync().ConfigureAwait(false);
			await hiding.RegisterAppEarlyAsync().ConfigureAwait(false);
		}
		catch (HidHideException)
		{
			// Tried again when the bridge starts.
		}
	}

	// App exit: the active session first, then the early registration.
	public static async Task StopAsync(IControllerHiding hiding)
	{
		if (!hiding.IsInstalled)
		{
			return;
		}
		try
		{
			await hiding.RestoreAsync().ConfigureAwait(false);
			await hiding.ReleaseAppEarlyAsync().ConfigureAwait(false);
		}
		catch (HidHideException)
		{
			// The next start recovers what is left.
		}
	}
}
