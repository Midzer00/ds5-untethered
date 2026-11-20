// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Hiding;
using Kopru.Core.Input;
using Kopru.Core.Platform;
using Kopru.Core.Setup;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// A platform made of fakes: records which of its parts the bridge asked for.
internal sealed class FakePlatform : IPlatform
{
	public ConcurrentQueue<string> Calls { get; } = new();

	public QueueControllers Controllers { get; } = new();

	public FakeAttacher Attacher { get; }

	public FakePlatform(ConcurrentQueue<string> log)
	{
		Attacher = new FakeAttacher(log);
	}

	public string Name => "fake";

	public IControllerSource CreateControllerSource()
	{
		Calls.Enqueue("source");
		return Controllers;
	}

	public IControllerFinder CreateControllerFinder()
	{
		Calls.Enqueue("finder");
		return new NoControllerSource();
	}

	public IUsbIpAttacher CreateUsbIpAttacher()
	{
		Calls.Enqueue("attacher");
		return Attacher;
	}

	public IInputSink CreateInputSink()
	{
		Calls.Enqueue("input");
		return Sink;
	}

	public RecordingSink Sink { get; } = new();

	public IAudioCaptureFactory CreateAudioCapture() => new NoAudioCapture("fake");

	public IControllerHiding CreateHiding() => new NoControllerHiding();

	public IControllerPower ControllerPower { get; } = NoControllerPower.Instance;

	public IPreciseWait CreatePreciseWait()
	{
		Calls.Enqueue("wait");
		return PreciseWait.CreateFallback();
	}

	public string PrepareProcess() => "fake";

	internal sealed class RecordingSink : IInputSink
	{
		public void MoveMouse(int dx, int dy) { }

		public void MouseButton(MouseButton button, bool down) { }

		public void Wheel(int delta, bool horizontal = false) { }

		public void Key(ushort virtualKey, bool down) { }
	}
}

public class PlatformTests
{
	[Fact]
	public void Default_IsUnsupported_UntilTheAppPicksLinux()
	{
		Assert.Equal("unsupported", PlatformServices.Default.Name);
	}

	[Fact]
	public async Task Unsupported_FindsNothing_AndNeverThrowsUntilAttach()
	{
		UnsupportedPlatform platform = UnsupportedPlatform.Instance;
		Assert.Null(platform.CreateControllerSource().TryOpen());
		Assert.Empty(platform.CreateControllerSource().Enumerate());
		Assert.Empty(platform.CreateControllerFinder().Find());
		Assert.Same(NullInputSink.Instance, platform.CreateInputSink());
		Assert.False(platform.CreateHiding().IsInstalled);
		Assert.Null(platform.ControllerPower.FindAddress(new HidDevice("x", 0x054C, 0x0CE6, 78, 78, 64)));
		Assert.False(await platform.ControllerPower.DisconnectAsync(1));
		using IPreciseWait wait = platform.CreatePreciseWait();
		Assert.False(wait.HighResolution);
		// A missing USB/IP client is what the bridge reports as UsbIpMissing.
		await Assert.ThrowsAsync<FileNotFoundException>(() => platform.CreateUsbIpAttacher().AttachAsync(3240, "1-1", CancellationToken.None));
	}

	[Fact]
	public void NoAudioCapture_LogsWhy_AndCapturesNothing()
	{
		List<string> lines = [];
		Assert.Null(new NoAudioCapture("why").Start(1, new NullSink(), lines.Add));
		Assert.Equal(["why"], lines);
	}

	[Fact]
	public async Task NoHiding_DoesNothing()
	{
		NoControllerHiding hiding = new();
		await hiding.RecoverAsync();
		await hiding.BeginAsync();
		await hiding.HideAsync("id");
		await hiding.RestoreAsync();
		await hiding.RegisterAppEarlyAsync();
		await hiding.ReleaseAppEarlyAsync();
		Assert.False(hiding.IsInstalled);
	}

