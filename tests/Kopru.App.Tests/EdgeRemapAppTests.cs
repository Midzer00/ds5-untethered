// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The DualSense Edge panel of the remap tab: back paddle targets, "Present as Edge", shown only for an Edge.
public class EdgeRemapAppTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) NewViewModel(ControllerKind kind)
	{
		var fake = new FakeControllerService(kind, animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null));
		vm.Sticks.StopPreview();
		return (vm, fake);
	}

	private static Border EdgePanel(Window window) =>
		window.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "EdgePanel");

	private static Window Show(MainWindowViewModel vm)
	{
		vm.OpenController(ControllerPageViewModel.RemapTab);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Settle();
		return window;
	}

	[AvaloniaFact]
	public void EdgePanel_OnlyForAnEdge()
	{
		foreach ((ControllerKind kind, bool visible) in new[] { (ControllerKind.DualSense, false), (ControllerKind.DualSenseEdge, true), (ControllerKind.None, false) })
		{
			(MainWindowViewModel vm, _) = NewViewModel(kind);
			Assert.Equal(visible, vm.Sticks.Remap.Edge.IsVisible);
			Window window = Show(vm);
			Assert.Equal(visible, EdgePanel(window).IsVisible);
			window.Close();
		}
	}

	[AvaloniaFact]
	public void PaddleTargets_And_Presentation_ReachTheService()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel(ControllerKind.DualSenseEdge);
		EdgeRemapPanel edge = vm.Sticks.Remap.Edge;
		Assert.Equal(ButtonRemap.Paddles, edge.Rows.Select(r => r.Physical));
		Assert.True(edge.PresentAsEdge);
		RemapRow left = edge.Rows[0];
		// Presented as an Edge: the paddle keeps its function by default ("Keep as Paddle" first).
		Assert.Equal(DsButton.LeftPaddle, left.Selected!.Button);
		Assert.Equal(Text.EdgeKeepPaddle(), left.Targets[0].Name);
		Assert.DoesNotContain(left.Targets.Skip(1), t => ButtonRemap.IsPaddle(t.Button));

		left.Selected = left.Targets.Single(t => t.Button == DsButton.Cross);
		Assert.Equal(DsButton.Cross, fake.Settings.Remap.TargetOf(DsButton.LeftPaddle));
		Assert.True(left.IsChanged);

		// Presented as a DualSense: no "keep" option, unmapped paddles do nothing, mappings stay.
		edge.PresentAsEdge = false;
		Assert.False(fake.Settings.Remap.PresentAsEdge);
		RemapRow right = edge.Rows[1];
		Assert.DoesNotContain(right.Targets, t => ButtonRemap.IsPaddle(t.Button));
		Assert.Equal(DsButton.None, right.Selected!.Button);
		Assert.Equal(DsButton.Cross, left.Selected!.Button);

		vm.Sticks.Remap.ResetRemapCommand.Execute(null);
		Assert.True(edge.PresentAsEdge);
		Assert.Equal(DsButton.LeftPaddle, left.Selected!.Button);
	}

	// Renders the remap tab with an Edge to docs/screens/remap-edge-{tr,en}.png when KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public void RenderEdgeRemapScreens()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, _) = NewViewModel(ControllerKind.DualSenseEdge);
				vm.Settings.Remap = RemapSettings.Default.With(DsButton.RightPaddle, DsButton.Cross);
				Window window = Show(vm);
				// Scroll the Edge panel into view.
				EdgePanel(window).BringIntoView();
				Settle();
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				if (writeFiles)
				{
					frame!.Save(Path.Combine(outDir, $"remap-edge-{code}.png"));
				}
				window.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}

	private static string RepoRoot()
	{
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			if (File.Exists(Path.Combine(dir.FullName, "Kopru.sln")))
			{
				return dir.FullName;
			}
		}
		throw new DirectoryNotFoundException("Kopru.sln");
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Thread.Sleep(320);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}
}
