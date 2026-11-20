// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.ViewModels;

// One choice of the default controller type: Type null is Automatic. Line: what the choice means (the item's tooltip).
public sealed class PadChoiceOption(VirtualPadType? type, Func<string> name, Func<string> line) : LabelledChoice(name)
{
	public VirtualPadType? Type { get; } = type;
	public string Line => line();
}

// The default controller type, "Varsayılan Kumanda Türü" (Quick Settings and the Games page share this one instance from
// SettingsState): the type Automatic games we know nothing about see, and the type with no game running; Automatic (Per
// Game) gives those Xbox 360 (Core GameModePadTypes). A game's own choice and what we know about a game win over it. The value lives in the game mode controller (ForcedPadType) and is saved with the game memory.
public sealed class PadTypeChoiceViewModel : ViewModelBase
{
	private readonly GameModeController _games;

	public PadTypeChoiceViewModel(GameModeController games)
	{
		_games = games;
		games.MemoryChanged += () => RunOnUi(Refresh);
	}

	// The same list for good (the names follow the language), so neither a re-read of the bindings nor a language switch
	// resets the selection.
	public IReadOnlyList<PadChoiceOption> Options { get; } =
	[
		new(null, () => Text.PadChoiceAutoPerGame(), () => Text.PadChoiceAutoLine()),
		.. TypeOptions().Select(o => new PadChoiceOption(o.Type, o.Name, o.Line))
	];

	public string Label => Text.PadTypeLabel();
	public string Hint => Text.PadTypeDefaultHint();
	// The label above the games list's controller type pickers.
	public string DefaultLabel => Text.PadTypeDefaultLabel();
	public string Tip => Text.PadTypeDefaultHint();

	// The four types with what each one means, in the pickers' order (product names, the same in every language; Xbox
	// Series is an ordinary type, only its trigger rumble is experimental, behind the Xbox Trigger Rumble switch).
	// Xbox Series is left out where its driver is missing (AppFeatures.XboxSeries).
	public static IEnumerable<(VirtualPadType Type, Func<string> Name, Func<string> Line)> TypeOptions() =>
		AllTypeOptions().Where(o => o.Type != VirtualPadType.XboxSeries || AppFeatures.XboxSeries);

	private static IEnumerable<(VirtualPadType Type, Func<string> Name, Func<string> Line)> AllTypeOptions() =>
	[
		(VirtualPadType.DualSense, () => "DualSense", () => Text.PadTypeDualSenseLine()),
		(VirtualPadType.DualShock4, () => "DualShock 4", () => Text.PadTypeDualShock4Line()),
		(VirtualPadType.XboxSeries, () => "Xbox Series", () => Text.PadTypeXboxSeriesLine()),
		(VirtualPadType.Xbox360, () => "Xbox 360", () => Text.PadTypeXboxLine())
	];

	public PadChoiceOption? Selected
	{
		get
		{
			VirtualPadType? forced = _games.ForcedPadType;
			return Options.FirstOrDefault(o => o.Type == forced);
		}
		set
		{
			// A cleared selection (null while the options are rebuilt) leaves the value alone.
			if (value != null && value.Type != _games.ForcedPadType)
			{
				_games.SetForcedPadType(value.Type);
			}
		}
	}

	private void Refresh() => OnPropertyChanged(nameof(Selected));
}
