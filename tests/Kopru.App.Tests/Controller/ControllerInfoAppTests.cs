// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Avalonia.Controls;
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

	public void Write(ReadOnlySpan<byte> report)
	{
		if (FailWrites)
		{
			throw new IOException("boom");
		}
		Interlocked.Increment(ref Writes);
	}

	public bool FailWrites;

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
	private const string UsbId = @"HID\VID_054C&PID_0CE6&MI_03\cable";

	private static readonly HidDevice BtPad = new(TestLink.BtPath, 0x054C, 0x0CE6, 78, 547, 64);
	private static readonly HidDevice UsbPad = new(InfoHidLink.UsbPath, 0x054C, 0x0CE6, 64, 48, 64);

	private static HidDevice Usb(string name) => new(@"\\?\hid#vid_054c&pid_0ce6&mi_03#" + name, 0x054C, 0x0CE6, 64, 48, 64);
	private static string UsbIdOf(string name) => @"HID\VID_054C&PID_0CE6&MI_03\" + name;

	private readonly TestControllers controllers = new();

	// The host the last Service() call built, for hooks like OpenWired.
	private BridgeHost? host;

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	// Wired, and the reconcile pass that entered it finished too: its entered record and retry time are
	// settled, so killing the link, unlisting the pad or advancing the time next cannot race the in-flight pass.
	private static void WaitWired(RealControllerService service) =>
		WaitUntil(() => service.IsWired && !service.IsReconcilingTwins);

	// Not wired, and no reconcile pass is in flight either.
	private static void WaitNotWired(RealControllerService service) =>
		WaitUntil(() => !service.IsWired && !service.IsReconcilingTwins);

	private RealControllerService Service(TestHiding hiding, List<ServiceNotice>? notices = null, TimeProvider? time = null,
		Kopru.Core.Health.HealthProbe? healthProbe = null)
	{
		host = new BridgeHost(controllers, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		RealControllerService service = new(host, hiding, controllers, a => a(),
			d => d.Transport == Transport.Usb ? UsbIdOf(d.Path[(d.Path.LastIndexOf('#') + 1)..]) : BtId,
			timers: false, time: time, healthProbe: healthProbe);
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

	// CableMode.ChargeOnly: cable in with Bluetooth up only charges. The bridge keeps running on Bluetooth, the USB twin
	// is hidden (never the virtual pad), no toast; cable out unhides it and the bridge still runs.
	[Fact]
	public async Task Cable_WithBluetoothUp_KeepsTheBridge_HidesTheUsbTwin_AndUnhidesItWhenOut()
	{
		List<ServiceNotice> notices = [];
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, notices, time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));
		Assert.True(service.CablePluggedIn);
		Assert.False(service.IsPausedForCable);
		Assert.True(service.IsBridgeRunning);
		Assert.True(service.IsGameConnected);
		Assert.Equal(Transport.Bluetooth, service.Details?.Transport);
		service.DeviceTick();
		await Task.Delay(100);
		Assert.Equal(["begin", "hide " + BtId, "hide usb " + UsbId], Calls(hiding));

		// Cable out: unhidden once it stayed out for the delay; the bridge never stopped.
		controllers.Paired.Remove(UsbPad);
		service.DeviceTick();
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "unhide usb " + UsbId));
		Assert.False(service.CablePluggedIn);
		Assert.True(service.IsBridgeRunning);
		lock (notices)
		{
			Assert.DoesNotContain(notices, n => n.Kind == ServiceNoticeKind.CableConnected);
		}

		// Plugged in again, then the bridge stops for another reason: the session's restore unhides the twin too.
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => Calls(hiding).Count(c => c == "hide usb " + UsbId) == 2);
		await service.StopBridgeAsync();
		Assert.Equal("restore", Calls(hiding)[^1]);
		service.DeviceTick();
		Assert.False(service.CablePluggedIn);
	}

	// A cable seen while a bridge start still waits for its hiding session: the hide is queued behind the start and
	// lands in the session that start created, not lost to the window before it.
	[Fact]
	public async Task Cable_SeenWhileTheBridgeStarts_IsHiddenOnceTheHidingSessionExists()
	{
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding, time: new StepTime());
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.Details?.Address == Address);
		await service.StopBridgeAsync();
		service.DeviceTick();

		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		hiding.HoldBegin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task start = service.StartBridgeAsync();
		Assert.True(service.IsBridgeRunning);
		service.DeviceTick();
		await Task.Delay(150);
		Assert.False(HasCall(hiding, "hide usb " + UsbId));

		hiding.HoldBegin.SetResult();
		await start;
		// A twin is only wanted once a bridge actually holds the Bluetooth link.
		WaitUntil(() => service.IsGameConnected);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));
		List<string> calls = Calls(hiding);
		Assert.True(calls.LastIndexOf("begin") < calls.IndexOf("hide usb " + UsbId));
		Assert.True(service.CablePluggedIn);
		await service.StopBridgeAsync();
	}

	private static List<string> Calls(TestHiding hiding)
	{
		lock (hiding.Calls)
		{
			return [.. hiding.Calls];
		}
	}

	private static bool HasCall(TestHiding hiding, string call) => Calls(hiding).Contains(call);

	// Bluetooth lost while the cable stays in (USB only): the old behaviour, the bridge stops with a toast.
	[Fact]
	public async Task Cable_WithBluetoothLost_StopsTheBridge_WithAToast_AndBluetoothResumesIt()
	{
		List<ServiceNotice> notices = [];
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, notices, time);
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
		Assert.True(service.IsBridgeRunning); // the stop is debounced, like the resume
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => !service.IsBridgeRunning);
		WaitUntil(() => notices.Any(n => n.Kind == ServiceNoticeKind.CableConnected));
		Assert.True(service.IsPausedForCable);
		Assert.Equal("restore", hiding.Calls[^1]);
		Assert.Equal(Transport.Usb, service.Details?.Transport);
		service.DeviceTick();
		Assert.Single(opened); // read once per plug
		Assert.False(service.IsBridgeRunning);

		// Unplugged and back on Bluetooth: the bridge resumes by itself once the cable stayed out for the resume delay.
		controllers.Paired.Clear();
		controllers.Paired.Add(BtPad);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		service.DeviceTick();
		await Task.Delay(100);
		Assert.False(service.IsBridgeRunning);
		time.Advance(RealControllerService.CableResumeDelay);
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

	// A paired controller that shows up on USB only is not the bridged one: it is never hidden; only the bridged
	// controller's own twin is.
	[Fact]
	public async Task Cable_APairedControllerOnUsbOnly_IsNotHidden_WhileAnotherIsBridged()
	{
		const ulong P2 = 0x665544332211;
		HealthServiceTests.FakeSystem system = new()
		{
			Paired = [new PairedController("DualSense Wireless Controller", Address, false, false),
				new PairedController("DualSense Wireless Controller", P2, false, false)]
		};
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time,
			healthProbe: new HealthProbe(system, system, system, null, null));
		await service.RefreshHealthAsync();
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		HidDevice p2 = Usb("p2");
		Dictionary<string, ulong> answers = new() { [p2.Path] = P2, [UsbPad.Path] = Address };
		service.OpenUsbReadOnly = d => new InfoHidLink(Transport.Usb, answers[d.Path]);
		controllers.Paired.Add(p2);
		service.DeviceTick();
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		await Task.Delay(150);
		Assert.DoesNotContain(Calls(hiding), c => c.StartsWith("hide usb"));
		Assert.True(service.IsBridgeRunning);
		Assert.False(service.IsPausedForCable);
		Assert.False(service.CablePluggedIn);

		// The bridged controller's own cable, listed after p2: only its twin is hidden.
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));
		Assert.DoesNotContain(Calls(hiding), c => c.StartsWith("hide usb " + UsbIdOf("p2")));
		await service.StopBridgeAsync();
	}

	// Our own virtual pads are listed as USB devices too (each slot answers 0x09 with its own virtual address): none of
	// them is ever hidden, wherever they sit in the listing.
	[Fact]
	public async Task Cable_VirtualPadsListedBeforeTheTwin_AreNeverHidden()
	{
		ulong slot2 = PairingInfo.ParseAddress([PairingInfo.ReportId, .. Kopru.Core.Virtual.DeviceIdentity.VirtualAddressFor(2)])!.Value;
		HealthServiceTests.FakeSystem system = new()
		{
			// Even if a virtual address ever got into the known set.
			Paired = [new PairedController("DualSense Wireless Controller", Address, false, false),
				new PairedController("DualSense", slot2, false, false)]
		};
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding, healthProbe: new HealthProbe(system, system, system, null, null));
		await service.RefreshHealthAsync();
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		HidDevice virtual1 = Usb("virtual1"), virtual2 = Usb("virtual2");
		Dictionary<string, ulong> answers = new()
		{
			[virtual1.Path] = PairingInfo.VirtualAddress,
			[virtual2.Path] = slot2,
			[UsbPad.Path] = Address
		};
		service.OpenUsbReadOnly = d => new InfoHidLink(Transport.Usb, answers[d.Path]);
		controllers.Paired.AddRange([virtual1, virtual2, UsbPad]);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));
		await Task.Delay(150);
		Assert.Equal(["hide usb " + UsbId], Calls(hiding).Where(c => c.StartsWith("hide usb")));
		await service.StopBridgeAsync();
	}

	// Bluetooth gone for a moment while the cable stays in (a re-enumeration, a sleep): the bridge must not stop; the
	// USB twin stays hidden the whole time.
	[Fact]
	public async Task Cable_ABluetoothBlipWhileCabled_DoesNotStopTheBridge()
	{
		List<ServiceNotice> notices = [];
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, notices, time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));

		controllers.Paired.Remove(BtPad);
		service.DeviceTick();
		time.Advance(TimeSpan.FromSeconds(2));
		controllers.Paired.Add(BtPad);
		service.DeviceTick();
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		await Task.Delay(150);
		Assert.True(service.IsBridgeRunning);
		Assert.True(service.IsGameConnected);
		Assert.False(service.IsPausedForCable);
		Assert.DoesNotContain(Calls(hiding), c => c == "restore");
		Assert.DoesNotContain(Calls(hiding), c => c.StartsWith("unhide usb"));
		lock (notices)
		{
			Assert.DoesNotContain(notices, n => n.Kind == ServiceNoticeKind.CableConnected);
		}
		await service.StopBridgeAsync();
	}

	// The pad is asked its 0x09 address again right before hiding (never trusted from the tick's cached read): a pad
	// that now answers as our virtual one is refused.
	[Fact]
	public async Task Cable_TheTwinIsAskedAgainRightBeforeHiding_AndAVirtualAnswerIsRefused()
	{
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		ulong slot2 = PairingInfo.ParseAddress([PairingInfo.ReportId, .. Kopru.Core.Virtual.DeviceIdentity.VirtualAddressFor(2)])!.Value;
		int opens = 0;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, opens++ == 0 ? Address : slot2);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => opens >= 2);
		await Task.Delay(150);
		Assert.DoesNotContain(Calls(hiding), c => c.StartsWith("hide usb"));
		await service.StopBridgeAsync();
	}

	// A hide HidHide refuses is not lost: the next tick after the retry interval tries it again. The logged failure
	// keeps the exception kind but never its message (it can carry an instance id or a user path).
	[Fact]
	public async Task Cable_AFailedHide_IsRetried_AfterTheRetryInterval()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("cable:", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			hiding.FailUsbHides = 1;
			service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
			controllers.Paired.Add(UsbPad);
			service.DeviceTick();
			WaitUntil(() => Calls(hiding).Count(c => c == "hide usb " + UsbId) == 1);
			WaitUntil(() => !string.IsNullOrEmpty(service.Warning));
			WaitUntil(() =>
			{
				lock (lines)
				{
					return lines.Any(l => l.Contains("HidHideException", StringComparison.Ordinal));
				}
			});
			lock (lines)
			{
				Assert.DoesNotContain(lines, l => l.Contains("boom-secret-detail", StringComparison.Ordinal));
			}

			service.DeviceTick();
			await Task.Delay(150);
			Assert.Equal(1, Calls(hiding).Count(c => c == "hide usb " + UsbId));
			WaitNotWired(service);
			time.Advance(RealControllerService.CableRetryInterval);
			service.DeviceTick();
			WaitUntil(() => Calls(hiding).Count(c => c == "hide usb " + UsbId) == 2);
			WaitUntil(() => service.Warning.Length == 0);
			await service.StopBridgeAsync();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
	}

	// A USB pad that does not answer 0x09 yet (the cable is still settling) is asked again after the retry interval,
	// not remembered for the life of the plug; once it answers as the bridged controller its twin is hidden.
	[Fact]
	public async Task Cable_APadThatDoesNotAnswerAtFirst_IsAskedAgain_AndThenHidden()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		int opens = 0;
		service.OpenUsbReadOnly = _ =>
		{
			opens++;
			return opens switch
			{
				1 => null,
				2 => new InfoHidLink(Transport.Usb, 0), // the 0x09 report holds no address yet
				_ => new InfoHidLink(Transport.Usb, Address)
			};
		};
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		Assert.Equal(1, opens);
		Assert.False(HasCall(hiding, "hide usb " + UsbId));
		service.DeviceTick();
		Assert.Equal(1, opens);
		time.Advance(RealControllerService.CableRetryInterval);
		service.DeviceTick();
		Assert.Equal(2, opens);
		Assert.False(HasCall(hiding, "hide usb " + UsbId));
		time.Advance(RealControllerService.CableRetryInterval);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));
		await service.StopBridgeAsync();
	}

	// CableMode.Wired (EXPERIMENTAL): once the twin is hidden the bridge moves input and output to its USB link while
	// Bluetooth stays connected (the bridge never stops, games never see the wired pad). Cable out of the listing: a
	// one-tick gap keeps the wired link, the second tick leaves it (no time advance), the twin still hidden, unhidden
	// after the usual delay.
	[Fact]
	public async Task Cable_WiredMode_SwitchesToUsbAfterHiding_AndBackToBluetoothWhenTheCableIsOut()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		List<int> openAt = [];
		host!.OpenWired = _ =>
		{
			lock (hiding.Calls)
			{
				openAt.Add(hiding.Calls.Count);
			}
			return new InfoHidLink(Transport.Usb, Address);
		};
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));
		WaitWired(service);
		Assert.True(service.IsBridgeRunning);
		Assert.True(service.IsGameConnected);
		int hideAt = Calls(hiding).IndexOf("hide usb " + UsbId);
		Assert.True(hideAt >= 0 && openAt.Count == 1 && openAt[0] > hideAt, $"hide at {hideAt}, wired opens at [{string.Join(',', openAt)}]");

		// Cable out of the listing: a one-tick gap keeps the wired link, the second tick leaves it (no time
		// advance), the twin still hidden.
		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("wired:", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			controllers.Paired.Remove(UsbPad);
			service.DeviceTick();
			await Task.Delay(150);
			Assert.True(service.IsWired);
			service.DeviceTick();
			WaitNotWired(service);
			WaitUntil(() =>
			{
				lock (lines)
				{
					return lines.Contains("wired: off (usb pad not listed)");
				}
			});
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
		Assert.False(HasCall(hiding, "unhide usb " + UsbId));
		Assert.True(service.IsBridgeRunning);
		Assert.True(service.IsGameConnected);
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "unhide usb " + UsbId));
		Assert.True(service.IsBridgeRunning);
		Assert.DoesNotContain(Calls(hiding), c => c == "restore");
		await service.StopBridgeAsync();
	}

	// Switching back to ChargeOnly at runtime leaves the wired link while the twin stays hidden; switching to Wired
	// again re-enters on the next tick.
	[Fact]
	public async Task Cable_WiredMode_FallsBackToChargeOnly_AtRuntime()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		host!.OpenWired = _ => new InfoHidLink(Transport.Usb, Address);
		host.WiredAudioRetryInterval = TimeSpan.FromHours(1);
		int audioAsks = 0;
		host.OpenWiredAudio = _ =>
		{
			audioAsks++;
			return null;
		};
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);

		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("wired:", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			service.CableMode = CableMode.ChargeOnly;
			service.DeviceTick();
			WaitNotWired(service);
			WaitUntil(() =>
			{
				lock (lines)
				{
					return lines.Contains("wired: off (charge only chosen)");
				}
			});
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
		Assert.False(HasCall(hiding, "unhide usb " + UsbId));
		Assert.True(service.IsBridgeRunning);

		service.CableMode = CableMode.Wired;
		service.DeviceTick();
		WaitWired(service);
		// The USB audio endpoint is asked once per enter (the retry is switched off above so the count is fixed).
		Assert.Equal(2, audioAsks);
		await service.StopBridgeAsync();
	}

	// The same fallback through the settings path: a pushed ChargeOnly takes the bridge back to Bluetooth, the twin
	// stays hidden and the bridge keeps running. StateChanged is raised when wired mode starts and when it ends.
	[Fact]
	public async Task Cable_WiredMode_FallsBackToChargeOnly_ThroughTheSettings()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		host!.OpenWired = _ => new InfoHidLink(Transport.Usb, Address);
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		int stateChanges = 0;
		service.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);
		int beforeTick = stateChanges;
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);
		WaitUntil(() => stateChanges > beforeTick);
		int afterEnter = stateChanges;

		service.ApplySettings(new ControllerSettings { CableMode = CableMode.ChargeOnly });
		service.DeviceTick();
		WaitNotWired(service);
		WaitUntil(() => stateChanges > afterEnter);
		Assert.False(HasCall(hiding, "unhide usb " + UsbId));
		Assert.True(service.IsBridgeRunning);
		await service.StopBridgeAsync();
	}

	// The default (ChargeOnly) never opens the USB link: the twin is hidden and everything stays on Bluetooth.
	[Fact]
	public async Task Cable_ChargeOnlyMode_NeverOpensTheUsbLink()
	{
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding, time: new StepTime());
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		int opens = 0;
		host!.OpenWired = _ =>
		{
			opens++;
			return new InfoHidLink(Transport.Usb, Address);
		};
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));
		service.DeviceTick();
		service.DeviceTick();
		await Task.Delay(150);
		Assert.Equal(0, opens);
		Assert.False(service.IsWired);
		await service.StopBridgeAsync();
	}

	// Bluetooth lost while cabled and wired: the bridge leaves the USB link at once (its session is about to go with
	// the bridge), then the stop works exactly as in charge-only mode.
	[Fact]
	public async Task Cable_WiredMode_BluetoothLostWhileCabled_LeavesWiredThenStopsAsBefore()
	{
		List<ServiceNotice> notices = [];
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, notices, time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		host!.OpenWired = _ => new InfoHidLink(Transport.Usb, Address);
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);

		controllers.Paired.Remove(BtPad);
		service.DeviceTick();
		// A one-tick listing gap keeps the wired link; the second tick leaves it.
		Assert.True(service.IsWired);
		service.DeviceTick();
		WaitNotWired(service);
		Assert.True(service.IsBridgeRunning);
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		// StopForCable runs StopBridgeAsync (including RestoreHidingAsync) and then raises CableConnected;
		// waiting on the notice guarantees the stop and restore have both finished before checking hiding calls.
		WaitUntil(() => { lock (notices) return notices.Any(n => n.Kind == ServiceNoticeKind.CableConnected); });
		Assert.False(service.IsBridgeRunning);
		Assert.True(service.IsPausedForCable);
		Assert.Equal("restore", Calls(hiding)[^1]);
	}

	// A wired link that cannot be opened yet leaves the bridge on Bluetooth; the twin stays hidden and the entry is
	// tried again after the retry interval.
	[Fact]
	public async Task Cable_WiredMode_AUsbLinkThatCannotBeOpened_IsRetried_AndTheBridgeStaysOnBluetooth()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		int opens = 0;
		host!.OpenWired = _ => ++opens == 1 ? null : new InfoHidLink(Transport.Usb, Address);
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => opens == 1);
		Assert.False(service.IsWired);
		Assert.True(service.IsBridgeRunning);
		Assert.True(HasCall(hiding, "hide usb " + UsbId));

		service.DeviceTick();
		await Task.Delay(150);
		Assert.Equal(1, opens);
		WaitNotWired(service);
		time.Advance(RealControllerService.CableRetryInterval);
		service.DeviceTick();
		WaitWired(service);
		Assert.Equal(2, opens);
		await service.StopBridgeAsync();
	}

	// A USB pad missing from the listing for one tick keeps the running wired link (nothing is opened or asked
	// again); missing on a second tick in a row leaves it.
	[Fact]
	public async Task Cable_WiredMode_AOneTickListingGap_KeepsTheLink_TwoMissingTicksLeaveIt()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		int opens = 0, audioAsks = 0;
		host!.OpenWired = _ =>
		{
			opens++;
			return new InfoHidLink(Transport.Usb, Address);
		};
		host.WiredAudioRetryInterval = TimeSpan.FromHours(1);
		host.OpenWiredAudio = _ =>
		{
			audioAsks++;
			return null;
		};
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);
		Assert.Equal(1, opens);
		Assert.Equal(1, audioAsks);

		// Missing for one tick: still wired, nothing opened or asked again.
		controllers.Paired.Remove(UsbPad);
		service.DeviceTick();
		await Task.Delay(150);
		Assert.True(service.IsWired);
		Assert.Equal(1, opens);
		Assert.Equal(1, audioAsks);

		// Missing on a second tick in a row: left (the twin stays hidden for the delay).
		service.DeviceTick();
		WaitNotWired(service);
		Assert.False(HasCall(hiding, "unhide usb " + UsbId));

		// Listed again: wired is entered again.
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);
		Assert.Equal(2, opens);
		await service.StopBridgeAsync();
	}

	// A wired link the session drops by itself (its reads stop) while the cable stays in is retried after the
	// interval — three strikes on one plug and wired mode is given up (the twin stays hidden, everything stays on
	// Bluetooth) until the mode is toggled.
	[Fact]
	public async Task Cable_WiredMode_ASelfFallingLink_ThreeStrikes_ThenGivenUp_UntilTheModeIsToggled()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		int opens = 0;
		List<InfoHidLink> opened = [];
		host!.OpenWired = _ =>
		{
			InfoHidLink link = new(Transport.Usb, Address);
			lock (opened)
			{
				opened.Add(link);
				opens = opened.Count;
			}
			return link;
		};
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("cable:", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			controllers.Paired.Add(UsbPad);
			service.DeviceTick();
			WaitWired(service);

			for (int strike = 1; strike <= RealControllerService.WiredFallbackLimit; strike++)
			{
				InfoHidLink link;
				lock (opened)
				{
					link = opened[^1];
				}
				// Its reads start throwing: the session falls back to Bluetooth by itself.
				link.CancelRead();
				WaitNotWired(service);
				service.DeviceTick();
				service.DeviceTick();
				await Task.Delay(150);
				Assert.Equal(strike, opens);
				WaitNotWired(service);
				time.Advance(RealControllerService.CableRetryInterval);
				service.DeviceTick();
				if (strike < RealControllerService.WiredFallbackLimit)
				{
					WaitWired(service);
					Assert.Equal(strike + 1, opens);
				}
			}

			// Given up for this plug: no reopen even after more intervals; the twin stays hidden, the bridge runs.
			time.Advance(RealControllerService.CableRetryInterval);
			service.DeviceTick();
			service.DeviceTick();
			await Task.Delay(200);
			Assert.Equal(RealControllerService.WiredFallbackLimit, opens);
			Assert.False(service.IsWired);
			Assert.False(HasCall(hiding, "unhide usb " + UsbId));
			Assert.True(service.IsBridgeRunning);
			WaitUntil(() =>
			{
				lock (lines)
				{
					return lines.Count(l =>
						l.Contains("fell back to Bluetooth 3 times on this plug", StringComparison.Ordinal)) == 1;
				}
			});

			// A mode toggle starts over: wired is entered again.
			service.CableMode = CableMode.ChargeOnly;
			service.CableMode = CableMode.Wired;
			service.DeviceTick();
			WaitWired(service);
			Assert.Equal(RealControllerService.WiredFallbackLimit + 1, opens);
			await service.StopBridgeAsync();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
	}

	// Same given-up state, reset the other way: the cable out for the whole delay and in again enters wired again.
	[Fact]
	public async Task Cable_WiredMode_ASelfFallingLink_GivenUp_Replugged_StartsOver()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		List<InfoHidLink> opened = [];
		host!.OpenWired = _ =>
		{
			InfoHidLink link = new(Transport.Usb, Address);
			lock (opened)
			{
				opened.Add(link);
			}
			return link;
		};
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);

		for (int strike = 1; strike <= RealControllerService.WiredFallbackLimit; strike++)
		{
			InfoHidLink link;
			lock (opened)
			{
				link = opened[^1];
			}
			link.CancelRead();
			WaitNotWired(service);
			service.DeviceTick();
			WaitNotWired(service);
			time.Advance(RealControllerService.CableRetryInterval);
			service.DeviceTick();
			if (strike < RealControllerService.WiredFallbackLimit)
			{
				WaitWired(service);
			}
		}
		int afterThree;
		lock (opened)
		{
			afterThree = opened.Count;
		}
		Assert.Equal(RealControllerService.WiredFallbackLimit, afterThree);

		// The cable out for the whole delay: the twin is unhidden and the bookkeeping reset.
		controllers.Paired.Remove(UsbPad);
		service.DeviceTick();
		service.DeviceTick();
		WaitNotWired(service);
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "unhide usb " + UsbId));

		// Plugged in again: hidden and wired entered again.
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);
		lock (opened)
		{
			Assert.Equal(RealControllerService.WiredFallbackLimit + 1, opened.Count);
		}
		await service.StopBridgeAsync();
	}

	// The cable out and back in within the delay (the twin is never unhidden): the link dropping on the unplug tick
	// is no self-fallback — nothing is counted, and the strike count starts over for the new plug.
	[Fact]
	public async Task Cable_WiredMode_AQuickReplug_IsNoStrike_AndStartsTheCountOver()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		List<InfoHidLink> opened = [];
		InfoHidLink Current()
		{
			lock (opened)
			{
				return opened[^1];
			}
		}
		host!.OpenWired = _ =>
		{
			InfoHidLink link = new(Transport.Usb, Address);
			lock (opened)
			{
				opened.Add(link);
			}
			return link;
		};
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("cable:", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			controllers.Paired.Add(UsbPad);
			service.DeviceTick();
			WaitWired(service);
			int Opened()
			{
				lock (opened)
				{
					return opened.Count;
				}
			}

			// Two self-fallback strikes: retried after the interval each time.
			for (int strike = 1; strike <= 2; strike++)
			{
				Current().CancelRead();
				WaitNotWired(service);
				service.DeviceTick();
				WaitNotWired(service);
				time.Advance(RealControllerService.CableRetryInterval);
				service.DeviceTick();
				WaitWired(service);
				Assert.Equal(strike + 1, Opened());
			}

			// A quick replug: the USB pad unlisted for one tick while the link dies — the twin stays hidden, no
			// strike is counted and the count starts over (no time advance: the same plug would not).
			controllers.Paired.Remove(UsbPad);
			Current().CancelRead();
			WaitNotWired(service);
			service.DeviceTick();
			controllers.Paired.Add(UsbPad);
			service.DeviceTick();
			WaitWired(service);
			Assert.Equal(4, Opened());

			// The count started over: two more strikes still get retried (without the fresh start the first would
			// have been strike three).
			for (int strike = 1; strike <= 2; strike++)
			{
				Current().CancelRead();
				WaitNotWired(service);
				service.DeviceTick();
				WaitNotWired(service);
				time.Advance(RealControllerService.CableRetryInterval);
				service.DeviceTick();
				WaitWired(service);
				Assert.Equal(4 + strike, Opened());
			}
			lock (lines)
			{
				Assert.DoesNotContain(lines, l => l.Contains("fell back to Bluetooth 3 times", StringComparison.Ordinal));
			}
			await service.StopBridgeAsync();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
	}

	// Given up on a plug, then a quick replug (the twin stays hidden): "until the cable is replugged" is over and
	// wired is entered again.
	[Fact]
	public async Task Cable_WiredMode_GivenUp_AQuickReplug_StartsOver()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		List<InfoHidLink> opened = [];
		host!.OpenWired = _ =>
		{
			InfoHidLink link = new(Transport.Usb, Address);
			lock (opened)
			{
				opened.Add(link);
			}
			return link;
		};
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);

		for (int strike = 1; strike <= RealControllerService.WiredFallbackLimit; strike++)
		{
			InfoHidLink link;
			lock (opened)
			{
				link = opened[^1];
			}
			link.CancelRead();
			WaitNotWired(service);
			service.DeviceTick();
			WaitNotWired(service);
			time.Advance(RealControllerService.CableRetryInterval);
			service.DeviceTick();
			if (strike < RealControllerService.WiredFallbackLimit)
			{
				WaitWired(service);
			}
		}
		// Given up for this plug: no reopen even after more intervals.
		time.Advance(RealControllerService.CableRetryInterval);
		service.DeviceTick();
		await Task.Delay(200);
		lock (opened)
		{
			Assert.Equal(RealControllerService.WiredFallbackLimit, opened.Count);
		}
		Assert.False(service.IsWired);

		// A quick replug: the USB pad unlisted for one tick (no time advance: the twin is never unhidden) ends the
		// given-up state — wired is entered again.
		controllers.Paired.Remove(UsbPad);
		service.DeviceTick();
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);
		lock (opened)
		{
			Assert.Equal(RealControllerService.WiredFallbackLimit + 1, opened.Count);
		}
		await service.StopBridgeAsync();
	}

	// Wired without a break for WiredStableReset forgets earlier self-fallbacks on this plug: the count starts
	// over instead of giving up.
	[Fact]
	public async Task Cable_WiredMode_StrikesAreForgotten_AfterThirtySecondsOfContinuousWired()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		List<InfoHidLink> opened = [];
		InfoHidLink Current()
		{
			lock (opened)
			{
				return opened[^1];
			}
		}
		host!.OpenWired = _ =>
		{
			InfoHidLink link = new(Transport.Usb, Address);
			lock (opened)
			{
				opened.Add(link);
			}
			return link;
		};
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitWired(service);

		// Two strikes, wired again each time.
		for (int strike = 1; strike <= 2; strike++)
		{
			Current().CancelRead();
			WaitNotWired(service);
			service.DeviceTick();
			WaitNotWired(service);
			time.Advance(RealControllerService.CableRetryInterval);
			service.DeviceTick();
			WaitWired(service);
		}
		int soFar;
		lock (opened)
		{
			soFar = opened.Count;
		}
		Assert.Equal(3, soFar);

		// Thirty seconds wired without a break: the next tick forgets the two strikes.
		time.Advance(RealControllerService.WiredStableReset);
		service.DeviceTick();

		// Two more strikes: counted as one and two, both retried — a kept count would have given up on the first.
		for (int strike = 1; strike <= 2; strike++)
		{
			Current().CancelRead();
			WaitNotWired(service);
			service.DeviceTick();
			WaitNotWired(service);
			time.Advance(RealControllerService.CableRetryInterval);
			service.DeviceTick();
			WaitWired(service);
			lock (opened)
			{
				Assert.Equal(3 + strike, opened.Count);
			}
		}
		await service.StopBridgeAsync();
	}

	// A USB link whose first write fails: the session falls back to Bluetooth at once, the enter counts as failed
	// (the twin stays hidden, a retry comes only after the interval) — never an open -> drop -> open loop.
	[Fact]
	public async Task Cable_WiredMode_AUsbLinkThatFailsItsFirstWrite_IsNotReopenedInALoop()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		int opens = 0;
		host!.OpenWired = _ =>
		{
			opens++;
			return new InfoHidLink(Transport.Usb, Address) { FailWrites = true };
		};
		host.OpenWiredAudio = _ => null;
		host.OpenWiredMic = (_, _) => null;
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));
		await Task.Delay(300);
		Assert.Equal(1, opens);
		Assert.False(service.IsWired);
		Assert.True(service.IsBridgeRunning);
		service.DeviceTick();
		await Task.Delay(150);
		Assert.Equal(1, opens);
		WaitNotWired(service);
		time.Advance(RealControllerService.CableRetryInterval);
		service.DeviceTick();
		WaitUntil(() => opens == 2);
		await service.StopBridgeAsync();
	}

	// A link whose reads start failing inside the enter itself (the mic re-apply opens the USB microphone, and the
	// fake's first open cancels the link's reads): the session fell back before EnterWired returned, which still
	// counts as a failed enter — logged once, never an open -> drop -> open loop, retried after the interval.
	[Fact]
	public async Task Cable_WiredMode_ALinkThatFallsBackDuringTheEnter_IsNotReEnteredAtOnce()
	{
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, time: time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		host!.ApplyMic(new MicSettings(true));
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		service.CableMode = CableMode.Wired;
		int opens = 0;
		InfoHidLink? usbLink = null;
		host.OpenWired = _ =>
		{
			usbLink = new InfoHidLink(Transport.Usb, Address);
			opens++;
			return usbLink;
		};
		host.OpenWiredAudio = _ => null;
		bool firstMicOpen = true;
		host.OpenWiredMic = (_, _) =>
		{
			if (firstMicOpen)
			{
				// Inside the enter's mic re-apply: the link's reads start throwing and the session falls back
				// to Bluetooth by itself while EnterWired is still running.
				firstMicOpen = false;
				usbLink!.CancelRead();
				Assert.True(SpinWait.SpinUntil(() => !host.IsWired, 3000), "the link did not fall back");
			}
			return null;
		};
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("cable:", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			controllers.Paired.Add(UsbPad);
			service.DeviceTick();
			WaitUntil(() => opens == 1);
			await Task.Delay(300);
			Assert.Equal(1, opens);
			Assert.False(service.IsWired);
			Assert.True(service.IsBridgeRunning);
			WaitUntil(() =>
			{
				lock (lines)
				{
					return lines.Any(l => l.Contains("fell back to Bluetooth right after it was entered", StringComparison.Ordinal));
				}
			});
			service.DeviceTick();
			service.DeviceTick();
			await Task.Delay(150);
			Assert.Equal(1, opens);
			WaitNotWired(service);

			time.Advance(RealControllerService.CableRetryInterval);
			service.DeviceTick();
			WaitUntil(() => opens == 2);
			WaitWired(service);
			await service.StopBridgeAsync();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
	}

	// The cable moved to another port (another USB instance id): the old entry is unhidden, the new one hidden.
	[Fact]
	public async Task Cable_MovedToAnotherPort_UnhidesTheOldInstance_AndHidesTheNewOne()
	{
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		HidDevice port2 = Usb("port2");
		Dictionary<string, ulong> answers = new() { [UsbPad.Path] = Address, [port2.Path] = Address };
		service.OpenUsbReadOnly = d => new InfoHidLink(Transport.Usb, answers[d.Path]);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));

		controllers.Paired.Remove(UsbPad);
		controllers.Paired.Add(port2);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbIdOf("port2")));
		List<string> calls = Calls(hiding);
		Assert.True(calls.IndexOf("unhide usb " + UsbId) >= 0 &&
			calls.IndexOf("unhide usb " + UsbId) < calls.IndexOf("hide usb " + UsbIdOf("port2")));
		await service.StopBridgeAsync();
	}

	// Bluetooth is back while the cable is still in: the bridge resumes by itself once Bluetooth stayed back for the
	// delay, and the twin is hidden again (the cable keeps only charging).
	[Fact]
	public async Task Cable_BluetoothBackWhileStillCabled_ResumesTheBridge_AndHidesTheTwinAgain()
	{
		List<ServiceNotice> notices = [];
		TestHiding hiding = new();
		StepTime time = new();
		using RealControllerService service = Service(hiding, notices, time);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		controllers.Paired.Add(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		controllers.Paired.Add(UsbPad);
		service.DeviceTick();
		WaitUntil(() => HasCall(hiding, "hide usb " + UsbId));

		// Bluetooth drops; with the cable still in the bridge stops once the delay ran out.
		controllers.Paired.Remove(BtPad);
		service.DeviceTick();
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => !service.IsBridgeRunning);
		WaitUntil(() => notices.Any(n => n.Kind == ServiceNoticeKind.CableConnected));

		// Back on Bluetooth with the cable still in: resume after the delay, then the twin is hidden again.
		controllers.Paired.Add(BtPad);
		controllers.Links.Enqueue(new InfoHidLink(Transport.Bluetooth, Address));
		service.DeviceTick();
		await Task.Delay(100);
		Assert.False(service.IsBridgeRunning);
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => service.IsBridgeRunning && service.IsGameConnected);
		service.DeviceTick();
		WaitUntil(() => Calls(hiding).Count(c => c == "hide usb " + UsbId) == 2);
		List<string> calls = Calls(hiding);
		Assert.True(calls.LastIndexOf("hide usb " + UsbId) > calls.LastIndexOf("restore"));
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

	internal static ControllerDetails Details() => new(TestLink.BtPath, false, Transport.Bluetooth,
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
	public void Connection_SaysCablePluggedIn_WhileChargingOnBluetooth()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			var fake = new FakeControllerService(animate: false) { Details = Details(), CablePluggedIn = true };
			var info = new ControllerInfoViewModel(fake);
			var tester = new TesterViewModel(fake, new SettingsState(fake), live: false);
			Assert.Equal("Bluetooth · Cable Plugged In", info.Connection);
			Assert.Equal("Bluetooth · Cable Plugged In", tester.ConnectionText);
			Assert.Equal("Bluetooth · Kablo Takılı", Text.ConnectionBluetoothCable(Language.Turkish));

			// Experimental wired mode: both view models name the USB cable while it carries the bridge.
			fake.IsWired = true;
			info.Refresh();
			Assert.Equal("USB Cable (Experimental)", info.Connection);
			Assert.Equal("USB Cable (Experimental)", tester.ConnectionText);
			fake.IsWired = false;
			info.Refresh();
			Assert.Equal("Bluetooth · Cable Plugged In", info.Connection);
			Assert.Equal("Bluetooth · Cable Plugged In", tester.ConnectionText);

			fake.CablePluggedIn = false;
			info.Refresh();
			Assert.Equal("Bluetooth", info.Connection);
			Assert.Equal("Bluetooth", tester.ConnectionText);
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

	// The info card is on the Settings page's Controllers tab (not on the controller page); it shows the details and its
	// copy writes through the window. Its picture: SettingsFitTests (settings-controllers-*.png).
	[AvaloniaFact]
	public async Task InfoCard_IsOnTheControllersTab_AndCopies()
	{
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
				vm.OpenSettingsAt(SettingsViewModel.ControllersTab);
				vm.SettingsPage.Info.Refresh();
				Dispatcher.UIThread.RunJobs();
				window.UpdateLayout();
				ControllerInfoCard card = window.GetVisualDescendants().OfType<ControllerInfoCard>().Single(c => c.IsEffectivelyVisible);
				Assert.Same(vm.SettingsPage.Info, card.DataContext);
				Assert.Empty(window.GetVisualDescendants().OfType<ControllerView>().SelectMany(v => v.GetVisualDescendants()).OfType<ControllerInfoCard>());
				Assert.NotNull(vm.SettingsPage.Info.Clipboard);
				await vm.SettingsPage.Info.CopyCommand.ExecuteAsync(null);
				Assert.Equal(Text.InfoCopied(), vm.SettingsPage.Info.CopyFeedback);
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
