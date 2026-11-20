// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Hiding;
using Kopru.Core.Input;
using Kopru.Core.Setup;

namespace Kopru.Core.Platform;

// An OS without a platform of its own: no controller is ever found, nothing is attached, hidden or captured. The bridge
// then just waits; nothing throws at construction.
public sealed class UnsupportedPlatform : IPlatform
{
	public static UnsupportedPlatform Instance { get; } = new();

	public string Name => "unsupported";

	public IControllerSource CreateControllerSource() => new NoControllerSource();

	public IControllerFinder CreateControllerFinder() => new NoControllerSource();

	public IUsbIpAttacher CreateUsbIpAttacher() => new NoUsbIpAttacher();

	public IInputSink CreateInputSink() => NullInputSink.Instance;

	public IAudioCaptureFactory CreateAudioCapture() => new NoAudioCapture("audio haptics: no sound capture in this build");

	public IControllerHiding CreateHiding() => new NoControllerHiding();

	public IControllerPower ControllerPower => NoControllerPower.Instance;

	public IPreciseWait CreatePreciseWait() => PreciseWait.CreateFallback();

	public string PrepareProcess() => "power: throttling opt-out skipped (not Windows)";
}

// Finds nothing.
public sealed class NoControllerSource : IControllerSource, IControllerFinder
{
	public IHidLink? TryOpen() => null;

	public IReadOnlyList<HidDevice> Find() => [];
}

// No USB/IP client: the bridge reports it as missing (BridgeError.UsbIpMissing).
public sealed class NoUsbIpAttacher : IUsbIpAttacher
{
	public Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel) =>
		Task.FromException<int>(new FileNotFoundException("no USB/IP client on this platform"));

	public Task DetachAsync(int port) => Task.CompletedTask;
}

// Never finds an address and never disconnects.
public sealed class NoControllerPower : IControllerPower
{
	public static NoControllerPower Instance { get; } = new();

	public ulong? FindAddress(HidDevice device) => null;

	public Task<bool> DisconnectAsync(ulong address, CancellationToken cancel = default) => Task.FromResult(false);
}
