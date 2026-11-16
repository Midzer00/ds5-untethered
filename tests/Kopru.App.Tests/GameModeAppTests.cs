// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;

// The UI language is global and several tests switch it: run the test classes one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kopru.App.Tests;

public class GameModeAppTests
{
	[AvaloniaFact]
	public void Overview_ShowsTheKnownGame_TheModeSwitchIsGone()
	{
		// English strings are checked in GameModeText_ReadsWell; here the current language is used.
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		Assert.Equal("Cyberpunk 2077", vm.Overview.GameValue);
		// No mode switch is left on the Overview: it says which controller the game sees.
		Assert.DoesNotContain(typeof(OverviewViewModel).GetProperties(), p => p.Name.Contains("SupportedMode", StringComparison.Ordinal));
		Assert.Equal(Text.GameSees("DualSense"), vm.Overview.GameSeesText);
		Assert.True(vm.SupportedGames.IsActive);
		Assert.Equal(GameMode.Supported, fake.Games.State.Mode);

		// An unknown app in front is not a game: the known game stays.
		fake.Watcher.Exe = "Tetris.exe";
		fake.Games.Tick();
		Assert.Equal("Cyberpunk 2077", vm.Overview.GameValue);

		// The game exits: no game.
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		Assert.Equal(Text.ValueNoGame(), vm.Overview.GameValue);
	}

