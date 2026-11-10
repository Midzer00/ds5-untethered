// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// A controller link that answers 0x20 / 0x09 from fixtures; reads block until it is closed (like a quiet pad).
internal sealed class InfoHidLink(Transport transport, ulong address) : IHidLink
{
	public const string UsbPath = @"\\?\hid#vid_054c&pid_0ce6&mi_03#cable";

	private readonly CancellationTokenSource closed = new();

	public HidDevice Device { get; } = new(transport == Transport.Bluetooth ? TestLink.BtPath : UsbPath, 0x054C, 0x0CE6, 78, 547, 64);
	public int Writes;

	public static byte[] Firmware()
	{
		byte[] r = new byte[64];
		r[0] = 0x20;
		Encoding.ASCII.GetBytes("Jun  5 2023").CopyTo(r, 1);
		Encoding.ASCII.GetBytes("14:13:39").CopyTo(r, 12);
		BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), 0x00000616);
		BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(28), 0x0110002A);
		BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(44), 0x0630);
		return r;
	}

	public int Read(Span<byte> buffer)
	{
		closed.Token.WaitHandle.WaitOne();
		throw new IOException("closed");
	}

	public void Write(ReadOnlySpan<byte> report) => Interlocked.Increment(ref Writes);

	public byte[] GetFeature(byte reportId)
	{
		if (reportId == 0x20)
		{
			return Firmware();
		}
		byte[] r = new byte[64];
		r[0] = reportId;
		if (reportId == 0x09)
		{
			for (int i = 0; i < 6; i++)
			{
				r[1 + i] = (byte)(address >> (8 * i));
			}
		}
		return r;
	}

	public void CancelRead() => closed.Cancel();
	public void Dispose() => closed.Cancel();
}

public class ControllerInfoAppTests
{
	private const ulong Address = 0xA0B1C2D3E4F5;
	private const string BtId = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\test";

	private static readonly HidDevice BtPad = new(TestLink.BtPath, 0x054C, 0x0CE6, 78, 547, 64);
	private static readonly HidDevice UsbPad = new(InfoHidLink.UsbPath, 0x054C, 0x0CE6, 64, 48, 64);

