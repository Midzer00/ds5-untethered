// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Hiding;

// Registers our exe in the HidHide app list at app start, so a controller already hidden by another tool is
// visible to us before the bridge starts. Recorded in the hiding state file (HidingChanges.EarlyApps), so
// HidingSession.RecoverAsync undoes it after a crash. Sessions reuse it and never unregister it.
// Call order: RecoverAsync -> EnsureAppAllowedAsync at start; dispose any session, then ReleaseAsync on exit.
public static class EarlyAppRegistration
{
	// Returns true when our exe is allowed (already or now). False when HidHide is not installed.
	public static async Task<bool> EnsureAppAllowedAsync(HidHideControl control, HidingStateStore store,
		string? appPath = null, CancellationToken cancellationToken = default)
	{
		if (!control.IsInstalled)
		{
			return false;
		}
		appPath ??= Environment.ProcessPath ?? throw new HidHideException(Localization.Text.HidHideNoAppPath());
		HidHideState state = await control.GetStateAsync(cancellationToken).ConfigureAwait(false);
		if (state.AllowedApps.Contains(appPath, StringComparer.OrdinalIgnoreCase))
		{
			return true; // already allowed: by another tool (never touched) or by us earlier
		}
		HidingChanges changes = store.Load() ?? new HidingChanges();
		if (!changes.EarlyApps.Contains(appPath, StringComparer.OrdinalIgnoreCase))
		{
			changes.EarlyApps.Add(appPath);
		}
		store.Save(changes); // persist first so a crash mid-way is still undone
		await control.AllowAppAsync(appPath, cancellationToken).ConfigureAwait(false);
		return true;
	}

	// Undoes the early registration (app exit). Leaves any other recorded changes in the state file.
	public static async Task ReleaseAsync(HidHideControl control, HidingStateStore store, CancellationToken cancellationToken = default)
	{
		HidingChanges? changes = store.Load();
		if (changes is null || changes.EarlyApps.Count == 0 || !control.IsInstalled)
		{
			return;
		}
		await HidingSession.RestoreAsync(control, new HidingChanges { EarlyApps = changes.EarlyApps }, includeEarly: true,
			cancellationToken).ConfigureAwait(false);
		changes.EarlyApps.Clear();
		if (changes.IsEmpty)
		{
			store.Delete();
		}
		else
		{
			store.Save(changes);
		}
	}
}
