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

	// Hides the USB twin of the bridged controller (HID\VID_...) while it charges over the cable; the caller passes
	// only the pad whose 0x09 address matched the bridged controller, so our own virtual pad is never given. False when
	// no hiding session runs (the bridge stopped meanwhile, or its start could not hide): nothing is hidden then.
	Task<bool> HideUsbAsync(string instanceId);

	// Unhides the USB twin again (cable out) while everything else stays hidden.
	Task UnhideUsbAsync(string instanceId);

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

// HidingSession over HidHideCLI. The ledger lives in %LOCALAPPDATA%\Untethered (the same for every exe folder); flat
// files of older versions are imported and recovered.
// relocate: looks for HidHideCLI.exe again while it was not found, so HidHide installed from the setup page while the app
// runs is used without a restart.
public sealed class HidHideHidingService(HidHideControl control, HidingStateStore store, IReadOnlyList<string>? legacyPaths = null,
	Func<HidHideControl>? relocate = null) : IHidingService
{
	private readonly SemaphoreSlim gate = new(1, 1);

	private HidingSession? session;

	public static HidHideHidingService CreateDefault() =>
		new(HidHideControl.CreateDefault(), HidingStateStore.CreateDefault(), HidingStateStore.LegacyPaths(), () => HidHideControl.CreateDefault());

	public bool IsInstalled
	{
		get
		{
			if (!control.IsInstalled && relocate?.Invoke() is { IsInstalled: true } found)
			{
				control = found;
			}
			return control.IsInstalled;
		}
	}

	public async Task RecoverAsync()
	{
		if (!control.IsInstalled)
		{
			return;
		}
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			await HidingSession.RecoverAsync(control, store, legacyPaths).ConfigureAwait(false);
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

	// Only into the running session: a hide that arrives after the bridge stopped must not start a new one.
	public async Task<bool> HideUsbAsync(string instanceId)
	{
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (session is not { } s)
			{
				return false;
			}
			await s.AddUsbTwinAsync(instanceId).ConfigureAwait(false);
			return true;
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task UnhideUsbAsync(string instanceId)
	{
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (session is { } s)
			{
				await s.RemoveDevicesAsync([instanceId], "cable removed").ConfigureAwait(false);
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
