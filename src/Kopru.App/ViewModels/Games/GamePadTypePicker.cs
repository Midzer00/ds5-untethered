// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.ViewModels;

// One choice of a game's controller type: Type null is Automatic. Line: what the choice means.
public sealed class PadTypeOption(VirtualPadType? type, Func<string> name, Func<string> line) : LabelledChoice(name)
{
	public VirtualPadType? Type { get; } = type;
	public string Line => line();
}

// The controller type picker on a games row: Automatic or the type this game always sees. Automatic names what it gives
// this game ("Otomatik (Xbox 360)"): the best controller for what we know about the game, else the default type when one
// is set, else Xbox 360 (the rules in Core GameModePadTypes). Never off: a game's own choice wins.
public sealed class GamePadTypePicker : ObservableObject
{
	private readonly GameModeController _games;
	private readonly string _key;

	public GamePadTypePicker(GameModeController games, string key)
	{
		_games = games;
		_key = key;
		Options =
		[
			new(null, () => Text.PadChoiceAutoFor(SettingsState.PadTypeName(_games.AutomaticPadTypeFor(_key))),
				() => _games.ForcedPadType != null ? Text.PadTypeAutoDefaultLine() : Text.PadTypeAutoLine()),
			.. PadTypeChoiceViewModel.TypeOptions().Select(o => new PadTypeOption(o.Type, o.Name, o.Line))
		];
	}

	public IReadOnlyList<PadTypeOption> Options { get; }

	public string Label => Text.PadTypeLabel();

	// What the picker is for, then what the chosen type means.
	public string Tip => Selected is { } chosen ? Text.PadTypeGameHint() + "\n" + chosen.Line : Text.PadTypeGameHint();

	// The type Automatic gives this game now (shown in its name).
	public VirtualPadType AutomaticType => _games.AutomaticPadTypeFor(_key);

	public void Refresh()
	{
		// Automatic's name and line follow the default type, what was learned and the found game's details.
		Options[0].Refresh();
		OnPropertyChanged(nameof(Selected));
		OnPropertyChanged(nameof(Tip));
		OnPropertyChanged(nameof(AutomaticType));
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