	[AvaloniaFact]
	public void Tray_HasNoModeItem()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var tray = new TrayViewModel(fake, vm.Settings, new NoShell());
		Assert.DoesNotContain(typeof(TrayViewModel).GetProperties(), p => p.Name.Contains("SupportedMode", StringComparison.Ordinal));
		Assert.NotNull(tray.ToggleDesktopModeCommand);
	}

	private sealed class NoShell : IAppShell
	{
		public void ShowMainWindow(AppPage? page = null)
		{
		}

		public void Exit()
		{
		}
	}

	[AvaloniaFact]
	public void WindowIcon_IsTheBrandIcon()
	{
		Uri uri = new("avares://Kopru.App/Assets/icon.ico");
		Assert.True(Avalonia.Platform.AssetLoader.Exists(uri));
		var window = new Kopru.App.Views.MainWindow { DataContext = new MainWindowViewModel(new FakeControllerService(animate: false)) };
		Assert.NotNull(window.Icon);
		string brand = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "brand", "icon.ico");
		using Stream asset = Avalonia.Platform.AssetLoader.Open(uri);
		using MemoryStream copy = new();
		asset.CopyTo(copy);
		Assert.Equal(File.ReadAllBytes(brand), copy.ToArray());
	}

	[Fact]
	public void GameModeText_ReadsWell()
	{
		Assert.Equal("DualSense Mode", Text.ModeSupported(Language.English));
		Assert.Equal("DualSense Modu", Text.ModeSupported(Language.Turkish));
		Assert.Equal("Klasik Titreşim", Text.ModeUnsupported(Language.Turkish));
		Assert.Equal("a · b", GameModeText.Join("a", "b"));
		Assert.Equal("x · DualSense Mode", WithEnglish(() => GameModeText.Line(new GameModeState("x.exe", "x", GameMode.Supported))));
		Assert.Equal("Classic Rumble", WithEnglish(() => GameModeText.Line(new GameModeState(null, null, GameMode.Unsupported))));
	}

	// No hint speaks of a question or of a game turning something on or off by itself.
	[Fact]
	public void ModeTexts_DescribeAPlainSwitch()
	{
		foreach (Language l in new[] { Language.Turkish, Language.English })
		{
			string[] texts = [Text.GameListHint(l), Text.QuickSpeakerTip(l), Text.SpeakerSwitchHint(l), Text.SpeakerSendGameAudioHint(l)];
			foreach (string t in texts)
			{
				Assert.DoesNotContain("sorulur", t);
				Assert.DoesNotContain("Otomatik Aç", t);
				Assert.DoesNotContain("asked", t);
				Assert.DoesNotContain("kendiliğinden", t);
				Assert.DoesNotContain("by itself", t);
				Assert.DoesNotContain("tepsi", t, StringComparison.OrdinalIgnoreCase);
			}
		}
	}

	private static string WithEnglish(Func<string> make)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			return make();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public void SupportedPage_SearchAndAddRemoveGames()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		Assert.Equal(4, vm.SupportedGames.Games.Count);
		vm.SupportedGames.SearchText = "spider";
		Assert.Single(vm.SupportedGames.Games);
		vm.SupportedGames.SearchText = "returnal-win64";
		Assert.Single(vm.SupportedGames.Games);
		vm.SupportedGames.SearchText = "zzz";
		Assert.True(vm.SupportedGames.HasNoMatch);
		vm.SupportedGames.SearchText = string.Empty;

		// Add a running app; it is then recognised and can be removed again.
		fake.Watcher.Source.Show("Indie.exe", @"C:\Games\Indie\Indie.exe");
		fake.Watcher.Source.Show("chrome.exe");
		fake.Watcher.Source.Show("Cyberpunk2077.exe");
		vm.SupportedGames.RefreshRunningApps();
		Assert.DoesNotContain(vm.SupportedGames.RunningApps, a => a.Name == "chrome");
		RunningAppItem indie = vm.SupportedGames.RunningApps.Single(a => a.Name == "Indie");
		vm.SupportedGames.AddRunningCommand.Execute(indie);
		// An added game the list does not know is on "Unsupported".
		GameItemViewModel added = Assert.Single(vm.SupportedGames.UnsupportedGames);
		Assert.True(added.IsUserAdded);
		Assert.Equal(("Indie", @"C:\Games\Indie\Indie.exe"), (added.Name, added.PathTip));
		Assert.Equal(4, vm.SupportedGames.Games.Count);
		fake.Watcher.Exe = "Indie.exe";
		fake.Games.Tick();
		Assert.Equal("Indie", fake.Games.State.Name);
		added.RemoveGameCommand.Execute(null);
		Assert.Empty(vm.SupportedGames.UnsupportedGames);
		Assert.Empty(fake.Games.UserGames);
	}

	[AvaloniaFact]
	public async Task SupportedPage_AddFromFile_UsesThePicker()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		vm.SupportedGames.PickExe = () => Task.FromResult<string?>(null);
		await vm.SupportedGames.AddFromFileCommand.ExecuteAsync(null);
		Assert.Empty(fake.Games.UserGames);
		vm.SupportedGames.PickExe = () => Task.FromResult<string?>(@"D:\Games\My Game\game.exe");
		await vm.SupportedGames.AddFromFileCommand.ExecuteAsync(null);
		Assert.Equal(new UserGame("game", @"D:\Games\My Game\game.exe"), Assert.Single(fake.Games.UserGames));
	}

	[AvaloniaFact]
	public void GameSettings_LoadSaveAndReachTheService()
	{
		Language? before = Localizer.Chosen;
		try
		{
			var saved = new AppSettings
			{
				TriggerStrength = 140,
				LightFromGame = false,
				RumbleFeel = RumbleFeel.Punchy,
				FallbackTrigger = FallbackTrigger.Weapon,
				FallbackStrength = 75,
				UnifiedProfile = true,
				UserGames = [new UserGame("Indie", @"C:\Games\Indie.exe")]
			};
			var store = new MemorySettingsStore(saved);
			var fake = new FakeControllerService(animate: false);
			var vm = new MainWindowViewModel(fake, new AppEnvironment(store));

			Assert.Equal(100, vm.Settings.TriggerStrength);
			Assert.False(fake.Settings.LightFromGame);
			Assert.Equal(RumbleFeel.Punchy, fake.Settings.RumbleFeel);
			Assert.Equal(FallbackTrigger.Weapon, fake.Settings.FallbackTrigger);
			Assert.Contains(vm.SupportedGames.UnsupportedGames, g => g.Name == "Indie" && g.IsUserAdded);
			// One profile: the trigger strength always applies.
			Assert.Equal(1.0, fake.Settings.Game.TriggerIntensity);
			Assert.Equal(FallbackTrigger.Weapon, fake.Settings.Game.Trigger);

			vm.OtherGames.Feel.IsSoft = true;
			vm.Settings.SaveNow();
			Assert.True(SpinWait.SpinUntil(() => store.Saved!.RumbleFeel == RumbleFeel.Soft, 3000));
			Assert.True(store.Saved!.UnifiedProfile);
			Assert.Null(store.Saved.SupportedModeOn);
			Assert.Null(store.Saved.GameOverrides);
			Assert.Single(store.Saved.UserGames);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public void OldSettings_Migrate_UnknownSupportedGamesBecomeUserGames_RestDropped()
	{
		var saved = new AppSettings
		{
			GameOverrides = new()
			{
				["Returnal-Win64-Shipping.exe"] = GameOverride.Supported,
				["Spider-Man.exe"] = GameOverride.Unsupported,
				["Indie.exe"] = GameOverride.Supported,
				["Classic.exe"] = GameOverride.Unsupported,
				["Auto.exe"] = GameOverride.Auto
			},
			LearnedGames = new() { ["Native.exe"] = true, ["claude.exe"] = false }
		};
		GameMemory memory = saved.ToGameMemory(FakeControllerService.SampleCatalog);
		Assert.Equal([new UserGame("Indie", "Indie.exe")], memory.UserGames);

		var store = new MemorySettingsStore(saved);
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		Assert.DoesNotContain(vm.SupportedGames.Games, g => g.Name is "Native" or "claude");
		Assert.Contains(vm.SupportedGames.UnsupportedGames, g => g.Name == "Indie" && g.IsUserAdded);
		vm.Settings.SaveNow();
		Assert.True(SpinWait.SpinUntil(() => store.Saved!.GameOverrides == null, 3000));
		Assert.Null(store.Saved!.LearnedGames);
	}

	[Fact]
	public void JsonStore_ReadsOldFilesAndWritesEnumsAsText()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		// A file from the version with the question and the DualSense Mode switch: the old keys still read.
		File.WriteAllText(path, """
			{ "Language": "en", "RumbleLevel": 70, "SupportedModeOn": false, "DualSenseModeMigrated": true, "DualSenseModeSpeaker": false,
			  "AskOnKnownGame": false, "AlwaysGames": ["Returnal"],
			  "NeverAskGames": ["acc.exe"], "GameOverrides": { "Foo.exe": "Supported" }, "LearnedGames": { "Bar.exe": false } }
			""");
		var store = new JsonSettingsStore(path);
		AppSettings old = store.Load()!;
		Assert.Equal(70, old.RumbleLevel);
		Assert.True(old.LightFromGame);
		Assert.Equal(RumbleFeel.Soft, old.RumbleFeel);
		Assert.False(old.SupportedModeOn);
		Assert.True(old.DualSenseModeMigrated);
		Assert.False(old.DualSenseModeSpeaker);
		Assert.False(old.UnifiedProfile);
		Assert.Equal(GameOverride.Supported, old.GameOverrides!["Foo.exe"]);

		old.RumbleFeel = RumbleFeel.Punchy;
		Assert.True(old.MigrateUnifiedProfile());
		// Mode off: the game's light was blocked, so it stays off.
		Assert.False(old.LightFromGame);
		old.WithGames(old.ToGameMemory(GameCatalog.Empty));
		store.Save(old);
		string json = File.ReadAllText(path);
		Assert.Contains("\"Punchy\"", json);
		Assert.DoesNotContain("GameOverrides", json);
		Assert.DoesNotContain("LearnedGames", json);
		Assert.DoesNotContain("AskOnKnownGame", json);
		Assert.DoesNotContain("AlwaysGames", json);
		Assert.DoesNotContain("NeverAskGames", json);
		Assert.DoesNotContain("SupportedModeOn", json);
		Assert.DoesNotContain("DualSenseMode", json);
		AppSettings back = store.Load()!;
		Assert.True(back.UnifiedProfile);
		Assert.False(back.MigrateUnifiedProfile());
		Assert.Equal([new UserGame("Foo", "Foo.exe")], back.UserGames);
		Assert.Null(back.GameOverrides);
		Directory.Delete(dir, true);
	}

	[Fact]
	public void ControllerSettings_MapToOneProfile()
	{
		var s = new ControllerSettings(GameHapticsLevel: 200, RumbleLevel: 50, TriggerStrength: 40, LightFromGame: false,
			RumbleFeel: RumbleFeel.Punchy, FallbackTrigger: FallbackTrigger.Feedback, FallbackStrength: 100);
		Assert.Equal(new GameProfile(2.0, 0.4, false, 0.5, RumbleFeel.Punchy, FallbackTrigger.Feedback, 1.0), s.Game);
	}
}
