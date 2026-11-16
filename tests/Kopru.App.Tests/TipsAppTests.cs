// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// In-app tips (TipBar): the touchpad tip shows when the main window becomes active while the gesture can work, once per run,
// hides when Mouse Mode turns on, when closed or after a while, is learned after 3 uses, 2 closes or 5 runs (saved),
// and follows the Show Tips switch. In the window it sits centred at the bottom of the content column, over no control of
// the page, whole at every size in both languages.
public class TipsAppTests
{
	// One run of the app on `store`: the Virtual Controller running with the controller (as the gesture needs).
	private static (MainWindowViewModel Vm, FakeControllerService Fake) Run(MemorySettingsStore? store = null, bool bridge = true)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store ?? new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		if (bridge)
		{
			_ = fake.StartBridgeAsync();
			fake.Tick();
		}
		return (vm, fake);
	}

	private static MainWindowViewModel Vm(MemorySettingsStore? store = null) => Run(store).Vm;

	[AvaloniaFact]
	public void TouchpadTip_ShowsOnActivation_OncePerRun_AndHides()
	{
		MainWindowViewModel vm = Vm();
		TipBar tips = vm.Tips;
		List<string> log = MouseModeAutoOffAppTests.WatchLog("tip shown: mouse-mode-gesture", tips.OnWindowActivated);
		Assert.True(tips.IsVisible);
		Assert.Equal(Text.TipMouseModeGesture(), tips.Text);
		Assert.Single(log);
		tips.Expire();
		Assert.False(tips.IsVisible);
		// Once per run: activated again later, nothing.
		Assert.Empty(MouseModeAutoOffAppTests.WatchLog("tip shown: mouse-mode-gesture", tips.OnWindowActivated));
		Assert.False(tips.IsVisible);

		// Mouse Mode turning on hides it.
		MainWindowViewModel second = Vm();
		second.Tips.OnWindowActivated();
		second.Settings.SetDesktopMode(true, "shortcut");
		Assert.False(second.Tips.IsVisible);

		// With Mouse Mode on it does not show.
		MainWindowViewModel on = Vm();
		on.Settings.SetDesktopMode(true, "shortcut");
		on.Tips.OnWindowActivated();
		Assert.False(on.Tips.IsVisible);

		// Show Tips off: hides it, and none shows.
		MainWindowViewModel off = Vm();
		off.Tips.OnWindowActivated();
		off.Settings.ShowTips = false;
		Assert.False(off.Tips.IsVisible);
		MainWindowViewModel offFromStart = Vm();
		offFromStart.Settings.ShowTips = false;
		offFromStart.Tips.OnWindowActivated();
		Assert.False(offFromStart.Tips.IsVisible);
	}

	// The gesture cannot work without the controller, without the Virtual Controller running or with shortcuts off: no tip.
	[AvaloniaFact]
	public void TouchpadTip_OnlyWhenTheGestureCanWork()
	{
		(MainWindowViewModel stopped, _) = Run(bridge: false);
		stopped.Tips.OnWindowActivated();
		Assert.False(stopped.Tips.IsVisible);

		(MainWindowViewModel noShortcuts, _) = Run();
		noShortcuts.Settings.ShortcutsEnabled = false;
		noShortcuts.Tips.OnWindowActivated();
		Assert.False(noShortcuts.Tips.IsVisible);

		var gone = new FakeControllerService(animate: false) { IsConnected = false };
		var vm = new MainWindowViewModel(gone);
		vm.Tips.OnWindowActivated();
		Assert.False(vm.Tips.IsVisible);
	}

	[AvaloniaFact]
	public void TouchpadTip_LearnedAfterThreeGestures_Saved()
	{
		var store = new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now });
		(MainWindowViewModel vm, FakeControllerService fake) = Run(store);
		List<string> log = MouseModeAutoOffAppTests.WatchLog("tip learned: mouse-mode-gesture", () =>
		{
			for (int i = 0; i < 3; i++)
			{
				fake.RaiseShortcut(ShortcutAction.TouchpadDoublePress);
				vm.Settings.SetDesktopMode(false, "shortcut");
			}
		});
		Assert.Equal(["tip learned: mouse-mode-gesture (turned mouse mode on with the touchpad 3 times)"], log);
		Assert.True(vm.Settings.IsTipLearned(TipBar.MouseModeGesture));
		vm.Tips.OnWindowActivated();
		Assert.False(vm.Tips.IsVisible);

		// Saved: a new start does not show it either.
		vm.Settings.SaveNow();
		Assert.True(store.Saved!.Tips!["mouse-mode-gesture"].Learned);
		MainWindowViewModel again = Vm(new MemorySettingsStore(store.Saved));
		Assert.True(again.Settings.IsTipLearned(TipBar.MouseModeGesture));
		again.Tips.OnWindowActivated();
		Assert.False(again.Tips.IsVisible);
	}

	// Closed in two runs: learned.
	[AvaloniaFact]
	public void TouchpadTip_LearnedAfterClosingItTwice()
	{
		var store = new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now });
		MainWindowViewModel first = Vm(store);
		first.Tips.OnWindowActivated();
		first.Tips.CloseCommand.Execute(null);
		Assert.False(first.Tips.IsVisible);
		Assert.False(first.Settings.IsTipLearned(TipBar.MouseModeGesture));
		first.Settings.SaveNow();

		MainWindowViewModel second = Vm(new MemorySettingsStore(store.Saved));
		second.Tips.OnWindowActivated();
		Assert.True(second.Tips.IsVisible);
		List<string> log = MouseModeAutoOffAppTests.WatchLog("tip learned: mouse-mode-gesture", () => second.Tips.CloseCommand.Execute(null));
		Assert.Equal(["tip learned: mouse-mode-gesture (closed 2 times)"], log);
	}

	// Shown in five runs (never used, never closed): learned, and the sixth run shows nothing.
	[AvaloniaFact]
	public void TouchpadTip_LearnedAfterFiveRuns()
	{
		AppSettings saved = new() { GameListCheckedAt = DateTimeOffset.Now };
		List<string> log = MouseModeAutoOffAppTests.WatchLog("tip learned: mouse-mode-gesture", () =>
		{
			for (int run = 1; run <= 5; run++)
			{
				var store = new MemorySettingsStore(saved);
				MainWindowViewModel vm = Vm(store);
				vm.Tips.OnWindowActivated();
				Assert.True(vm.Tips.IsVisible, $"run {run}");
				vm.Tips.Expire();
				vm.Settings.SaveNow();
				saved = store.Saved!;
			}
		});
		Assert.Equal(["tip learned: mouse-mode-gesture (shown in 5 runs)"], log);
		MainWindowViewModel sixth = Vm(new MemorySettingsStore(saved));
		sixth.Tips.OnWindowActivated();
		Assert.False(sixth.Tips.IsVisible);
	}


	[Fact]
	public void Texts()
	{
		Assert.Equal("Fare Modu için touchpad'e iki kez bas.", Text.TipMouseModeGesture(Language.Turkish));
		Assert.Equal("Press the touchpad twice for Mouse Mode.", Text.TipMouseModeGesture(Language.English));
		Assert.Equal("İpuçlarını Göster", Text.ShowTips(Language.Turkish));
		Assert.Equal("Show Tips", Text.ShowTips(Language.English));
		Assert.True(new AppSettings().ShowTips);
	}


	// The tip in the window at every size, in both languages, on the Overview, Supported Games and Settings: whole (not
	// cut), centred at the bottom of the content column (inside it, below the page's content), touching no control of the
	// page, and away from the top band. Saves main-tip-{page}-{size}-{lang}.png to KOPRU_FIT_RENDER_DIR when it is set.
	[AvaloniaFact]
	public void TipBar_SitsAtTheBottomOfTheContent_AtEverySize_OnThePages()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in FitCheck.Matrix)
				{
					(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.Tips.OnWindowActivated();
					foreach ((PageViewModel page, string name) in new (PageViewModel, string)[] { (vm.Overview, "overview"), (vm.SupportedGames, "supported-games"), (vm.SettingsPage, "settings") })
					{
						vm.Navigate(page);
						FitCheck.Settle(window);
						string at = $"{code} {width}x{height} {name} (scale {window.ContentScale:F2})";
						Border tip = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TipHost");
						Visual root = window.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "Root");
						Rect r = FitCheck.Bounds(tip, root);
						PageHost host = window.GetVisualDescendants().OfType<PageHost>().Single();
						Rect column = FitCheck.Bounds(host, root);
						if (!tip.IsEffectivelyVisible || r.Width < 50)
						{
							problems.Add($"{at}: tip not shown {r}");
						}
						if (r.Left < column.Left - 0.5 || r.Right > column.Right + 0.5 || r.Bottom > root.Bounds.Height + 0.5 || r.Top < 32)
						{
							problems.Add($"{at}: tip {r} outside the content column {column}");
						}
						if (Math.Abs((r.Left + r.Right) / 2 - (column.Left + column.Right) / 2) > 1)
						{
							problems.Add($"{at}: tip {r} not centred in the column {column}");
						}
						foreach (Control c in host.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && FitCheck.IsContent(c)))
						{
							Rect b = FitCheck.Bounds(c, root);
							if (b.Width > 0.5 && b.Height > 0.5 && b.Intersects(r))
							{
								problems.Add($"{at}: tip {r} over {FitCheck.Describe(c)} {b}");
							}
						}
						problems.AddRange(FitCheck.CutText(tip).Select(p => $"{at}: {p}"));
						if (dir != null && code == "tr" && name != "supported-games" && (width, height) is (1100, 700) or (1920, 1080))
						{
							Thread.Sleep(320);
							FitCheck.Settle(window);
							using var frame = window.CaptureRenderedFrame();
							Directory.CreateDirectory(dir);
							frame!.Save(Path.Combine(dir, $"main-tip-{name}-{width}x{height}-{code}.png"));
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
}
