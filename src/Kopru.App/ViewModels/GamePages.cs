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
		ModeReason.Game => Text.ReasonGame(),
		ModeReason.Always => Text.ReasonAlways(),
		ModeReason.User => Text.ReasonUser(),
		_ => Text.ReasonDefault()
	};

	// "Cyberpunk 2077 · DualSense supported · turned on for this game", or "DualSense supported · your choice" without a game.
	public static string Line(GameModeState state) => state.Name == null
		? GameModeText.Join(Mode(state.Mode), Reason(state.Reason))
		: GameModeText.Join(GameModeText.Join(state.Name, Mode(state.Mode)), Reason(state.Reason));
}

// One row of the supported games list: features, the "always" / "never ask" marks (each removable) and, for a game the
// user added, a remove button.
public sealed partial class GameItemViewModel : ObservableObject
{
	private readonly GameModeController _games;

	public GameItemViewModel(KnownGame game, GameModeController games)
	{
		_games = games;
		Game = game;
	}

	public KnownGame Game { get; }
	public string Name => Game.Name;
	public string Exe => Game.Exe;
	public string Key => Game.Key;
	public bool HasHaptics => Game.Haptics;
	public bool HasTriggers => Game.AdaptiveTriggers;
	public bool IsUserAdded => Game.IsUserAdded;
	public bool IsAlways => _games.IsAlways(Key);
	public bool IsNeverAsk => _games.IsNeverAsk(Key);
	public string HapticsText => Text.BadgeHaptics();
	public string TriggersText => Text.BadgeTriggers();
	public string UserAddedText => Text.MarkUserAdded();
	public string AlwaysText => Text.MarkAlways();
	public string AlwaysTip => Text.MarkAlwaysTip();
	public string NeverAskText => Text.MarkNeverAsk();
	public string NeverAskTip => Text.MarkNeverAskTip();
	public string RemoveMarkText => Text.RemoveMark();
	public string RemoveGameText => Text.RemoveGame();
	public string PathTip => Game.Path ?? Game.Exe;

	// The profile used while this game is recognised.
	public GameProfilePicker? Profile { get; init; }

	// The controller type games see while this game is in front (DualSense unless chosen).
	public GamePadTypePicker? PadType { get; init; }

	// The picture at the start of the row (null without a picture service: the placeholder).
	public GameImageHandle? Image { get; init; }

	[RelayCommand]
	private void RemoveAlways()
	{
		_games.SetAlways(Key, false);
		Refresh();
	}

	[RelayCommand]
	private void RemoveNeverAsk()
	{
		_games.SetNeverAsk(Key, false);
		Refresh();
	}

	[RelayCommand]
	private void RemoveGame()
	{
		if (Game.Path != null)
		{
			_games.RemoveUserGame(Game.Path);
		}
	}

	public void Refresh()
	{
		OnPropertyChanged(nameof(IsAlways));
		OnPropertyChanged(nameof(IsNeverAsk));
		PadType?.Refresh();
	}

	public bool Matches(string query) => query.Length == 0
		|| CultureInfo.InvariantCulture.CompareInfo.IndexOf(Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0
		|| Exe.Contains(query, StringComparison.OrdinalIgnoreCase);
}

// A running app offered by "Add game".
public sealed class RunningAppItem(ProcessInfo process)
{
	public ProcessInfo Process { get; } = process;
	public string Name => ForegroundGame.DisplayName(Process.Exe);
	public string Tip => Process.Path ?? Process.Exe;
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
	// "Cyberpunk 2077 · DualSense supported", or the mode alone without a known game (the reason is in CurrentGameDetail).
	public string CurrentGameText => Service.Games.State is { Name: { } name } state
		? GameModeText.Join(name, GameModeText.Mode(state.Mode))
		: GameModeText.Mode(Service.Games.State.Mode);
	public string CurrentGameDetail => GameModeText.Line(Service.Games.State);
	public bool IsActive => Service.Games.State.Mode == Mode;
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

