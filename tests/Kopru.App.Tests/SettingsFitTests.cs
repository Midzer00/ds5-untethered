// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Every Settings tab fits the window: at every size in the matrix (FitCheck.Matrix) and in both languages, through the
// window's own scale (UiScale), each tab shows all of its cards without vertical page scrolling, nothing is clipped, cut off
// or outside the page column, the cards are apart, and the controls in a card are apart. The content is a full case: two
// remembered controllers (one renamed), the connected one's details, three profiles, the microphone level row and a tool
// result.
public sealed class SettingsFitTests
{
	private static readonly string[] TabKeys = ["general", "controllers", "audio", "shortcuts", "mouse", "notifications"];

	[AvaloniaFact]
	public void EveryTab_FitsWithoutScrolling_AtEverySize_InBothLanguages()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		bool writeDocs = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string? fitDir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, FakeControllerService _) = FullCase(code);
				Assert.Equal(TabKeys.Length, vm.SettingsPage.Tabs.Count);
				foreach ((double width, double height) in FitCheck.Matrix)
				{
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.Navigate(vm.SettingsPage);
					for (int tab = 0; tab < TabKeys.Length; tab++)
					{
						vm.SettingsPage.SelectedTab = tab;
						FitCheck.Settle(window);
						Assert.Equal(UiScale.Factor(width, height), window.ContentScale, 3);
						problems.AddRange(Check(window).Select(p => $"{code} {width}x{height} {TabKeys[tab]}: {p}"));
						// The General tab at the default size is the page's own picture (AppTests: settings-*.png).
						bool docs = writeDocs && tab != SettingsViewModel.GeneralTab && ((width, height) == (1100, 700) || (tab is SettingsViewModel.AudioTab or SettingsViewModel.MouseTab && width is 960 or 1920 or 2560 && height != 1440));
						if (fitDir != null || docs)
						{
							Thread.Sleep(320);
							FitCheck.Settle(window);
							using var frame = window.CaptureRenderedFrame();
							if (fitDir != null)
							{
								Directory.CreateDirectory(fitDir);
								frame!.Save(Path.Combine(fitDir, $"settings-{TabKeys[tab]}-{width}x{height}-{code}.png"));
							}
							if (docs)
							{
								string size = (width, height) == (1100, 700) ? "" : $"-{width}x{height}";
								frame!.Save(Path.Combine(AppTests.RepoRoot(), "docs", "screens", $"settings-{TabKeys[tab]}{size}-{code}.png"));
							}
						}
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// The tab switch: one tab per group, Title Case names in both languages, the selected tab kept while the app runs.
	[AvaloniaFact]
	public void Tabs_AreNamed_AndTheSelectionIsKept()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		Assert.Equal([Text.General(), Text.ControllersSection(), Text.SettingsTabAudio(), Text.SettingsTabShortcuts(), Text.DesktopModeSection(),
			Text.GroupNotifications()], vm.SettingsPage.Tabs.Select(t => t.Name));
		Assert.True(vm.SettingsPage.IsGeneralTab);
		vm.Navigate(vm.SettingsPage);
		vm.SettingsPage.SelectedTab = SettingsViewModel.AudioTab;
		vm.Navigate(vm.Overview);
		vm.Navigate(vm.SettingsPage);
		Assert.True(vm.SettingsPage.IsAudioTab);
		// Nothing selected (-1) and a tab that does not exist are ignored; ShowTab opens General for one that does not exist.
		vm.SettingsPage.SelectedTab = -1;
		vm.SettingsPage.SelectedTab = 9;
		Assert.True(vm.SettingsPage.IsAudioTab);
		vm.SettingsPage.ShowTab(9);
		Assert.True(vm.SettingsPage.IsGeneralTab);

		// Links land on the tab they mean, whatever tab was shown last: the Overview's "Advanced Settings" and the adapter test
		// fix on General, the shortcuts tile on Shortcuts.
		vm.SettingsPage.SelectedTab = SettingsViewModel.NotificationsTab;
		vm.Overview.Quick.AdvancedCommand.Execute(null);
		Assert.Same(vm.SettingsPage, vm.CurrentPage);
		Assert.True(vm.SettingsPage.IsGeneralTab);
		vm.Overview.Quick.EditShortcutsCommand.Execute(null);
		Assert.True(vm.SettingsPage.IsShortcutsTab);
		vm.HandleFix(Kopru.Core.Health.FixAction.RunAdapterTest);
		Assert.True(vm.SettingsPage.IsGeneralTab);
	}

	// The Mouse Mode tab with every button on the action with the longest name still fits (pickers show their whole
	// text, two pickers per line), at every size and in both languages.
	[AvaloniaFact]
	public void MouseTab_FitsWithTheLongestActionNames()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, FakeControllerService _) = FullCase(code);
				// The 16 longest names, one per button; a second pass swaps each line's two, so every name is tried in both columns.
				IReadOnlyList<Kopru.Core.Input.DsButton> buttons = Kopru.Core.Input.DesktopButtonMap.Buttons;
				List<Kopru.Core.Input.MouseAction> names = Kopru.Core.Input.DesktopButtonMap.Actions
					.OrderByDescending(a => Text.DesktopActionName(a).Length).Take(buttons.Count).ToList();
				foreach (bool swap in new[] { false, true })
				foreach ((double width, double height) in FitCheck.Matrix)
				{
					Kopru.Core.Input.DesktopButtonMap map = Kopru.Core.Input.DesktopButtonMap.Default;
					for (int i = 0; i < buttons.Count; i++)
					{
						map = map.With(buttons[i], names[swap ? i ^ 1 : i]);
					}
					string longest = Text.DesktopActionName(names[0]);
					vm.Settings.DesktopMode = vm.Settings.DesktopMode with { Buttons = map, PointerStick = Kopru.Core.Input.PointerStick.Left };
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.Navigate(vm.SettingsPage);
					vm.SettingsPage.SelectedTab = SettingsViewModel.MouseTab;
					FitCheck.Settle(window);
					problems.AddRange(Check(window).Select(p => $"{code} {width}x{height} mouse ({longest}): {p}"));
					ItemsControl rows = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "DesktopMappingRows");
					List<ComboBox> pickers = rows.GetVisualDescendants().OfType<ComboBox>().ToList();
					if (pickers.Count != 16 || pickers.Select(p => Math.Round(FitCheck.Bounds(p, rows).Top)).Distinct().Count() != 8)
					{
						problems.Add($"{code} {width}x{height} mouse: {pickers.Count} pickers not in 8 lines of two");
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// A remembered-controller list and a profile list longer than the room scroll inside their cards, never the page.
	[AvaloniaFact]
	public void LongLists_ScrollInsideTheirCards()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			(MainWindowViewModel vm, FakeControllerService fake) = FullCase("en");
			for (int i = 0; i < 12; i++)
			{
				vm.Settings.Profiles.Create($"Profile {i + 4}", vm.Settings.CaptureValues());
			}
			for (int i = 0; i < 10; i++)
			{
				vm.Settings.KnownControllers.Observe([Entry($"fake-pad-extra-{i}", battery: 50 + i)]);
				vm.Settings.KnownControllers.Observe([]);
			}
			fake.RaiseStateChanged();
			vm.OpenSettingsAt(SettingsViewModel.ControllersTab);
			var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
			window.Show();
			FitCheck.Settle(window);
			List<string> problems = Check(window).ToList();
			Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
			SettingsView page = window.GetVisualDescendants().OfType<SettingsView>().Single();
			var inner = page.GetVisualDescendants().OfType<ScrollViewer>().Skip(1).Where(s => s.IsEffectivelyVisible).ToList();
			Assert.Equal(2, inner.Count);
			Assert.All(inner, s => Assert.True(s.Extent.Height > s.Viewport.Height + 1, $"list {s.Extent.Height} fits {s.Viewport.Height}"));
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The General tab's Updates block in each of its states (up to date; a new version with notes; the confirmation while
	// playing; downloading; an error; a zip copy's download page button) fits at every size in both languages. Writes the
	// up-to-date and new-version pictures (1100x700 and 960x640) to docs/screens with KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public async Task GeneralTab_UpdatesBlock_FitsInEveryState()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		bool writeDocs = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string? fitDir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		List<TaskCompletionSource> stalled = [];
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach (string state in new[] { "idle", "ready", "confirm", "downloading", "error", "zip" })
				{
					var fakes = new UpdateFakes { ManifestVersion = state == "idle" ? "0.1.5" : UpdateFakes.Version, Installed = state != "zip" };
					if (state == "downloading")
					{
						stalled.Add(fakes.Stall(42));
					}
					if (state == "error")
					{
						fakes.Download = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
					}
					(MainWindowViewModel vm, FakeControllerService _) = FullCase(code, fakes);
					UpdatesViewModel updates = vm.Updates;
					await updates.RunScheduledCheckAsync();
					Avalonia.Threading.Dispatcher.UIThread.RunJobs();
					if (state is "confirm" or "error" or "downloading")
					{
						// The virtual controller feeds a game in the full case: the first click asks.
						await updates.ActCommand.ExecuteAsync(null);
						Assert.Equal(UpdatePhase.Confirming, updates.Phase);
					}
					if (state is "error")
					{
						await updates.ActCommand.ExecuteAsync(null);
					}
					if (state is "downloading")
					{
						_ = updates.ActCommand.ExecuteAsync(null);
						for (int i = 0; i < 300 && updates.Progress < 42; i++)
						{
							await Task.Delay(10);
							Avalonia.Threading.Dispatcher.UIThread.RunJobs();
						}
					}
					Avalonia.Threading.Dispatcher.UIThread.RunJobs();
					UpdatePhase expected = state switch
					{
						"idle" => UpdatePhase.Idle,
						"confirm" => UpdatePhase.Confirming,
						"downloading" => UpdatePhase.Downloading,
						"error" => UpdatePhase.Error,
						_ => UpdatePhase.Ready
					};
					Assert.Equal(expected, updates.Phase);
					foreach ((double width, double height) in FitCheck.Matrix)
					{
						var window = new MainWindow { DataContext = vm, Width = width, Height = height };
						window.Show();
						vm.OpenSettingsAt(SettingsViewModel.GeneralTab);
						FitCheck.Settle(window);
						problems.AddRange(Check(window).Select(p => $"{code} {width}x{height} updates {state}: {p}"));
						bool docs = writeDocs && state is "idle" or "ready" && (width, height) is (1100, 700) or (960, 640);
						if (fitDir != null || docs)
						{
							Thread.Sleep(320);
							FitCheck.Settle(window);
							using var frame = window.CaptureRenderedFrame();
							if (fitDir != null)
							{
								Directory.CreateDirectory(fitDir);
								frame!.Save(Path.Combine(fitDir, $"settings-updates-{state}-{width}x{height}-{code}.png"));
							}
							if (docs)
							{
								string size = (width, height) == (1100, 700) ? "" : $"-{width}x{height}";
								frame!.Save(Path.Combine(AppTests.RepoRoot(), "docs", "screens", $"settings-updates-{state}{size}-{code}.png"));
							}
						}
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
			foreach (TaskCompletionSource release in stalled)
			{
				release.TrySetResult();
			}
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	private static ControllerEntry Entry(string id, int slot = 1, double battery = 78, bool edge = false) =>
		new(id, slot, edge ? "DualSense Edge" : "DualSense", edge, true, battery, false, BridgeState.Stopped);

	// Settings with everything that can make a tab taller shown.
	internal static (MainWindowViewModel Vm, FakeControllerService Fake) FullCase(string code, UpdateFakes? updates = null)
	{
		var fake = new FakeControllerService(animate: false)
		{
			Details = ControllerInfoAppTests.Details(),
			ConnectedSince = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(4 * 60 + 7)
		};
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code }),
			Updates: updates?.Services()));
		vm.Sticks.StopPreview();
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		bool tr = code == "tr";
		vm.Settings.KnownControllers.Observe([Entry("fake-pad-b", 2, battery: 41, edge: true)]);
		vm.Settings.KnownControllers.Observe([]);
		vm.Settings.KnownControllers.Observe([Entry("fake-pad-a", battery: 78)]);
		vm.Settings.RenameController("fake-pad-a", tr ? "Salon Kumandası" : "Living Room Pad");
		vm.Settings.Profiles.Create(tr ? "Yarış" : "Racing", vm.Settings.CaptureValues());
		vm.Settings.Profiles.Create(tr ? "Nişancı" : "Shooter", vm.Settings.CaptureValues());
		fake.ControllerList.Add(Entry("fake-pad-a", battery: 78));
		vm.Settings.MicEnabled = true;
		fake.MicLevel = 0.35;
		vm.SettingsPage.RefreshMic();
		vm.SettingsPage.Info.Refresh();
		vm.SettingsPage.ToolResult = Text.DiagnosticsSaved(@"C:\Users\Player\Documents\ds5-untethered-diagnostics-2026-11-11.zip");
		fake.RaiseStateChanged();
		return (vm, fake);
	}

	// What is wrong with the shown Settings tab as laid out now.
	internal static IEnumerable<string> Check(Window window)
	{
		SettingsView page = window.GetVisualDescendants().OfType<SettingsView>().Single(v => v.IsEffectivelyVisible);
		ScrollViewer scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First();
		StackPanel column = page.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("page"));
		SettingsLayout cards = page.GetVisualDescendants().OfType<SettingsLayout>().Single(l => l.IsEffectivelyVisible);
		ListBox tabs = page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SettingsTabs");
		List<Control> shown = cards.Children.Where(c => c.IsVisible).ToList();

		List<(string Name, Rect Rect)> blocks = [("tabs", FitCheck.Bounds(tabs, column))];
		blocks.AddRange(shown.Select((c, i) => ($"card {i}", FitCheck.Bounds(c, column))));
		string arrangement = (cards.IsTwoColumns ? "two columns" : "one column") + (cards.IsNarrow ? ", narrow" : "");
		IEnumerable<string> problems = FitCheck.Scrolls(scroll, arrangement)
			.Concat(FitCheck.Apart(blocks, FitCheck.Bounds(column, column)))
			.Concat(shown.SelectMany(card => FitCheck.Inside(card, column)))
			.Concat(FitCheck.CutText(page));
		// The controls inside a card (switches, sliders, pickers, buttons) are at least 8 apart.
		foreach (Control card in shown)
		{
			List<(string, Rect)> controls = card.GetVisualDescendants().OfType<Control>()
				.Where(c => c.IsEffectivelyVisible && c.TemplatedParent == null && c is ToggleSwitch or Slider or ComboBox or ListBox or Button or ProgressBar or TextBox
					&& !FitCheck.InsideList(c, card))
				.Select(c => (FitCheck.Describe(c) + " " + (AutomationPropertiesName(c) ?? ""), FitCheck.Bounds(c, column)))
				.Where(b => b.Item2.Width > 0.5)
				.ToList();
			problems = problems.Concat(FitCheck.Apart(controls, FitCheck.Bounds(card, column)));
		}
		return problems;
	}

	private static string? AutomationPropertiesName(Control c) => Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } n ? n
		: c is ContentControl { Content: string s } ? s : null;
}
