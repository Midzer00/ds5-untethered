// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

// The default controller type ("Varsayılan Kumanda Türü") in Quick Settings and on the Games page: one shared choice,
// saved with the settings, the per-game pickers always on (a game's own choice wins), what games get shown on the Overview
// and in the sidebar, and the existing pad type toast.
public sealed class PadTypeChoiceAppTests : IDisposable
{
	private readonly string folder = Path.Combine(Path.GetTempPath(), "kopru-padchoice-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create(ISettingsStore? store = null)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		Settle();
		return (vm, fake);
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
	}

	// With no game running a change of the default type swaps at once (with one it waits; see UnifiedProfileAppTests).
	private static void NoGame(FakeControllerService fake)
	{
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
	}

	[AvaloniaFact]
	public void OneSharedChoice_AutomaticByDefault_FourOptionsInOrder()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		NoGame(fake);
		PadTypeChoiceViewModel choice = vm.Overview.Quick.PadType;
		Assert.Same(vm.Settings.PadType, choice);
		Assert.Equal([null, VirtualPadType.DualSense, VirtualPadType.DualShock4, VirtualPadType.XboxSeries, VirtualPadType.Xbox360], choice.Options.Select(o => o.Type));
		WithLanguage(Language.Turkish, () =>
			Assert.Equal(["Otomatik (Oyuna Göre)", "DualSense", "DualShock 4", "Xbox Series", "Xbox 360"], choice.Options.Select(o => o.Name)));
		WithLanguage(Language.English, () => Assert.Equal("Xbox Series", choice.Options[3].Name));
		// Every option says what it means (its tooltip).
		Assert.Equal([Text.PadChoiceAutoLine(), Text.PadTypeDualSenseLine(), Text.PadTypeDualShock4Line(), Text.PadTypeXboxSeriesLine(), Text.PadTypeXboxLine()], choice.Options.Select(o => o.Line));
		Assert.Null(choice.Selected!.Type);

