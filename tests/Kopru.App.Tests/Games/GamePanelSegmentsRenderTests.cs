// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// For a visual review of the game pop-up's three tabs (Game, Vibration, Triggers) for (a) a supported game with a profile
// of its own, (b) an unsupported game with a profile of its own and (c) a game on the default profile, at 1100x700 and
// 1152x700, Turkish and English: panel-{a|b|c}-{game|vibration|triggers}-{w}x{h}-{lang}.png. Only with
// KOPRU_PANEL_RENDER_DIR set; otherwise it does nothing.
public sealed class GamePanelSegmentsRenderTests
{
	private static readonly (int Segment, string Name)[] Tabs =
		[(GamePanelViewModel.GameSegment, "game"), (GamePanelViewModel.VibrationSegment, "vibration"), (GamePanelViewModel.TriggersSegment, "triggers")];

	[AvaloniaFact]
	public async Task RenderSegments()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PANEL_RENDER_DIR") is { Length: > 0 } d ? d : null;
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
				foreach ((double width, double height) in new[] { (1100.0, 700.0), (1152.0, 700.0) })
				{
					string size = $"{width}x{height}";
					foreach ((string kind, string name) in new[] { ("supported", "a"), ("unsupported", "b"), ("supported-default", "c") })
					{
						(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel(kind, code, width, height);
						GamePanelViewModel panel = vm.SupportedGames.Panel;
						// Custom Trigger with an effect on each trigger, so its rows show.
						panel.Feel.LeftTrigger.EffectIndex = 1;
						panel.Feel.RightTrigger.EffectIndex = 2;
						foreach ((int segment, string tab) in Tabs)
						{
							panel.SelectedSegment = segment;
							Save(window, outDir, $"panel-{name}-{tab}-{size}-{code}.png");
						}
						panel.SelectedSegment = GamePanelViewModel.GameSegment;
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Save(Window window, string outDir, string name)
	{
		FitCheck.Settle(window);
		Thread.Sleep(120);
		FitCheck.Settle(window);
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(outDir, name));
	}
}