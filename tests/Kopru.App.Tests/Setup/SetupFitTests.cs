// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// The setup page fits the window without scrolling in every accordion state (each step open, the adapter test running and
// with the longest "not suitable" result, the restart question), at the three review sizes, in English, Turkish and the
// pseudo-locale. With KOPRU_FIT_RENDER_DIR set every state is also saved as a PNG for a design review.
public sealed class SetupFitTests
{
	// One state of the page: its name and how to reach it from a fresh page.
	internal sealed record SetupState(string Name, bool Drivers, bool Restart, Func<SetupViewModel, FakeControllerService, Task> Prepare);

	internal static readonly SetupState[] States =
	[
		new("drivers", false, false, (_, _) => Task.CompletedTask),
		new("language", false, false, (vm, _) => Open(vm, SetupStepId.Language)),
		new("pair", false, false, (vm, _) => Open(vm, SetupStepId.PairController)),
		new("adapter", true, false, (vm, _) => Open(vm, SetupStepId.AdapterTest)),
		new("restart-question", true, true, (vm, _) =>
		{
			vm.ExpandStep(SetupStepId.Drivers);
			vm.AskRestartCommand.Execute(null);
			return Task.CompletedTask;
		}),
		new("adapter-testing", true, false, (vm, fake) =>
		{
			fake.AdapterGate = new TaskCompletionSource<AdapterTestResult>();
			SetupAction start = vm.Wizard.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Actions[0];
			_ = vm.RunAsync(start);
			return Task.CompletedTask;
		}),
		// The tightest case: every value bad, both pieces of advice.
		new("adapter-unsuitable", true, false, (vm, _) =>
		{
			vm.ShowAdapterResult(FakeControllerService.Adapter(310, 48, 9, 40, gaps: 6));
			return Task.CompletedTask;
		}),
	];

	private static Task Open(SetupViewModel vm, SetupStepId id)
	{
		vm.ExpandStep(id);
		return Task.CompletedTask;
	}

	public static IEnumerable<object[]> Languages() => [["en"], ["tr"], ["qps"]];

	[AvaloniaTheory]
	[MemberData(nameof(Languages))]
	public async Task SetupPage_FitsWithoutScrolling_InEveryAccordionState(string code)
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		Language language = code == "qps" ? Language.Pseudo : new Language(code);
		string? renderDir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		try
		{
			foreach (SetupState state in States)
			{
				foreach ((double width, double height) in FitCheck.ReviewSizes)
				{
					Localizer.Chosen = language;
					(MainWindow window, SetupViewModel setup, FakeControllerService fake) = OpenSetup(state.Drivers, state.Restart, width, height);
					Localizer.Chosen = language;
					try
					{
						await state.Prepare(setup, fake);
						FitCheck.Settle(window);
						string at = $"{code} {width}x{height} setup-{state.Name}";
						problems.AddRange(Check(window).Select(p => $"{at}: {p}"));
						if (renderDir != null)
						{
							Directory.CreateDirectory(renderDir);
							using var frame = window.CaptureRenderedFrame();
							frame!.Save(Path.Combine(renderDir, $"setup-{state.Name}-{width}x{height}-{code}.png"));
						}
					}
					finally
					{
						fake.AdapterGate?.TrySetCanceled();
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

	// A main window showing the setup page (drivers missing or installed, a restart pending or not).
	internal static (MainWindow Window, SetupViewModel Setup, FakeControllerService Fake) OpenSetup(bool drivers, bool restart, double width, double height)
	{
		var probes = new RealServiceTests.Probes(drivers, restart);
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(), null,
			() => new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
		var setup = new SetupViewModel(new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links()), fake, vm.CloseSetup,
			clock: new SetupFlowTests.FakeClock(), tick: _ => Task.Delay(Timeout.Infinite), 
			// Never confirmed here: only the question shows.
			restart: new SetupRestart(new RestartServices(null!, null!), () => true, () => { }, () => { }));
		vm.Setup = setup;
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		FitCheck.Settle(window);
		return (window, setup, fake);
	}

	// What is wrong with the setup page as laid out now: page scrolling, text cut off.
	internal static IEnumerable<string> Check(Window window)
	{
		SetupView page = window.GetVisualDescendants().OfType<SetupView>().Single(v => v.IsEffectivelyVisible);
		ScrollViewer scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First();
		return FitCheck.Scrolls(scroll, "setup").Concat(FitCheck.CutText(page));
	}
}
