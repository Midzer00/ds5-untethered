// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The game pop-up's rows line up in every state (a game on the default profile, one with its own profile and a start file,
// the running game with Back To Game, the Xbox trigger rumble setting on with the Trigger Rumble row), Turkish and English,
// at 1152x700 and 1100x700: in each column every row's label starts on one left edge, its control column has one left and
// one right edge, no button, picker, slider or switch reaches into the label column (the profile's "â€¦" button sat between
// the label and the picker), and the right column starts level with the left column's first row (the default profile's
// box started level with Play, above the first row).
public sealed class GamePanelAlignmentTests
{
	private static readonly string[] States = ["default", "own", "running", "xbox"];
	private const string MadeUpStartFile = @"C:\Games\Hollow Peak\HollowPeak.exe";

	internal static async Task<(MainWindowViewModel Vm, MainWindow Window)> Open(string state, string code, double width, double height)
	{
		(MainWindowViewModel vm, MainWindow window) = await AuditRenderTests.OpenGames(code, width, height);
		GameItemViewModel game = state == "running"
			? vm.SupportedGames.Games.Single(g => g.Name == AuditRenderTests.MadeUpGame)
			: vm.SupportedGames.Games.First(g => !g.IsRunning && g.CanLaunch);
		vm.SupportedGames.OpenGameCommand.Execute(game);
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		if (state is "own" or "xbox")
		{
			panel.UseOwnSettingsCommand.Execute(null);
			panel.SaveProfileNameCommand.Execute(null);
		}
		if (state == "own")
		{
			panel.PickStartFile = () => Task.FromResult<string?>(MadeUpStartFile);
			await panel.ChooseStartFileCommand.ExecuteAsync(null);
		}
		if (state == "xbox")
		{
			vm.Settings.XboxTriggerRumbleOn = true;
		}
		Settle(window);
		return (vm, window);
	}

