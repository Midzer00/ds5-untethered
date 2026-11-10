// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Hid;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Xunit;

namespace Kopru.App.Tests;

// Setup completion (the adapter test is optional), leaving for the Overview, and the adapter test progress.
public sealed class SetupFlowTests
{
	// Drivers can be "installed" mid-test so a recheck completes the setup.
	internal sealed class MutableProbes : IInstallRegistry, IBluetoothRadio, IControllerFinder, IRestartProbe
	{
		public bool Drivers { get; set; }
		public (string? Version, string? Location)? FindProgram(string displayNamePrefix) => Drivers ? ("1.0", @"C:\x") : null;
		public bool ServiceExists(string serviceName) => Drivers;
		public bool FileExists(string path) => Drivers;
		public RadioState Query() => RadioState.On;
		public IReadOnlyList<HidDevice> Find() => [new HidDevice(TestLink.BtPath, 0x054C, 0x0CE6, 78, 547, 64)];
		public bool? IsRestartPending() => false;
	}

	internal sealed class FakeClock : IElapsedClock
	{
		public TimeSpan Elapsed { get; set; }
		public int Restarts { get; private set; }
		public void Restart()
		{
			Restarts++;
			Elapsed = TimeSpan.Zero;
		}
	}

	private static SetupWizard Wizard(MutableProbes p) => new(new SystemCheck(p, p, p, p), new RealServiceTests.Links());

