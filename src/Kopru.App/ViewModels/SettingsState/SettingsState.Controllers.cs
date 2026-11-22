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
		_service.StateChanged += (_, _) => RunOnUi(() => KnownControllers.Observe(_service.Controllers));
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
