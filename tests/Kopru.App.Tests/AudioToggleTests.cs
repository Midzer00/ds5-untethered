// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;

namespace Kopru.App.Tests;

// Speaker / microphone / Low Power switches: one user change is at most one settings push, nothing is pushed again in
// response to the app's own state changes, and the running bridge is never restarted for them (they are applied in place,
// in order, coalesced to the latest value).
public class AudioToggleTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null));
		return (vm, fake);
	}

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	[AvaloniaFact]
	public async Task SpeakerAndMicSwitches_OnePushEach_NoRestart()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		await fake.StartBridgeAsync();
		Settle();
		QuickSettingsViewModel q = vm.Overview.Quick;
		int applies = fake.Applies;

		q.SpeakerOn = true;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);
		Assert.True(fake.Settings.Speaker.Enabled);

		// The Settings page switch is bound to the same value: its echo pushes nothing.
		vm.Settings.SpeakerEnabled = true;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);

		// The mic switch also moves the mute LED to "follow the mic": still one push.
		q.MicOn = true;
		Settle();
		Assert.Equal(applies + 2, fake.Applies);
		Assert.True(fake.Settings.Mic.Enabled);

		q.SpeakerOn = false;
		q.MicOn = false;
		Settle();
		Assert.Equal(applies + 4, fake.Applies);
		Assert.Equal(1, fake.Starts);
		Assert.Equal(0, fake.Stops);
	}

	[AvaloniaFact]
	public void LowPower_OnAndOff_OnePushEach_AndSwitchesItHoldsPushNothing()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.Settings.SpeakerEnabled = true;
		Settle();
		QuickSettingsViewModel q = vm.Overview.Quick;
		int applies = fake.Applies;

		q.LowPowerOn = true;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);
		Assert.False(fake.Settings.Speaker.Enabled);

		// Low Power holds the speaker off: changing the user's own value changes nothing the controller gets.
		vm.Settings.SpeakerEnabled = false;
		vm.Settings.SpeakerEnabled = true;
		vm.Settings.SpeakerEnabled = false;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);

		q.LowPowerOn = false;
		Settle();
		Assert.Equal(applies + 2, fake.Applies);
		Assert.False(fake.Settings.Speaker.Enabled);
	}

	[AvaloniaFact]
	public void StateChanges_NeverPushAgain_AndTheAutoSwitchPushesOnce()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.Settings.AutoLowPower = true;
		Settle();
		int applies = fake.Applies;

		// Status updates arrive several times a second while the bridge runs.
		for (int i = 0; i < 20; i++)
		{
			fake.BatteryPercent = 60 - i;
			fake.RaiseStateChanged();
		}
		Settle();
		Assert.Equal(applies, fake.Applies);

		fake.BatteryPercent = 18;
		for (int i = 0; i < 10; i++)
		{
			fake.RaiseStateChanged();
		}
		Settle();
		Assert.True(vm.Settings.LowPowerOn);
		Assert.Equal(applies + 1, fake.Applies);
	}

	[AvaloniaFact]
	public void ControllerWithItsOwnProfile_GetsOnePushPerChange()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		var racing = vm.Settings.Profiles.Create("Racing", vm.Settings.CaptureValues() with { Brightness = 100 })!;
		vm.Settings.AssignControllerProfile("pad-2", racing.Id);
		Settle();
		int pushes = fake.ProfilePushes;

		// A change of the active (default) profile updates the profiles (ProfilesChanged pushes again) but not what
		// pad-2 gets from its own profile: nothing is pushed to it.
		vm.Settings.GameHapticsLevel = 120;
		Settle();
		Assert.Equal(pushes, fake.ProfilePushes);

		vm.Overview.Quick.SpeakerOn = true;
		Settle();
		Assert.Equal(pushes + 1, fake.ProfilePushes);
		Assert.True(fake.ControllerProfiles["pad-2"]!.Speaker.Enabled);

		vm.Overview.Quick.LowPowerOn = true;
		Settle();
		Assert.Equal(pushes + 2, fake.ProfilePushes);
		Assert.False(fake.ControllerProfiles["pad-2"]!.Speaker.Enabled);
	}

	[AvaloniaFact]
	public void StepLists_StayTheSameUntilTheLanguageChanges()
	{
		(MainWindowViewModel vm, _) = Create();
		QuickSettingsViewModel q = vm.Overview.Quick;
		Assert.Same(q.StrengthSteps, q.StrengthSteps);
		Assert.Same(q.LightSteps, q.LightSteps);
	}

	[Fact]
	public void AppTests_DoNotWriteTheUsersLog() =>
		Assert.NotEqual(Path.GetFullPath(AppLog.DefaultDirectory), Path.GetFullPath(AppLog.Directory));
}