	[Fact]
	public void KnownTransport_WinsOverThePath()
	{
		HidDevice windows = new(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6", 0x054C, 0x0CE6, 78, 547, 64);
		Assert.Equal(Transport.Bluetooth, windows.Transport);
		Assert.Equal(Transport.Usb, new HidDevice("/dev/hidraw3", 0x054C, 0x0CE6, 78, 547, 64).Transport);
		Assert.Equal(Transport.Bluetooth, new HidDevice("/dev/hidraw3", 0x054C, 0x0CE6, 78, 547, 64) { KnownTransport = Transport.Bluetooth }.Transport);
	}

	[Fact]
	public void Create_TakesEveryPartFromThePlatform()
	{
		FakePlatform platform = new(new ConcurrentQueue<string>());
		BridgeHost host = BridgeHost.Create(platform);

		Assert.Same(platform.Sink, host.InputSink);
		Assert.Same(platform.ControllerPower, host.Power);
		Assert.NotNull(host.PreciseWaitFactory);
		// The fake source can't open read-only: no battery probe.
		Assert.Null(host.BatteryProbe);
		Assert.Equal(["source", "attacher", "input"], platform.Calls);
	}

	[Fact]
	public void Manager_GivesEverySlotThePlatformsParts()
	{
		FakePlatform platform = new(new ConcurrentQueue<string>());
		BridgeHost primary = BridgeHost.Create(platform);
		BridgeManager manager = BridgeManager.Create(platform, primary);

		Assert.Same(primary, manager.Primary);
		Assert.All(manager.Hosts, h => Assert.Same(platform.ControllerPower, h.Power));
		Assert.All(manager.Hosts, h => Assert.NotNull(h.PreciseWaitFactory));
		// Slot 1's own source and attacher, then the manager's source and one attacher per slot 2-4.
		Assert.Equal(["source", "attacher", "input", "source", "attacher", "attacher", "attacher"], platform.Calls);
	}

	[Fact]
	public async Task PadServing_IsRaisedWithThePadAndItsServer_BeforeAttaching()
	{
		ConcurrentQueue<string> log = new();
		FakePlatform platform = new(log);
		await using BridgeHost host = new(platform.Controllers, new FakeIdentity(Identity()), platform.Attacher)
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		ConcurrentQueue<(IVirtualPad Pad, int Port)> served = new();
		host.PadServing += (pad, server) =>
		{
			log.Enqueue("serving");
			served.Enqueue((pad, server.Port));
		};
		host.PadServing += (_, _) => throw new InvalidOperationException("a broken handler must not stop the bridge");
		platform.Controllers.Links.Enqueue(new RemovableLink(log, "a"));

		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000), "timed out");

		(IVirtualPad pad, int port) = Assert.Single(served);
		Assert.IsType<VirtualDualSense>(pad);
		Assert.True(port > 0);
		List<string> order = [.. log.Where(l => l is "serving" || l.StartsWith("attach", StringComparison.Ordinal))];
		Assert.Equal(["serving", "attach 1"], order);
	}

	[Fact]
	public void StalePorts_PortZeroIsReal_ButNegativePortsAreNeverOurs()
	{
		UsbIpImport[] imports =
		[
			new(0, "127.0.0.1", 40123, "1-1"),   // ours, on Linux's first port
			new(1, "192.168.0.9", 3240, "1-1"),  // someone else's device with the same bus id
			new(2, null, null, null),            // the listing did not say where it comes from
		];
		Assert.Equal([0], BridgeHost.StalePorts(imports, null));
		Assert.Equal([0], BridgeHost.StalePorts(imports, new BridgeAttachState(IUsbIpAttacher.UnknownPort, 40123, "1-1")));
		Assert.Empty(BridgeHost.StalePorts(null, new BridgeAttachState(IUsbIpAttacher.UnknownPort, 40123, "1-1")));
		// A foreign import on port 0 is left alone unless the state file says port 0 was ours.
		UsbIpImport[] foreign = [new(0, "192.168.0.9", 3240, "1-1"), new(3, null, null, null)];
		Assert.Empty(BridgeHost.StalePorts(foreign, null));
		Assert.Empty(BridgeHost.StalePorts(foreign, new BridgeAttachState(IUsbIpAttacher.UnknownPort, 40123, "1-1")));
		Assert.Equal([0], BridgeHost.StalePorts(null, new BridgeAttachState(0, 40123, "1-1")));
		Assert.Empty(BridgeHost.StalePorts([new(-1, "127.0.0.1", 40123, "1-1")], null));
	}

	[Fact]
	public async Task AnUnknownPort_IsNeitherRecordedNorDetached()
	{
		ConcurrentQueue<string> log = new();
		FakePlatform platform = new(log);
		string state = Path.Combine(Path.GetTempPath(), "kopru-unknown-port-" + Guid.NewGuid().ToString("N") + ".json");
		UnknownPortAttacher attacher = new();
		try
		{
			await using (BridgeHost host = new(platform.Controllers, new FakeIdentity(Identity()), attacher)
			{
				PollInterval = TimeSpan.FromMilliseconds(10),
				MonitorInterval = TimeSpan.FromMilliseconds(10),
				NoInputTimeout = Timeout.InfiniteTimeSpan,
				StateFile = new BridgeStateFile(state)
			})
			{
				platform.Controllers.Links.Enqueue(new RemovableLink(log, "a"));
				await host.StartAsync();
				Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000), "timed out");
				Assert.False(File.Exists(state));
				await host.StopAsync();
			}
			Assert.Equal(1, attacher.Attaches);
			Assert.Empty(attacher.Detached);
		}
		finally
		{
			File.Delete(state);
		}
	}

	private sealed class UnknownPortAttacher : IUsbIpAttacher
	{
		public int Attaches;

		public ConcurrentQueue<int> Detached { get; } = new();

		public Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel)
		{
			Interlocked.Increment(ref Attaches);
			return Task.FromResult(IUsbIpAttacher.UnknownPort);
		}

		public Task DetachAsync(int port)
		{
			Detached.Enqueue(port);
			return Task.CompletedTask;
		}
	}

	private static DeviceIdentity Identity() => DeviceIdentity.Create(
		BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });

	private sealed class NullSink : IAudioCaptureSink
	{
		public void OnAudio(ReadOnlySpan<float> samples, int channels, int sampleRate) { }
	}
}
