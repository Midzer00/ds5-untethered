// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Input;
using Kopru.Core.Localization;
using Kopru.Core.Protocol;

namespace Kopru.App.Tests;

// A pad opened read-only for the test page: full reports on demand; records writes and disposal.
internal sealed class LiveLink : IHidLink
{
	private readonly BlockingCollection<byte[]> inputs = [];
	private readonly CancellationTokenSource closed = new();

	public HidDevice Device { get; } = new(TestLink.BtPath, 0x054C, 0x0CE6, 78, 547, 64);
	public ConcurrentQueue<byte[]> Writes { get; } = new();
	public bool Disposed { get; private set; }

	public void Feed(byte leftX)
	{
		byte[] report = new byte[78];
		report[0] = 0x31;
		report[2] = leftX;
		report[2 + 7] = 0x28; // cross, hat released
		report[2 + 32] = 0x80;
		report[2 + 36] = 0x80;
		report[2 + BatteryStatus.StatusOffset] = 0x07;
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc.Compute(Crc.InputSeed, report.AsSpan(0, 74)));
		inputs.Add(report);
	}

	public int Read(Span<byte> buffer)
	{
		try
		{
			byte[] next = inputs.Take(closed.Token);
			next.CopyTo(buffer);
			return next.Length;
		}
		catch (OperationCanceledException)
		{
			throw new IOException("closed");
		}
	}

	public void Write(ReadOnlySpan<byte> report) => Writes.Enqueue(report.ToArray());
	public byte[] GetFeature(byte reportId) => new byte[64];
	public void CancelRead() => closed.Cancel();

	public void Dispose()
	{
		Disposed = true;
		closed.Cancel();
	}
}

internal sealed class LiveSource : IReadOnlyControllerSource
{
	public ConcurrentQueue<LiveLink> Opened { get; } = new();

	public IHidLink? TryOpenReadOnly()
	{
		LiveLink link = new();
		for (int i = 0; i < 3; i++)
		{
			link.Feed(200);
		}
		Opened.Enqueue(link);
		return link;
	}
}

public class TesterTests
{
	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	// Some buttons pressed, both sticks deflected, triggers part-way, two fingers down, motion and battery.
	public static InputSnapshot Sample { get; } = new(40, 70, 215, 160, 96, 230,
		PadButtons.Cross | PadButtons.Triangle | PadButtons.DpadLeft | PadButtons.R1 | PadButtons.L2 | PadButtons.Options,
		new TouchPoint(true, 3, 480, 300), new TouchPoint(true, 4, 1500, 760),
		-1200, 340, 15, 410, 8150, -620, new BatteryStatus(70, ChargeState.Charging), true);

	[AvaloniaFact]
	public void Snapshot_UpdatesTheViewModel_OnTick()
	{
		var service = new FakeControllerService(animate: false);
		var settings = new SettingsState(service);
		var vm = new TesterViewModel(service, settings, live: false);
		vm.SetVisible(true);
		Assert.NotNull(service.LiveHandler);
		Assert.False(vm.HasInput);

		service.LiveHandler!(Sample);
		Assert.False(vm.HasInput); // nothing changes before the next tick
		vm.Tick();

		Assert.True(vm.HasInput);
		Assert.Equal(Sample.Buttons, vm.Pressed);
		Assert.True(vm.Buttons.Single(b => b.Button == PadButtons.Cross).IsPressed);
		Assert.False(vm.Buttons.Single(b => b.Button == PadButtons.Circle).IsPressed);
		Assert.Equal("230", vm.R2.ValueText);
		Assert.Equal(230 / 255.0 * 100, vm.R2.Level, 3);
		Assert.Equal(vm.R2.Level, vm.R2.GameLevel, 3);
		Assert.True(vm.LeftStick.IsLive);
		Assert.True(vm.LeftStick.RawX < -0.6);
		Assert.Equal("X 40 · Y 70", vm.LeftStick.ValueText);
		Assert.Equal("X 480 · Y 300", vm.Touch1Text);
		Assert.Equal(-1200, vm.Gyro[0].Value);
		Assert.True(vm.Gyro[0].Negative > 0 && vm.Gyro[0].Positive == 0);
		Assert.Equal(8150, vm.Accel[1].Value);
		Assert.Equal(new BatteryStatus(70, ChargeState.Charging), vm.Battery);
		Assert.Equal(1, vm.RateHz);

		// Tuning moves the tuned dot, not the raw one.
		double raw = vm.RightStick.RawX;
		settings.Tuning = settings.Tuning with { RightStick = settings.Tuning.RightStick with { InnerDeadzone = 50 } };
		Assert.Equal(50, vm.RightStick.Inner);
		Assert.Equal(raw, vm.RightStick.RawX);
		Assert.True(Math.Abs(vm.RightStick.TunedX) < Math.Abs(raw));
	}