// The real service on a running bridge (fake controller and attacher).
public class AudioToggleServiceTests
{
	private readonly TestControllers controllers = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private (RealControllerService Service, BridgeHost Host) Create(TestHiding hiding)
	{
		BridgeHost host = new(controllers, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		return (new RealControllerService(host, hiding, controllers, a => a(), _ => null, timers: false), host);
	}

	[Fact]
	public async Task RapidSpeakerAndMicToggles_DoNotRestartTheBridge_AndEndInTheLastState()
	{
		(RealControllerService service, BridgeHost host) = Create(new TestHiding());
		using (service)
		{
			// Waits for the Running state itself (no deadline that a loaded machine can miss); the recorder below is
			// attached after that event was delivered, so it sees only what the toggles cause.
			TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
			host.StateChanged += s =>
			{
				if (s == BridgeState.Running)
				{
					running.TrySetResult();
				}
			};
			controllers.Links.Enqueue(new TestLink());
			await service.StartBridgeAsync();
			await running.Task;
			Assert.True(service.IsGameConnected);
			ConcurrentQueue<BridgeState> states = new();
			host.StateChanged += states.Enqueue;

			ControllerSettings off = new();
			ControllerSettings on = off with { Speaker = new SpeakerSettings(true), Mic = new MicSettings(true) };
			for (int i = 0; i < 9; i++)
			{
				service.ApplySettings(i % 2 == 0 ? on : off);
			}
			await service.SettleAppliesAsync();

			// A state change here can only come from a teardown (watchdog, server closed); say which.
			Assert.True(states.IsEmpty, $"states {string.Join(", ", states)}; last error {host.Status.LastError}: {host.Status.LastErrorDetail}");
			Assert.Equal(BridgeState.Running, host.State);
			Assert.True(host.Speaker.Enabled);
			Assert.True(host.Mic.Enabled);
			(int speaker, int mic) = service.AudioApplies(host);
			Assert.InRange(speaker, 1, 9);
			Assert.InRange(mic, 1, 9);

			// The same settings again: nothing is applied.
			service.ApplySettings(on);
			await service.SettleAppliesAsync();
			Assert.Equal((speaker, mic), service.AudioApplies(host));

			service.ApplySettings(off);
			await service.SettleAppliesAsync();
			Assert.False(host.Speaker.Enabled);
			Assert.False(host.Mic.Enabled);
			Assert.Empty(states);
			await service.ShutdownAsync();
		}
	}

	[Fact]
	public async Task MissingIdentity_LogsAReason()
	{
		BridgeHost host = new(controllers, new TestIdentity(false), new TestAttacher());
		await host.StartAsync();
		Assert.Equal(BridgeError.NoCapture, host.Status.LastError);
		Assert.Equal(BridgeHost.NoCaptureDetail, host.Status.LastErrorDetail);
		await host.StopAsync();
	}

	[Fact]
	public async Task OverlappingStarts_RunOneStartSequence()
	{
		TestHiding hiding = new();
		(RealControllerService service, BridgeHost host) = Create(hiding);
		using (service)
		{
			int waiting = 0;
			host.StateChanged += s =>
			{
				if (s == BridgeState.WaitingForController)
				{
					Interlocked.Increment(ref waiting);
				}
			};
			await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(service.StartBridgeAsync)));
			Assert.True(service.IsBridgeRunning);
			WaitUntil(() => Volatile.Read(ref waiting) >= 1);
			Thread.Sleep(50);
			Assert.Equal(1, hiding.Calls.Count(c => c == "begin"));
			Assert.Equal(1, Volatile.Read(ref waiting));

			// A stop and a start racing end in a consistent state, never two overlapping sequences.
			await Task.WhenAll(Task.Run(service.StopBridgeAsync), Task.Run(service.StartBridgeAsync));
			await service.StopBridgeAsync();
			Assert.Equal(BridgeState.Stopped, host.State);
			Assert.False(service.IsBridgeRunning);
		}
	}

	// The log says who stopped and started the bridge, and that a mic change was applied in place (no stop, no start).
	[Fact]
	public async Task Log_NamesWhoStoppedTheBridge_AndMicChangesApplyInPlace()
	{
		ConcurrentQueue<string> lines = new();
		void Watch(string line)
		{
			if (line.StartsWith("virtual controller ", StringComparison.Ordinal) || line.StartsWith("bridge st", StringComparison.Ordinal)
				|| line.StartsWith("mic apply", StringComparison.Ordinal) || line.StartsWith("bt: mic stream", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		(RealControllerService service, BridgeHost host) = Create(new TestHiding());
		TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
		host.StateChanged += s =>
		{
			if (s == BridgeState.Running)
			{
				running.TrySetResult();
			}
		};
		AppLog.Written += Watch;
		try
		{
			using (service)
			{
				controllers.Links.Enqueue(new TestLink());
				await service.StartBridgeAsync();
				await running.Task;
				service.ApplySettings(new ControllerSettings { Mic = new MicSettings(true) });
				await service.SettleAppliesAsync();
				service.ApplySettings(new ControllerSettings { Mic = MicSettings.Off });
				await service.SettleAppliesAsync();
				await service.ReconnectAsync();
				await service.ShutdownAsync();
			}
		}
		finally
		{
			AppLog.Written -= Watch;
		}
		// Other tests run in parallel and log too: keep this host's sequence only (it is the only one with a reconnect
		// and a mic switch in this order; the lines are compared as a subsequence).
		string[] expected =
		[
			"virtual controller start: UI or auto start",
			"bridge start: UI or auto start",
			"mic apply: on",
			"bt: mic stream on",
			"mic apply: done",
			"mic apply: off",
			"bt: mic stream off",
			"mic apply: done",
			"virtual controller stop: reconnect controller",
			"bridge stop: reconnect controller",
			"virtual controller start: reconnect controller",
			"bridge stop: app exit",
		];
		int at = 0;
		foreach (string line in lines)
		{
			if (at < expected.Length && line.StartsWith(expected[at], StringComparison.Ordinal))
			{
				at++;
			}
		}
		Assert.True(at == expected.Length, $"stopped at '{(at < expected.Length ? expected[at] : "")}' in: {string.Join(" | ", lines)}");
	}
}

// LatestValueWorker: ordered, coalescing, no repeat of an unchanged value.
public class LatestValueWorkerTests
{
	[Fact]
	public async Task ValuesPostedWhileOneIsApplied_CoalesceToTheNewest()
	{
		using ManualResetEventSlim release = new(false);
		List<int> applied = [];
		LatestValueWorker<int> worker = new(v =>
		{
			lock (applied)
			{
				applied.Add(v);
			}
			if (v == 1)
			{
				release.Wait(5000);
			}
		});
		worker.Post(1);
		SpinWait.SpinUntil(() => { lock (applied) { return applied.Count == 1; } }, 5000);
		for (int i = 2; i <= 10; i++)
		{
			worker.Post(i);
		}
		release.Set();
		await worker.Idle;
		Assert.Equal([1, 10], applied);
		Assert.Equal(2, worker.Applied);
	}

	[Fact]
	public async Task AnUnchangedValue_IsNotAppliedAgain_UnlessForced()
	{
		int count = 0;
		LatestValueWorker<string> worker = new(_ => Interlocked.Increment(ref count));
		Assert.True(worker.Post("a"));
		await worker.Idle;
		Assert.False(worker.Post("a"));
		await worker.Idle;
		Assert.Equal(1, count);
		Assert.True(worker.Post("a", force: true));
		await worker.Idle;
		Assert.Equal(2, count);
	}

	[Fact]
	public async Task AFailingApply_DoesNotStopTheWorker()
	{
		List<int> applied = [];
		LatestValueWorker<int> worker = new(v =>
		{
			if (v == 1)
			{
				throw new InvalidOperationException("gone");
			}
			applied.Add(v);
		}, run: a =>
		{
			a();
			return Task.CompletedTask;
		});
		worker.Post(1);
		worker.Post(2);
		await worker.Idle;
		Assert.Equal([2], applied);
	}
}
