// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Kopru.Core.Hid;

namespace Kopru.Core.Hiding;

// What one session changed in HidHide; exactly this is undone later.
public sealed class HidingChanges
{
	public List<string> AddedDevices { get; set; } = [];
	public List<string> AddedApps { get; set; } = [];
	public bool TurnedCloakOn { get; set; }
	// Our exe registered at app start (HidingSession.EnsureAppAllowedAsync); kept across sessions, undone on exit.
	public List<string> EarlyApps { get; set; } = [];

	public bool IsEmpty => AddedDevices.Count == 0 && AddedApps.Count == 0 && !TurnedCloakOn && EarlyApps.Count == 0;
}

// Small JSON file that survives a crash so the next start can undo our changes.
public sealed class HidingStateStore(string path)
{
	public string Path { get; } = path;

	public const string FileName = "hiding-state.json";

	public static HidingStateStore CreateDefault() => new(System.IO.Path.Combine(AppPaths.DataDirectory, FileName));

	// Where versions before the rename kept the file; only read for recovery.
	public static HidingStateStore CreateLegacy() => new(System.IO.Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kopru", FileName));

	public HidingChanges? Load()
	{
		try
		{
			return File.Exists(Path) ? JsonSerializer.Deserialize<HidingChanges>(File.ReadAllText(Path)) : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public void Save(HidingChanges changes)
	{
		string? dir = System.IO.Path.GetDirectoryName(Path);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		string temp = Path + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(changes));
		File.Move(temp, Path, overwrite: true);
	}

	public void Delete()
	{
		if (File.Exists(Path))
		{
			File.Delete(Path);
		}
	}
}

// Hides the real Bluetooth controller from games while letting our own exe see it. Dispose restores only what
// this session added; entries owned by other apps are left untouched.
public sealed class HidingSession : IDisposable, IAsyncDisposable
{
	private readonly HidHideControl _control;
	private readonly HidingStateStore _store;
	private readonly HidingChanges _changes;
	private bool _disposed;

	private HidingSession(HidHideControl control, HidingStateStore store, HidingChanges changes)
	{
		_control = control;
		_store = store;
		_changes = changes;
	}

	public IReadOnlyList<string> AddedDevices => _changes.AddedDevices;
	public IReadOnlyList<string> AddedApps => _changes.AddedApps;
	public bool TurnedCloakOn => _changes.TurnedCloakOn;

	// Undoes a previous run that did not restore (crash). Safe to call on every start.
	public static async Task RecoverAsync(HidHideControl control, HidingStateStore store, CancellationToken cancellationToken = default)
	{
		HidingChanges? leftover = store.Load();
		if (leftover is null)
		{
			store.Delete();
			return;
		}
		await RestoreAsync(control, leftover, includeEarly: true, cancellationToken).ConfigureAwait(false);
		store.Delete();
	}

	// Undoes leftover session changes only, keeping this run's early app registration.
	private static async Task RecoverSessionAsync(HidHideControl control, HidingStateStore store, CancellationToken cancellationToken)
	{
		HidingChanges? leftover = store.Load();
		if (leftover is null)
		{
			store.Delete();
			return;
		}
		await RestoreAsync(control, leftover, includeEarly: false, cancellationToken).ConfigureAwait(false);
		SaveEarlyOnly(store, leftover.EarlyApps);
	}

	internal static void SaveEarlyOnly(HidingStateStore store, List<string> earlyApps)
	{
		if (earlyApps.Count == 0)
		{
			store.Delete();
		}
		else
		{
			store.Save(new HidingChanges { EarlyApps = [.. earlyApps] });
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
		await RecoverSessionAsync(control, store, cancellationToken).ConfigureAwait(false);
		appPath ??= Environment.ProcessPath ?? throw new HidHideException(Localization.Text.HidHideNoAppPath());

		HidHideState state = await control.GetStateAsync(cancellationToken).ConfigureAwait(false);
		var planned = new HidingChanges
		{
			TurnedCloakOn = !state.CloakOn,
			EarlyApps = store.Load()?.EarlyApps ?? []
		};
		if (!state.AllowedApps.Contains(appPath, StringComparer.OrdinalIgnoreCase))
		{
			planned.AddedApps.Add(appPath);
		}
		foreach (string id in deviceInstanceIds.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!DeviceInstance.IsBluetoothHid(id) || DeviceInstance.IsUsbDevice(id))
			{
				continue; // never hide the virtual (USB) pad or anything that is not the BT controller
			}
			if (!state.HiddenDevices.Contains(id, StringComparer.OrdinalIgnoreCase))
			{
				planned.AddedDevices.Add(id);
			}
		}

		// Persist the plan first: a crash mid-way is still undone (undoing a not-yet-applied entry is harmless).
		if (!planned.IsEmpty)
		{
			store.Save(planned);
		}
		var session = new HidingSession(control, store, planned);
		try
		{
			// Allow our app before hiding anything so we never lose the controller ourselves.
			foreach (string app in planned.AddedApps)
			{
				await control.AllowAppAsync(app, cancellationToken).ConfigureAwait(false);
			}
			foreach (string id in planned.AddedDevices)
			{
				await control.HideAsync(id, cancellationToken).ConfigureAwait(false);
			}
			if (planned.TurnedCloakOn)
			{
				await control.CloakOnAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception e)
		{
			try
			{
				await session.DisposeAsync().ConfigureAwait(false);
			}
			catch (HidHideException)
			{
				// Keep the state file; the next start retries.
			}
			throw e is HidHideException ? e : new HidHideException(Localization.Text.HidHideHideFailed(e.Message));
		}
		return session;
	}

	// Hides more Bluetooth controllers in this session (e.g. one found after the session started).
	public async Task AddDevicesAsync(IEnumerable<string> deviceInstanceIds, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		HidHideState state = await _control.GetStateAsync(cancellationToken).ConfigureAwait(false);
		var added = new List<string>();
		foreach (string id in deviceInstanceIds.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!DeviceInstance.IsBluetoothHid(id) || DeviceInstance.IsUsbDevice(id) ||
				_changes.AddedDevices.Contains(id, StringComparer.OrdinalIgnoreCase) ||
				state.HiddenDevices.Contains(id, StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}
			added.Add(id);
		}
		if (added.Count == 0)
		{
			return;
		}
		_changes.AddedDevices.AddRange(added);
		_store.Save(_changes);
		foreach (string id in added)
		{
			await _control.HideAsync(id, cancellationToken).ConfigureAwait(false);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		await RestoreAsync(_control, _changes, includeEarly: false, CancellationToken.None).ConfigureAwait(false);
		SaveEarlyOnly(_store, _changes.EarlyApps);
	}

	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

	// Reverse order: cloak off first (games see the controller again), then unhide, then unregister the app.
	internal static async Task RestoreAsync(HidHideControl control, HidingChanges changes, bool includeEarly, CancellationToken cancellationToken)
	{
		if (changes.IsEmpty)
		{
			return;
		}
		HidHideException? failure = null;
		async Task Try(Func<Task> step)
		{
			try
			{
				await step().ConfigureAwait(false);
			}
			catch (HidHideException e)
			{
				failure ??= e;
			}
		}
		if (changes.TurnedCloakOn)
		{
			await Try(() => control.CloakOffAsync(cancellationToken)).ConfigureAwait(false);
		}
		foreach (string id in changes.AddedDevices)
		{
			await Try(() => control.UnhideAsync(id, cancellationToken)).ConfigureAwait(false);
		}
		foreach (string app in changes.AddedApps)
		{
			await Try(() => control.DisallowAppAsync(app, cancellationToken)).ConfigureAwait(false);
		}
		if (includeEarly)
		{
			foreach (string app in changes.EarlyApps)
			{
				await Try(() => control.DisallowAppAsync(app, cancellationToken)).ConfigureAwait(false);
			}
		}
		if (failure is not null)
		{
			throw new HidHideException(Localization.Text.HidHideRestoreFailed(failure.Message));
		}
	}
}
