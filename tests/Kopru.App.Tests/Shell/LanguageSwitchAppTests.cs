// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Input;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

// Switching the language while the app runs (Settings > General > Language) keeps every picker and segmented switch on
// the same choice, now named in the new language: the option lists stay the same objects and only their names change.
public sealed class LanguageSwitchAppTests
{
	// One shown page: how to open it.
	private sealed record PageStop(string Name, Action<MainWindowViewModel> Open);

	private static readonly PageStop[] Stops =
	[
		new("Overview", vm => vm.SelectedNav = vm.NavItems[0]),
		new("Games", vm => vm.Navigate(vm.SupportedGames)),
		new("Settings General", vm => { vm.Navigate(vm.SettingsPage); vm.SettingsPage.ShowTab(SettingsViewModel.GeneralTab); }),
		new("Settings Controllers", vm => { vm.Navigate(vm.SettingsPage); vm.SettingsPage.ShowTab(SettingsViewModel.ControllersTab); }),
		new("Settings Audio", vm => { vm.Navigate(vm.SettingsPage); vm.SettingsPage.ShowTab(SettingsViewModel.AudioTab); }),
		new("Settings Shortcuts", vm => { vm.Navigate(vm.SettingsPage); vm.SettingsPage.ShowTab(SettingsViewModel.ShortcutsTab); }),
		new("Settings Mouse Mode", vm => { vm.Navigate(vm.SettingsPage); vm.SettingsPage.ShowTab(SettingsViewModel.MouseTab); }),
		new("Controller Remap", vm => { vm.Navigate(vm.Controller); vm.Controller.ShowTab(ControllerPageViewModel.RemapTab); }),
		new("Light", vm => vm.Navigate(vm.Light)),
	];

	// What a picker shows: its selected index, whether something is selected and the names it shows.
	private sealed record Shown(string Where, int Index, bool HasSelection, IReadOnlyList<string> Texts);

	[AvaloniaFact]
	public void LanguageSwitch_KeepsEveryChoice_InTheNewLanguage()
	{
		Language? before = Localizer.Chosen;
		Language system = Localizer.FromCulture(System.Globalization.CultureInfo.CurrentUICulture);
		try
		{
			foreach ((Language? from, Language? to) in new (Language?, Language?)[]
			{
				(Language.Turkish, Language.English), (Language.English, Language.Turkish),
				// Follow System, both ways: from the other language it changes the texts, from the system's own it does not.
				(system == Language.Turkish ? Language.English : Language.Turkish, null), (system, null),
				(null, system == Language.Turkish ? Language.English : Language.Turkish),
			})
			{
				Switch(from, to);
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Switch(Language? from, Language? to)
	{
		string label = $"{Code(from)} -> {Code(to)}";
		Localizer.Chosen = from;
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings
		{
			GameListCheckedAt = DateTimeOffset.Now,
			Language = from == null ? null : Localizer.Code(from.Value)
		})));
		// Choices away from the defaults, a profile of the user's own (the default one stays active).
		ControllerProfile racing = vm.Settings.Profiles.Create("Racing", vm.Settings.CaptureValues())!;
		vm.Overview.Quick.HapticsIndex = 0;
		vm.Overview.Quick.MicButtonModeIndex = 1;
		vm.Settings.Remap = vm.Settings.Remap.With(DsButton.Cross, DsButton.Circle);
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[2];
		var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
		window.Show();
		try
		{
			GameItemViewModel row = vm.SupportedGames.Games.First(g => g.PadType != null);
			row.PadType!.Selected = row.PadType.Options[3];
			row.Profile!.Selected = row.Profile.Options.Single(o => o.Id == racing.Id);
			Dictionary<string, List<Shown>> beforeSwitch = Stops.ToDictionary(s => s.Name, s => Read(window, vm, s));
			Assert.All(beforeSwitch, p => Assert.NotEmpty(p.Value));
			Language old = Localizer.Current;

			// The switch, as the user makes it on Settings > General.
			Stops[2].Open(vm);
			FitCheck.Settle(window);
			vm.SettingsPage.SelectedLanguage = vm.SettingsPage.Languages.Single(o => o.Language == to);
			FitCheck.Settle(window);
			Assert.Equal(to, Localizer.Chosen);
			Language now = Localizer.Current;

			foreach (PageStop stop in Stops)
			{
				List<Shown> after = Read(window, vm, stop);
				List<Shown> was = beforeSwitch[stop.Name];
				Assert.True(was.Count == after.Count, $"{label} {stop.Name}: {was.Count} pickers before, {after.Count} after");
				for (int i = 0; i < was.Count; i++)
				{
					Assert.True(!was[i].HasSelection || after[i].HasSelection, $"{label} {after[i].Where}: the selection is gone");
					if (!after[i].Where.Contains("Language"))
					{
						Assert.True(was[i].Index == after[i].Index, $"{label} {after[i].Where}: was {was[i].Index}, now {after[i].Index}");
					}
				}
			}

			// The texts are in the new language: the page labels, the tabs, the default profile's name; the user's profile
			// keeps its own.
			Stops[0].Open(vm);
			FitCheck.Settle(window);
			OverviewView overview = window.GetVisualDescendants().OfType<OverviewView>().Single();
			Assert.Contains(Visible(overview), t => t == Text.HapticsStrength(now));
			ComboBox profile = overview.GetVisualDescendants().OfType<ComboBox>().Single(c => c.ItemsSource is IEnumerable<ProfileOption>);
			Assert.Equal(Text.ProfileDefault(now), Assert.IsType<ProfileOption>(profile.SelectedItem).Name);
			Assert.Contains(Text.ProfileDefault(now), Visible(profile));
			Assert.Contains("Racing", ((IEnumerable<ProfileOption>)profile.ItemsSource!).Select(o => o.Name));
			Assert.Contains(Text.StepLight(now), Visible(overview));
			if (old != now)
			{
				Assert.DoesNotContain(Text.StepLight(old), Visible(overview));
				Assert.DoesNotContain(Text.ProfileDefault(old), Visible(overview));
			}
			Stops[2].Open(vm);
			FitCheck.Settle(window);
			SettingsView settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
			Assert.Contains(Text.LanguageLabel(now), Visible(settings));
			Assert.Contains(Text.General(now), Visible(settings));
			ComboBox language = settings.GetVisualDescendants().OfType<ComboBox>().Single(c => c.ItemsSource is IEnumerable<LanguageOption>);
			Assert.Equal(to, Assert.IsType<LanguageOption>(language.SelectedItem).Language);
			Stops[7].Open(vm);
			FitCheck.Settle(window);
			RemapView remap = window.GetVisualDescendants().OfType<RemapView>().Single();
			Assert.Contains(Text.ButtonName(DsButton.Circle, now), Visible(remap));
			Assert.Contains(Text.ButtonRemapping(now), Visible(remap));
			// The settings themselves never changed.
			Assert.Equal(0, vm.Overview.Quick.HapticsIndex);
			Assert.Equal(1, vm.Overview.Quick.MicButtonModeIndex);
			Assert.Equal(DsButton.Circle, vm.Settings.Remap.TargetOf(DsButton.Cross));
			Assert.Equal(ProfileBook.DefaultId, vm.Settings.Profiles.Active.Id);
			Assert.Equal(row.PadType.Options[3], row.PadType.Selected);
		}
		finally
		{
			window.Close();
		}
	}

