// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

// A Bluetooth pad as the battery probe sees it: simple 0x01 reports until feature 0x05 is read (when SwitchOnFeature),
// full 0x31 reports after that. Every write is recorded (the probe must never write).
internal sealed class ProbeLink : IHidLink
{
	private readonly BlockingCollection<byte[]> inputs = [];

	private readonly CancellationTokenSource closed = new();

	public HidDevice Device { get; } = new("\\\\?\\hid#{00001124-0000-1000-8000-00805f9b34fb}_probe", 0x054C, 0x0CE6, 78, 547, 64);

	public byte Status { get; set; } = 0x17;

	public bool SwitchOnFeature { get; set; } = true;

	public ConcurrentQueue<byte[]> Writes { get; } = new();

	public ConcurrentQueue<byte> Features { get; } = new();

	public bool Disposed { get; private set; }

	public void FeedFull() => inputs.Add(Full(Status));

	// The short Bluetooth report: ID, sticks, buttons, triggers (no battery anywhere).
	public void FeedSimple() => inputs.Add([0x01, 0x80, 0x80, 0x80, 0x80, 0x08, 0x00, 0x00, 0x00, 0x00]);

	public void FeedRaw(byte[] report) => inputs.Add(report);

	public static byte[] Full(byte status)
	{
		byte[] report = new byte[78];
		report[0] = 0x31;
		report[2 + BatteryStatus.StatusOffset] = status;
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc.Compute(Crc.InputSeed, report.AsSpan(0, 74)));
		return report;
	}

	public int Read(Span<byte> buffer)
	{
		byte[] next = inputs.Take(closed.Token);
		next.CopyTo(buffer);
		return next.Length;
	}

	public void Write(ReadOnlySpan<byte> report) => Writes.Enqueue(report.ToArray());

	public byte[] GetFeature(byte reportId)
	{
		Features.Enqueue(reportId);
		if (SwitchOnFeature && reportId == 0x05)
		{
			FeedFull();
		}
		return new byte[64];
	}

	public void CancelRead() => closed.Cancel();

	public void Dispose()
	{
		Disposed = true;
		closed.Cancel();
	}
}

internal sealed class ProbeSource : IReadOnlyControllerSource
{
	private int opens;

	public ConcurrentQueue<ProbeLink> Links { get; } = new();

	public int Opens => Volatile.Read(ref opens);

	public IHidLink? TryOpenReadOnly()
	{
		Interlocked.Increment(ref opens);
		return Links.TryDequeue(out ProbeLink? link) ? link : null;
	}
}

public class BatteryProbeTests
{
	private readonly ProbeSource source = new();

	private BatteryProbe Probe(double seconds = 1) => new(source) { ReadTime = TimeSpan.FromSeconds(seconds) };

	[Fact]
	public void FullReport_GivesBatteryAndChargeState_WithoutWritesOrFeatureReads()
	{
		ProbeLink link = new() { Status = 0x17 };
		link.FeedFull();
		source.Links.Enqueue(link);

		BatteryStatus? read = Probe().Read();

		Assert.Equal(new BatteryStatus(70, ChargeState.Charging), read);
		Assert.Empty(link.Writes);
		Assert.Empty(link.Features);
		Assert.True(link.Disposed);
	}

	[Fact]
	public void SimpleReportsOnly_ReadsFeature05Once_ThenParsesTheFullReport()
	{
		ProbeLink link = new() { Status = 0x05 };
		link.FeedSimple();
		link.FeedSimple();
		source.Links.Enqueue(link);

		BatteryStatus? read = Probe().Read();

		Assert.Equal(new BatteryStatus(50, ChargeState.Discharging), read);
		Assert.Equal([(byte)0x05], link.Features);
		Assert.Empty(link.Writes);
		Assert.True(link.Disposed);
	}

