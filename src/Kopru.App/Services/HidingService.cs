// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hiding;

namespace Kopru.App.Services;

// Hides the real Bluetooth controller from games while the bridge runs; faked in tests.
public interface IHidingService
{
	bool IsInstalled { get; }

	// Undoes what an earlier run left behind (crash). Safe on every start.
	Task RecoverAsync();

	// Allows our own exe through HidHide (before the controller is opened).
	Task BeginAsync();

	// Hides one Bluetooth HID instance (HID\{00001124-...}).
	Task HideAsync(string instanceId);

	// Undoes everything this run changed.
	Task RestoreAsync();

	// Allows our exe through HidHide at app start, so a controller hidden by another tool is visible before the bridge
	// starts (after RecoverAsync).
	Task RegisterAppEarlyAsync();

	// Undoes RegisterAppEarlyAsync (app exit, after RestoreAsync).
	Task ReleaseAppEarlyAsync();
}

// The order the app runs the hiding steps in.
public static class HidingLifecycle
{
	// App start: undo a crashed run, then register our exe early. HidHide errors are left for the bridge start.
	public static async Task StartAsync(IHidingService hiding)
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
	public static async Task StopAsync(IHidingService hiding)
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

// HidingSession over HidHideCLI. State lives in %LOCALAPPDATA%\Untethered; the old Kopru file is still recovered.
public sealed class HidHideHidingService(HidHideControl control, HidingStateStore store, HidingStateStore? legacy = null) : IHidingService
{
	private readonly SemaphoreSlim gate = new(1, 1);

	private HidingSession? session;

	public static HidHideHidingService CreateDefault() =>
		new(HidHideControl.CreateDefault(), HidingStateStore.CreateDefault(), HidingStateStore.CreateLegacy());

	public bool IsInstalled => control.IsInstalled;

	public async Task RecoverAsync()
	{
		if (!control.IsInstalled)
		{
			return;
		}
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (legacy != null)
			{
				await HidingSession.RecoverAsync(control, legacy).ConfigureAwait(false);
			}
			await HidingSession.RecoverAsync(control, store).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task BeginAsync()
	{
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			session ??= await HidingSession.StartAsync(control, store, []).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task HideAsync(string instanceId)
	{
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (session == null)
			{
				session = await HidingSession.StartAsync(control, store, [instanceId]).ConfigureAwait(false);
			}
			else
			{
				await session.AddDevicesAsync([instanceId]).ConfigureAwait(false);
			}
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task RegisterAppEarlyAsync()
	{
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			await EarlyAppRegistration.EnsureAppAllowedAsync(control, store).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task ReleaseAppEarlyAsync()
	{
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			HidingSession? s = session;
			session = null;
			if (s != null)
			{
				await s.DisposeAsync().ConfigureAwait(false);
			}
			await EarlyAppRegistration.ReleaseAsync(control, store).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task RestoreAsync()
	{
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			HidingSession? s = session;
			session = null;
			if (s != null)
			{
				await s.DisposeAsync().ConfigureAwait(false);
			}
		}
		finally
		{
			gate.Release();
		}
	}
}
