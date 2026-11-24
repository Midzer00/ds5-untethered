// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

// The Light page fits the window: at every size in the matrix (FitCheck.Matrix), in both languages, with and without Low
// Power (its one-line reason under the game light switch), the Use The Game's Light card sits on top and is whole, the
// "Your Light" card is below it and inside the page (it may scroll inside, never sideways), the preview is inside the page
// and no text is cut off. With KOPRU_FIT_RENDER_DIR set each is saved as light-{normal|low-power}-{w}x{h}-{tr|en}.png.
public sealed class LightFitTests
{
	[AvaloniaFact]
	public void LightPage_Fits_AtEverySize_InBothLanguages_WithAndWithoutLowPower()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		string? fitDir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		try
		{
			foreach ((Language language, string code) in FitCheck.ShippedLanguages())
			{
				Localizer.Chosen = language;
				foreach (bool lowPower in new[] { false, true })
				{
					(MainWindowViewModel vm, FakeControllerService _) = NewViewModel(code);
					// The tallest card: breathing shows the colour and its three sliders.
					vm.Settings.LightMode = LightMode.Breathing;
					vm.Settings.LowPowerOn = lowPower;
					string state = lowPower ? "low-power" : "normal";
					foreach ((double width, double height) in FitCheck.Matrix)
					{
						var window = new MainWindow { DataContext = vm, Width = width, Height = height };
						window.Show();
						vm.Navigate(vm.Light);
						FitCheck.Settle(window);
						problems.AddRange(Check(window, lowPower ? Text.LightOffLowPower() : null).Select(p => $"{code} {width}x{height} {state}: {p}"));
						if (fitDir != null)
						{
							Thread.Sleep(320);
							FitCheck.Settle(window);
							using var frame = window.CaptureRenderedFrame();
							Directory.CreateDirectory(fitDir);
							frame!.Save(Path.Combine(fitDir, $"light-{state}-{width}x{height}-{code}.png"));
						}
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// The switch is the one place for the game's light: it sets LightFromGame; the reason line shows only when the switch can
	// do nothing (Low Power, the Xbox 360 type) and the switch stays usable then.
	[AvaloniaFact]
	public void GameLightSwitch_SetsLightFromGame_AndExplainsWhenItDoesNothing()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel("tr");
			fake.Watcher.Exit("Cyberpunk2077.exe");
			fake.Games.Tick();
			var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
			window.Show();
			vm.Navigate(vm.Light);
			FitCheck.Settle(window);
			LightView view = window.GetVisualDescendants().OfType<LightView>().Single();
			ToggleSwitch toggle = view.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "GameLightSwitch");
			Assert.Equal("Oyunun Işığını Kullan", Avalonia.Automation.AutomationProperties.GetName(toggle));
			Assert.True(toggle.IsChecked);
			Assert.False(vm.Light.HasGameLightOffReason);
			Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Senin Işığın");

			toggle.IsChecked = false;
			FitCheck.Settle(window);
			Assert.False(vm.Settings.LightFromGame);
			Assert.False(fake.Settings.LightFromGame);
			toggle.IsChecked = true;
			FitCheck.Settle(window);
			Assert.True(fake.Settings.LightFromGame);

			vm.Settings.LowPowerOn = true;
			FitCheck.Settle(window);
			Assert.Equal(Text.LightOffLowPower(), vm.Light.GameLightOffReason);
			Assert.True(Reason(view, vm).IsEffectivelyVisible);
			Assert.True(toggle.IsEffectivelyEnabled);
			vm.Settings.LowPowerOn = false;
			FitCheck.Settle(window);
			Assert.False(vm.Light.HasGameLightOffReason);

			fake.Games.SetForcedPadType(VirtualPadType.Xbox360);
			FitCheck.Settle(window);
			Assert.Equal(Text.GameLightOffXbox(), vm.Light.GameLightOffReason);
			Assert.True(Reason(view, vm).IsEffectivelyVisible);
			fake.Games.SetForcedPadType(VirtualPadType.DualShock4);
			FitCheck.Settle(window);
			Assert.False(vm.Light.HasGameLightOffReason);
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static TextBlock Reason(LightView view, MainWindowViewModel vm) =>
		view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("caption") && t.Text == vm.Light.GameLightOffReason);

	private static (MainWindowViewModel Vm, FakeControllerService Fake) NewViewModel(string code)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code })));
		vm.Sticks.StopPreview();
		return (vm, fake);
	}

	// What is wrong with the Light page as laid out now (`reason`: the line expected under the switch, null for none).
	private static IEnumerable<string> Check(Window window, string? reason)
	{
		LightView view = window.GetVisualDescendants().OfType<LightView>().Single(v => v.IsEffectivelyVisible);
		Rect page = FitCheck.Bounds(view, view);
		Border gameCard = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GameLightCard");
		Border ownCard = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OwnLightCard");
		Border stage = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("stage"));
		ScrollViewer scroll = ownCard.GetVisualDescendants().OfType<ScrollViewer>().First();
		List<string> problems = [];
		foreach ((string name, Control c) in new (string, Control)[] { ("game light card", gameCard), ("your light card", ownCard), ("preview", stage) })
		{
			Rect r = FitCheck.Bounds(c, view);
			if (r.Left < page.Left - 0.5 || r.Right > page.Right + 0.5 || r.Top < page.Top - 0.5 || r.Bottom > page.Bottom + 0.5 || r.Height < 1)
			{
				problems.Add($"{name} {r} outside the page {page}");
			}
		}
		if (FitCheck.Bounds(gameCard, view).Bottom + 8 > FitCheck.Bounds(ownCard, view).Top)
		{
			problems.Add("the game light card is not above the your light card");
		}
		problems.AddRange(FitCheck.Inside(gameCard, view));
		problems.AddRange(FitCheck.Inside(ownCard, view));
		problems.AddRange(FitCheck.CutText(view));
		if (scroll.Extent.Width > scroll.Viewport.Width + 0.5)
		{
			problems.Add($"your light card scrolls sideways: {scroll.Extent.Width:F1} > {scroll.Viewport.Width:F1}");
		}
		// Room for at least the mode picker and its hint before the card scrolls.
		if (scroll.Viewport.Height < 150)
		{
			problems.Add($"your light card too short: {scroll.Viewport.Height:F1}");
		}
		List<TextBlock> captions = gameCard.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("caption") && t.IsEffectivelyVisible).ToList();
		if (reason == null ? captions.Count != 0 : captions.Count != 1 || captions[0].Text != reason)
		{
			problems.Add($"reason line: {string.Join(" | ", captions.Select(t => t.Text))}");
		}
		return problems;
	}
}
