// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Hiding;
using Kopru.Core.Input;
using Kopru.Core.Setup;

namespace Kopru.Core.Platform;

// Everything the bridge needs from the operating system, in one place. The shared code (USB/IP server, virtual pads,
// protocol, haptics, profiles) never names an OS class; it gets these from the platform the app picked at start
// (PlatformServices.Current): LinuxPlatform in Kopru.Platform.Linux (experimental).
public interface IPlatform
{
	// Shown in logs ("windows", "linux").
	string Name { get; }

	// Finds and opens the Bluetooth DualSense (the bridge's controller links).
	IControllerSource CreateControllerSource();

	// Lists every DualSense present, USB and Bluetooth (the app's controller presence and setup check).
	IControllerFinder CreateControllerFinder();

	// Plugs the virtual pad served by our USB/IP server on the loopback into the system, and unplugs it.
	IUsbIpAttacher CreateUsbIpAttacher();

	// Where desktop mode and gyro mouse send mouse and keyboard input.
	IInputSink CreateInputSink();

	// Captures a game's sound for "turn sound into haptics".
	IAudioCaptureFactory CreateAudioCapture();

	// Hides the real controller from games while the bridge runs.
	IControllerHiding CreateHiding();

	// Turns the controller off by dropping its Bluetooth link.
	IControllerPower ControllerPower { get; }

	// The wait report pacing and the virtual pad's frame clock use (one per thread).
	IPreciseWait CreatePreciseWait();

	// Once at process start, before any report pacing: keeps the process from being throttled. Returns the log line.
	// Never throws.
	string PrepareProcess();
}

// The platform the app runs on. The app sets it once at start (Program.Main, LinuxPlatform); until then it is the
// unsupported platform (nothing found, nothing attached).
public static class PlatformServices
{
	private static IPlatform? current;

	public static IPlatform Current
	{
		get => Volatile.Read(ref current) ?? Default;
		set => Volatile.Write(ref current, value ?? throw new ArgumentNullException(nameof(value)));
	}

	public static IPlatform Default => UnsupportedPlatform.Instance;
}
