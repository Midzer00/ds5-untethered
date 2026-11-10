// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Kopru.Core.Bridge;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// An attacher whose "usbip port" listing is scripted.
internal sealed class ListingAttacher(ConcurrentQueue<string> log) : IUsbIpAttacher
{
	private int next;

	public List<UsbIpImport> Imported { get; } = [];

	public Exception? ListFailure { get; set; }

	public Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel)
	{
		int port = Interlocked.Increment(ref next);
		log.Enqueue($"attach {port}");
		lock (Imported)
		{
			Imported.Add(new UsbIpImport(port, "127.0.0.1", tcpPort, busId));
		}
		return Task.FromResult(port);
	}

	public Task DetachAsync(int port)
	{
		log.Enqueue($"detach {port}");
		lock (Imported)
		{
			Imported.RemoveAll(i => i.Port == port);
		}
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<UsbIpImport>> ListAsync(CancellationToken cancel)
	{
		log.Enqueue("list");
		if (ListFailure != null)
		{
			throw ListFailure;
		}
		lock (Imported)
		{
			return Task.FromResult<IReadOnlyList<UsbIpImport>>([.. Imported]);
		}
	}
}

internal sealed class FakePowerEvents : IPowerEvents
{
	public event Action? Suspending;

	public event Action? Resumed;

	public void Suspend() => Suspending?.Invoke();

	public void Resume() => Resumed?.Invoke();

	public void Dispose()
	{
	}
}

public class UsbIpPortListTests
{
	private static string Fixture(string name, [CallerFilePath] string here = "") =>
		File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", name));

	[Fact]
	public void Parse_ObservedEmptyOutput_HasNoImports()
	{
		// "usbip.exe port" with nothing attached printed nothing (exit 0) on the dev machine.
		Assert.Empty(UsbIpPortList.Parse(Fixture("usbip-port-empty.txt")));
	}

	[Fact]
	public void Parse_AttachedDevices_ReadsPortAndRemote()
	{
		IReadOnlyList<UsbIpImport> imports = UsbIpPortList.Parse(Fixture("usbip-port-attached.txt").Replace("\n", "\r\n", StringComparison.Ordinal));

		Assert.Equal(
		[
			new UsbIpImport(1, "127.0.0.1", 52144, "1-1"),
			new UsbIpImport(2, "192.168.1.20", 3240, "1-1"),
			new UsbIpImport(3, "127.0.0.1", 3240, "2-4")
		], imports);
	}

	[Fact]
	public void Parse_PortWithoutRemote_KeepsThePort()
	{
		Assert.Equal([new UsbIpImport(5, null, null, null)], UsbIpPortList.Parse("Port 05: <Port in Use> at Full Speed(12Mbps)\n   unknown"));
	}

	[Fact]
	public void StalePorts_OnlyLoopbackImportsOfOurBus()
	{
		IReadOnlyList<UsbIpImport> imports = UsbIpPortList.Parse(Fixture("usbip-port-attached.txt"));

		Assert.Equal([1], BridgeHost.StalePorts(imports, null));
	}

	[Fact]
	public void StalePorts_UsesTheRecordedPort_WhenTheListingCantTell()
	{
		BridgeAttachState recorded = new(4, 50000, VirtualDualSense.BusId);

		Assert.Equal([4], BridgeHost.StalePorts(null, recorded));
		Assert.Equal([4], BridgeHost.StalePorts([new UsbIpImport(4, null, null, null)], recorded));
		// Listed and clearly someone else's device now: left alone.
		Assert.Empty(BridgeHost.StalePorts([new UsbIpImport(4, "10.0.0.2", 3240, "2-1")], recorded));
		// Listing worked and the port is gone: nothing to do.
		Assert.Empty(BridgeHost.StalePorts([], recorded));
	}
}

public sealed class BridgeRecoveryTests : IDisposable
{
	private readonly ConcurrentQueue<string> log = new();

	private readonly QueueControllers controllers = new();

	private readonly ListingAttacher attacher;

