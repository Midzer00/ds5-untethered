// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

internal sealed class StepTime : TimeProvider
{
	private DateTimeOffset now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

	public override DateTimeOffset GetUtcNow()
	{
		lock (this)
		{
			return now;
		}
	}

	public void Advance(TimeSpan by)
	{
		lock (this)
		{
			now += by;
		}
	}
}

// Hands out a fresh pad (reporting Status) on every read-only open and keeps them to check for writes.
internal sealed class TestReadOnlySource : IReadOnlyControllerSource
{
	private int opens;

	public byte Status { get; set; } = 0x05;

	public ConcurrentQueue<TestLink> Opened { get; } = new();

	public int Opens => Volatile.Read(ref opens);

	public IHidLink? TryOpenReadOnly()
	{
		Interlocked.Increment(ref opens);
		TestLink link = new();
		link.Feed(Status);
		Opened.Enqueue(link);
		return link;
	}
}

public class BatteryProbeServiceTests
{
	private readonly TestControllers controllers = new();
	private readonly TestReadOnlySource readOnly = new();
	private readonly StepTime time = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private RealControllerService Service() =>
		new(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			BatteryProbe = new BatteryProbe(readOnly)
		}, new TestHiding(), controllers, a => a(), _ => null, timers: false, time: time);

	private void Pair() => controllers.Paired.Add(new HidDevice(TestLink.BtPath, 0x054C, HidDevice.DualSenseProductId, 78, 547, 64));

	private void Settle(RealControllerService service) => WaitUntil(() => !service.IsReadingBattery);

	[Fact]
	public void Stopped_ReadsTheBatteryOnConnect_ThenEvery30Seconds()
	{
		using RealControllerService service = Service();
		service.RefreshPresence();
		Assert.Equal(0, readOnly.Opens); // nothing paired, nothing opened
		Assert.False(service.HasBattery);

		Pair();
		readOnly.Status = 0x16; // charging, 60 %
		service.RefreshPresence();
		WaitUntil(() => service.HasBattery);
		Settle(service);
		Assert.Equal(1, readOnly.Opens);
		Assert.Equal(60, service.BatteryPercent);
		Assert.True(service.IsCharging);

		time.Advance(TimeSpan.FromSeconds(29));
		service.RefreshPresence();
		Assert.Null(service.ProbeBatteryIfDue());
		Assert.Equal(1, readOnly.Opens);

		readOnly.Status = 0x04;
		time.Advance(TimeSpan.FromSeconds(1));
		service.RefreshPresence();
		WaitUntil(() => service.BatteryPercent == 40);
		Settle(service);
		Assert.Equal(2, readOnly.Opens);
		Assert.False(service.IsCharging);
		Assert.All(readOnly.Opened, l => Assert.Empty(l.Writes));
	}

	[Fact]
	public async Task WhileTheBridgeRuns_NoProbe_AndRightAfterItStops_OneRuns()
	{
		using RealControllerService service = Service();
		Pair();
		service.RefreshPresence();
		WaitUntil(() => service.HasBattery);
		Settle(service);
		Assert.Equal(1, readOnly.Opens);

		controllers.Links.Enqueue(new TestLink());
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected);
		time.Advance(TimeSpan.FromMinutes(5));
		Assert.Null(service.ProbeBatteryIfDue());
		Assert.Equal(1, readOnly.Opens);

		await service.StopBridgeAsync();
		WaitUntil(() => readOnly.Opens == 2);
		Settle(service);
		Assert.All(readOnly.Opened, l => Assert.Empty(l.Writes));
	}

	[Fact]
	public void DeviceCardStateText_SaysTheBridgeIsOff()
	{
		Assert.Equal("Sanal Kumanda Kapalı", Text.BridgeStateStopped(Language.Turkish));
		Assert.Equal("Virtual Controller Off", Text.BridgeStateStopped(Language.English));
	}
}