	private readonly TestControllers controllers = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private RealControllerService Service(TestHiding hiding, List<ServiceNotice>? notices = null)
	{
		RealControllerService service = new(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		}, hiding, controllers, a => a(), _ => BtId, timers: false);
		if (notices != null)
		{
			service.Notice += (_, n) =>
			{
				lock (notices)
				{
					notices.Add(n);
				}
			};
		}
		return service;
	}

	[Fact]
	public async Task Bridge_ReadsTheDetails_OnConnect()
	{
		using RealControllerService service = Service(new TestHiding());
		InfoHidLink link = new(Transport.Bluetooth, Address);
		controllers.Links.Enqueue(link);
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.Details != null);
		Assert.Equal("06.30", service.Details!.Firmware!.FirmwareText);
		Assert.Equal(Address, service.Details.Address);
		service.DeviceTick();
		Assert.NotNull(service.ConnectedSince);
		await service.StopBridgeAsync();
	}

	[Fact]
	public async Task Cable_StopsTheBridge_WithAToast_AndBluetoothResumesIt()
	{
		List<ServiceNotice> notices = [];
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding, notices);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.Details?.Address == Address);

		// Plugged in: Bluetooth drops, the same controller shows up on USB (0x09 gives the same address).
		List<HidDevice> opened = [];
		service.OpenUsbReadOnly = d =>
		{
			opened.Add(d);
			return new InfoHidLink(Transport.Usb, Address);
		};
		controllers.Paired.Clear();
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => !service.IsBridgeRunning);
		WaitUntil(() => notices.Any(n => n.Kind == ServiceNoticeKind.CableConnected));
		Assert.True(service.IsPausedForCable);
		Assert.Equal("restore", hiding.Calls[^1]);
		Assert.Equal(Transport.Usb, service.Details?.Transport);
		service.DeviceTick();
		Assert.Single(opened); // read once per plug
		Assert.False(service.IsBridgeRunning);

		// Unplugged and back on Bluetooth: the bridge resumes by itself.
		controllers.Paired.Clear();
		controllers.Paired.Add(BtPad);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		service.DeviceTick();
		WaitUntil(() => service.IsBridgeRunning);
		WaitUntil(() => service.IsGameConnected);
		Assert.False(service.IsPausedForCable);
		await service.StopBridgeAsync();
	}

	[Fact]
	public async Task Cable_OfAnotherController_LeavesTheBridgeAlone()
	{
		using RealControllerService service = Service(new TestHiding());
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected);
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, 0x112233445566);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		await Task.Delay(100);
		Assert.True(service.IsBridgeRunning);
		Assert.False(service.IsPausedForCable);
		await service.StopBridgeAsync();
	}

	[Fact]
	public async Task Reconnect_StopsReopensAndRestarts_WithoutWritingToTheController()
	{
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding);
		InfoHidLink first = new(Transport.Bluetooth, Address);
		InfoHidLink second = new(Transport.Bluetooth, Address);
		controllers.Links.Enqueue(first);
		controllers.Links.Enqueue(second);
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected);

		await service.ExecuteFix(FixAction.ReconnectController);
		WaitUntil(() => service.IsGameConnected && controllers.Links.IsEmpty);
		Assert.True(service.IsBridgeRunning);
		Assert.Equal(["begin", "hide " + BtId, "restore", "begin", "hide " + BtId], hiding.Calls);
		await service.StopBridgeAsync();
	}

	[Fact]
	public async Task Reconnect_WhileStopped_DoesNotStartTheBridge()
	{
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding);
		controllers.Paired.Add(BtPad);
		await service.ReconnectAsync();
		Assert.False(service.IsBridgeRunning);
		Assert.Empty(hiding.Calls);
		Assert.True(service.IsConnected);
	}

	// View model, tray and toast.

	private static ControllerDetails Details() => new(TestLink.BtPath, false, Transport.Bluetooth,
		FirmwareInfo.Parse(InfoHidLink.Firmware()), Address);

	[AvaloniaFact]
	public async Task Card_ShowsTheFields_CopiesMaskedText_AndReconnects()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			StepTime time = new();
			var fake = new FakeControllerService(animate: false) { Details = Details(), ConnectedSince = time.GetUtcNow() - TimeSpan.FromSeconds(303) };
			var vm = new ControllerInfoViewModel(fake, time);
			Assert.True(vm.HasDetails);
			Assert.Equal("DualSense", vm.Model);
			Assert.Equal("Bluetooth", vm.Connection);
			Assert.Equal("06.30", vm.Firmware);
			Assert.Equal("0x00000616", vm.Hardware);
			Assert.Equal("XX:XX:XX:XX:E4:F5", vm.Address);
			Assert.Equal("5 min 03 s", vm.ConnectedFor);
			Assert.Equal("78%", vm.Battery);
			Assert.Equal("DualSense · Bluetooth · firmware 06.30 · connected 5 min 03 s", vm.Compact);

			time.Advance(TimeSpan.FromSeconds(60));
			vm.Tick();
			Assert.Equal("6 min 03 s", vm.ConnectedFor);

			string? clip = null;
			vm.Clipboard = t =>
			{
				clip = t;
				return Task.CompletedTask;
			};
			await vm.CopyCommand.ExecuteAsync(null);
			Assert.Contains("XX:XX:XX:XX:E4:F5", clip);
			Assert.DoesNotContain("A0:B1", clip);
			Assert.Contains("Connected For: 6 min 03 s", clip);
			Assert.Equal(Text.InfoCopied(), vm.CopyFeedback);

			await vm.ReconnectCommand.ExecuteAsync(null);
			Assert.Equal(1, fake.Reconnects);

			fake.Details = null;
			fake.IsConnected = false;
			vm.Refresh();
			Assert.False(vm.HasDetails);
			Assert.Equal(Text.InfoNotConnected(), vm.Compact);
			Assert.False(vm.ReconnectCommand.CanExecute(null));
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public async Task Tray_Reconnect_AndTheCableToast()
	{
		var fake = new FakeControllerService(animate: false) { Details = Details() };
		var vm = new MainWindowViewModel(fake);
		vm.Sticks.StopPreview();
		var tray = new TrayViewModel(fake, vm.Settings, new NoShell());
		Assert.True(tray.CanReconnect);
		Assert.Equal(Text.ReconnectController(), tray.ReconnectText);
		await tray.ReconnectCommand.ExecuteAsync(null);
		Assert.Equal(1, fake.Reconnects);

		List<AppNotification> raised = [];
		vm.Notifications.Raised += raised.Add;
		vm.Notifications.OnNotice(new ServiceNotice(ServiceNoticeKind.CableConnected));
		AppNotification toast = Assert.Single(raised);
		Assert.Equal(NotificationType.CableConnected, toast.Type);
		Assert.Equal(Text.NotifyCableTitle(), toast.Title);
		Assert.Equal(Text.ReconnectController(), OverviewHealthLabel(FixAction.ReconnectController));
	}

	private static string OverviewHealthLabel(FixAction fix) => HealthFixes.Label(fix);

	private sealed class NoShell : IAppShell
	{
		public void ShowMainWindow(AppPage? page = null) { }
		public void Exit() { }
	}

	// Renders the controller test page with the info card to docs/screens/tester-info-tr.png / -en.png
	// (files only with KOPRU_RENDER_SCREENS=1).
	[AvaloniaFact]
	public void RenderInfoScreens()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				var service = new FakeControllerService(animate: false)
				{
					Details = Details(),
					ConnectedSince = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(4 * 60 + 7)
				};
				var vm = new MainWindowViewModel(service);
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				vm.OpenController(ControllerPageViewModel.TestTab);
				service.LiveHandler!(TesterTests.Sample);
				vm.Tester.Tick();
				vm.Tester.Info.Refresh();
				Dispatcher.UIThread.RunJobs();
				// The info card is the last one on the Test tab.
				window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().First(s => s.Classes.Contains("page") && s.IsEffectivelyVisible).ScrollToEnd();
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				Thread.Sleep(320);
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				if (writeFiles)
				{
					frame!.Save(Path.Combine(RepoRoot(), "docs", "screens", $"tester-info-{code}.png"));
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
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}
}
