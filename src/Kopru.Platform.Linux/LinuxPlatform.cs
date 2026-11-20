// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hiding;
using Kopru.Core.Input;
using Kopru.Core.Platform;
using Kopru.Core.Setup;
using Kopru.Platform.Linux.Hidraw;
using Kopru.Platform.Linux.Hiding;
using Kopru.Platform.Linux.UsbIp;

namespace Kopru.Platform.Linux;

public sealed record LinuxPlatformOptions
{
	public UsbIpAttachMode AttachMode { get; init; } = UsbIpAttachMode.Auto;

	// Cut output reports to their declared length (HidrawLink.TrimOutput).
	public bool TrimOutputReports { get; init; } = true;

	// Messages for the person at the terminal (the exact sudo command to run); null = the app log.
	public Action<string>? Notice { get; init; }

	public string SysClassHidraw { get; init; } = HidrawFinder.SysClassHidraw;
}

// EXPERIMENTAL, community-maintained. What works: finding and opening the Bluetooth DualSense through hidraw, and
// attaching our USB/IP server's virtual pad with the usbip client (vhci_hcd). Not done yet: hiding the real pad
// (LinuxHiding), audio capture for audio-to-haptics, desktop mode input (uinput), turning the pad off (BlueZ), a
// timerfd-based precise wait (the portable fallback is used). See docs/linux/README.md.
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatform(LinuxPlatformOptions? options = null) : IPlatform
{
	private readonly LinuxPlatformOptions options = options ?? new LinuxPlatformOptions();

	public string Name => "linux";

	public LinuxPlatformOptions Options => options;

	private Action<string> Notice => options.Notice ?? AppLog.Write;

	public HidrawFinder CreateFinder() => new(options.SysClassHidraw);

	private readonly List<LinuxUsbIpAttacher> attachers = [];

	// Each controller link opened by a source of this platform (the spike reads its write counters).
	public event Action<HidrawLink>? LinkOpened;

	public IControllerSource CreateControllerSource()
	{
		HidrawControllerSource source = new(CreateFinder(), AppLog.Write) { TrimOutput = options.TrimOutputReports };
		source.Opened += link => LinkOpened?.Invoke(link);
		return source;
	}

	public IControllerFinder CreateControllerFinder() => CreateFinder();

	public IUsbIpAttacher CreateUsbIpAttacher()
	{
		LinuxUsbIpAttacher attacher = new(new ProcessCommandRunner(), RealLinuxHost.Instance, options.AttachMode, Notice);
		lock (attachers)
		{
			attachers.Add(attacher);
		}
		return attacher;
	}

	// Our USB/IP server saw the client import a pad: a Manual attach waiting for it goes on (LinuxUsbIpAttacher).
	public void NotifyImported()
	{
		lock (attachers)
		{
			attachers.ForEach(a => a.NotifyImported());
		}
	}

	// TODO(linux): uinput for desktop mode and gyro mouse.
	public IInputSink CreateInputSink() => NullInputSink.Instance;

	// TODO(linux): PipeWire capture of the game's sound. Game haptics do not need it: they reach the virtual pad as USB
	// audio through our USB/IP server.
	public IAudioCaptureFactory CreateAudioCapture() =>
		new NoAudioCapture("audio haptics: not available on Linux yet (needs a PipeWire capture); game haptics are not affected");

	public IControllerHiding CreateHiding() => new LinuxHiding(AppLog.Write);

	// TODO(linux): disconnect through BlueZ (org.bluez.Device1.Disconnect over D-Bus); no root needed.
	public IControllerPower ControllerPower => NoControllerPower.Instance;

	// TODO(linux): a timerfd wait. The fallback sleeps and spins the last 1.5 ms, which is accurate on Linux.
	public IPreciseWait CreatePreciseWait() => PreciseWait.CreateFallback();

	public string PrepareProcess() => "power: no throttling opt-out on Linux";
}
