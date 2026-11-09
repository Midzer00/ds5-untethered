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
	public void Overview_ShowsTheForegroundGameAndItsMode()
	{
		// English strings are checked in GameModeText_ReadsWell; here the current language is used (no language switch, so
		// tests running in parallel are not affected).
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		Assert.Equal("Cyberpunk 2077", vm.Overview.GameValue);
		Assert.Equal(Text.OverrideSupported(), vm.Overview.ModeValue);
		Assert.True(vm.Overview.IsSupportedMode);
		Assert.Equal("Cyberpunk 2077 · " + Text.ModeSupported(), vm.SupportedGames.CurrentGameText);
		Assert.Equal("Cyberpunk 2077 · " + Text.ModeSupported() + " · " + Text.ReasonCatalog(), vm.SupportedGames.CurrentGameDetail);
		Assert.True(vm.SupportedGames.IsActive);
		Assert.False(vm.OtherGames.IsActive);

		fake.Watcher.Exe = "Tetris.exe";
		fake.Games.Tick();
		Assert.Equal("Tetris", vm.Overview.GameValue);
		Assert.Equal(Text.ModeShortClassic(), vm.Overview.ModeValue);
		Assert.True(vm.OtherGames.IsActive);
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
		Assert.Equal("DualSense supported", Text.ModeSupported(Language.English));
		Assert.Equal("DualSense destekli", Text.ModeSupported(Language.Turkish));
		Assert.Equal("Klasik titreşim", Text.ModeUnsupported(Language.Turkish));
		Assert.Equal("a · b", GameModeText.Join("a", "b"));
	}

	[AvaloniaFact]
	public void SupportedPage_SearchFiltersAndOverrideReachesTheController()
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

		spider.SelectedOverride = spider.Overrides.Single(o => o.Value == GameOverride.Unsupported);
		Assert.Equal(GameOverride.Unsupported, fake.Games.GetOverride("SPIDER-MAN.EXE"));
	}

	[AvaloniaFact]
	public void LearnedGame_AppearsInTheListAsDetected()
	{
		var bridge = new SignalBridge();
		var watcher = new FakeGameWatcher("Indie.exe");
		var games = new GameModeController(FakeControllerService.SampleCatalog, watcher, bridge);
		bridge.Next = new GameSignals(Haptics: true, Triggers: false, Rumble: false);
		games.Tick();
		KnownGame indie = games.KnownGames().Single(g => g.Exe == "Indie.exe");
		Assert.True(indie.IsLearned);
		Assert.Equal("Indie", indie.Name);
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
				GameOverrides = new() { ["Returnal-Win64-Shipping.exe"] = GameOverride.Unsupported },
				LearnedGames = new() { ["Indie.exe"] = true }
			};
			var store = new MemorySettingsStore(saved);
			var fake = new FakeControllerService(animate: false);
			var vm = new MainWindowViewModel(fake, new AppEnvironment(store));

			Assert.Equal(100, vm.Settings.TriggerStrength);
			Assert.False(fake.Settings.LightFromGame);
			Assert.Equal(RumbleFeel.Punchy, fake.Settings.RumbleFeel);
			Assert.Equal(FallbackTrigger.Weapon, fake.Settings.FallbackTrigger);
			Assert.Equal(GameOverride.Unsupported, fake.Games.GetOverride("returnal-win64-shipping.exe"));
			Assert.Contains(vm.SupportedGames.Games, g => g.Exe == "Indie.exe" && g.IsLearned);

			vm.OtherGames.IsSoft = true;
			fake.Games.SetOverride("Cyberpunk2077.exe", GameOverride.Supported);
			vm.Settings.SaveNow();
			Assert.True(SpinWait.SpinUntil(() => store.Saved!.RumbleFeel == RumbleFeel.Soft
				&& store.Saved.GameOverrides.ContainsKey("Cyberpunk2077.exe"), 3000));
			Assert.True(store.Saved!.LearnedGames["Indie.exe"]);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[Fact]
	public void JsonStore_ReadsOldFilesAndWritesEnumsAsText()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		File.WriteAllText(path, """{ "Language": "en", "RumbleLevel": 70 }""");
		var store = new JsonSettingsStore(path);
		AppSettings old = store.Load()!;
		Assert.Equal(70, old.RumbleLevel);
		Assert.True(old.LightFromGame);
		Assert.Equal(RumbleFeel.Soft, old.RumbleFeel);
		Assert.Empty(old.GameOverrides);

		old.RumbleFeel = RumbleFeel.Punchy;
		old.GameOverrides["Foo.exe"] = GameOverride.Supported;
		old.LearnedGames["Bar.exe"] = false;
		store.Save(old);
		string json = File.ReadAllText(path);
		Assert.Contains("\"Punchy\"", json);
		AppSettings back = store.Load()!;
		Assert.Equal(GameOverride.Supported, back.GameOverrides["Foo.exe"]);
		Assert.False(back.LearnedGames["Bar.exe"]);
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

	private sealed class SignalBridge : IGameBridge
	{
		public GameSignals Next;

		public void ApplyProfile(BridgeProfile profile)
		{
		}

		public GameSignals TakeGameSignals()
		{
			GameSignals s = Next;
			Next = default;
			return s;
		}
	}
}
