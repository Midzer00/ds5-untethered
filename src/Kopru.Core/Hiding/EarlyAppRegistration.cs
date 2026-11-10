// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Hiding;

// Registers our exe in the HidHide app list at app start, so a controller already hidden by another tool is
// visible to us before the bridge starts. Claimed in the ledger (scope Early), so HidingSession.RecoverAsync undoes it
// after a crash. Sessions share it and never unregister it.
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
		using IDisposable _ = await store.LockAsync(cancellationToken).ConfigureAwait(false);
		HidingLedger ledger = store.Load();
		if (ledger.Claims.Any(c => c.Owner == store.Owner && c.Scope == HidingClaimScope.Early && c.SameEntry(HidingClaimKind.App, appPath)))
		{
			return true;
		}
		HidHideState state = await control.GetStateAsync(cancellationToken).ConfigureAwait(false);
		HidingClaim claim = HidingClaim.For(store.Owner, HidingClaimScope.Early, HidingClaimKind.App, appPath);
		if (state.AllowedApps.Contains(appPath, StringComparer.OrdinalIgnoreCase))
		{
			if (ledger.IsClaimed(HidingClaimKind.App, appPath))
			{
				ledger.Claims.Add(claim); // allowed by another running copy of ours: shared
				store.Save(ledger);
				AppLog.Write("hiding: early: our exe is already allowed by another running copy of ours (shared)");
			}
			else
			{
				AppLog.Write("hiding: early: our exe is already allowed by another tool or the user (left alone)");
			}
			return true;
		}
		ledger.Claims.Add(claim);
		store.Save(ledger); // claimed first so a crash mid-way is still undone
		await control.AllowAppAsync(appPath, cancellationToken).ConfigureAwait(false);
		AppLog.Write("hiding: early: allowed our exe");
		return true;
	}

	// Undoes the early registration (app exit). Other claims in the ledger stay.
	public static async Task ReleaseAsync(HidHideControl control, HidingStateStore store, CancellationToken cancellationToken = default)
	{
		if (!control.IsInstalled)
		{
			return;
		}
		using IDisposable _ = await store.LockAsync(cancellationToken).ConfigureAwait(false);
		HidingLedger ledger = store.Load();
		await HidingSession.ReleaseAsync(control, store, ledger, c => c.Owner == store.Owner && c.Scope == HidingClaimScope.Early,
			"exit (early registration)", cancellationToken).ConfigureAwait(false);
	}
}
