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
	public void Overview_ShowsTheKnownGame_AndTheSwitchPicksTheMode()
	{
		// English strings are checked in GameModeText_ReadsWell; here the current language is used.
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		Assert.Equal("Cyberpunk 2077", vm.Overview.GameValue);
		Assert.False(vm.Overview.IsSupportedModeOn);
		Assert.Equal(Text.ModeShortClassic(), vm.Overview.ModeValue);
		Assert.True(vm.OtherGames.IsActive);
		Assert.Equal("Cyberpunk 2077 · " + Text.ModeUnsupported(), vm.SupportedGames.CurrentGameText);

		vm.Overview.IsSupportedModeOn = true;
		Assert.True(fake.Games.IsSupportedModeOn);
		Assert.Equal(Text.ModeShortSupported(), vm.Overview.ModeValue);
		Assert.True(vm.Overview.IsSupportedMode);
		Assert.Equal("Cyberpunk 2077 · " + Text.ModeSupported() + " · " + Text.ReasonUser(), vm.SupportedGames.CurrentGameDetail);
		Assert.True(vm.SupportedGames.IsActive);
		Assert.False(vm.OtherGames.IsActive);

		// An unknown app in front is not a game: the known game stays.
		fake.Watcher.Exe = "Tetris.exe";
		fake.Games.Tick();
		Assert.Equal("Cyberpunk 2077", vm.Overview.GameValue);

		// The game exits: the switch it was turned on for goes off, no game.
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		Assert.Equal(Text.ValueNoGame(), vm.Overview.GameValue);
		Assert.False(vm.Overview.IsSupportedModeOn);
		Assert.Equal(Text.ModeUnsupported() + " · " + Text.ReasonDefault(), vm.Overview.GameDetail);
	}

	[AvaloniaFact]
	public void Tray_SwitchItem_FlipsTheMode()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var tray = new TrayViewModel(fake, vm.Settings, new NoShell());
		Assert.False(tray.IsSupportedModeOn);
		Assert.Equal(Text.TraySupportedMode(), tray.SupportedModeText);
		tray.ToggleSupportedModeCommand.Execute(null);
		Assert.True(fake.Games.IsSupportedModeOn);
		Assert.True(tray.IsSupportedModeOn);
		Assert.True(vm.Overview.IsSupportedModeOn);
		tray.ToggleSupportedModeCommand.Execute(null);
		Assert.False(fake.Games.IsSupportedModeOn);
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
		Assert.Equal("x · DualSense Mode · always on for this game",
			WithEnglish(() => GameModeText.Line(new GameModeState("x.exe", "x", GameMode.Supported, ModeReason.Always))));
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
	public void SupportedPage_SearchMarksAndAddRemoveGames()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		Assert.Equal(4, vm.SupportedGames.Games.Count);
		vm.SupportedGames.SearchText = "spider";
		GameItemViewModel spider = Assert.Single(vm.SupportedGames.Games);
		vm.SupportedGames.SearchText = "returnal-win64";
		Assert.Single(vm.SupportedGames.Games);
		vm.SupportedGames.SearchText = "zzz";
		Assert.True(vm.SupportedGames.HasNoMatch);
		vm.SupportedGames.SearchText = string.Empty;

		// The "Auto" mark and its remove button.
		Assert.False(spider.IsAlways);
		fake.Games.SetAlways(spider.Key, true);
		spider = vm.SupportedGames.Games.Single(g => g.Name == spider.Name);
		Assert.True(spider.IsAlways);
		spider.RemoveAlwaysCommand.Execute(null);
		Assert.False(spider.IsAlways);
		Assert.False(fake.Games.IsAlways(spider.Key));
		fake.Games.SetNeverAsk(spider.Key, true);
		Assert.True(spider.IsNeverAsk);
		spider.RemoveNeverAskCommand.Execute(null);
		Assert.False(fake.Games.IsNeverAsk(spider.Key));

		// Add a running app; it is then recognised and can be removed again.
		fake.Watcher.Source.Show("Indie.exe", @"C:\Games\Indie\Indie.exe");
		fake.Watcher.Source.Show("chrome.exe");
		fake.Watcher.Source.Show("Cyberpunk2077.exe");
		vm.SupportedGames.RefreshRunningApps();
		Assert.DoesNotContain(vm.SupportedGames.RunningApps, a => a.Name == "chrome");
		RunningAppItem indie = vm.SupportedGames.RunningApps.Single(a => a.Name == "Indie");
		vm.SupportedGames.AddRunningCommand.Execute(indie);
		GameItemViewModel added = vm.SupportedGames.Games.Single(g => g.IsUserAdded);
		Assert.Equal(("Indie", @"C:\Games\Indie\Indie.exe"), (added.Name, added.PathTip));
		Assert.Equal(5, vm.SupportedGames.Games.Count);
		fake.Watcher.Exe = "Indie.exe";
		fake.Games.Tick();
		Assert.Equal("Indie", fake.Games.State.Name);
		added.RemoveGameCommand.Execute(null);
		Assert.Equal(4, vm.SupportedGames.Games.Count);
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
				AskOnKnownGame = false,
				AlwaysGames = ["Returnal"],
				UserGames = [new UserGame("Indie", @"C:\Games\Indie.exe")]
			};
			var store = new MemorySettingsStore(saved);
			var fake = new FakeControllerService(animate: false);
			var vm = new MainWindowViewModel(fake, new AppEnvironment(store));

			Assert.Equal(100, vm.Settings.TriggerStrength);
			Assert.False(fake.Settings.LightFromGame);
			Assert.Equal(RumbleFeel.Punchy, fake.Settings.RumbleFeel);
			Assert.Equal(FallbackTrigger.Weapon, fake.Settings.FallbackTrigger);
			Assert.False(vm.Settings.AskOnKnownGame);
			Assert.False(fake.Games.AskOnKnownGame);
			Assert.True(fake.Games.IsAlways("Returnal"));
			Assert.Contains(vm.SupportedGames.Games, g => g.Name == "Returnal" && g.IsAlways);
			Assert.Contains(vm.SupportedGames.Games, g => g.Name == "Indie" && g.IsUserAdded);

			vm.OtherGames.IsSoft = true;
			vm.Settings.AskOnKnownGame = true;
			fake.Games.SetAlways("Cyberpunk 2077", true);
			vm.Settings.SaveNow();
			Assert.True(SpinWait.SpinUntil(() => store.Saved!.RumbleFeel == RumbleFeel.Soft
				&& store.Saved.AlwaysGames.Contains("Cyberpunk 2077"), 3000));
			Assert.True(store.Saved!.AskOnKnownGame);
			Assert.Null(store.Saved.GameOverrides);
			Assert.Single(store.Saved.UserGames);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public void OldSettings_Migrate_SupportedToAlways_UnsupportedToNeverAsk_LearnedDropped()
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
		Assert.Equal(["Returnal", "Indie.exe"], memory.Always);
		Assert.Equal(["Marvel's Spider-Man Remastered"], memory.NeverAsk);
		Assert.Equal([new UserGame("Indie", "Indie.exe")], memory.UserGames);
		// An old file has no SupportedModeOn: DualSense Mode takes its new default (on).
		Assert.True(memory.SupportedModeOn);
		Assert.True(memory.AskOnKnownGame);

		var store = new MemorySettingsStore(saved);
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		Assert.True(fake.Games.IsAlways("Returnal"));
		Assert.True(fake.Games.IsNeverAsk("Marvel's Spider-Man Remastered"));
		Assert.DoesNotContain(vm.SupportedGames.Games, g => g.Name is "Native" or "claude");
		vm.Settings.AskOnKnownGame = false;
		vm.Settings.SaveNow();
		Assert.True(SpinWait.SpinUntil(() => store.Saved!.AlwaysGames.Count == 2, 3000));
		Assert.Null(store.Saved!.GameOverrides);
		Assert.Null(store.Saved.LearnedGames);
	}

	[Fact]
	public void JsonStore_ReadsOldFilesAndWritesEnumsAsText()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		File.WriteAllText(path, """{ "Language": "en", "RumbleLevel": 70, "GameOverrides": { "Foo.exe": "Supported" }, "LearnedGames": { "Bar.exe": false } }""");
		var store = new JsonSettingsStore(path);
		AppSettings old = store.Load()!;
		Assert.Equal(70, old.RumbleLevel);
		Assert.True(old.LightFromGame);
		Assert.Equal(RumbleFeel.Soft, old.RumbleFeel);
		Assert.True(old.AskOnKnownGame);
		Assert.Empty(old.AlwaysGames);
		Assert.Equal(GameOverride.Supported, old.GameOverrides!["Foo.exe"]);

		old.RumbleFeel = RumbleFeel.Punchy;
		old.WithGames(old.ToGameMemory(GameCatalog.Empty));
		store.Save(old);
		string json = File.ReadAllText(path);
		Assert.Contains("\"Punchy\"", json);
		Assert.DoesNotContain("GameOverrides", json);
		Assert.DoesNotContain("LearnedGames", json);
		AppSettings back = store.Load()!;
		Assert.Equal(["Foo.exe"], back.AlwaysGames);
		Assert.Equal([new UserGame("Foo", "Foo.exe")], back.UserGames);
		Assert.Null(back.GameOverrides);
		Directory.Delete(dir, true);
	}

	[Fact]
	public void ControllerSettings_MapToProfiles()
	{
		var s = new ControllerSettings(GameHapticsLevel: 200, RumbleLevel: 50, TriggerStrength: 40, LightFromGame: false,
			RumbleFeel: RumbleFeel.Punchy, FallbackTrigger: FallbackTrigger.Feedback, FallbackStrength: 100);
		Assert.Equal(new SupportedProfile(2.0, 0.4, false), s.Supported);
		Assert.Equal(new UnsupportedProfile(0.5, RumbleFeel.Punchy, FallbackTrigger.Feedback, 1.0), s.Unsupported);
	}
}