		choice.Selected = choice.Options[2];
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.ForcedPadType);
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.CurrentPadType);
		// A cleared selection (null while the list is rebuilt) leaves the value alone.
		choice.Selected = null;
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.ForcedPadType);
		choice.Selected = choice.Options[0];
		Assert.Null(fake.Games.ForcedPadType);
	}

	[AvaloniaFact]
	public void Saved_WithTheGlobalSettings_OlderFilesAreAutomatic()
	{
		var store = new MemorySettingsStore(new AppSettings());
		(MainWindowViewModel vm, _) = Create(store);
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options.Single(o => o.Type == VirtualPadType.Xbox360);
		vm.Settings.SaveNow();
		Assert.Equal(VirtualPadType.Xbox360, store.Saved!.ForcedPadType);

		(MainWindowViewModel again, FakeControllerService fake) = Create(store);
		Assert.Equal(VirtualPadType.Xbox360, fake.Games.ForcedPadType);
		Assert.Equal(VirtualPadType.Xbox360, again.Settings.PadType.Selected!.Type);

		// The file on disk: the value by name; a file from an older version has no key and loads as Automatic.
		JsonSettingsStore json = new(Path.Combine(folder, "settings.json"));
		json.Save(new AppSettings().WithGames(GameMemory.Default with { ForcedPad = VirtualPadType.DualShock4 }));
		Assert.Contains("\"ForcedPadType\": \"DualShock4\"", File.ReadAllText(json.Path));
		Assert.Equal(VirtualPadType.DualShock4, json.Load()!.ToGameMemory(GameCatalog.Empty).ForcedPad);
		File.WriteAllText(json.Path, "{ \"GameHapticsLevel\": 120 }");
		Assert.Null(json.Load()!.ForcedPadType);
		Assert.Null(json.Load()!.ToGameMemory(GameCatalog.Empty).ForcedPad);
		Assert.Null(new AppSettings().WithGames(GameMemory.Default).ForcedPadType);
	}

	// A game's own choice wins over the default type; Automatic on a game follows the default type.
	[AvaloniaFact]
	public void Default_LeavesTheGamesOwnChoice_AutomaticFollowsIt()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		NoGame(fake);
		List<GameItemViewModel> items = vm.SupportedGames.Games.Where(g => g.PadType != null).Take(2).ToList();
		GameItemViewModel chosen = items[0], auto = items[1];
		Assert.Null(chosen.PadType!.Selected!.Type);
		// Automatic names what it gives the game: a listed game, DualSense.
		Assert.Equal(Text.PadChoiceAutoFor("DualSense"), chosen.PadType.Selected.Name);
		Assert.EndsWith(Text.PadTypeAutoLine(), chosen.PadType.Tip);
		chosen.PadType.Selected = chosen.PadType.Options.Single(o => o.Type == VirtualPadType.Xbox360);
		Assert.Equal(VirtualPadType.Xbox360, fake.Games.PadChoiceFor(chosen.Key));
		Assert.EndsWith(Text.PadTypeXboxLine(), chosen.PadType.Tip);

		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[2];
		Settle();
		Assert.Equal(VirtualPadType.Xbox360, chosen.PadType.Selected!.Type);
		Assert.Equal(VirtualPadType.Xbox360, fake.Games.PadTypeFor(chosen.Key));
		// The default type is for games we know nothing about: a listed game on Automatic keeps DualSense.
		Assert.Null(auto.PadType!.Selected!.Type);
		Assert.Equal(VirtualPadType.DualSense, fake.Games.PadTypeFor(auto.Key));

		// Back to Automatic: DualSense again, and the line names the default type's part.
		chosen.PadType.Selected = chosen.PadType.Options[0];
		Assert.Null(fake.Games.PadChoiceFor(chosen.Key));
		Assert.Equal(VirtualPadType.DualSense, fake.Games.PadTypeFor(chosen.Key));
		Assert.EndsWith(Text.PadTypeAutoDefaultLine(), chosen.PadType.Tip);
		Assert.Equal(Text.PadChoiceAutoFor("DualSense"), chosen.PadType.Options[0].Name);
	}

	[AvaloniaFact]
	public void Forced_ShowsWhatGamesGet_OnTheOverviewAndInTheSidebar()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		NoGame(fake);
		vm.Overview.ToggleBridgeCommand.Execute(null);
		Settle();
		Assert.Equal(Text.HealthOn(), vm.Overview.VirtualStateText);
		Assert.Equal(Text.GameSees("DualSense"), vm.Overview.GameSeesText);
		Assert.False(vm.Overview.HasPadLoss);
		Assert.True(vm.Overview.ShowsPadStatus);

		var changed = new List<string?>();
		vm.Overview.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[2];
		Settle();
		Assert.Contains(nameof(OverviewViewModel.GameSeesText), changed);
		Assert.Equal(Text.HealthOn(), vm.Overview.VirtualStateText);
		Assert.Equal(Text.GameSees("DualShock 4"), vm.Overview.GameSeesText);
		Assert.True(vm.Overview.HasPadLoss);
		Assert.Equal(Text.PadTypeDualShock4Line(), vm.Overview.PadTypeTip);
		WithLanguage(Language.Turkish, () =>
			Assert.Equal("Titreşim ve ışık var; adaptif tetik, oyun haptiği ve hoparlör yok.", vm.Overview.PadLossLine));
		WithLanguage(Language.English, () =>
			Assert.Equal("Rumble and light work; no adaptive triggers, game haptics or speaker.", vm.Overview.PadLossLine));

		vm.Settings.PadType.Selected = vm.Settings.PadType.Options.Single(o => o.Type == VirtualPadType.Xbox360);
		Settle();
		Assert.Equal(Text.GameSees("Xbox 360"), vm.Overview.GameSeesText);
		WithLanguage(Language.Turkish, () =>
			Assert.Equal("Sadece titreşim var; dokunmatik yüzey ve hareket sensörü de yok.", vm.Overview.PadLossLine));

		// Forced DualSense: nothing is missing.
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[1];
		Settle();
		Assert.Equal(Text.GameSees("DualSense"), vm.Overview.GameSeesText);
		Assert.False(vm.Overview.HasPadLoss);
	}

	// A type chosen while the game runs waits: the Overview and the sidebar show it with "Şimdi Uygula", which puts it in.
	[AvaloniaFact]
	public void Pending_ShowsOnTheOverview_ApplyNowPutsItIn()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.Overview.ToggleBridgeCommand.Execute(null);
		Settle();
		string key = fake.Games.ActiveGame!.Key;
		Assert.False(vm.Overview.HasPendingPadType);
		fake.Games.SetPadType(key, VirtualPadType.Xbox360);
		Settle();
		Assert.True(vm.Overview.HasPendingPadType);
		Assert.Equal(Text.GameSees("DualSense"), vm.Overview.GameSeesText);
		WithLanguage(Language.Turkish, () => Assert.Equal("Xbox 360 · Oyunu Yeniden Başlatınca Geçerli Olur", vm.Overview.PendingPadLine));
		Assert.Equal(Text.PadTypeDeferredHint(), vm.Overview.PendingPadTip);
		vm.Overview.ApplyPadTypeNowCommand.Execute(null);
		Settle();
		Assert.False(vm.Overview.HasPendingPadType);
		Assert.Equal(Text.GameSees("Xbox 360"), vm.Overview.GameSeesText);
	}

	private static void WithLanguage(Language language, Action action)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = language;
			action();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public void LowPower_LeavesTheForcedTypeAlone()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		NoGame(fake);
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[2];
		vm.Overview.Quick.LowPowerOn = true;
		Settle();
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.ForcedPadType);
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.CurrentPadType);
		vm.Overview.Quick.LowPowerOn = false;
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.CurrentPadType);
	}

	// The default type changed while the virtual controller runs and no game does: one toast (not one per controller).
	[AvaloniaFact]
	public void Toast_DefaultType_OncePerChange()
	{
		long now = 1_000_000;
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null, FullScreen: () => false, Clock: () => now));
		NoGame(fake);
		vm.Overview.ToggleBridgeCommand.Execute(null);
		Settle();
		List<AppNotification> raised = [];
		vm.Notifications.Raised += raised.Add;

		now += (long)NotificationCenter.RepeatCooldown.TotalMilliseconds + 1000;
		fake.Games.SetForcedPadType(VirtualPadType.DualShock4);
		fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, nameof(VirtualPadType.DualShock4)));
		fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, nameof(VirtualPadType.DualShock4)));
		AppNotification toast = Assert.Single(raised);
		Assert.Equal(Text.PadModeDualShock4Title(), toast.Title);
		Assert.Equal(Text.PadTypeDefaultHint(), toast.Body);
		Assert.Equal(AppPage.SupportedGames, toast.Page);
	}

	[Fact]
	public void Texts_PlainAndInBothLanguages()
	{
		Assert.Equal("Kumanda Türü", Text.PadTypeLabel(Language.Turkish));
		Assert.Equal("Otomatik", Text.PadChoiceAuto(Language.Turkish));
		Assert.Equal("Automatic", Text.PadChoiceAuto(Language.English));
		Assert.Equal("Varsayılan Kumanda Türü", Text.PadTypeDefaultLabel(Language.Turkish));
		Assert.Equal("Automatic (Per Game)", Text.PadChoiceAutoPerGame(Language.English));
		Assert.Equal("Hakkında bilgi olmayan oyunlarda kullanılır.", Text.PadTypeDefaultHint(Language.Turkish));
		Assert.Equal("Used for games we know nothing about.", Text.PadTypeDefaultHint(Language.English));
		foreach (Language l in new[] { Language.Turkish, Language.English })
		{
			foreach (string line in new[] { Text.PadTypeAutoLine(l), Text.PadTypeAutoDefaultLine(l), Text.PadChoiceAutoLine(l), Text.PadTypeDefaultHint(l), Text.PadTypeGameHint(l),
				Text.PadChoiceAutoFor("Xbox 360", l) })
			{
				Assert.False(string.IsNullOrWhiteSpace(line));
				Assert.DoesNotContain("tepsi", line, StringComparison.OrdinalIgnoreCase);
			}
		}
	}

	// Overview and Supported Games with DualShock 4 forced and the virtual controller running, both languages, default and
	// large window; PNGs to docs/screens only with KOPRU_RENDER_SCREENS=1. The new rows stay inside their cards.
	[AvaloniaFact]
	public void RenderOverviewAndSupportedGames_Forced()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, FakeControllerService fake) = Create();
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				vm.SelectedNav = vm.NavItems[0];
				vm.Overview.ToggleBridgeCommand.Execute(null);
				vm.Settings.PadType.Selected = vm.Settings.PadType.Options[2];
				fake.RaiseStateChanged();
				Settle();
				foreach ((double w, double h, string size) in new[] { (1100.0, 700.0, "default"), (1680.0, 1050.0, "large") })
				{
					window.Width = w;
					window.Height = h;
					vm.SelectedNav = vm.NavItems[0];
					Settle();
					OverviewView overview = window.GetLogicalDescendants().OfType<OverviewView>().Single();
					ComboBox picker = overview.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "QuickPadTypePicker");
					Border card = overview.GetLogicalDescendants().OfType<Border>().First(b => b.Classes.Contains("panel") && b.GetLogicalDescendants().Contains(picker));
					ComboBox profile = overview.GetLogicalDescendants().OfType<ComboBox>().Last();
					Point? last = profile.TranslatePoint(new Point(0, profile.Bounds.Height), card);
					Assert.True(last!.Value.Y <= card.Bounds.Height - 8, $"{code} {size}: the profile row is clipped");
					Point? right = picker.TranslatePoint(new Point(picker.Bounds.Width, 0), card);
					Assert.True(right!.Value.X <= card.Bounds.Width, $"{code} {size}: the controller type picker leaves the card");
					Assert.Equal("DualShock 4", ((PadChoiceOption)picker.SelectedItem!).Name);
					Save(window, writeFiles, outDir, $"pad-type-overview-{size}-{code}.png");

					vm.SelectedNav = vm.NavItems.First(n => n.Page is SupportedGamesViewModel);
					Settle();
					SupportedGamesView games = window.GetLogicalDescendants().OfType<SupportedGamesView>().Single();
					ComboBox choice = games.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "PadTypeChoicePicker");
					Assert.True(choice.IsVisible && choice.Bounds.Width > 0);
					Assert.Contains(games.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Text.PadTypeDefaultLabel() && t.IsVisible);
					// The rows' pickers stay on with a default type set.
					Assert.All(games.GetLogicalDescendants().OfType<ComboBox>().Where(c => c.DataContext is GameItemViewModel), c => Assert.True(c.IsEffectivelyEnabled));
					Save(window, writeFiles, outDir, $"pad-type-games-{size}-{code}.png");
				}
				window.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}

	private static void Save(Window window, bool writeFiles, string outDir, string name)
	{
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		if (writeFiles)
		{
			Directory.CreateDirectory(outDir);
			frame!.Save(Path.Combine(outDir, name));
		}
	}

	private static string RepoRoot()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "screens")))
		{
			dir = dir.Parent;
		}
		return dir?.FullName ?? AppContext.BaseDirectory;
	}
}
