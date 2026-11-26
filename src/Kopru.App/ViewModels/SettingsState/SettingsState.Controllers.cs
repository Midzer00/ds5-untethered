// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;

namespace Kopru.App.ViewModels;

// Remembered controllers: every controller the service lists is kept (with the user's name for it) and saved with the
// settings. Forgetting one also drops its own profile; a name shows wherever the controller does.
public sealed partial class SettingsState
{
	public static readonly TimeSpan BatterySaveInterval = TimeSpan.FromMinutes(5);

	private ControllerRegistry? _knownControllers;
	private long? _batterySavedAt;
	// Battery-only changes so far, and how many of them the last written snapshot included: a newer reading is unsaved
	// until a snapshot taken after it was written (an older pending snapshot written by the debounce timer does not count).
	private long _batteryVersion;
	private long _batteryVersionSaved;

	private bool BatteryUnsaved => Interlocked.Read(ref _batteryVersion) > Interlocked.Read(ref _batteryVersionSaved);

	public ControllerRegistry KnownControllers => _knownControllers ??= new ControllerRegistry();

	// A controller was remembered, renamed, forgotten or seen again (raised on the thread that changed it).
	public event Action? KnownControllersChanged;

	// The name a controller shows everywhere: the user's name, or the model name.
	public string ControllerName(string id, bool isEdge) => KnownControllers.NameOf(id, isEdge);

	public string ControllerName(ControllerEntry entry) => ControllerName(entry.Id, entry.IsEdge);

	public bool RenameController(string id, string? name) => KnownControllers.Rename(id, name);

	// Not while it is connected. Its own picture goes too.
	public bool ForgetController(string id)
	{
		string? ownImage = KnownControllers.Find(id)?.OwnImage;
		if (!KnownControllers.Forget(id))
		{
			return false;
		}
		if (ownImage != null)
		{
			Kopru.App.Controls.PadArt.Forget(OwnImages.PathOf(ownImage));
			OwnImages.Delete(ownImage);
		}
		return true;
	}

	private void InitKnownControllers(AppSettings? saved)
	{
		_knownControllers = new ControllerRegistry(saved?.KnownControllers);
		// Records saved without an address get it now, before the service can match a cable-only controller against
		// them (a presence tick may already run). Before Changed is wired: the save follows once the constructor is done.
		bool addressesFilled = FillKnownAddresses() > 0;
		_knownControllers.Forgotten += id => RunOnUi(() => AssignControllerProfile(id, null));
		_knownControllers.Changed += () =>
		{
			_batterySavedAt = Clock();
			ScheduleSave();
			KnownControllersChanged?.Invoke();
		};
		// A battery reading alone is saved at most every BatterySaveInterval (and with any other save, and at exit).
		_knownControllers.BatteryChanged += () =>
		{
			Interlocked.Increment(ref _batteryVersion);
			long now = Clock();
			if (_batterySavedAt == null || now - _batterySavedAt.Value >= (long)BatterySaveInterval.TotalMilliseconds)
			{
				_batterySavedAt = now;
				ScheduleSave();
			}
			KnownControllersChanged?.Invoke();
		};
		_knownControllers.Observe(_service.Controllers);
		ControllerRegistry registry = _knownControllers;
		if (addressesFilled)
		{
			// After the constructor: the save timer exists then.
			Avalonia.Threading.Dispatcher.UIThread.Post(ScheduleSave);
		}
		_service.UseKnownIdentities(address => registry.FindIdByAddress(address) ?? MissedAddress());
		_service.StateChanged += (_, _) => RunOnUi(() => KnownControllers.Observe(_service.Controllers));
	}

	// Remembered ids whose Bluetooth address was already looked up in Windows this run (found or not).
	private readonly HashSet<string> _addressLookedUp = new(StringComparer.OrdinalIgnoreCase);
	private int _addressFillQueued;

	// A cable-only controller's address matched no remembered controller: remembered ones without an address that were
	// not looked up yet are looked up (on the UI thread; the next presence tick matches again). Null for the lookup.
	private string? MissedAddress()
	{
		bool pending;
		lock (_addressLookedUp)
		{
			pending = KnownControllers.IdsWithoutAddress(_addressLookedUp).Count > 0;
		}
		if (pending && Interlocked.Exchange(ref _addressFillQueued, 1) == 0)
		{
			// Posted even on the UI thread: never inside the presence tick that asked.
			Avalonia.Threading.Dispatcher.UIThread.Post(() =>
			{
				Volatile.Write(ref _addressFillQueued, 0);
				_ = FillKnownAddresses();
			});
		}
		return null;
	}

	// Remembered controllers saved without their Bluetooth address (by a build before the address was kept, or whose
	// 0x09 read failed) get it from Windows' record of their paired Bluetooth node, without a Bluetooth connection.
	// Each id is looked up once per run; one log line per id, never the address. Returns how many were filled in.
	private int FillKnownAddresses()
	{
		IReadOnlyList<string> ids;
		lock (_addressLookedUp)
		{
			ids = KnownControllers.IdsWithoutAddress(_addressLookedUp);
			_addressLookedUp.UnionWith(ids);
		}
		if (ids.Count == 0)
		{
			return 0;
		}
		HashSet<string> filled = new(KnownControllers.FillAddresses(ids, _service.BluetoothAddressOf), StringComparer.OrdinalIgnoreCase);
		foreach (string id in ids)
		{
			Kopru.Core.Diagnostics.AppLog.Write(filled.Contains(id)
				? "controllers: Bluetooth address of a controller remembered without one found in Windows (its paired Bluetooth device); it can be taken over on the cable alone"
				: "controllers: no Bluetooth address in Windows for a controller remembered without one (not paired any more?); connect it over Bluetooth once");
		}
		return filled.Count;
	}
}

public static class KnownControllerSettingsExtensions
{
	public static AppSettings WithKnownControllers(this AppSettings settings, ControllerRegistry registry)
	{
		IReadOnlyList<KnownController> items = registry.Items;
		settings.KnownControllers = items.Count == 0 ? null : [.. items];
		return settings;
	}
}