	[AvaloniaFact]
	public async Task PopUpRows_LabelsAndControlsLineUp_AndTheColumnsStartLevel()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in new[] { (1152.0, 700.0), (1100.0, 700.0) })
				{
					foreach (string state in States)
					{
						(MainWindowViewModel vm, MainWindow window) = await Open(state, code, width, height);
						string at = $"{code} {width}x{height} {state}";
						problems.AddRange(Check(window, at, state == "xbox"));
						problems.AddRange(FitCheck.CutText(window.GetVisualDescendants().OfType<GamePanelView>().Single()).Select(p => $"{at}: {p}"));
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

	private static IEnumerable<string> Check(Window window, string at, bool expectsTriggerRumble)
	{
		GamePanelView panel = window.GetVisualDescendants().OfType<GamePanelView>().Single();
		if (!panel.IsTwoColumns)
		{
			yield return $"{at}: one column";
			yield break;
		}
		if (expectsTriggerRumble && !panel.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PanelTriggerRumbleRow").IsEffectivelyVisible)
		{
			yield return $"{at}: no Trigger Rumble row";
		}
		StackPanel left = Column(panel, "PanelLeft"), right = Column(panel, "PanelRight");
		foreach (StackPanel column in new[] { left, right })
		{
			List<Grid> rows = Rows(column);
			if (rows.Count == 0)
			{
				continue;
			}
			List<Control> controls = rows.Select(r => ShownIn(r, 1)).OfType<Control>().ToList();
			List<Control> full = controls.Where(c => c.Classes.Contains("panelControl")).ToList();
			if (full.Count == 0)
			{
				yield return $"{at} {column.Name}: no control of the control column's width";
				continue;
			}
			Rect slot = FitCheck.Bounds(full[0], window);
			foreach (Control c in full.Skip(1))
			{
				Rect r = FitCheck.Bounds(c, window);
				if (Math.Abs(r.Left - slot.Left) > 0.5 || Math.Abs(r.Right - slot.Right) > 0.5)
				{
					yield return $"{at} {column.Name}: {Describe(c)} {r} is not in the control column {slot}";
				}
			}
			// Switches: as narrow as they are, ending on the column's right edge.
			foreach (Control c in controls.Except(full))
			{
				Rect r = FitCheck.Bounds(c, window);
				if (Math.Abs(r.Right - slot.Right) > 0.5 || r.Left < slot.Left - 0.5)
				{
					yield return $"{at} {column.Name}: {Describe(c)} {r} does not end on the control column {slot}";
				}
			}
			List<Control> labels = rows.Select(r => ShownIn(r, 0)).OfType<Control>().ToList();
			if (labels.Count > 0)
			{
				double labelLeft = FitCheck.Bounds(labels[0], window).Left;
				foreach (Control label in labels.Skip(1))
				{
					Rect r = FitCheck.Bounds(label, window);
					if (Math.Abs(r.Left - labelLeft) > 0.5)
					{
						yield return $"{at} {column.Name}: label {Describe(label)} starts at {r.Left:F1}, the others at {labelLeft:F1}";
					}
				}
			}
			// Nothing to click or set in the label column, nothing past the control column.
			foreach (Grid row in rows)
			{
				foreach (Control c in row.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.TemplatedParent == null
					&& c is Button or ComboBox or Slider or ToggleSwitch or ListBox or TextBox))
				{
					Rect r = FitCheck.Bounds(c, window);
					if (r.Left < slot.Left - 0.5 || r.Right > slot.Right + 0.5)
					{
						yield return $"{at} {column.Name}: {Describe(c)} {r} reaches out of the control column {slot}";
					}
				}
			}
		}
		// The right column starts level with the left column's first row.
		Grid firstRow = Rows(left).First();
		Control? rightTop = right.Children.FirstOrDefault(c => c.IsEffectivelyVisible);
		if (rightTop == null)
		{
			yield return $"{at}: nothing in the right column";
		}
		else
		{
			double leftTop = FitCheck.Bounds(firstRow, window).Top, top = FitCheck.Bounds(rightTop, window).Top;
			if (Math.Abs(leftTop - top) > 2)
			{
				yield return $"{at}: the right column starts at {top:F1}, the left column's first row at {leftTop:F1}";
			}
		}
	}

	private static StackPanel Column(GamePanelView panel, string name) => panel.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == name);

	private static List<Grid> Rows(StackPanel column) =>
		column.GetVisualDescendants().OfType<Grid>().Where(g => g.IsEffectivelyVisible && g.Classes.Contains("panelRow")).ToList();

	private static Control? ShownIn(Grid row, int column) =>
		row.Children.FirstOrDefault(c => c.IsEffectivelyVisible && Grid.GetColumn(c) == column && Grid.GetRow(c) == 0);

	private static string Describe(Control c) =>
		Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } name ? $"{c.GetType().Name} \"{name}\""
		: c is TextBlock { Text: { } text } ? $"\"{text}\"" : c.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text is { } inner ? $"{c.GetType().Name} \"{inner}\"" : c.Name ?? c.GetType().Name;

	// With KOPRU_POPUP_ALIGN_DIR set: renders popup-align-{state}-{tag}.png (Turkish, 1152x700) there, the tag from
	// KOPRU_POPUP_ALIGN_TAG (default "after"). Otherwise it does nothing.
	[AvaloniaFact]
	public async Task RenderStates()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_POPUP_ALIGN_DIR") is { Length: > 0 } d ? d : null;
		if (dir == null)
		{
			return;
		}
		string tag = Environment.GetEnvironmentVariable("KOPRU_POPUP_ALIGN_TAG") is { Length: > 0 } t ? t : "after";
		Directory.CreateDirectory(dir);
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			foreach (string state in States)
			{
				(MainWindowViewModel _, MainWindow window) = await Open(state, "tr", 1152, 700);
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				using var frame = window.CaptureRenderedFrame();
				frame!.Save(Path.Combine(dir, $"popup-align-{state}-{tag}.png"));
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Settle(Window window)
	{
		FitCheck.Settle(window);
		Thread.Sleep(320);
		FitCheck.Settle(window);
	}
}
