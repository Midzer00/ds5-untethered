// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Localized names for the game mode, the reason and the overrides.
public static class GameModeText
{
	// Parts of a status line, e.g. "Cyberpunk 2077 · DualSense supported" (the same in every language).
	public static string Join(string first, string second) => first + " · " + second;

	public static string Mode(GameMode mode) => mode == GameMode.Supported ? Text.ModeSupported() : Text.ModeUnsupported();

	public static string Reason(ModeReason reason) => reason switch
	{
		ModeReason.Catalog => Text.ReasonCatalog(),
		ModeReason.Detected => Text.ReasonDetected(),
		ModeReason.User => Text.ReasonUser(),
		_ => Text.ReasonDefault()
	};

	// "Cyberpunk 2077 · DualSense supported · from the list", or "No game detected yet".
	public static string Line(GameModeState state) => state.Name == null
		? Text.NoGameYet()
		: GameModeText.Join(GameModeText.Join(state.Name, Mode(state.Mode)), Reason(state.Reason));
}

public sealed class OverrideOption(GameOverride value, Func<string> name)
{
	public GameOverride Value { get; } = value;
	public string Name => name();

	public static IReadOnlyList<OverrideOption> Build() =>
	[
		new(GameOverride.Auto, () => Text.OverrideAuto()),
		new(GameOverride.Supported, () => Text.OverrideSupported()),
		new(GameOverride.Unsupported, () => Text.OverrideUnsupported()),
	];
}

// One row of the supported games list; changing the override goes straight to the game mode controller.
public sealed partial class GameItemViewModel : ObservableObject
{
	private readonly GameModeController _games;

	public GameItemViewModel(KnownGame game, GameModeController games, IReadOnlyList<OverrideOption> options)
	{
		_games = games;
		Game = game;
		Overrides = options;
		_selectedOverride = options.First(o => o.Value == games.GetOverride(game.Exe));
	}

	public KnownGame Game { get; }
	public string Name => Game.Name;
	public string Exe => Game.Exe;
	public bool HasHaptics => Game.Haptics;
	public bool HasTriggers => Game.AdaptiveTriggers;
	public bool IsLearned => Game.IsLearned;
	public string HapticsText => Text.BadgeHaptics();
	public string TriggersText => Text.BadgeTriggers();
	public string DetectedText => Text.DetectedHere();
	public IReadOnlyList<OverrideOption> Overrides { get; }

	[ObservableProperty]
	private OverrideOption _selectedOverride;

	partial void OnSelectedOverrideChanged(OverrideOption value)
	{
		if (value != null)
		{
			_games.SetOverride(Game.Exe, value.Value);
		}
	}

	public bool Matches(string query) => query.Length == 0
		|| CultureInfo.InvariantCulture.CompareInfo.IndexOf(Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0
		|| Exe.Contains(query, StringComparison.OrdinalIgnoreCase);
}

// Shared by both game pages: the foreground game line and whether this page's mode is the active one.
public abstract class GameModePageViewModel : PageViewModel
{
	protected GameModePageViewModel(IControllerService service, SettingsState settings, GameMode mode)
	{
		Service = service;
		Settings = settings;
		Mode = mode;
		service.StateChanged += (_, _) => RunOnUi(() =>
		{
			OnPropertyChanged(nameof(CurrentGameText));
			OnPropertyChanged(nameof(CurrentGameDetail));
			OnPropertyChanged(nameof(IsActive));
			OnPropertyChanged(nameof(HasSteamWarning));
			OnPropertyChanged(nameof(HasSteamNote));
			OnPropertyChanged(nameof(SteamText));
		});
	}

	protected IControllerService Service { get; }
	public SettingsState Settings { get; }
	public GameMode Mode { get; }
	// "Cyberpunk 2077 · DualSense supported" (the reason is in CurrentGameDetail).
	public string CurrentGameText => Service.Games.State is { Name: { } name } state ? GameModeText.Join(name, GameModeText.Mode(state.Mode)) : Text.NoGameYet();
	public string CurrentGameDetail => GameModeText.Line(Service.Games.State);
	public bool IsActive => Service.Games.State.Name != null && Service.Games.State.Mode == Mode;
	public string ActiveText => Text.ActiveNow();
	public string ProfileLabel => Text.ProfileSettings();

	// Steam Input: a warning when it may take the controller, a muted note when it could not be read.
	public bool HasSteamWarning => SteamInputText.IsWarning(Service.SteamInput);
	public bool HasSteamNote => SteamInputText.IsUnknown(Service.SteamInput);
	public string SteamText => HasSteamWarning || HasSteamNote ? Service.SteamInput!.Advice : string.Empty;
	// Product name, the same in every language.
	public string SteamTitle => "Steam Input";
}

public sealed partial class SupportedGamesViewModel : GameModePageViewModel
{
	private IReadOnlyList<GameItemViewModel> _all = [];
	private int _knownCount = -1;

	public SupportedGamesViewModel(IControllerService service, SettingsState settings) : base(service, settings, GameMode.Supported)
	{
		Rebuild();
		service.Games.MemoryChanged += () => RunOnUi(() =>
		{
			if (Service.Games.KnownGames().Count != _knownCount)
			{
				Rebuild();
			}
		});
	}

