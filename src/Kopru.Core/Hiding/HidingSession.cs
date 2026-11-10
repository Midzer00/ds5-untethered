// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;

namespace Kopru.Core.Hiding;

// Hides the real Bluetooth controller from games while letting our own exe see it. Every entry it adds is claimed in
// the ledger (HidingStateStore) before HidHide is changed; Dispose undoes only entries no other claim still needs.
// Entries that were already in HidHide without a claim of ours (another tool, the user) are never touched.
public sealed class HidingSession : IDisposable, IAsyncDisposable
{
	private readonly HidHideControl _control;
	private readonly HidingStateStore _store;
	private readonly List<string> _addedDevices = [];
	private readonly List<string> _addedApps = [];
	private bool _disposed;

	private HidingSession(HidHideControl control, HidingStateStore store)
	{
		_control = control;
		_store = store;
	}

	// What this session itself put into HidHide (not entries it shares with another running copy).
	public IReadOnlyList<string> AddedDevices => _addedDevices;
	public IReadOnlyList<string> AddedApps => _addedApps;
	public bool TurnedCloakOn { get; private set; }

	// Undoes what exited runs left behind (crash, kill, a build in another folder). Claims of copies that still run
	// are kept. Flat files of older versions are imported into the ledger first, then removed. Safe on every start.
	public static async Task RecoverAsync(HidHideControl control, HidingStateStore store, IEnumerable<string>? legacyPaths = null,
		CancellationToken cancellationToken = default)
	{
		using IDisposable _ = await store.LockAsync(cancellationToken).ConfigureAwait(false);
		HidingLedger ledger = store.Load();
		ImportLegacy(store, ledger, legacyPaths ?? []);
		await ReleaseAsync(control, store, ledger, c => !store.IsRunning(c.Owner), "recovery (left by an exited run)", cancellationToken)
			.ConfigureAwait(false);
	}

	private static void ImportLegacy(HidingStateStore store, HidingLedger ledger, IEnumerable<string> legacyPaths)
	{
		foreach (string path in legacyPaths)
		{
			if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(store.Path), StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
			{
				continue;
			}
			HidingChanges? old = HidingChanges.Load(path);
			if (old is { IsEmpty: false })
			{
				ledger.Claims.AddRange(old.ToClaims(HidingOwner.Unknown));
				store.Save(ledger); // owned by the ledger before the old file goes
				AppLog.Write("hiding: imported the record of an older version: " + Describe(old.ToClaims(HidingOwner.Unknown)));
			}
			try
			{
				File.Delete(path);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				// Imported already; a second import only adds claims for the same entries.
			}
		}
	}

	// deviceInstanceIds: HID instance IDs of the real controller (see DeviceInstance). appPath defaults to our exe.
	public static async Task<HidingSession> StartAsync(HidHideControl control, HidingStateStore store,
		IEnumerable<string> deviceInstanceIds, string? appPath = null, CancellationToken cancellationToken = default)
	{
		if (!control.IsInstalled)
		{
			throw new HidHideException(Localization.Text.HidHideNotInstalled());
		}
		appPath ??= Environment.ProcessPath ?? throw new HidHideException(Localization.Text.HidHideNoAppPath());
		var session = new HidingSession(control, store);
		using (await store.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			HidingLedger ledger = store.Load();
			// Recovery first, so a fresh "already hidden" decision never hides an exited run's entries as foreign.
			// Our own session claims here are leftovers of a stop that could not undo them.
			await ReleaseAsync(control, store, ledger,
				c => !store.IsRunning(c.Owner) || (c.Owner == store.Owner && c.Scope == HidingClaimScope.Session),
				"recovery before start", cancellationToken).ConfigureAwait(false);

			HidHideState state = await control.GetStateAsync(cancellationToken).ConfigureAwait(false);
			var plan = new Plan(store, ledger, state);
			plan.Want(HidingClaimKind.App, appPath);
			foreach (string id in BluetoothOnly(deviceInstanceIds))
			{
				plan.Want(HidingClaimKind.Device, id);
			}
			plan.Want(HidingClaimKind.Cloak, "");
			plan.Log("start");

			// Claimed in the ledger first: a crash mid-way is still undone (undoing a not-yet-applied entry is skipped).
			ledger.Claims.AddRange(plan.Claims);
			store.Save(ledger);
			try
			{
				// Allow our app before hiding anything so we never lose the controller ourselves.
				foreach (string app in plan.Add(HidingClaimKind.App))
				{
					await control.AllowAppAsync(app, cancellationToken).ConfigureAwait(false);
					session._addedApps.Add(app);
				}
				foreach (string id in plan.Add(HidingClaimKind.Device))
				{
					await control.HideAsync(id, cancellationToken).ConfigureAwait(false);
					session._addedDevices.Add(id);
				}
				if (plan.Add(HidingClaimKind.Cloak).Count > 0)
				{
					await control.CloakOnAsync(cancellationToken).ConfigureAwait(false);
					session.TurnedCloakOn = true;
				}
			}
			catch (Exception e)
			{
				session._disposed = true;
				try
				{
					await ReleaseAsync(control, store, ledger, session.Mine, "rollback after a failed start", CancellationToken.None)
						.ConfigureAwait(false);
				}
				catch (HidHideException)
				{
					// The claims stay in the ledger; the next start retries.
				}
				throw e is HidHideException ? e : new HidHideException(Localization.Text.HidHideHideFailed(e.Message));
			}
		}
		return session;
	}