	private readonly GameCatalogUpdater? _updater;
	private readonly GameImageService? _images;
	private readonly Func<DateTimeOffset> _now;
	private CatalogUpdateResult? _lastResult;

	public SupportedGamesViewModel(IControllerService service, SettingsState settings, GameCatalogUpdater? updater = null,
		Func<DateTimeOffset>? now = null, GameImageService? images = null, InstalledGames? installed = null) : base(service, settings, GameMode.Supported)
	{
		_updater = updater;
		_images = images;
		_installedGames = installed;
		_now = now ?? (() => DateTimeOffset.Now);
		InstalledFilters = [new FilterOption(() => Text.InstalledFilterAll()), new FilterOption(() => InstalledFilterName)];
		Rebuild();
		InitInstalled();
		// The pictures follow the download setting: the rows load again (downloaded pictures only while it is on).
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.DownloadGameImages) && _images != null)
			{
				_images.Forget();
				Rebuild();
			}
		};
		service.Games.MemoryChanged += () => RunOnUi(() =>
		{
			if (Service.Games.KnownGames().Count != _knownCount)
			{
				Rebuild();
			}
			else
			{
				foreach (GameItemViewModel item in _all)
				{
					item.Refresh();
				}
			}
		});
		settings.ProfilesChanged += () =>
		{
			foreach (GameItemViewModel item in _all)
			{
				item.Profile?.Refresh();
			}
		};
		service.Games.CatalogChanged += () => RunOnUi(() =>
		{
			Rebuild();
			OnPropertyChanged(nameof(ListStatusText));
		});
	}

	// The automatic check found new games (the count); raised on the UI thread.
	public event Action<int>? ListUpdatedAutomatically;

	public bool CanUpdateList => _updater != null;
	public string UpdateListText => IsUpdatingList ? Text.UpdatingGameList() : Text.UpdateGameList();
	public string AutoUpdateLabel => Text.AutoUpdateGameList();
	public bool HasListError => _lastResult?.Status == CatalogUpdateStatus.Failed;

	// "72 games · updated 9 Nov 2026", then the outcome of the last check.
	public string ListStatusText
	{
		get
		{
			GameCatalog catalog = Service.Games.Catalog;
			string info = Text.GameListInfo(catalog.Games.Count, catalog.Updated ?? string.Empty);
			return _lastResult switch
			{
				{ Status: CatalogUpdateStatus.UpToDate } => GameModeText.Join(info, Text.GameListUpToDate()),
				{ Status: CatalogUpdateStatus.Updated, Added.Count: > 0 } r => GameModeText.Join(info, Text.GameListAdded(r.Added.Count)),
				{ Status: CatalogUpdateStatus.Failed } r => Text.GameListFailed(r.Reason),
				_ => info
			};
		}
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(UpdateListText))]
	[NotifyCanExecuteChangedFor(nameof(UpdateListCommand))]
	private bool _isUpdatingList;

	private bool CanRunUpdate() => _updater != null && !IsUpdatingList;

	[RelayCommand(CanExecute = nameof(CanRunUpdate))]
	private Task UpdateList() => CheckListAsync(automatic: false);

	// The weekly automatic check (when on); silent unless it added games.
	public Task AutoUpdateAsync() =>
		GameCatalogUpdater.ShouldAutoCheck(_updater != null && Settings.AutoUpdateGameList, Settings.GameListCheckedAt, _now())
			? CheckListAsync(automatic: true)
			: Task.CompletedTask;

	private async Task CheckListAsync(bool automatic)
	{
		if (_updater == null || IsUpdatingList)
		{
			return;
		}
		IsUpdatingList = true;
		CatalogUpdateResult result;
		try
		{
			result = await _updater.CheckAsync(Service.Games.Catalog);
		}
		finally
		{
			IsUpdatingList = false;
		}
		if (result.Status != CatalogUpdateStatus.Failed)
		{
			Settings.GameListCheckedAt = _now();
		}
		if (result.Status == CatalogUpdateStatus.Updated)
		{
			Service.Games.SetCatalog(result.Catalog);
		}
		if (!automatic)
		{
			_lastResult = result;
		}
		RunOnUi(() =>
		{
			Rebuild();
			OnPropertyChanged(nameof(ListStatusText));
			OnPropertyChanged(nameof(HasListError));
			if (automatic && result.Added.Count > 0)
			{
				ListUpdatedAutomatically?.Invoke(result.Added.Count);
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
	public string ListHint => Text.GameListHint();
	public string AskLabel => Text.AskOnKnownGame();
	public string AskHint => Text.AskOnKnownGameHint();
	public string DownloadImagesLabel => Text.DownloadGameImages();
	public string DownloadImagesHint => Text.DownloadGameImagesHint();
	public string AddGameText => Text.AddGame();
	public string AddFromFileText => Text.AddGameFromFile();
	public string RunningHeader => Text.AddGameRunningHeader();
	public string NoRunningText => Text.AddGameNoRunning();

	// Running apps for "Add game" (filled when its menu opens).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNoRunningApps))]
	private IReadOnlyList<RunningAppItem> _runningApps = [];

	public bool HasNoRunningApps => RunningApps.Count == 0;

	public void RefreshRunningApps() => RunningApps = Service.Games.RunningApps().Select(p => new RunningAppItem(p)).ToList();

	// Asks for an exe file (local path out; null when cancelled). Set by the view.
	public Func<Task<string?>>? PickExe { get; set; }

	[RelayCommand]
	private void AddRunning(RunningAppItem? app)
	{
		if (app != null)
		{
			Service.Games.AddUserGame(new UserGame(app.Name, app.Process.Path ?? app.Process.Exe));
		}
	}

	[RelayCommand]
	private async Task AddFromFile()
	{
		if (PickExe == null || await PickExe() is not { Length: > 0 } path)
		{
			return;
		}
		Service.Games.AddUserGame(new UserGame(Path.GetFileNameWithoutExtension(path), path));
	}
	public string CountText => Text.GameCount(Games.Count);
	public string NoMatchText => Text.NoMatchingGames();
	// "No matching games" for a search; the "Installed" view says instead that nothing was found (or that it is looking).
	public bool HasNoMatch => Games.Count == 0 && !HasNoInstalled && !IsSearchingInstalled;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Games))]
	[NotifyPropertyChangedFor(nameof(CountText))]
	[NotifyPropertyChangedFor(nameof(HasNoMatch))]
	private string _searchText = string.Empty;

	public IReadOnlyList<GameItemViewModel> Games => _all.Where(g => (!InstalledOnly || g.IsInstalled) && g.Matches((SearchText ?? string.Empty).Trim())).ToList();

	protected override void OnLanguageChanged()
	{
		// Item templates do not observe the items: build them again with the new names.
		Rebuild();
		base.OnLanguageChanged();
	}

	private void Rebuild()
	{
		IReadOnlyList<KnownGame> known = Service.Games.KnownGames();
		_knownCount = known.Count;
		_all = known.Select(g => new GameItemViewModel(g, Service.Games)
		{
			Profile = new GameProfilePicker(Settings, g.Key),
			PadType = new GamePadTypePicker(Service.Games, g.Key),
			Image = _images == null ? null : new GameImageHandle(_images, new GameImageRequest(g.Key, g.Catalog?.SteamAppId, g.IsUserAdded ? g.Path ?? g.Exe : null))
		}).ToList();
		MarkInstalled();
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
	public string RumbleCardTitle => Text.OtherGamesRumbleCard();
	public string TriggerCardTitle => Text.OtherGamesTriggerCard();
	public string TryLabel => Text.OtherGamesTryLabel();
	public string RumbleTryHint => Text.OtherGamesRumbleTryHint();
	public string TriggerTryHint => Text.OtherGamesTriggerTryHint();
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