	public override string Title => Text.NavSupportedGames();
	public string Intro => Text.SupportedGamesIntro();
	public string GameHapticsLabel => Text.GameHapticsLevel();
	public string GameHapticsHint => Text.GameHapticsHint();
	public string TriggerLabel => Text.TriggerStrength();
	public string TriggerHint => Text.TriggerStrengthHint();
	public string LightLabel => Text.LightFromGame();
	public string LightHint => Text.LightFromGameHint();
	public string ListLabel => Text.SupportedGameList();
	public string SearchWatermark => Text.SearchGames();
	public string OverrideHint => Text.OverrideHint();
	public string CountText => Text.GameCount(Games.Count);
	public string NoMatchText => Text.NoMatchingGames();
	public bool HasNoMatch => Games.Count == 0;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Games))]
	[NotifyPropertyChangedFor(nameof(CountText))]
	[NotifyPropertyChangedFor(nameof(HasNoMatch))]
	private string _searchText = string.Empty;

	public IReadOnlyList<GameItemViewModel> Games => _all.Where(g => g.Matches((SearchText ?? string.Empty).Trim())).ToList();

	protected override void OnLanguageChanged()
	{
		// Item templates do not observe the items: build them again with the new names.
		Rebuild();
		base.OnLanguageChanged();
	}

	private void Rebuild()
	{
		IReadOnlyList<OverrideOption> options = OverrideOption.Build();
		IReadOnlyList<KnownGame> known = Service.Games.KnownGames();
		_knownCount = known.Count;
		_all = known.Select(g => new GameItemViewModel(g, Service.Games, options)).ToList();
		OnPropertyChanged(nameof(Games));
		OnPropertyChanged(nameof(CountText));
		OnPropertyChanged(nameof(HasNoMatch));
	}
}

public sealed partial class OtherGamesViewModel : GameModePageViewModel
{
	public OtherGamesViewModel(IControllerService service, SettingsState settings) : base(service, settings, GameMode.Unsupported)
	{
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(SettingsState.RumbleFeel) or nameof(SettingsState.FallbackTrigger))
			{
				OnPropertyChanged(string.Empty);
			}
		};
	}

	public override string Title => Text.NavOtherGames();
	public string Intro => Text.OtherGamesIntro();
	public string RumbleLabel => Text.RumbleLevel();
	public string RumbleHint => Text.RumbleHint();
	public string FeelLabel => Text.RumbleFeelLabel();
	public string FeelHint => Text.RumbleFeelHint();
	public string SoftText => Text.RumbleFeelSoft();
	public string PunchyText => Text.RumbleFeelPunchy();
	public string FallbackLabel => Text.FallbackTriggerLabel();
	public string FallbackHint => Text.FallbackTriggerHint();
	public string FallbackStrengthLabel => Text.FallbackStrength();
	public string OffText => Text.EffectOff();
	public string FeedbackText => Text.EffectFeedback();
	public string WeaponText => Text.EffectWeapon();
	public string VibrationText => Text.EffectVibration();
	public string TestsLabel => Text.Tests();
	public string RumbleTestText => Text.TestRumble();
	public string TriggerTestText => Text.TestFallbackTrigger();
	public string LeftText => Text.TestHapticsLeft();
	public string RightText => Text.TestHapticsRight();
	public bool HasFallback => Settings.FallbackTrigger != FallbackTrigger.Off;

	// Chip toggles: checking one picks it; unchecking the picked one leaves it picked.
	public bool IsSoft { get => Settings.RumbleFeel == RumbleFeel.Soft; set => SetFeel(RumbleFeel.Soft); }
	public bool IsPunchy { get => Settings.RumbleFeel == RumbleFeel.Punchy; set => SetFeel(RumbleFeel.Punchy); }
	public bool IsFallbackOff { get => Settings.FallbackTrigger == FallbackTrigger.Off; set => SetFallback(FallbackTrigger.Off); }
	public bool IsFallbackFeedback { get => Settings.FallbackTrigger == FallbackTrigger.Feedback; set => SetFallback(FallbackTrigger.Feedback); }
	public bool IsFallbackWeapon { get => Settings.FallbackTrigger == FallbackTrigger.Weapon; set => SetFallback(FallbackTrigger.Weapon); }
	public bool IsFallbackVibration { get => Settings.FallbackTrigger == FallbackTrigger.Vibration; set => SetFallback(FallbackTrigger.Vibration); }

	private void SetFeel(RumbleFeel feel)
	{
		Settings.RumbleFeel = feel;
		OnPropertyChanged(string.Empty);
	}

	private void SetFallback(FallbackTrigger trigger)
	{
		Settings.FallbackTrigger = trigger;
		OnPropertyChanged(string.Empty);
	}

	[RelayCommand]
	private void TestRumble() => Service.TestRumble();

	[RelayCommand]
	private void TestTrigger() => Service.TestFallbackTrigger();

	[RelayCommand]
	private void TestHaptics(HapticSide side) => Service.TestHaptics(side);
}
