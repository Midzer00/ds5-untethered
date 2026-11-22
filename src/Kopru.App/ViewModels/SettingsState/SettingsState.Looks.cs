// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Controls;
using Kopru.App.Services;

namespace Kopru.App.ViewModels;

// How each controller is drawn (its look: the default art, a pack variant or the user's own picture), kept with the
// remembered controller (KnownController.Look). ActiveLook is the selected controller's: every drawing of "the controller"
// (sidebar card, Overview, Light, the tester, toasts) shows it. An Edge only takes the default art or an own picture.
public sealed partial class SettingsState
{
	private OwnImageStore? _ownImages;
	private PadLook _activeLook = PadLook.Default;
	private bool _looksHooked;
	private string? _activeLookFor;

	// Where own pictures are copied: next to settings.json (a "looks" folder); without a settings file, a temp folder.
	public OwnImageStore OwnImages
	{
		get => _ownImages ??= new OwnImageStore(_store is JsonSettingsStore json && Path.GetDirectoryName(json.Path) is { Length: > 0 } dir
			? Path.Combine(dir, "looks")
			: Path.Combine(Path.GetTempPath(), "kopru-looks-" + Environment.ProcessId));
		set => _ownImages = value;
	}

	// The selected controller's look (the default art without one).
	public PadLook ActiveLook
	{
		get
		{
			HookLooks();
			return _activeLook;
		}
	}

	// The look a controller is drawn with: its choice, or the default art for none, a variant on an Edge, a variant the pack
	// no longer has, or an own picture whose file is gone.
	public PadLook LookOf(string id, bool isEdge)
	{
		KnownController? c = KnownControllers.Find(id);
		if (c?.Look == null)
		{
			return PadLook.Default;
		}
		if (c.Look == KnownController.OwnLook)
		{
			return c.OwnImage != null && File.Exists(OwnImages.PathOf(c.OwnImage)) ? PadLook.Own(OwnImages.PathOf(c.OwnImage)) : PadLook.Default;
		}
		return !isEdge && PadPack.Find(c.Look) != null ? PadLook.Variant(c.Look) : PadLook.Default;
	}

	// A remembered controller's look (by its own model); the default art for one not known.
	public PadLook LookOf(string id) => KnownControllers.Find(id) is { } c ? LookOf(id, c.IsEdge) : PadLook.Default;

	// The controller's own picture (full path) while it has one whose file is there.
	public string? OwnImageOf(string id)
	{
		KnownController? c = KnownControllers.Find(id);
		return c?.OwnImage != null && File.Exists(OwnImages.PathOf(c.OwnImage)) ? OwnImages.PathOf(c.OwnImage) : null;
	}

	// Chooses the default art (null) or a pack variant (its id) for a controller; its own picture is kept.
	public bool SetLook(string id, string? variantId)
	{
		KnownController? c = KnownControllers.Find(id);
		if (c == null || (variantId != null && (c.IsEdge || PadPack.Find(variantId) == null)))
		{
			return false;
		}
		return KnownControllers.SetLook(id, variantId, c.OwnImage);
	}

	// Shows the controller's own picture again (one it already has).
	public bool UseOwnImage(string id) =>
		OwnImageOf(id) != null && KnownControllers.Find(id) is { } c && KnownControllers.SetLook(id, KnownController.OwnLook, c.OwnImage);

	// Copies a picture in as the controller's own and shows it (a previous own picture is replaced). False when the picture
	// cannot be read or the controller is not known; nothing changes then. May run off the UI thread.
	public bool ImportOwnImage(string id, Stream picture)
	{
		if (KnownControllers.Find(id) == null)
		{
			return false;
		}
		string? name = OwnImages.Import(picture);
		if (name == null)
		{
			return false;
		}
		KnownController? c = KnownControllers.Find(id);
		if (c == null)
		{
			OwnImages.Delete(name);
			return false;
		}
		string? previous = c.OwnImage;
		KnownControllers.SetLook(id, KnownController.OwnLook, name);
		if (previous != null && previous != name)
		{
			PadArt.Forget(OwnImages.PathOf(previous));
			OwnImages.Delete(previous);
		}
		// Pictures of controllers forgotten since are left over: only the ones in use stay.
		OwnImages.DeleteAllBut(KnownControllers.Items.Select(k => k.OwnImage).OfType<string>());
		return true;
	}

	// Deletes the controller's own picture; when it was shown, the default art comes back.
	public bool RemoveOwnImage(string id)
	{
		KnownController? c = KnownControllers.Find(id);
		if (c?.OwnImage == null)
		{
			return false;
		}
		string file = c.OwnImage;
		KnownControllers.SetLook(id, c.Look == KnownController.OwnLook ? null : c.Look, null);
		PadArt.Forget(OwnImages.PathOf(file));
		OwnImages.Delete(file);
		return true;
	}

	// Re-reads the selected controller's look (raises ActiveLook's change only when it is different). Cheap while the
	// selection and its choice stay the same (the service's state changes often).
	public void RefreshActiveLook()
	{
		ControllerEntry? selected = _service.SelectedController;
		KnownController? known = selected != null ? KnownControllers.Find(selected.Id) : null;
		string key = $"{selected?.Id}|{selected?.IsEdge}|{known?.Look}|{known?.OwnImage}";
		if (key == _activeLookFor)
		{
			return;
		}
		_activeLookFor = key;
		PadLook now = selected != null ? LookOf(selected.Id, selected.IsEdge) : PadLook.Default;
		if (now == _activeLook)
		{
			return;
		}
		_activeLook = now;
		OnPropertyChanged(nameof(ActiveLook));
	}

	// The first read follows the selection and the remembered controllers from then on.
	private void HookLooks()
	{
		if (_looksHooked)
		{
			return;
		}
		_looksHooked = true;
		KnownControllersChanged += () => RunOnUi(RefreshActiveLook);
		_service.StateChanged += (_, _) => RunOnUi(RefreshActiveLook);
		// A first read off the UI thread (a toast) gets the default until the UI thread has looked.
		RunOnUi(RefreshActiveLook);
	}
}
