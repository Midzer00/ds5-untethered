// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One choice in the look picker: the default art, a pack variant, or the own image (Key: null, the variant's id, or
// KnownController.OwnLook). The own image's tile adds a picture when there is none and offers Remove when there is.
public sealed partial class PadLookTile(string? key, PadLook look, Func<string> name, Action<PadLookTile> choose, Action? remove = null) : ViewModelBase
{
	public string? Key { get; } = key;
	public PadLook Look { get; } = look;
	public bool IsOwn => Key == KnownController.OwnLook;
	// The own image's tile with a picture (it can be removed); without one it adds a picture.
	public bool HasImage => IsOwn && Look.ImagePath != null;
	public bool ShowsAdd => IsOwn && !HasImage;
	public bool CanRemove => HasImage && remove != null;
	public string Name => name();
	public string AccessibleName => IsSelected ? Text.LookSelected(Name) : Name;
	public string RemoveText => Text.LookRemove();
	public string RemoveTip => Text.LookRemoveTip();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AccessibleName))]
	private bool _isSelected;

	[ObservableProperty]
	private Bitmap? _thumbnail;

	[RelayCommand]
	private void Choose() => choose(this);

	[RelayCommand]
	private void Remove() => remove?.Invoke();
}

// A titled block of tiles in the picker (Standard, Special Editions).
public sealed class PadLookGroup(Func<string> title, IReadOnlyList<PadLookTile> tiles) : ViewModelBase
{
	public string Title => title();
	public IReadOnlyList<PadLookTile> Tiles { get; } = tiles;
}