	[Fact]
	public void PadThatStaysOnSimpleReports_GivesNothingAfterTheReadTime()
	{
		ProbeLink link = new() { SwitchOnFeature = false };
		for (int i = 0; i < 5; i++)
		{
			link.FeedSimple();
		}
		source.Links.Enqueue(link);

		Stopwatch clock = Stopwatch.StartNew();
		BatteryStatus? read = Probe(0.2).Read();

		Assert.Null(read);
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
		Assert.Equal([(byte)0x05], link.Features);
		Assert.Empty(link.Writes);
		Assert.True(link.Disposed);
	}

	[Fact]
	public void BadCrc_IsIgnored()
	{
		ProbeLink link = new();
		byte[] bad = ProbeLink.Full(0x17);
		bad[74] ^= 0xFF;
		link.FeedRaw(bad);
		source.Links.Enqueue(link);

		Assert.Null(Probe(0.2).Read());
		Assert.Empty(link.Writes);
	}

	[Fact]
	public void NoController_GivesNothing()
	{
		Assert.Null(Probe().Read());
		Assert.Equal(1, source.Opens);
	}
}

public class BridgeHostBatteryProbeTests
{
	private readonly ConcurrentQueue<string> log = new();

	private readonly QueueControllers controllers = new();

	private readonly ProbeSource source = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private BridgeHost Host(double probeSeconds = 1) =>
		new(controllers, new FakeIdentity(DeviceIdentity()), new FakeAttacher(log))
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			BatteryProbe = new BatteryProbe(source) { ReadTime = TimeSpan.FromSeconds(probeSeconds) }
		};

	private static Kopru.Core.Virtual.DeviceIdentity DeviceIdentity() => Kopru.Core.Virtual.DeviceIdentity.Create(
		BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "S", [2] = "D" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });

	[Fact]
	public async Task WhileStopped_TheProbeFillsStatusBattery()
	{
		await using BridgeHost host = Host();
		ProbeLink link = new() { Status = 0x23 };
		link.FeedFull();
		source.Links.Enqueue(link);
		int updates = 0;
		host.StatusUpdated += _ => Interlocked.Increment(ref updates);

		Assert.Null(host.Status.Battery);
		BatteryStatus? read = await host.ProbeBatteryAsync();

		Assert.Equal(new BatteryStatus(100, ChargeState.Full), read);
		Assert.Equal(read, host.Status.Battery);
		Assert.Equal(BridgeState.Stopped, host.State);
		Assert.True(updates >= 1);
		Assert.Empty(link.Writes);
	}

	[Fact]
	public async Task WhileTheBridgeRuns_TheProbeIsSkipped()
	{
		await using BridgeHost host = Host();
		controllers.Links.Enqueue(new FakeLink());
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		Assert.Null(await host.ProbeBatteryAsync());
		Assert.Equal(0, source.Opens);
		await host.StopAsync();
	}

	[Fact]
	public async Task WhileWaitingForTheController_TheProbeIsSkipped()
	{
		await using BridgeHost host = Host();
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.WaitingForController);

		Assert.Null(await host.ProbeBatteryAsync());
		Assert.Equal(0, source.Opens);
		await host.StopAsync();
	}

	[Fact]
	public async Task Start_CancelsAProbeInProgress_AndWaitsForIt()
	{
		await using BridgeHost host = Host(probeSeconds: 30);
		ProbeLink silent = new(); // never sends anything
		source.Links.Enqueue(silent);
		Task<BatteryStatus?> probe = host.ProbeBatteryAsync();
		WaitUntil(() => source.Opens == 1);

		Stopwatch clock = Stopwatch.StartNew();
		await host.StartAsync();

		Assert.True(silent.Disposed, "StartAsync returned before the probe closed the controller");
		Assert.Null(await probe);
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
		Assert.Empty(silent.Writes);
		// The supervisor sets its first state on a pool thread, so StartAsync can return a moment before it.
		WaitUntil(() => host.State != BridgeState.Stopped);
		await host.StopAsync();
	}
}
