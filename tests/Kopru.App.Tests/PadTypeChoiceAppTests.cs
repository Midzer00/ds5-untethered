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

// The global controller type ("Kumanda Türü") in Quick Settings and on the Supported Games page: one shared choice,
// saved with the settings, the per-game pickers off while a type is forced, what games get shown on the Overview and in
// the sidebar, and the existing pad type toast.
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

	[AvaloniaFact]
	public void OneSharedChoice_AutomaticByDefault_FourOptionsInOrder()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		PadTypeChoiceViewModel choice = vm.Overview.Quick.PadType;
		Assert.Same(vm.Settings.PadType, choice);
		Assert.Equal([null, VirtualPadType.DualSense, VirtualPadType.DualShock4, VirtualPadType.Xbox360], choice.Options.Select(o => o.Type));
		Assert.Equal(["Otomatik", "DualSense", "DualShock 4", "Xbox 360"], new[] { Text.PadChoiceAuto(Language.Turkish) }.Concat(choice.Options.Skip(1).Select(o => o.Name)));
		Assert.Null(choice.Selected!.Type);
		Assert.False(choice.IsForced);

		choice.Selected = choice.Options[2];
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.ForcedPadType);
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.CurrentPadType);
		Assert.True(vm.Settings.PadType.IsForced);
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
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[3];
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

	[AvaloniaFact]
	public void Forced_TurnsThePerGamePickersOff_AndKeepsTheirChoices()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		GameItemViewModel item = vm.SupportedGames.Games.First(g => g.PadType != null);
		item.PadType!.Selected = item.PadType.Options.Single(o => o.Type == VirtualPadType.Xbox360);
		Assert.True(item.PadType.IsEnabled);
		int raised = 0;
		item.PadType.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(GamePadTypePicker.IsEnabled) ? 1 : 0;

		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[1];
		Settle();
		Assert.False(item.PadType.IsEnabled);
		Assert.True(raised > 0);
		Assert.Equal(VirtualPadType.Xbox360, item.PadType.Selected!.Type);
		Assert.Equal(VirtualPadType.Xbox360, fake.Games.PadTypeFor(item.Key));

		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[0];
		Settle();
		Assert.True(item.PadType.IsEnabled);
	}

	[AvaloniaFact]
	public void Forced_ShowsWhatGamesGet_OnTheOverviewAndInTheSidebar()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.Overview.ToggleBridgeCommand.Execute(null);
		Settle();
		Assert.Equal(Text.HealthOn(), vm.Overview.VirtualStateText);
		Assert.Equal(Text.SupportedModeHelper(), vm.Overview.SupportedModeHelper);

		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[2];
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(Text.HealthOn() + " · DS4", vm.Overview.VirtualStateText);
		Assert.Contains(Text.PadShownAsDualShock4(), vm.Overview.VirtualStatusTip);
		Assert.Equal(Text.PadShownAsDualShock4(), vm.Overview.SupportedModeHelper);
		Assert.Contains(Text.PadShownAsDualShock4(), vm.Overview.SupportedModeTip);

		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[3];
		Settle();
		Assert.Equal(Text.HealthOn() + " · Xbox", vm.Overview.VirtualStateText);
		Assert.Equal(Text.PadShownAsXbox(), vm.Overview.SupportedModeHelper);
		Assert.EndsWith(" · Xbox", vm.Overview.ModeValue);

		// Forced DualSense looks like today's DualSense.
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[1];
		Settle();
		Assert.Equal(Text.HealthOn(), vm.Overview.VirtualStateText);
		Assert.Equal(Text.SupportedModeHelper(), vm.Overview.SupportedModeHelper);
	}

	[AvaloniaFact]
	public void LowPower_LeavesTheForcedTypeAlone()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[2];
		vm.Overview.Quick.LowPowerOn = true;
		Settle();
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.ForcedPadType);
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.CurrentPadType);
		vm.Overview.Quick.LowPowerOn = false;
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.CurrentPadType);
	}

	[AvaloniaFact]
	public void Toast_ReusesThePadTypeNotice_SaysByHand_OncePerChangeForEverySlot()
	{
		long now = 1_000_000;
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null, FullScreen: () => false, Clock: () => now));
		Settle();
		List<AppNotification> raised = [];
		vm.Notifications.Raised += raised.Add;

		// The game's own choice: the old "for this game" text.
		fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, nameof(VirtualPadType.Xbox360)));
		Assert.Equal(Text.PadModeXboxBody(), raised[^1].Body);

		// Forced: the same toast, saying it was chosen by hand; two controllers swapping give one toast.
		raised.Clear();
		now += (long)NotificationCenter.RepeatCooldown.TotalMilliseconds + 1000;
		fake.Games.SetForcedPadType(VirtualPadType.DualShock4);
		fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, nameof(VirtualPadType.DualShock4)));
		fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, nameof(VirtualPadType.DualShock4)));
		AppNotification toast = Assert.Single(raised);
		Assert.Equal(Text.PadModeDualShock4Title(), toast.Title);
		Assert.Equal(Text.PadForcedDualShock4Body(), toast.Body);
		Assert.Equal(AppPage.SupportedGames, toast.Page);

		Assert.Equal(Text.PadForcedXboxBody(), NotificationCenter.PadTypeNotice(nameof(VirtualPadType.Xbox360), forced: true).Body);
		Assert.Equal(Text.PadForcedDualSenseBody(), NotificationCenter.PadTypeNotice(nameof(VirtualPadType.DualSense), forced: true).Body);
	}

	[Fact]
	public void Texts_PlainAndInBothLanguages()
	{
		Assert.Equal("Kumanda Türü", Text.PadTypeLabel(Language.Turkish));
		Assert.Equal("Otomatik", Text.PadChoiceAuto(Language.Turkish));
		Assert.Equal("Automatic", Text.PadChoiceAuto(Language.English));
		Assert.Equal("Kumanda türü elle seçili; oyun başına seçimler Otomatik'te geçerli.", Text.PadForcedNote(Language.Turkish));
		foreach (Language l in new[] { Language.Turkish, Language.English })
		{
			string tip = Text.PadChoiceTip(l);
			Assert.Contains("DualShock 4", tip);
			Assert.Contains("Xbox 360", tip);
		}
		Assert.DoesNotContain("tepsi", Text.PadChoiceTip(Language.Turkish), StringComparison.OrdinalIgnoreCase);
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
					Assert.Contains(games.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Text.PadForcedNote() && t.IsVisible);
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