// The Controller page's colour tile and its picker, for the selected controller: the tile (beside the header, with the
// current look's name) opens the picker over the page: Default and the pack's standard colours, then its special editions (they
// scroll), and the own image's tile in the footer beside Done (an Edge gets only Default and the own image: the pack is
// standard-controller art). A choice applies at once and the picker stays open until Done, Esc or a click beside it.
// Thumbnails are decoded in the background when the picker opens (made once, kept by PadArt).
public sealed partial class ControllerLookViewModel : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly SettingsState _settings;
	private string _shown = string.Empty;

	public ControllerLookViewModel(IControllerService service, SettingsState settings)
	{
		_service = service;
		_settings = settings;
		settings.KnownControllersChanged += () => RunOnUi(Refresh);
		service.StateChanged += (_, _) => RunOnUi(Refresh);
		Refresh();
	}

	// Opens the file picker; the chosen file's content, or null when cancelled (the view sets it; tests fake it).
	public Func<Task<Stream?>>? PickImage { get; set; }

	// The selected (connected) controller.
	private ControllerEntry? Target => _service.IsConnected ? _service.SelectedController : null;

	public bool IsAvailable => Target != null;
	public bool IsEdge => Target?.IsEdge == true;

	public string Label => Text.LookLabel();
	public string ChangeTip => Text.LookChangeTip();
	public string Hint => Text.LookPickerHint();
	public string DoneText => Text.LookDone();
	public string EdgeNote => Text.LookEdgeNote();
	public string OwnHint => Text.LookOwnHint();

	// The look the controller is drawn with now.
	public PadLook Current => Target is { } t ? _settings.LookOf(t.Id, t.IsEdge) : PadLook.Default;

	public string CurrentName => NameOf(Current);

	// The tile's name for a screen reader: its title and the current look.
	public string AccessibleName => Text.LookTileAccessible(CurrentName);

	[ObservableProperty]
	private bool _isOpen;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	private string? _error;

	public bool HasError => !string.IsNullOrEmpty(Error);

	// The pack's tiles (they scroll); the own image's tile is apart (OwnTile, in the footer).
	public IReadOnlyList<PadLookGroup> Groups { get; private set; } = [];

	public PadLookTile? OwnTile { get; private set; }

	// Every tile of the picker, in order (the own image's last).
	public IEnumerable<PadLookTile> Tiles => OwnTile != null ? [.. Groups.SelectMany(g => g.Tiles), OwnTile] : Groups.SelectMany(g => g.Tiles);

	// The thumbnails of the open picker are all in place (tests wait for it).
	public Task ThumbnailsReady { get; private set; } = Task.CompletedTask;

	// Thumbnail loads still running in any picker (tests wait for none before the next test).
	internal static int Loading => Volatile.Read(ref _loading);

	private static int _loading;

	[RelayCommand]
	private void Open()
	{
		if (!IsAvailable)
		{
			return;
		}
		Error = null;
		Build();
		IsOpen = true;
	}

	[RelayCommand]
	private void Close()
	{
		IsOpen = false;
		Error = null;
	}

	// Chooses the default art, a variant, or the own image (adding one through the file picker when there is none).
	public void Choose(PadLookTile tile)
	{
		if (Target is not { } t)
		{
			return;
		}
		Error = null;
		if (tile.IsOwn)
		{
			if (tile.HasImage)
			{
				_settings.UseOwnImage(t.Id);
			}
			else
			{
				_ = AddOwnImageAsync();
			}
		}
		else
		{
			_settings.SetLook(t.Id, tile.Key);
		}
		Refresh();
	}

	// Asks for a picture and makes it the controller's own (copied and, when large, downscaled off the UI thread).
	public async Task AddOwnImageAsync()
	{
		if (Target is not { } t || PickImage == null)
		{
			return;
		}
		Stream? picked;
		try
		{
			picked = await PickImage();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			picked = null;
		}
		if (picked == null)
		{
			return;
		}
		bool ok;
		await using (picked)
		{
			ok = await Task.Run(() => _settings.ImportOwnImage(t.Id, picked));
		}
		RunOnUi(() =>
		{
			Error = ok ? null : Text.LookImageFailed();
			Refresh();
		});
	}

	public void RemoveOwnImage()
	{
		if (Target is { } t && _settings.RemoveOwnImage(t.Id))
		{
			Error = null;
			Refresh();
		}
	}

	// Re-reads the controller and its look (the tile; the open picker's tiles and selection). Cheap while the controller and
	// its choice stay the same (the service's state changes often).
	public void Refresh()
	{
		ControllerEntry? t = Target;
		KnownController? known = t != null ? _settings.KnownControllers.Find(t.Id) : null;
		// The tiles depend on the controller, its model and its own image; the selection on the look.
		string tiles = $"{t?.Id}|{t?.IsEdge}|{known?.OwnImage}|";
		string shown = tiles + known?.Look;
		if (shown == _shown)
		{
			return;
		}
		bool rebuild = IsOpen && !_shown.StartsWith(tiles, StringComparison.Ordinal);
		_shown = shown;
		if (t == null && IsOpen)
		{
			IsOpen = false;
		}
		else if (rebuild)
		{
			Build();
		}
		else
		{
			MarkSelected();
		}
		OnPropertyChanged(nameof(IsAvailable));
		OnPropertyChanged(nameof(IsEdge));
		OnPropertyChanged(nameof(Current));
		OnPropertyChanged(nameof(CurrentName));
		OnPropertyChanged(nameof(AccessibleName));
	}

	private void Build()
	{
		ControllerEntry? t = Target;
		bool edge = t?.IsEdge == true;
		var standard = new List<PadLookTile> { new(null, PadLook.Default, () => Text.LookDefault(), Choose) };
		List<PadLookTile> special = [];
		if (!edge)
		{
			foreach (PadVariant v in PadPack.Variants)
			{
				var tile = new PadLookTile(v.Id, PadLook.Variant(v.Id), () => v.Name, Choose);
				(v.IsSpecial ? special : standard).Add(tile);
			}
		}
		string? own = t != null ? _settings.OwnImageOf(t.Id) : null;
		OwnTile = new PadLookTile(KnownController.OwnLook, own != null ? PadLook.Own(own) : new PadLook(PadLookKind.Own),
			() => own != null ? Text.LookOwn() : Text.LookOwnAdd(), Choose, RemoveOwnImage);
		List<PadLookGroup> groups = [new(() => Text.LookStandard(), standard)];
		if (special.Count > 0)
		{
			groups.Add(new(() => Text.LookSpecial(), special));
		}
		Groups = groups;
		MarkSelected();
		OnPropertyChanged(nameof(Groups));
		OnPropertyChanged(nameof(OwnTile));
		OnPropertyChanged(nameof(Tiles));
		LoadThumbnails(edge);
	}

	private void MarkSelected()
	{
		PadLook current = Current;
		foreach (PadLookTile tile in Tiles)
		{
			tile.IsSelected = tile.Look == current || (tile.IsOwn && current.Kind == PadLookKind.Own);
		}
	}

	// Thumbnails in the background, put in place on the UI thread.
	private void LoadThumbnails(bool edge)
	{
		List<PadLookTile> tiles = [.. Tiles.Where(x => !x.ShowsAdd)];
		Interlocked.Increment(ref _loading);
		ThumbnailsReady = Task.Run(() =>
			{
				try
				{
					return tiles.Select(x => (Tile: x, Picture: PadArt.Thumbnail(x.Look, edge))).ToList();
				}
				finally
				{
					Interlocked.Decrement(ref _loading);
				}
			})
			.ContinueWith(done =>
			{
				if (done.IsCompletedSuccessfully)
				{
					RunOnUi(() =>
					{
						foreach ((PadLookTile tile, Bitmap? picture) in done.Result)
						{
							tile.Thumbnail = picture;
						}
					});
				}
			}, TaskScheduler.Default);
	}

	private static string NameOf(PadLook look) => look.Kind switch
	{
		PadLookKind.Variant => PadPack.Find(look.VariantId)?.Name ?? Text.LookDefault(),
		PadLookKind.Own => Text.LookOwn(),
		_ => Text.LookDefault()
	};
}
