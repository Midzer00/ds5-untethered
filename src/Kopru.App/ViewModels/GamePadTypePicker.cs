// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.ViewModels;

public sealed record PadTypeOption(VirtualPadType Type, string Name);

// Product names, the same in every language.
// The controller type picker on a games row: DualSense (default) or Xbox 360 for games that don't support DualSense.
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
		new(VirtualPadType.DualSense, "DualSense"),
		new(VirtualPadType.Xbox360, "Xbox 360"),
		new(VirtualPadType.DualShock4, "DualShock 4")
	];

	public string Label => Text.PadTypeLabel();

	public string Tip => Text.PadTypeTip() + " " + Text.PadTypeDualShock4Tip();

	// Off while a controller type is forced for every game (the choice is kept and applies again on Automatic).
	public bool IsEnabled => _games.ForcedPadType == null;

	public void Refresh() => OnPropertyChanged(nameof(IsEnabled));

	public PadTypeOption? Selected
	{
		get
		{
			VirtualPadType type = _games.PadTypeFor(_key);
			return Options.FirstOrDefault(o => o.Type == type);
		}
		set
		{
			if (value != null && value.Type != _games.PadTypeFor(_key))
			{
				_games.SetPadType(_key, value.Type);
				OnPropertyChanged();
			}
		}
	}
}