	private bool Mine(HidingClaim c) => c.Owner == _store.Owner && c.Scope == HidingClaimScope.Session;

	private static IEnumerable<string> BluetoothOnly(IEnumerable<string> ids) =>
		ids.Distinct(StringComparer.OrdinalIgnoreCase)
			.Where(id => DeviceInstance.IsBluetoothHid(id) && !DeviceInstance.IsUsbDevice(id)); // never the virtual (USB) pad

	// Hides more Bluetooth controllers in this session (e.g. one found after the session started).
	public async Task AddDevicesAsync(IEnumerable<string> deviceInstanceIds, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		List<string> ids = BluetoothOnly(deviceInstanceIds).ToList();
		if (ids.Count == 0)
		{
			return;
		}
		using IDisposable _ = await _store.LockAsync(cancellationToken).ConfigureAwait(false);
		HidingLedger ledger = _store.Load();
		ids.RemoveAll(id => ledger.Claims.Any(c => Mine(c) && c.SameEntry(HidingClaimKind.Device, id)));
		if (ids.Count == 0)
		{
			return;
		}
		HidHideState state = await _control.GetStateAsync(cancellationToken).ConfigureAwait(false);
		var plan = new Plan(_store, ledger, state);
		foreach (string id in ids)
		{
			plan.Want(HidingClaimKind.Device, id);
		}
		plan.Log("add");
		ledger.Claims.AddRange(plan.Claims);
		_store.Save(ledger);
		foreach (string id in plan.Add(HidingClaimKind.Device))
		{
			await _control.HideAsync(id, cancellationToken).ConfigureAwait(false);
			_addedDevices.Add(id);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		using IDisposable _ = await _store.LockAsync().ConfigureAwait(false);
		HidingLedger ledger = _store.Load();
		await ReleaseAsync(_control, _store, ledger, Mine, "stop", CancellationToken.None).ConfigureAwait(false);
	}

	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

	// Drops the selected claims (call under the store lock). An entry is undone only when no other claim needs it and
	// it is still in HidHide; claims whose undo failed stay in the ledger so a later start retries. Reverse order:
	// cloak off first (games see the controller again), then unhide, then unregister apps.
	internal static async Task ReleaseAsync(HidHideControl control, HidingStateStore store, HidingLedger ledger,
		Func<HidingClaim, bool> releasing, string why, CancellationToken cancellationToken)
	{
		List<HidingClaim> released = ledger.Claims.Where(releasing).ToList();
		if (released.Count == 0)
		{
			return;
		}
		List<HidingClaim> kept = ledger.Claims.Except(released).ToList();
		var entries = released
			.Select(c => (c.Kind, c.Value))
			.DistinctBy(e => (e.Kind, e.Value.ToUpperInvariant()))
			.OrderBy(e => e.Kind)
			.ToList();
		HidHideState state = await control.GetStateAsync(cancellationToken).ConfigureAwait(false);
		List<HidingClaim> undone = [], shared = [], gone = [], failed = [];
		HidHideException? failure = null;
		foreach ((HidingClaimKind kind, string value) in entries)
		{
			List<HidingClaim> claims = released.Where(c => c.SameEntry(kind, value)).ToList();
			if (kept.Any(c => c.SameEntry(kind, value)))
			{
				shared.AddRange(claims); // another claim of ours (a running copy, or this run's other scope) still needs it
				continue;
			}
			bool present = kind switch
			{
				HidingClaimKind.Cloak => state.CloakOn,
				HidingClaimKind.Device => state.HiddenDevices.Contains(value, StringComparer.OrdinalIgnoreCase),
				_ => state.AllowedApps.Contains(value, StringComparer.OrdinalIgnoreCase)
			};
			if (!present)
			{
				gone.AddRange(claims); // already undone (before a kill, or by hand)
				continue;
			}
			try
			{
				await (kind switch
				{
					HidingClaimKind.Cloak => control.CloakOffAsync(cancellationToken),
					HidingClaimKind.Device => control.UnhideAsync(value, cancellationToken),
					_ => control.DisallowAppAsync(value, cancellationToken)
				}).ConfigureAwait(false);
				undone.AddRange(claims);
			}
			catch (HidHideException e)
			{
				failure ??= e;
				failed.AddRange(claims);
			}
		}
		ledger.Claims = [.. kept, .. failed];
		store.Save(ledger);
		AppLog.Write($"hiding: {why}: undid {Describe(undone)}; kept for another claim of ours {Describe(shared)}; " +
			$"already gone {Describe(gone)}" + (failed.Count > 0 ? $"; FAILED {Describe(failed)} (kept in the ledger, retried later)" : ""));
		if (failure is not null)
		{
			throw new HidHideException(Localization.Text.HidHideRestoreFailed(failure.Message));
		}
	}

	// "1 device, 1 app entry, cloak" without ids or paths; "nothing" when empty.
	internal static string Describe(IEnumerable<HidingClaim> claims)
	{
		var distinct = claims.DistinctBy(c => (c.Kind, c.Value.ToUpperInvariant())).ToList();
		int devices = distinct.Count(c => c.Kind == HidingClaimKind.Device);
		int apps = distinct.Count(c => c.Kind == HidingClaimKind.App);
		bool cloak = distinct.Any(c => c.Kind == HidingClaimKind.Cloak);
		var parts = new List<string>();
		if (devices > 0)
		{
			parts.Add(devices == 1 ? "1 device" : $"{devices} devices");
		}
		if (apps > 0)
		{
			parts.Add(apps == 1 ? "1 app entry" : $"{apps} app entries");
		}
		if (cloak)
		{
			parts.Add("cloak");
		}
		return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
	}

	// Decides, per wanted entry, whether this run adds it, shares it with another claim of ours, or leaves it alone.
	private sealed class Plan(HidingStateStore store, HidingLedger ledger, HidHideState state)
	{
		public readonly List<HidingClaim> Claims = [];
		private readonly List<HidingClaim> _added = [];
		private readonly List<HidingClaim> _shared = [];
		private readonly List<HidingClaim> _foreign = [];

		public void Want(HidingClaimKind kind, string value)
		{
			HidingClaim claim = HidingClaim.For(store.Owner, HidingClaimScope.Session, kind, value);
			bool present = kind switch
			{
				HidingClaimKind.Cloak => state.CloakOn,
				HidingClaimKind.Device => state.HiddenDevices.Contains(value, StringComparer.OrdinalIgnoreCase),
				_ => state.AllowedApps.Contains(value, StringComparer.OrdinalIgnoreCase)
			};
			if (!present)
			{
				Claims.Add(claim);
				_added.Add(claim);
			}
			else if (ledger.IsClaimed(kind, value))
			{
				Claims.Add(claim); // put there by another running copy of ours (or this run's early registration)
				_shared.Add(claim);
			}
			else
			{
				_foreign.Add(claim); // there without any claim of ours: another tool or the user; never undone by us
			}
		}

		public List<string> Add(HidingClaimKind kind) => _added.Where(c => c.Kind == kind).Select(c => c.Value).ToList();

		public void Log(string what) =>
			AppLog.Write($"hiding: {what}: adding {Describe(_added)}; shared with another claim of ours {Describe(_shared)}; " +
				$"already set by another tool or the user, left alone {Describe(_foreign)}");
	}
}
