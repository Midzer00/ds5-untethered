// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;

namespace Kopru.App.ViewModels;

// Remembered controllers: every controller the service lists is kept (with the user's name for it) and saved with the
// settings. Forgetting one also drops its own profile; a name shows wherever the controller does.
public sealed partial class SettingsState
{
	private ControllerRegistry? _knownControllers;

	public ControllerRegistry KnownControllers => _knownControllers ??= new ControllerRegistry();

	// A controller was remembered, renamed, forgotten or seen again (raised on the thread that changed it).
	public event Action? KnownControllersChanged;

	// The name a controller shows everywhere: the user's name, or the model name.
	public string ControllerName(string id, bool isEdge) => KnownControllers.NameOf(id, isEdge);

	public string ControllerName(ControllerEntry entry) => ControllerName(entry.Id, entry.IsEdge);

	public bool RenameController(string id, string? name) => KnownControllers.Rename(id, name);

	// Not while it is connected.
	public bool ForgetController(string id) => KnownControllers.Forget(id);

	private void InitKnownControllers(AppSettings? saved)
	{
		_knownControllers = new ControllerRegistry(saved?.KnownControllers);
		_knownControllers.Forgotten += id => RunOnUi(() => AssignControllerProfile(id, null));
		_knownControllers.Changed += () =>
		{
			ScheduleSave();
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