	private readonly string directory = Path.Combine(Path.GetTempPath(), "kopru-recovery-" + Guid.NewGuid().ToString("N"));

	public BridgeRecoveryTests()
	{
		attacher = new ListingAttacher(log);
	}

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, true);
		}
	}

	private BridgeStateFile State() => new(Path.Combine(directory, BridgeStateFile.FileName));

	private BridgeHost Host(BridgeStateFile? state = null) =>
		new(controllers, new FakeIdentity(BridgeHostTestsIdentity.Create()), attacher, new ManualTime())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			StateFile = state
		};

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	[Fact]
	public async Task Cleanup_DetachesOnlyOurStalePads()
	{
		attacher.Imported.Add(new UsbIpImport(7, "127.0.0.1", 51000, VirtualDualSense.BusId));
		attacher.Imported.Add(new UsbIpImport(8, "192.168.1.20", 3240, VirtualDualSense.BusId));
		attacher.Imported.Add(new UsbIpImport(9, "127.0.0.1", 3240, "2-4"));
		await using BridgeHost host = Host();

		IReadOnlyList<int> detached = await host.CleanupStaleAsync();

		Assert.Equal([7], detached);
		Assert.Equal(["list", "detach 7"], log);
	}

	[Fact]
	public async Task Cleanup_ListingFails_DetachesTheRecordedPort_AndClearsTheState()
	{
		BridgeStateFile state = State();
		state.Save(new BridgeAttachState(3, 50000, VirtualDualSense.BusId));
		attacher.ListFailure = new InvalidOperationException("usbip port failed");
		await using BridgeHost host = Host(state);

		Assert.Equal([3], await host.CleanupStaleAsync());
		Assert.Null(state.Load());
	}

	[Fact]
	public async Task Cleanup_WithoutUsbIp_DoesNothing()
	{
		attacher.ListFailure = new FileNotFoundException("usbip.exe not found");
		await using BridgeHost host = Host();

		Assert.Empty(await host.CleanupStaleAsync());
		Assert.Equal(["list"], log);
	}

	[Fact]
	public async Task Attach_CleansUpFirst_RecordsThePort_AndCleanStopClearsIt()
	{
		attacher.Imported.Add(new UsbIpImport(40, "127.0.0.1", 51000, VirtualDualSense.BusId));
		BridgeStateFile state = State();
		await using BridgeHost host = Host(state);
		controllers.Links.Enqueue(new RemovableLink(log, "a"));

		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		List<string> entries = [.. log];
		Assert.True(entries.IndexOf("detach 40") >= 0 && entries.IndexOf("detach 40") < entries.IndexOf("attach 1"), string.Join(", ", entries));
		BridgeAttachState? recorded = state.Load();
		Assert.NotNull(recorded);
		Assert.Equal(1, recorded.Port);
		Assert.Equal(VirtualDualSense.BusId, recorded.BusId);

		await host.StopAsync();

		Assert.Null(state.Load());
		Assert.False(File.Exists(state.Path));
	}

	[Fact]
	public void StateFile_CorruptFile_ReadsAsNothing()
	{
		BridgeStateFile state = State();
		Directory.CreateDirectory(directory);
		File.WriteAllText(state.Path, "{ not json");

		Assert.Null(state.Load());
	}
}

public class BridgeWatchdogTests
{
	private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void DeadServer_Trips()
	{
		BridgeWatchdog watchdog = new(TimeSpan.FromSeconds(3), Start);

		Assert.NotNull(watchdog.Check(Start, serverAlive: false, hapticsStreaming: false, hapticsReports: 0));
	}

	[Fact]
	public void StalledHapticsStream_TripsAfterTheTimeout()
	{
		BridgeWatchdog watchdog = new(TimeSpan.FromSeconds(3), Start);

		Assert.Null(watchdog.Check(Start, true, true, 10));
		Assert.Null(watchdog.Check(Start.AddSeconds(2), true, true, 10));
		Assert.Null(watchdog.Check(Start.AddSeconds(2.5), true, true, 11));
		Assert.Null(watchdog.Check(Start.AddSeconds(5.4), true, true, 11));
		Assert.NotNull(watchdog.Check(Start.AddSeconds(5.6), true, true, 11));
	}