	// The pickers (combo boxes) and choice lists (segmented switches, the Light page's lists) the page shows, in order.
	private static List<Shown> Read(Window window, MainWindowViewModel vm, PageStop stop)
	{
		stop.Open(vm);
		FitCheck.Settle(window);
		Control page = window.GetVisualDescendants().OfType<PageHost>().Single().Children.Single(c => c.IsVisible);
		var shown = new List<Shown>();
		int n = 0;
		foreach (SelectingItemsControl picker in page.GetVisualDescendants().OfType<SelectingItemsControl>().Where(c => c is ComboBox or ListBox && c.IsEffectivelyVisible))
		{
			string where = $"{stop.Name} #{n++} {picker.GetType().Name} {picker.Name} {(picker.ItemsSource is IEnumerable<LanguageOption> ? "Language" : "")}";
			List<string> texts = Visible(picker).ToList();
			if (picker.SelectedItem is { } item and not ColourPreset)
			{
				// It shows its choice under the choice's current name.
				string name = NameOf(item);
				Assert.True(texts.Contains(name), $"{where}: shows [{string.Join(", ", texts)}], not \"{name}\"");
				Assert.Contains(item, picker.ItemsSource!.Cast<object>());
			}
			if (picker is ListBox && picker.ItemsSource is IEnumerable items)
			{
				foreach (object o in items)
				{
					string name = NameOf(o);
					if (name.Length > 0 && o is not ColourPreset)
					{
						Assert.True(texts.Contains(name), $"{where}: shows [{string.Join(", ", texts)}], not \"{name}\"");
					}
				}
			}
			shown.Add(new Shown(where, picker.SelectedIndex, picker.SelectedItem != null, texts));
		}
		return shown;
	}

	private static string NameOf(object item) =>
		item.GetType().GetProperty("Name")?.GetValue(item) as string ?? item.ToString() ?? string.Empty;

	private static IEnumerable<string> Visible(Visual root) =>
		root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!);

	private static string Code(Language? language) => language == null ? "system" : Localizer.Code(language.Value);
}
