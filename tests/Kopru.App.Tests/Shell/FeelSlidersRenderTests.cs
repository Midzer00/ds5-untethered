// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// For a visual review of the feel settings: Haptics And Triggers with the weapon effect (no shape row there), Light in Static
// with a preset and with the custom colour (its colour disc pop-up closed and open), and a game's pop-up with classic
// rumble's shape rows per effect, at 1152x700 and 1100x700, Turkish and English. Only with KOPRU_FEEL_RENDER_DIR set;
// otherwise it does nothing.
public sealed class FeelSlidersRenderTests
{
	private static readonly (double Width, double Height)[] Sizes = [(1152, 700), (1100, 700)];
	private static readonly (int Index, string Name)[] Effects = [(1, "feedback"), (2, "weapon"), (3, "vibration")];

	[AvaloniaFact]
	public async Task RenderFeelSliders()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_FEEL_RENDER_DIR") is { Length: > 0 } d ? d : null;
		if (outDir == null)
		{
			return;
		}
		Directory.CreateDirectory(outDir);
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in Sizes)
				{
					string size = $"{width}x{height}";
					(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();

					// Haptics And Triggers with an effect chosen: no shape row (the shape is set per game).
					vm.Navigate(vm.OtherGames);
					vm.OtherGames.Feel.FallbackIndex = 2;
					Show(window, "FallbackRow");
					Save(window, outDir, $"haptics-weapon-{size}-{code}.png");
					vm.OtherGames.Feel.FallbackIndex = 0;

					// Light, Static: a preset, then Custom with its pop-up closed and open.
					vm.Navigate(vm.Light);
					Save(window, outDir, $"light-preset-{size}-{code}.png");
					vm.Light.SelectedPreset = vm.Light.Custom;
					vm.Light.CustomHue = 200;
					vm.Light.CustomSaturation = 80;
					vm.Light.FlushCustomColour();
					Save(window, outDir, $"light-custom-closed-{size}-{code}.png");
					LightView light = window.GetVisualDescendants().OfType<LightView>().Single();
					light.OpenCustomColour();
					Save(window, outDir, $"light-custom-open-{size}-{code}.png");
					File.AppendAllText(Path.Combine(outDir, "flyout-rects.txt"), $"{code} {size}: {CustomColourTests.FlyoutRect(light, window)}{Environment.NewLine}");
					light.CustomColourFlyout.Hide();
					window.Close();
					// A game's pop-up with its own profile: sound to haptics (left column), classic rumble per effect (right).
					(MainWindowViewModel games, MainWindow gamesWindow) = await AuditRenderTests.OpenGames(code, width, height);
					games.SupportedGames.OpenGameCommand.Execute(games.SupportedGames.Games.First(g => !g.IsRunning && g.CanLaunch));
					GamePanelViewModel panel = games.SupportedGames.Panel;
					panel.UseOwnSettingsCommand.Execute(null);
					panel.SaveProfileNameCommand.Execute(null);
					foreach ((int index, string name) in Effects)
					{
						// The game's pop-up has Custom Trigger now: the same effect on both triggers.
						panel.Feel.LeftTrigger.EffectIndex = index;
						panel.Feel.RightTrigger.EffectIndex = index;
						panel.SelectedSegment = GamePanelViewModel.TriggersSegment;
						Show(gamesWindow, "PanelCustomTrigger");
						Save(gamesWindow, outDir, $"popup-rumble-{name}-{size}-{code}.png");
					}
					gamesWindow.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// Scrolls the named control into view.
	private static void Show(Window window, string name)
	{
		FitCheck.Settle(window);
		Control target = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name && c.IsEffectivelyVisible);
		target.BringIntoView();
		FitCheck.Settle(window);
	}

	private static void Save(Window window, string outDir, string name)
	{
		FitCheck.Settle(window);
		Thread.Sleep(320);
		FitCheck.Settle(window);
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(outDir, name));
	}
}
