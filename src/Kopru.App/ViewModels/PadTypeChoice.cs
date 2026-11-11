// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.ViewModels;

// One choice of the global controller type: Type null is Automatic.
public sealed record PadChoiceOption(VirtualPadType? Type, string Name);

// The global "Kumanda Türü" (Quick Settings and the Supported Games page share this one instance from SettingsState):
// Automatic (the per-game choice) or one type forced for every game and every controller. The value lives in the game
// mode controller and is saved with the game memory.
public sealed class PadTypeChoiceViewModel : ViewModelBase
{
	private readonly GameModeController _games;
	private IReadOnlyList<PadChoiceOption>? _options;

	public PadTypeChoiceViewModel(GameModeController games)
	{
		_games = games;
		games.MemoryChanged += () => RunOnUi(Refresh);
	}

	// Rebuilt only when the language changed, so a re-read of the bindings never resets the selection.
	public IReadOnlyList<PadChoiceOption> Options
	{
		get
		{
			string auto = Text.PadChoiceAuto();
			if (_options == null || _options[0].Name != auto)
			{
				_options =
				[
					new(null, auto),
					new(VirtualPadType.DualSense, "DualSense"),
					new(VirtualPadType.DualShock4, "DualShock 4"),
					new(VirtualPadType.Xbox360, "Xbox 360")
				];
			}
			return _options;
		}
	}

	public string Label => Text.PadTypeLabel();
	public string Hint => Text.PadChoiceHint();
	public string Tip => Text.PadChoiceTip();
	public string ForcedNote => Text.PadForcedNote();

	// A type other than Automatic is chosen: the per-game choices are kept but not applied.
	public bool IsForced => _games.ForcedPadType != null;

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

	private void Refresh()
	{
		OnPropertyChanged(nameof(Selected));
		OnPropertyChanged(nameof(IsForced));
	}

	protected override void OnLanguageChanged()
	{
		OnPropertyChanged(nameof(Options));
		base.OnLanguageChanged();
	}

	// What games get while the virtual controller is not a DualSense (null for DualSense).
	public static string? ShownAs(VirtualPadType type) => type switch
	{
		VirtualPadType.DualShock4 => Text.PadShownAsDualShock4(),
		VirtualPadType.Xbox360 => Text.PadShownAsXbox(),
		_ => null
	};

	// The short name after "On" in the sidebar ("Açık · DS4"), null for DualSense.
	public static string? ShortName(VirtualPadType type) => type switch
	{
		VirtualPadType.DualShock4 => "DS4",
		VirtualPadType.Xbox360 => "Xbox",
		_ => null
	};
}
