// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

// The Games page shows what Automatic gives each game ("Otomatik (Xbox 360)"), in both languages, and follows the store
// categories, the default type and what was learned.
public sealed class AutoPadTypeAppTests
{
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

	private static async Task<(MainWindowViewModel Vm, FakeControllerService Service)> OpenAsync()
	{
		var service = new FakeControllerService(animate: false);
		service.Watcher.Exit("Cyberpunk2077.exe");
		service.Watcher.Exe = null;
		service.Games.Tick();
		// The found Steam games of the fake scan (9009, 9020) get no PlayStation support unless a test gives them one.
		service.Games.PlayStation = new PlayStationSupport([]);
		var vm = new MainWindowViewModel(service,
			new AppEnvironment(new MemorySettingsStore(new AppSettings()), InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
		await vm.SupportedGames.ScanTask;
		vm.SupportedGames.ViewIndex = 2;
		return (vm, service);
	}

	[Fact]
	public void Texts_TurkishAndEnglish()
	{
		Assert.Equal("Otomatik (Xbox 360)", Text.PadChoiceAutoFor("Xbox 360", Language.Turkish));
		Assert.Equal("Automatic (Xbox 360)", Text.PadChoiceAutoFor("Xbox 360", Language.English));
		Assert.Equal("Otomatik (DualShock 4)", Text.PadChoiceAutoFor("DualShock 4", Language.Turkish));
		Assert.Equal("Oyuna En Uygun Kumandayı Seçer", Text.PadTypeAutoLine(Language.Turkish));
		Assert.Equal("Picks The Best Controller For The Game", Text.PadTypeAutoLine(Language.English));
		Assert.Contains("Otomatik", Text.PadTypeGameHint(Language.Turkish));
		Assert.Contains("Automatic", Text.PadTypeGameHint(Language.English));
		Assert.DoesNotContain("DualSense", Text.PadChoiceAutoLine(Language.English));
	}

	[AvaloniaFact]
	public async Task UnsupportedRows_NameWhatAutomaticGives()
	{
		(MainWindowViewModel vm, FakeControllerService service) = await OpenAsync();
		GamePadTypePicker racer = vm.SupportedGames.Games.Single(g => g.Key == "steam:9009").PadType!;
		GamePadTypePicker epic = vm.SupportedGames.Games.Single(g => g.Name == "Hollow Peak").PadType!;
		Assert.Null(racer.Selected!.Type);
		// Nothing known: Xbox 360 (the Xbox Series pad only for Xbox trigger rumble games, with its setting on).
		Assert.Equal(VirtualPadType.Xbox360, racer.AutomaticType);
		WithLanguage(Language.Turkish, () =>
		{
			Assert.Equal("Otomatik (Xbox 360)", racer.Selected.Name);
			Assert.Equal("Otomatik (Xbox 360)", epic.Options[0].Name);
			Assert.Equal("Oyuna En Uygun Kumandayı Seçer", racer.Options[0].Line);
		});
		WithLanguage(Language.English, () => Assert.Equal("Automatic (Xbox 360)", racer.Options[0].Name));
		service.Games.XboxTriggerRumbleOn = true;
		WithLanguage(Language.English, () => Assert.Equal("Automatic (Xbox 360)", racer.Options[0].Name));
		service.Games.XboxTriggerRumbleOn = false;

		// The store says DualShock 4 / DualSense.
		service.Games.PlayStation = new PlayStationSupport([new(9009, PlayStationPad.DualShock4)]);
		WithLanguage(Language.English, () => Assert.Equal("Automatic (DualShock 4)", racer.Options[0].Name));
		service.Games.PlayStation = new PlayStationSupport([new(9009, PlayStationPad.DualSense)]);
		WithLanguage(Language.English, () => Assert.Equal("Automatic (DualSense)", racer.Options[0].Name));

		// A default type is for games we know nothing about: the store data still wins, and the line says so.
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options.Single(o => o.Type == VirtualPadType.DualShock4);
		WithLanguage(Language.English, () =>
		{
			Assert.Equal("Automatic (DualSense)", racer.Options[0].Name);
			Assert.Equal(Text.PadTypeAutoDefaultLine(Language.English), racer.Options[0].Line);
		});
		service.Games.PlayStation = new PlayStationSupport([]);
		WithLanguage(Language.English, () => Assert.Equal("Automatic (DualShock 4)", racer.Options[0].Name));
	}

	// A found Steam game without PlayStation support starting (no game ran before) gets Xbox 360 at once; the game line reads it.
	[AvaloniaFact]
	public async Task AutomaticSteamGame_Starting_GetsXbox360AtOnce()
	{
		(MainWindowViewModel vm, FakeControllerService service) = await OpenAsync();
		Assert.Equal(VirtualPadType.DualSense, service.Games.CurrentPadType);
		service.Watcher.Source.Show("Racer.exe", FakeFoundGames.RacerFolder + @"\bin\Racer.exe");
		service.Games.Tick();
		Assert.Equal("steam:9009", service.Games.ActiveGame!.Key);
		Assert.Equal(VirtualPadType.Xbox360, service.Games.CurrentPadType);
		Assert.Equal(PadTypeSource.Automatic, service.Games.PadStatus.Source);
		Assert.Null(service.Games.PendingPadType);
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.EndsWith("Xbox 360", vm.SupportedGames.CurrentGameText);
	}

	// The longest names ("Automatic (DualShock 4)", "Otomatik (DualShock 4)") fit the row pickers at the smallest window.
	[AvaloniaFact]
	public async Task RowPickers_FitTheLongestAutomaticName_AtTheSmallestWindow()
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach (Language language in new[] { Language.Turkish, Language.English })
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, FakeControllerService service) = await OpenAsync();
				service.Games.PlayStation = new PlayStationSupport([new(9009, PlayStationPad.DualShock4), new(9020, PlayStationPad.DualShock4)]);
				var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
				window.Show();
				vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
				FitCheck.Settle(window);
				List<ComboBox> pickers = window.GetVisualDescendants().OfType<ComboBox>()
					.Where(b => b.Classes.Contains("rowPicker") && b.DataContext is GameItemViewModel { Key: "steam:9009" or "steam:9020" } && b.IsEffectivelyVisible).ToList();
				Assert.NotEmpty(pickers);
				Assert.Contains(pickers, p => p.SelectedItem is PadTypeOption { Type: null } o && o.Name == Text.PadChoiceAutoFor("DualShock 4"));
				Assert.Empty(pickers.SelectMany(FitCheck.CutText));
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}
}
