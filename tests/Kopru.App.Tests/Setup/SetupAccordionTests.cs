// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// The setup stepper is an accordion: one step open at a time (the current one at first), a step's title line opens it by
// click, Enter or Space and closes the others; a closed optional step offers its first button on its title line.
public sealed class SetupAccordionTests
{
	[AvaloniaFact]
	public void OnlyTheCurrentStep_IsOpenAtFirst()
	{
		(MainWindow window, SetupViewModel setup, _) = SetupFitTests.OpenSetup(drivers: false, restart: false, 1100, 700);
		try
		{
			Assert.Equal([SetupStepId.Drivers], setup.Steps.Where(s => s.IsExpanded).Select(s => s.Id));
			Assert.True(setup.Steps.Single(s => s.Id == SetupStepId.Drivers).IsCurrent);
			// The open step's text and buttons show; a closed step's do not.
			Assert.True(Shown(window, setup.Steps.Single(s => s.Id == SetupStepId.Drivers).Instructions));
			Assert.False(Shown(window, setup.Steps.Single(s => s.Id == SetupStepId.PairController).Instructions));
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void ClickingATitle_OpensThatStep_AndClosesTheOthers()
	{
		(MainWindow window, SetupViewModel setup, _) = SetupFitTests.OpenSetup(drivers: false, restart: false, 1100, 700);
		try
		{
			Click(window, Title(window, SetupStepId.PairController));
			Assert.Equal([SetupStepId.PairController], setup.Steps.Where(s => s.IsExpanded).Select(s => s.Id));
			Assert.True(Shown(window, setup.Steps.Single(s => s.Id == SetupStepId.PairController).Instructions));
			Assert.False(Shown(window, setup.Steps.Single(s => s.Id == SetupStepId.Drivers).Instructions));

			Click(window, Title(window, SetupStepId.Language));
			Assert.Equal([SetupStepId.Language], setup.Steps.Where(s => s.IsExpanded).Select(s => s.Id));
			// A second click on the open step keeps it open (one step is always open once chosen).
			Click(window, Title(window, SetupStepId.Language));
			Assert.Equal([SetupStepId.Language], setup.Steps.Where(s => s.IsExpanded).Select(s => s.Id));
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void TheTitleLine_WorksFromTheKeyboard()
	{
		(MainWindow window, SetupViewModel setup, _) = SetupFitTests.OpenSetup(drivers: false, restart: false, 1100, 700);
		try
		{
			Button pair = Title(window, SetupStepId.PairController);
			Assert.True(pair.Focusable && pair.IsTabStop);
			Assert.True(pair.Focus(NavigationMethod.Tab));
			window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
			FitCheck.Settle(window);
			Assert.Equal([SetupStepId.PairController], setup.Steps.Where(s => s.IsExpanded).Select(s => s.Id));

			Button adapter = Title(window, SetupStepId.AdapterTest);
			Assert.True(adapter.Focus(NavigationMethod.Tab));
			window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
			window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
			FitCheck.Settle(window);
			Assert.Equal([SetupStepId.AdapterTest], setup.Steps.Where(s => s.IsExpanded).Select(s => s.Id));
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public async Task ClosedOptionalStep_OffersStartTest_OnItsTitleLine_AndOpensWhenItRuns()
	{
		(MainWindow window, SetupViewModel setup, FakeControllerService fake) = SetupFitTests.OpenSetup(drivers: true, restart: false, 1100, 700);
		try
		{
			// Everything required is done: no step is current, so none is open; Start Test waits on the adapter test's title line.
			Assert.DoesNotContain(setup.Steps, s => s.IsExpanded);
			SetupStepItem adapter = setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest);
			Assert.True(adapter.IsLast);
			Assert.True(adapter.ShowsTitleAction);
			Button start = TitleAction(window, SetupStepId.AdapterTest);
			Assert.True(start.IsEffectivelyVisible);
			Assert.Equal(adapter.Actions[0].Label, start.Content);

			fake.AdapterGate = new TaskCompletionSource<AdapterTestResult>();
			Click(window, start);
			Assert.True(setup.IsTesting);
			Assert.Equal([SetupStepId.AdapterTest], setup.Steps.Where(s => s.IsExpanded).Select(s => s.Id));
			Assert.False(TitleAction(window, SetupStepId.AdapterTest).IsEffectivelyVisible);

			// The result shows in the open step and its instructions give way to it.
			fake.AdapterGate.SetResult(FakeControllerService.Adapter(310, 48, 9, 40));
			// The fake answers off the UI thread; the page picks the result up on it.
			for (int i = 0; i < 200 && setup.IsTesting; i++)
			{
				await Task.Delay(10);
				Dispatcher.UIThread.RunJobs();
			}
			FitCheck.Settle(window);
			Assert.True(setup.HasAdapterResult);
			Assert.True(setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest).IsExpanded);
			Assert.True(Shown(window, setup.AdapterHeadline));
			Assert.False(Shown(window, setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Instructions));
		}
		finally
		{
			fake.AdapterGate?.TrySetCanceled();
			window.Close();
		}
	}

	[AvaloniaFact]
	public void ClosedRows_Are48High()
	{
		(MainWindow window, SetupViewModel setup, _) = SetupFitTests.OpenSetup(drivers: false, restart: false, 1100, 700);
		try
		{
			SetupView page = window.GetVisualDescendants().OfType<SetupView>().Single();
			foreach (SetupStepItem step in setup.Steps.Where(s => !s.IsExpanded))
			{
				Border body = page.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("stepBody") && b.DataContext == step);
				// 24 title line and 12 above and below, plus the hairline under the step.
				Assert.Equal(48, body.Bounds.Height - body.BorderThickness.Bottom, 1);
			}
		}
		finally
		{
			window.Close();
		}
	}

	private static Button Title(Window window, SetupStepId id) =>
		window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StepTitle" && b.DataContext is SetupStepItem s && s.Id == id);

	private static Button TitleAction(Window window, SetupStepId id) =>
		window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StepTitleAction" && b.DataContext is SetupStepItem s && s.Id == id);

	private static bool Shown(Window window, string text) =>
		window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

	private static void Click(Window window, Visual target)
	{
		Point center = FitCheck.Bounds(target, window).Center;
		window.MouseDown(center, MouseButton.Left);
		window.MouseUp(center, MouseButton.Left);
		FitCheck.Settle(window);
	}
}