	private static MainWindowViewModel OpenMain(MutableProbes p, FakeControllerService? service = null) =>
		new(service ?? new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(), null, () => Wizard(p)));

	private static SetupAction Action(SetupViewModel vm, SetupStepId id, SetupActionKind kind) =>
		vm.Wizard.Steps.Single(s => s.Id == id).Actions.First(a => a.Kind == kind);

	[AvaloniaFact]
	public void UntestedAdapter_DoesNotBlockCompletion_AndIsNeverCurrent()
	{
		var vm = new SetupViewModel(Wizard(new MutableProbes { Drivers = true }), new FakeControllerService(animate: false), () => { });
		Assert.True(vm.IsComplete);
		SetupStepItem adapter = vm.Steps.Single(s => s.Id == SetupStepId.AdapterTest);
		Assert.True(adapter.IsOptional);
		Assert.Equal(Text.SetupOptional(), adapter.OptionalText);
		Assert.False(adapter.IsCurrent);
		Assert.DoesNotContain(vm.Steps, s => s.IsCurrent);
		Assert.True(vm.Steps.Single(s => s.Id == SetupStepId.Done).IsDone);
	}

	[AvaloniaFact]
	public void MissingDrivers_AreRequired_AndCurrent()
	{
		var vm = new SetupViewModel(Wizard(new MutableProbes()), new FakeControllerService(animate: false), () => { });
		Assert.False(vm.IsComplete);
		Assert.True(vm.Steps.Single(s => s.Id == SetupStepId.Drivers).IsCurrent);
		Assert.Single(vm.Steps, s => s.IsCurrent);
	}

	[AvaloniaFact]
	public void OptionalPendingStep_IsSkippedForCurrent()
	{
		var steps = new List<SetupStep>
		{
			new(SetupStepId.Drivers, StepStatus.Done, _ => "a", _ => "b", [], []),
			new(SetupStepId.AdapterTest, StepStatus.Needed, _ => "a", _ => "b", [], [], IsOptional: true),
			new(SetupStepId.Done, StepStatus.Needed, _ => "a", _ => "b", [], []),
		};
		IReadOnlyList<SetupStepItem> items = SetupViewModel.BuildItems(steps, _ => [], "opt");
		Assert.False(items[1].IsCurrent);
		Assert.True(items[1].ShowsNumber);
		Assert.True(items[2].IsCurrent);
	}

	[AvaloniaFact]
	public async Task FirstRun_GoesToOverviewByItself_WhenRecheckCompletesSetup()
	{
		var probes = new MutableProbes();
		MainWindowViewModel main = OpenMain(probes);
		SetupViewModel setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
		Assert.True(setup.AutoFinish);
		probes.Drivers = true;
		await setup.RunAsync(Action(setup, SetupStepId.Drivers, SetupActionKind.Recheck));
		Assert.Null(main.Setup);
		Assert.Same(main.Overview, main.CurrentPage);
	}

	[AvaloniaFact]
	public async Task ReopenedSetup_StaysOpenWhenComplete_AndGoToOverviewLeaves()
	{
		var probes = new MutableProbes { Drivers = true };
		MainWindowViewModel main = OpenMain(probes);
		Assert.Same(main.Overview, main.CurrentPage);
		main.Navigate(main.SettingsPage);
		main.SettingsPage.RunWizardCommand.Execute(null);
		SetupViewModel setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
		Assert.False(setup.AutoFinish);
		Assert.True(setup.IsComplete);
		await setup.RunAsync(Action(setup, SetupStepId.Drivers, SetupActionKind.Recheck));
		Assert.Same(setup, main.CurrentPage);
		setup.GoToOverviewCommand.Execute(null);
		Assert.Same(main.Overview, main.CurrentPage);
		Assert.Same(main.Overview, main.SelectedNav.Page);
	}

	[AvaloniaFact]
	public void Close_FromAnyPage_LandsOnOverview()
	{
		var probes = new MutableProbes();
		MainWindowViewModel main = OpenMain(probes);
		main.Navigate(main.SettingsPage);
		main.ShowSetup();
		SetupViewModel setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
		setup.CloseCommand.Execute(null);
		Assert.Null(main.Setup);
		Assert.Same(main.Overview, main.CurrentPage);
	}

	[AvaloniaFact]
	public async Task AdapterTest_ReportsPhasesAndSecondsLeft_OverTheClock()
	{
		var service = new FakeControllerService(animate: false) { AdapterGate = new TaskCompletionSource<AdapterTestResult>() };
		var clock = new FakeClock();
		var vm = new SetupViewModel(Wizard(new MutableProbes { Drivers = true }), service, () => { }, clock: clock,
			tick: _ => Task.Delay(Timeout.Infinite)) { PhaseLength = TimeSpan.FromSeconds(5) };
		SetupActionItem start = vm.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Actions.Single();
		Task run = vm.RunAsync(Action(vm, SetupStepId.AdapterTest, SetupActionKind.RunAdapterTest));

		Assert.True(vm.IsTesting);
		Assert.Equal(1, clock.Restarts);
		Assert.False(start.Command.CanExecute(null));
		Assert.True(vm.CancelTestCommand.CanExecute(null));
		Assert.Equal(0, vm.TestProgress);
		Assert.Equal(Text.AdapterTestIdlePhase(), vm.TestPhase);
		Assert.Equal(Text.AdapterTestSecondsLeft(10), vm.TestRemaining);

		clock.Elapsed = TimeSpan.FromSeconds(2.5);
		vm.RefreshProgress();
		Assert.Equal(25, vm.TestProgress);
		Assert.Equal(Text.AdapterTestIdlePhase(), vm.TestPhase);
		Assert.Equal(Text.AdapterTestSecondsLeft(8), vm.TestRemaining);

		clock.Elapsed = TimeSpan.FromSeconds(6.5);
		vm.RefreshProgress();
		Assert.Equal(65, vm.TestProgress);
		Assert.Equal(Text.AdapterTestLoadPhase(), vm.TestPhase);
		Assert.Equal(Text.AdapterTestSecondsLeft(4), vm.TestRemaining);

		// Past the measured time: the bar waits short of full until the result arrives.
		clock.Elapsed = TimeSpan.FromSeconds(12);
		vm.RefreshProgress();
		Assert.Equal(99, vm.TestProgress);
		Assert.Equal(Text.AdapterTestFinishing(), vm.TestPhase);
		Assert.Equal(string.Empty, vm.TestRemaining);

		service.AdapterGate.SetResult(new AdapterTestResult(AdapterVerdict.Suitable, "ok"));
		await run;
		Assert.False(vm.IsTesting);
		Assert.False(vm.CancelTestCommand.CanExecute(null));
		Assert.Equal("ok", vm.Message);
		Assert.True(vm.Steps.Single(s => s.Id == SetupStepId.AdapterTest).IsDone);
	}

	[AvaloniaFact]
	public async Task AdapterTest_CanBeCancelled()
	{
		var service = new FakeControllerService(animate: false) { AdapterGate = new TaskCompletionSource<AdapterTestResult>() };
		var vm = new SetupViewModel(Wizard(new MutableProbes { Drivers = true }), service, () => { }, clock: new FakeClock(),
			tick: _ => Task.Delay(Timeout.Infinite));
		Task run = vm.RunAsync(Action(vm, SetupStepId.AdapterTest, SetupActionKind.RunAdapterTest));
		Assert.True(vm.IsTesting);
		vm.CancelTestCommand.Execute(null);
		await run;
		Assert.False(vm.IsTesting);
		Assert.False(vm.IsBusy);
		Assert.Equal(Text.AdapterTestCancelled(), vm.Message);
		Assert.Null(vm.Wizard.AdapterVerdict);
		Assert.Equal(StepStatus.Needed, vm.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Status);
	}

	[AvaloniaFact]
	public async Task AdapterTest_AfterCompletion_DoesNotAutoLeave()
	{
		int closed = 0;
		var vm = new SetupViewModel(Wizard(new MutableProbes { Drivers = true }), new FakeControllerService(animate: false), () => closed++, autoFinish: true);
		await vm.RunAsync(Action(vm, SetupStepId.AdapterTest, SetupActionKind.RunAdapterTest));
		Assert.Equal(0, closed);
		vm.GoToOverviewCommand.Execute(null);
		Assert.Equal(1, closed);
	}
}