	[AvaloniaFact]
	public void LeavingThePage_ReleasesTheInput_AndOutputTestsUseTheService()
	{
		var service = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(service);
		Assert.Null(service.LiveHandler);
		vm.OpenController(ControllerPageViewModel.TestTab);
		Assert.NotNull(service.LiveHandler);
		Assert.Equal(1, service.LiveOpens);

		vm.Tester.TestHapticsCommand.Execute(HapticSide.Left);
		vm.Tester.TestLightCommand.Execute(vm.Tester.Swatches[0]);
		Assert.Equal(2, service.TestCount);

		vm.Navigate(vm.Overview);
		Assert.Null(service.LiveHandler);
		Assert.False(vm.Tester.HasInput);
	}

	[Fact]
	public async Task RealService_ReadsReadOnly_NeverWrites_ReleasesOnLeaveAndBridgeStart()
	{
		var controllers = new TestControllers();
		var source = new LiveSource();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			BatteryProbe = new BatteryProbe(source)
		}, new TestHiding(), controllers, a => a(), _ => null, timers: false);

		// Page shown, then left.
		ConcurrentQueue<InputSnapshot> seen = new();
		IDisposable session = service.OpenLiveInput(seen.Enqueue);
		WaitUntil(() => seen.Count >= 3);
		Assert.Equal(200, seen.First().LeftX);
		Assert.True(seen.First().IsPressed(PadButtons.Cross));
		session.Dispose();
		WaitUntil(() => source.Opened.All(l => l.Disposed));
		int opens = source.Opened.Count;
		Thread.Sleep(RealControllerService.LiveInputRetry * 2);
		Assert.Equal(opens, source.Opened.Count);

		// Page shown again, then the bridge starts: the read-only handle is released before the bridge opens the pad.
		session = service.OpenLiveInput(seen.Enqueue);
		WaitUntil(() => source.Opened.Count > opens);
		controllers.Links.Enqueue(new TestLink());
		await service.StartBridgeAsync();
		Assert.All(source.Opened, l => Assert.True(l.Disposed));
		opens = source.Opened.Count;
		Thread.Sleep(RealControllerService.LiveInputRetry * 2);
		Assert.Equal(opens, source.Opened.Count);
		session.Dispose();
		await service.StopBridgeAsync();

		Assert.All(source.Opened, l => Assert.Empty(l.Writes));
	}

	// Renders every tab of the controller page to docs/screens/controller-{tab}-{tr,en}.png, the Test tab with a sample
	// snapshot, also at full height and at more window sizes (controller-test-{w}x{h}-...: the window's minimum, a 1080p
	// and an ultra-wide screen), only with KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public void RenderControllerScreens()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				var service = new FakeControllerService(animate: false);
				var vm = new MainWindowViewModel(service);
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();

				void ShowSample()
				{
					service.LiveHandler!(Sample);
					vm.Tester.Tick();
				}

				void Capture(string name)
				{
					ControllerTestFitTests.Settle(window);
					Thread.Sleep(320);
					// The sample again right before the frame (the live view marks input older than a second as gone).
					if (vm.Tester.IsVisible)
					{
						ShowSample();
					}
					ControllerTestFitTests.Settle(window);
					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (writeFiles)
					{
						frame!.Save(Path.Combine(RepoRoot(), "docs", "screens", $"{name}-{code}.png"));
					}
				}


				vm.OpenController(ControllerPageViewModel.TestTab);
				ShowSample();
				Capture("controller-test");
				// The whole page: the window grown to the page's height (the same as above while the tab fits).
				var scroll = window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().First(s => s.IsEffectivelyVisible && s.Classes.Contains("page"));
				window.Height = 700 + Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
				ShowSample();
				Capture("controller-test-full");
				foreach ((double width, double height) in new[] { (960.0, 640.0), (1920.0, 1080.0), (2560.0, 1080.0) })
				{
					window.Width = width;
					window.Height = height;
					ShowSample();
					Capture($"controller-test-{width}x{height}");
				}
				window.Width = 1100;
				window.Height = 700;

				vm.Controller.SelectedTab = ControllerPageViewModel.OutputsTab;
				Capture("controller-outputs");
				vm.Controller.SelectedTab = ControllerPageViewModel.RemapTab;
				Capture("controller-remap");
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
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}
}
