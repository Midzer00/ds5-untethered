// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Hiding;

// HidingSession over HidHideCLI. The ledger lives in %LOCALAPPDATA%\Untethered (the same for every exe folder); flat
// files of older versions are imported and recovered.
// relocate: looks for HidHideCLI.exe again while it was not found, so HidHide installed from the setup page while the app
// runs is used without a restart.
public sealed class HidHideHidingService(HidHideControl control, HidingStateStore store, IReadOnlyList<string>? legacyPaths = null,
	Func<HidHideControl>? relocate = null) : IControllerHiding
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
