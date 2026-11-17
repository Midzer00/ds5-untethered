// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.ViewModels;

// One choice of a game's controller type: Type null is Automatic (the default type). Line: what the choice means.
public sealed class PadTypeOption(VirtualPadType? type, Func<string> name, Func<string> line) : LabelledChoice(name)
{
	public VirtualPadType? Type { get; } = type;
	public string Line => line();
}

// The controller type picker on a games row: Automatic (the default type) or the type this game always sees. Never off: a
// game's own choice wins over the default type.
public sealed class GamePadTypePicker : ObservableObject
{
	private readonly GameModeController _games;
	private readonly string _key;

	public GamePadTypePicker(GameModeController games, string key)
	{
		_games = games;
		_key = key;
	}

	public IReadOnlyList<PadTypeOption> Options { get; } =
	[
		new(null, () => Text.PadChoiceAuto(), () => Text.PadTypeAutoLine()),
		.. PadTypeChoiceViewModel.TypeOptions().Select(o => new PadTypeOption(o.Type, () => o.Name, o.Line))
	];

	public string Label => Text.PadTypeLabel();

	// What the picker is for, then what the chosen type means.
	public string Tip => Selected is { } chosen ? Text.PadTypeGameHint() + "\n" + chosen.Line : Text.PadTypeGameHint();

	public void Refresh()
	{
		OnPropertyChanged(nameof(Selected));
		OnPropertyChanged(nameof(Tip));
	}

	public PadTypeOption? Selected
	{
		get
		{
			VirtualPadType? choice = _games.PadChoiceFor(_key);
			return Options.FirstOrDefault(o => o.Type == choice);
		}
		set
		{
			// A cleared selection (null while the list is rebuilt) leaves the choice alone.
			if (value != null && value.Type != _games.PadChoiceFor(_key))
			{
				_games.SetPadType(_key, value.Type);
				OnPropertyChanged();
				OnPropertyChanged(nameof(Tip));
			}
		}
	}
}
