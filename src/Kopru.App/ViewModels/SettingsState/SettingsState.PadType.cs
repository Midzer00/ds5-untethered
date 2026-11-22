// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.ViewModels;

// The controller type games see, for every page that shows it: the default type picker (one instance), what the running
// game sees and why, a type held back while the game runs (with "apply now"), and the live swap (for the "restart the game"
// hint). See Core GameModePadTypes for the rules.
public sealed partial class SettingsState
{
	private PadTypeChoiceViewModel? _padType;

	public PadTypeChoiceViewModel PadType => _padType ??= new PadTypeChoiceViewModel(_service.Games);

	// What the virtual controller is now, and why (PadStatus.Source), with the type waiting for the running game.
	public PadTypeStatus PadStatus => _service.Games.PadStatus;

	public VirtualPadType CurrentPadType => PadStatus.Current;

	// "Oyun Şunu Görüyor: Xbox 360".
	public string GameSeesText => Text.GameSees(PadTypeName(PadStatus.Current));

	// A change made while the game runs waits for the game to restart (PendingPadType is what it will get).
	public bool HasPendingPadType => PadStatus.HasPending;

	public VirtualPadType? PendingPadType => PadStatus.Pending;

	public string PendingPadTypeText => Text.PadTypeDeferred();

	public string PendingPadTypeHint => Text.PadTypeDeferredHint();

	public string ApplyPadTypeNowText => Text.PadTypeApplyNow();

	// The game's light can show: not in Low Power, and not on an Xbox pad (games can't set its light).
	public bool GameLightWorks => !LowPowerOn && PadStatus.Current.HasLight();

	// The speaker plays: the switch on, not in Low Power and the type is DualSense (only it has a speaker).
	public bool SpeakerWorks => SpeakerEnabled && !LowPowerOn && PadStatus.Current == VirtualPadType.DualSense;

	// Raised on the UI thread after the controller type was swapped (PadSwap: from, to, the game running then). A game that
	// was already running may not see the new controller: the UI shows Text.PadSwapRestartHint.
	public event Action<PadSwap>? PadSwapped;

	[RelayCommand]
	private void ApplyPadTypeNow() => _service.Games.ApplyPadTypeNow();

	// The product name of a type (the same in every language).
	public static string PadTypeName(VirtualPadType type) => type switch
	{
		VirtualPadType.DualShock4 => "DualShock 4",
		VirtualPadType.Xbox360 => "Xbox 360",
		VirtualPadType.XboxSeries => "Xbox Series",
		_ => "DualSense"
	};

	// The experimental Xbox Trigger Rumble switch (Settings > Controllers, and Turn On on the Games page's Xbox view): off
	// by default. It lives in the game controller and is saved with the games (its MemoryChanged saves it), so it is not a
	// profile value and is not pushed here.
	public bool XboxTriggerRumbleOn
	{
		get => _service.Games.XboxTriggerRumbleOn;
		set => _service.Games.XboxTriggerRumbleOn = value;
	}

	[RelayCommand]
	private void TurnOnXboxTriggerRumble() => XboxTriggerRumbleOn = true;

	private bool _xboxTriggerRumbleShown;

	// Read-only state raised here: never a setting change to push or save.
	private static readonly HashSet<string> PadStateNames =
	[
		nameof(PadStatus), nameof(CurrentPadType), nameof(HasPendingPadType), nameof(PendingPadType), nameof(GameLightWorks),
		nameof(SpeakerWorks), nameof(XboxTriggerRumbleOn)
	];

	// Constructor.
	private void InitPadType()
	{
		_xboxTriggerRumbleShown = XboxTriggerRumbleOn;
		_service.Games.MemoryChanged += () => RunOnUi(() =>
		{
			if (_xboxTriggerRumbleShown != XboxTriggerRumbleOn)
			{
				_xboxTriggerRumbleShown = !_xboxTriggerRumbleShown;
				OnPropertyChanged(nameof(XboxTriggerRumbleOn));
			}
		});
		_service.Games.PadStateChanged += () => RunOnUi(NotifyPadType);
		_service.Games.PadSwapped += swap => RunOnUi(() => PadSwapped?.Invoke(swap));
		PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(LowPowerOn))
			{
				OnPropertyChanged(nameof(GameLightWorks));
				OnPropertyChanged(nameof(SpeakerWorks));
			}
		};
	}

	private void NotifyPadType()
	{
		OnPropertyChanged(nameof(PadStatus));
		OnPropertyChanged(nameof(CurrentPadType));
		OnPropertyChanged(nameof(GameSeesText));
		OnPropertyChanged(nameof(HasPendingPadType));
		OnPropertyChanged(nameof(PendingPadType));
		OnPropertyChanged(nameof(GameLightWorks));
		OnPropertyChanged(nameof(SpeakerWorks));
	}
}