	[Fact]
	public void IdleHapticsStream_IsHealthy()
	{
		BridgeWatchdog watchdog = new(TimeSpan.FromSeconds(3), Start);

		Assert.Null(watchdog.Check(Start, true, false, 10));
		Assert.Null(watchdog.Check(Start.AddMinutes(5), true, false, 10));
		// Starting again after a long pause is not a stall.
		Assert.Null(watchdog.Check(Start.AddMinutes(5).AddSeconds(1), true, true, 10));
	}

	[Fact]
	public void InfiniteTimeout_NeverTripsOnStall()
	{
		BridgeWatchdog watchdog = new(Timeout.InfiniteTimeSpan, Start);

		Assert.Null(watchdog.Check(Start, true, true, 1));
		Assert.Null(watchdog.Check(Start.AddHours(1), true, true, 1));
	}

	[Fact]
	public void WatchdogError_HasText()
	{
		Assert.False(string.IsNullOrWhiteSpace(BridgeHost.Describe(BridgeError.Watchdog)));
	}
}

public class BridgeSleepGuardTests
{
	[Fact]
	public async Task Suspend_StopsARunningBridge_ResumeStartsItAgain()
	{
		FakePowerEvents power = new();
		List<string> calls = [];
		bool active = true;
		using BridgeSleepGuard guard = new(power, () => active,
			() => { calls.Add("stop"); active = false; return Task.CompletedTask; },
			() => { calls.Add("start"); active = true; return Task.CompletedTask; })
		{ ResumeDelay = TimeSpan.Zero };

		power.Suspend();
		Assert.Equal(["stop"], calls);
		power.Resume();
		await guard.LastResume;

		Assert.Equal(["stop", "start"], calls);
	}

	[Fact]
	public async Task StoppedBridge_StaysStopped()
	{
		FakePowerEvents power = new();
		List<string> calls = [];
		using BridgeSleepGuard guard = new(power, () => false,
			() => { calls.Add("stop"); return Task.CompletedTask; },
			() => { calls.Add("start"); return Task.CompletedTask; })
		{ ResumeDelay = TimeSpan.Zero };

		power.Suspend();
		power.Resume();
		await guard.LastResume;

		Assert.Empty(calls);
	}

	[Fact]
	public async Task SleepingAgainBeforeTheDelay_CancelsTheRestart()
	{
		FakePowerEvents power = new();
		List<string> calls = [];
		bool active = true;
		using BridgeSleepGuard guard = new(power, () => active,
			() => { calls.Add("stop"); active = false; return Task.CompletedTask; },
			() => { calls.Add("start"); active = true; return Task.CompletedTask; })
		{ ResumeDelay = TimeSpan.FromMinutes(10) };

		power.Suspend();
		power.Resume();
		Task pending = guard.LastResume;
		power.Suspend();
		await pending;

		Assert.Equal(["stop"], calls);
	}

	[Fact]
	public async Task WithTheHost_TearsDownAndReconnects()
	{
		ConcurrentQueue<string> log = new();
		QueueControllers controllers = new();
		FakeAttacher attacher = new(log);
		await using BridgeHost host = new(controllers, new FakeIdentity(BridgeHostTestsIdentity.Create()), attacher, new ManualTime())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		FakePowerEvents power = new();
		using BridgeSleepGuard guard = new(power, host) { ResumeDelay = TimeSpan.Zero };
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));

		power.Suspend();
		Assert.Equal(BridgeState.Stopped, host.State);
		Assert.Contains("detach 1", log);
		Assert.Contains("dispose a", log);

		controllers.Links.Enqueue(new RemovableLink(log, "b"));
		power.Resume();
		await guard.LastResume;
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));
		Assert.Contains("attach 2", log);
	}
}

internal static class BridgeHostTestsIdentity
{
	public static DeviceIdentity Create() => DeviceIdentity.Create(
		BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });
}
