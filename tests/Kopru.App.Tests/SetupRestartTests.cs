// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// "Restart The Computer" on the setup page's drivers step: shown only while Windows reports a pending restart, asks
// inline, then saves, registers the one-time start, requests the restart once and exits; a refused restart shows a
// plain error and the app keeps running.
public class SetupRestartTests
{
	private sealed class FakeRestart : ISystemRestart
	{
		public List<TimeSpan> Requests { get; } = [];
		public bool Refuse { get; set; }

		public void Request(TimeSpan delay)
		{
			Requests.Add(delay);
			if (Refuse)
			{
				throw new Win32Exception(5);
			}
		}
	}

	private sealed class FakeRunOnce : IRunOnceStore
	{
		public Dictionary<string, string> Values { get; } = new() { ["SomethingElse"] = "\"C:\\Other\\other.exe\"" };
		public int Sets { get; private set; }

		public string? Get(string name) => Values.GetValueOrDefault(name);

		public void Set(string name, string value)
		{
			Sets++;
			Values[name] = value;
		}

		public void Remove(string name) => Values.Remove(name);
	}

	private const string Exe = @"C:\Users\Test\AppData\Local\Programs\DS5 Untethered\DS5 Untethered.exe";

	private sealed class Rig
	{
		public FakeRestart Restart { get; } = new();
		public FakeRunOnce RunOnce { get; } = new();
		public MemorySettingsStore Store { get; } = new();
		public RealServiceTests.Probes Probes { get; }
		public MainWindowViewModel Vm { get; }
		public int Exits { get; private set; }

		public Rig(bool drivers, bool restartPending, bool startWithWindows = false)
		{
			Probes = new RealServiceTests.Probes(drivers, restartPending);
			Vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(Store, new TestStartup(),
				() => new SetupWizard(new SystemCheck(Probes, Probes, Probes, Probes), new RealServiceTests.Links()),
				Restart: new RestartServices(Restart, new RunOnceRegistration(RunOnce, () => Exe))));
			Vm.ExitApp = () => Exits++;
			Vm.Settings.StartWithWindows = startWithWindows;
			Vm.ShowSetup(SetupStepId.Drivers);
		}

		public SetupViewModel Setup => Vm.Setup!;

		public SetupStepItem Drivers => Setup.Steps.Single(s => s.Id == SetupStepId.Drivers);

		public bool HasRestartButton => Drivers.Actions.Any(a => a.Label == Text.SetupRestartButton());

