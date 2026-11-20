// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Hiding;
using Kopru.Core.Input;
using Kopru.Core.Setup;

namespace Kopru.Core.Platform;

// Windows: the HID class driver (hid.dll, SetupAPI), usbip-win2, HidHide, SendInput, WASAPI, the Bluetooth radio ioctl
// and the high-resolution waitable timer. The same classes the app constructed directly before the platform seam.
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
	public static WindowsPlatform Instance { get; } = new();

	public string Name => "windows";

	public IControllerSource CreateControllerSource() => new WindowsControllerSource();

	public IControllerFinder CreateControllerFinder() => new HidControllerFinder();

	public IUsbIpAttacher CreateUsbIpAttacher() => new UsbIpExeAttacher();

	public IInputSink CreateInputSink() => new WindowsInputSink();

	public IAudioCaptureFactory CreateAudioCapture() => new WasapiCaptureFactory();

	public IControllerHiding CreateHiding() => HidHideHidingService.CreateDefault();

	public IControllerPower ControllerPower => WindowsControllerPower.Instance;

	public IPreciseWait CreatePreciseWait() => PreciseWait.Create();

	public string PrepareProcess() => PowerThrottling.OptOut();
}