		public Task PressRestart() => Drivers.Actions.Single(a => a.Label == Text.SetupRestartButton()).Command.ExecuteAsync(null);
	}

	[AvaloniaTheory]
	// No restart pending: no button, with or without the drivers.
	[InlineData(true, false, false)]
	[InlineData(false, false, false)]
	// Pending: the button, also when the drivers are found.
	[InlineData(true, true, true)]
	[InlineData(false, true, true)]
	public void Button_ShowsOnlyWhileARestartIsPending(bool drivers, bool pending, bool shown)
	{
		var rig = new Rig(drivers, pending);
		Assert.Equal(shown, rig.HasRestartButton);
		Assert.Equal(shown, rig.Setup.IsRestartPending);
		Assert.Same(rig.Setup, rig.Drivers.RestartHost);
		// Only the drivers row carries it.
		Assert.All(rig.Setup.Steps.Where(s => s.Id != SetupStepId.Drivers), s =>
		{
			Assert.Null(s.RestartHost);
			Assert.DoesNotContain(s.Actions, a => a.Label == Text.SetupRestartButton());
		});
	}

	[AvaloniaFact]
	public async Task Button_GoesAway_WhenARecheckFindsNoPendingRestart()
	{
		var rig = new Rig(true, true);
		await rig.PressRestart();
		Assert.True(rig.Setup.IsConfirmingRestart);
		rig.Probes.Restart = false;
		await rig.Drivers.Actions.Single(a => a.Label == Text.SetupActionRecheck()).Command.ExecuteAsync(null);
		Assert.False(rig.HasRestartButton);
		Assert.False(rig.Setup.IsConfirmingRestart);
	}

	[AvaloniaFact]
	public async Task Press_AsksFirst_AndCancelDoesNothing()
	{
		var rig = new Rig(true, true);
		Assert.True(rig.Setup.ShowsActions);
		await rig.PressRestart();
		Assert.True(rig.Setup.IsConfirmingRestart);
		Assert.False(rig.Setup.ShowsActions);
		Assert.Contains(Text.SetupRestartQuestion(), rig.Setup.RestartQuestion);
		Assert.Empty(rig.Restart.Requests);

		rig.Setup.CancelRestartCommand.Execute(null);
		Assert.False(rig.Setup.IsConfirmingRestart);
		Assert.True(rig.Setup.ShowsActions);
		Assert.Empty(rig.Restart.Requests);
		Assert.Equal(0, rig.RunOnce.Sets);
		Assert.Equal(0, rig.Exits);
	}

	[AvaloniaFact]
	public async Task Confirm_Saves_RegistersOnce_RestartsOnce_AndExits()
	{
		var rig = new Rig(true, true);
		rig.Vm.Settings.GameHapticsLevel = 120;
		await rig.PressRestart();
		rig.Setup.ConfirmRestartCommand.Execute(null);
		// A second confirm (double click) does nothing more.
		rig.Setup.ConfirmRestartCommand.Execute(null);

		Assert.Equal([SetupRestart.Delay], rig.Restart.Requests);
		Assert.Equal(1, rig.Exits);
		Assert.Equal(120, rig.Store.Saved!.GameHapticsLevel);
		Assert.Equal("\"" + Exe + "\"", rig.RunOnce.Values[RunOnceRegistration.ValueName]);
		Assert.Equal(1, rig.RunOnce.Sets);
		// Nothing else under RunOnce was touched.
		Assert.Equal(2, rig.RunOnce.Values.Count);
		Assert.Equal("\"C:\\Other\\other.exe\"", rig.RunOnce.Values["SomethingElse"]);
		Assert.False(rig.Setup.HasRestartError);
		Assert.InRange(SetupRestart.Delay.TotalSeconds, 3, 15);
	}

	[AvaloniaFact]
	public async Task Confirm_WithStartWithWindowsOn_DoesNotRegisterAgain()
	{
		var rig = new Rig(true, true, startWithWindows: true);
		await rig.PressRestart();
		rig.Setup.ConfirmRestartCommand.Execute(null);
		Assert.Single(rig.Restart.Requests);
		Assert.Equal(0, rig.RunOnce.Sets);
		Assert.False(rig.RunOnce.Values.ContainsKey(RunOnceRegistration.ValueName));
		Assert.Equal(1, rig.Exits);
	}

	[AvaloniaFact]
	public async Task RefusedRestart_ShowsAPlainError_TakesTheRunOnceBack_AndKeepsRunning()
	{
		var rig = new Rig(true, true);
		rig.Restart.Refuse = true;
		await rig.PressRestart();
		rig.Setup.ConfirmRestartCommand.Execute(null);

		Assert.Single(rig.Restart.Requests);
		Assert.Equal(0, rig.Exits);
		Assert.True(rig.Setup.HasRestartError);
		Assert.Equal(Text.SetupRestartFailed(), rig.Setup.RestartError);
		Assert.False(rig.RunOnce.Values.ContainsKey(RunOnceRegistration.ValueName));
		Assert.True(rig.RunOnce.Values.ContainsKey("SomethingElse"));
		Assert.False(rig.Setup.IsConfirmingRestart);
		Assert.True(rig.HasRestartButton);

		// Asking again clears the error.
		await rig.PressRestart();
		Assert.False(rig.Setup.HasRestartError);
	}

	[Fact]
	public void RunOnce_RegistersTheQuotedExe_UnderOneName()
	{
		var store = new FakeRunOnce();
		var registration = new RunOnceRegistration(store, () => Exe);
		Assert.True(registration.Register());
		Assert.True(registration.Register());
		Assert.Equal("\"" + Exe + "\"", store.Get("Untethered"));
		Assert.Equal(2, store.Values.Count);
		Assert.False(new RunOnceRegistration(store, () => null).Register());
		registration.Unregister();
		Assert.Null(store.Get("Untethered"));
		Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", RegistryRunOnceStore.RunOnceKey);
	}

	// The setup page with drivers found and a restart pending, then with the question and after a refused restart, at the
	// default window size (docs/screens/setup-restart*-{tr,en}.png when KOPRU_RENDER_SCREENS=1).
	[AvaloniaFact]
	public async Task RenderRestartStates()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				var rig = new Rig(drivers: true, restartPending: true);
				var window = new Kopru.App.Views.MainWindow { DataContext = rig.Vm, Width = 1100, Height = 700 };
				window.Show();
				async Task Capture(string name)
				{
					Avalonia.Threading.Dispatcher.UIThread.RunJobs();
					Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (writeFiles)
					{
						frame!.Save(Path.Combine(outDir, $"{name}-{code}.png"));
					}
					await Task.CompletedTask;
				}
				await Capture("setup-restart");
				await rig.PressRestart();
				await Capture("setup-restart-confirm");
				rig.Restart.Refuse = true;
				rig.Setup.ConfirmRestartCommand.Execute(null);
				await Capture("setup-restart-failed");
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}

	[Fact]
	public void Texts_AreTranslated()
	{
		foreach (Func<Language?, string> text in new Func<Language?, string>[] { Text.SetupRestartButton, Text.SetupRestartQuestion, Text.SetupRestartConfirm,
			Text.SetupRestartCancel, Text.SetupRestartFailed })
		{
			Assert.NotEqual(text(Language.Turkish), text(Language.English));
		}
		Assert.Equal("Bilgisayarı Yeniden Başlat", Text.SetupRestartButton(Language.Turkish));
		Assert.Contains("programlar kapanır", Text.SetupRestartQuestion(Language.Turkish));
	}
}
