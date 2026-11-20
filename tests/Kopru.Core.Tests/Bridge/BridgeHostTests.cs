// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

internal sealed class ManualTime : TimeProvider
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

// A Bluetooth controller that can be removed: reads then fail like a vanished device.
internal sealed class RemovableLink(ConcurrentQueue<string> log, string name) : IHidLink
{
	private readonly BlockingCollection<byte[]> inputs = [];

	private readonly CancellationTokenSource closed = new();

	private volatile bool removed;

	public HidDevice Device { get; } = new("\\\\?\\hid#{00001124-0000-1000-8000-00805f9b34fb}_" + name, 0x054C, 0x0CE6, 78, 547, 64);

	public void Feed(byte[] report) => inputs.Add(report);

	public void Remove()
	{
		removed = true;
		closed.Cancel();
	}

	public int Read(Span<byte> buffer)
	{
		if (removed)
		{
			throw new IOException("device not connected");
		}
		try
		{
			byte[] next = inputs.Take(closed.Token);
			next.CopyTo(buffer);
			return next.Length;
		}
		catch (OperationCanceledException)
		{
			throw new IOException("cancelled");
		}
	}

	public void Write(ReadOnlySpan<byte> report)
	{
		if (removed)
		{
			throw new IOException("device not connected");
		}
	}

	public byte[] GetFeature(byte reportId) => new byte[64];

	public void CancelRead() => closed.Cancel();

	public void Dispose()
	{
		log.Enqueue("dispose " + name);
		closed.Cancel();
	}

	public static byte[] Input(byte status, byte l2 = 0, byte buttons = 0x08)
	{
		byte[] report = new byte[78];
		report[0] = 0x31;
		report[2] = report[3] = report[4] = report[5] = 0x80;
		report[2 + 4] = l2;
		report[2 + 7] = buttons;
		report[2 + BatteryStatus.StatusOffset] = status;
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc.Compute(Crc.InputSeed, report.AsSpan(0, 74)));
		return report;
	}
}

internal sealed class QueueControllers : IControllerSource
{
	public ConcurrentQueue<IHidLink> Links { get; } = new();

	public IHidLink? TryOpen() => Links.TryDequeue(out IHidLink? link) ? link : null;
}

internal sealed class FakeIdentity(DeviceIdentity? identity) : IIdentitySource
{
	public DeviceIdentity? Load() => identity;
}

internal sealed class FakeAttacher(ConcurrentQueue<string> log) : IUsbIpAttacher
{
	private int next;

	public Exception? Failure { get; set; }

	public Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel)
	{
		if (Failure != null)
		{
			throw Failure;
		}
		int port = Interlocked.Increment(ref next);
		log.Enqueue($"attach {port}");
		return Task.FromResult(port);
	}

	public Task DetachAsync(int port)
	{
		log.Enqueue($"detach {port}");
		return Task.CompletedTask;
	}
}

public class BridgeHostTests
{
	private readonly ConcurrentQueue<string> log = new();

	private readonly QueueControllers controllers = new();

	private readonly FakeAttacher attacher;

	private readonly ManualTime time = new();

	public BridgeHostTests()
	{
		attacher = new FakeAttacher(log);
	}

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private static DeviceIdentity Identity() => DeviceIdentity.Create(
		BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });

	private BridgeHost Host(DeviceIdentity? identity = null, bool noIdentity = false) =>
		new(controllers, new FakeIdentity(noIdentity ? null : identity ?? Identity()), attacher, time)
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};

	[Fact]
	public async Task Start_WithoutController_Waits_ThenRunsWhenItAppears()
	{
		await using BridgeHost host = Host();
		List<BridgeState> states = [];
		host.StateChanged += s => { lock (states) states.Add(s); };

		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.WaitingForController);
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		// State flips before StateChanged fires, so wait for the event itself.
		WaitUntil(() => { lock (states) return states.Contains(BridgeState.Running); });

		lock (states)
		{
			Assert.Equal([BridgeState.WaitingForController, BridgeState.Connecting, BridgeState.Running], states);
		}
		Assert.Contains("attach 1", log);
	}

	[Fact]
	public async Task StartAndStop_AreIdempotent()
	{
		await using BridgeHost host = Host();
		controllers.Links.Enqueue(new RemovableLink(log, "a"));

		await host.StartAsync();
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);
		await host.StopAsync();
		await host.StopAsync();

		Assert.Equal(BridgeState.Stopped, host.State);
		Assert.Single(log, e => e.StartsWith("attach", StringComparison.Ordinal));
		Assert.Single(log, e => e.StartsWith("detach", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Stop_DetachesBeforeClosingTheController()
	{
		await using BridgeHost host = Host();
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		await host.StopAsync();

		List<string> entries = [.. log];
		Assert.True(entries.IndexOf("detach 1") >= 0 && entries.IndexOf("detach 1") < entries.IndexOf("dispose a"), string.Join(", ", entries));
	}

	[Fact]
	public async Task LostController_TearsDown_Waits_AndReconnects()
	{
		await using BridgeHost host = Host();
		RemovableLink first = new(log, "a");
		controllers.Links.Enqueue(first);
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		first.Remove();
		WaitUntil(() => host.Status.LastError == BridgeError.ControllerLost && host.State == BridgeState.WaitingForController);
		Assert.Equal("input reads failed", host.Status.LastErrorDetail);
		List<string> entries = [.. log];
		Assert.True(entries.IndexOf("detach 1") < entries.IndexOf("dispose a"), string.Join(", ", entries));

		controllers.Links.Enqueue(new RemovableLink(log, "b"));
		WaitUntil(() => log.Contains("attach 2") && host.State == BridgeState.Running);
	}

	[Fact]
	public async Task NoInputForTooLong_CountsAsLost()
	{
		await using BridgeHost host = Host();
		host.NoInputTimeout = TimeSpan.FromSeconds(3);
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		time.Advance(TimeSpan.FromSeconds(4));

		WaitUntil(() => log.Contains("dispose a"));
		Assert.Contains("detach 1", log);
		WaitUntil(() => host.Status.LastError == BridgeError.ControllerLost);
		Assert.StartsWith("no input for 4.0 s", host.Status.LastErrorDetail);
	}

	// Every stop and power-off says who asked, and the power-off line comes before the disconnect line.
	[Fact]
	public async Task StopAndPowerOff_LogWhoAsked()
	{
		ConcurrentQueue<string> lines = new();
		void Watch(string line)
		{
			if (line.Contains("test-reason-7f3", StringComparison.Ordinal) || line.StartsWith("power off: controller disconnected", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		Diagnostics.AppLog.Written += Watch;
		try
		{
			await using BridgeHost host = Host();
			host.Power = new FakePower();
			controllers.Links.Enqueue(new RemovableLink(log, "a"));
			await host.StartAsync("test-reason-7f3 start");
			WaitUntil(() => host.State == BridgeState.Running);

			Assert.True(await host.PowerOffAsync("test-reason-7f3 power"));
			WaitUntil(() => host.State == BridgeState.WaitingForController);
			await host.StopAsync("test-reason-7f3 stop");
			await host.StopAsync("test-reason-7f3 second stop");

			List<string> got = [.. lines.Where(l => l.Contains("test-reason-7f3", StringComparison.Ordinal))];
			Assert.Equal(["bridge start: test-reason-7f3 start", "power off requested: test-reason-7f3 power", "bridge stop: test-reason-7f3 stop"], got);
		}
		finally
		{
			Diagnostics.AppLog.Written -= Watch;
		}
	}

	[Fact]
	public async Task MissingCapture_IsAnError()
	{
		await using BridgeHost host = Host(noIdentity: true);

		await host.StartAsync();

		Assert.Equal(BridgeState.Error, host.State);
		Assert.Equal(BridgeError.NoCapture, host.Status.LastError);
		Assert.Equal(Text.BridgeNoCapture(Language.English), host.Status.LastErrorText(Language.English));
	}

	[Fact]
	public async Task MissingUsbIp_IsAnError_AndClosesTheController()
	{
		await using BridgeHost host = Host();
		attacher.Failure = new FileNotFoundException("usbip.exe");
		controllers.Links.Enqueue(new RemovableLink(log, "a"));

		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Error);

		Assert.Equal(BridgeError.UsbIpMissing, host.Status.LastError);
		WaitUntil(() => log.Contains("dispose a"));
		await host.StopAsync();
		Assert.Equal(BridgeState.Stopped, host.State);
	}

	[Fact]
	public async Task Battery_IsReported_AndLowBatteryFiresOncePerDischarge()
	{
		await using BridgeHost host = Host();
		RemovableLink link = new(log, "a");
		controllers.Links.Enqueue(link);
		int lowEvents = 0;
		host.LowBattery += _ => Interlocked.Increment(ref lowEvents);
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		link.Feed(RemovableLink.Input(0x05));
		WaitUntil(() => host.Status.Battery == new BatteryStatus(50, ChargeState.Discharging));
		link.Feed(RemovableLink.Input(0x01));
		WaitUntil(() => lowEvents == 1);
		link.Feed(RemovableLink.Input(0x00));
		WaitUntil(() => host.Status.Battery?.Percent == 0);
		Thread.Sleep(50);
		Assert.Equal(1, lowEvents);

		link.Feed(RemovableLink.Input(0x11));
		WaitUntil(() => host.Status.Battery?.State == ChargeState.Charging);
		link.Feed(RemovableLink.Input(0x01));
		WaitUntil(() => lowEvents == 2);
	}

	[Fact]
	public async Task Idle_FiresAfterTheSetTime_OnlyOnce_AndAgainAfterUse()
	{
		await using BridgeHost host = Host();
		host.IdleMinutes = 15;
		RemovableLink link = new(log, "a");
		controllers.Links.Enqueue(link);
		int idle = 0;
		host.IdleTimeout += () => Interlocked.Increment(ref idle);
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		time.Advance(TimeSpan.FromMinutes(14));
		Thread.Sleep(50);
		Assert.Equal(0, idle);
		time.Advance(TimeSpan.FromMinutes(2));
		WaitUntil(() => idle == 1);
		Thread.Sleep(50);
		Assert.Equal(1, idle);

		link.Feed(RemovableLink.Input(0x08, l2: 200));
		WaitUntil(() => host.Status.Battery != null);
		Thread.Sleep(50);
		time.Advance(TimeSpan.FromMinutes(16));
		WaitUntil(() => idle == 2);
	}

	[Fact]
	public async Task Idle_ZeroMinutes_IsOff()
	{
		await using BridgeHost host = Host();
		host.IdleMinutes = 0;
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		int idle = 0;
		host.IdleTimeout += () => Interlocked.Increment(ref idle);
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		time.Advance(TimeSpan.FromHours(5));
		Thread.Sleep(100);

		Assert.Equal(0, idle);
	}

	[Fact]
	public async Task Levels_AreKept_AcrossSettings()
	{
		await using BridgeHost host = Host();
		Assert.Equal(1.5, host.GameHapticsLevel);
		Assert.Equal(1.0, host.RumbleLevel);
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		host.GameHapticsLevel = 2;
		host.RumbleLevel = 0.5;

		Assert.Equal(2, host.GameHapticsLevel);
		Assert.Equal(0.5, host.RumbleLevel);
	}

	[Fact]
	public void Descriptions_AreLocalized()
	{
		foreach (BridgeState s in Enum.GetValues<BridgeState>())
		{
			Assert.NotEqual(BridgeHost.Describe(s, Language.Turkish), BridgeHost.Describe(s, Language.English));
		}
		foreach (BridgeError e in Enum.GetValues<BridgeError>().Where(e => e != BridgeError.None))
		{
			Assert.False(string.IsNullOrEmpty(BridgeHost.Describe(e, Language.English)));
		}
		Assert.Null(BridgeHost.Describe(BridgeError.None));
	}
}

public class BatteryStatusTests
{
	[Theory]
	[InlineData(0x00, 0, ChargeState.Discharging)]
	[InlineData(0x05, 50, ChargeState.Discharging)]
	[InlineData(0x0A, 100, ChargeState.Discharging)]
	[InlineData(0x0F, 100, ChargeState.Discharging)]
	[InlineData(0x13, 30, ChargeState.Charging)]
	[InlineData(0x27, 100, ChargeState.Full)]
	[InlineData(0xF4, 40, ChargeState.Error)]
	public void StatusByte_IsParsed(byte status, int percent, ChargeState state) =>
		Assert.Equal(new BatteryStatus(percent, state), BatteryStatus.FromStatusByte(status));

	[Fact]
	public void BluetoothReport_StatusIsAtBody52()
	{
		byte[] report = RemovableLink.Input(0x16);
		ReadOnlySpan<byte> body = InputState.Body(report);

		Assert.Equal(new BatteryStatus(60, ChargeState.Charging), BatteryStatus.Parse(body));
		Assert.Equal(BatteryStatus.Parse(body), BatteryStatus.From(InputState.Parse(body)));
	}
}

public class InputActivityTests
{
	[Fact]
	public void Noise_IsNotActivity_ButButtonsSticksAndTriggersAre()
	{
		byte[] remembered = new byte[InputActivity.Size];
		byte[] body = new byte[InputState.BodySize];
		body[0] = body[1] = body[2] = body[3] = 0x80;
		body[7] = 0x08;
		Assert.True(InputActivity.Changed(remembered, body));

		body[6]++;
		body[0] += 3;
		body[20] = 0x55;
		Assert.False(InputActivity.Changed(remembered, body));

		body[8] = 0x01;
		Assert.True(InputActivity.Changed(remembered, body));
		body[5] = 10;
		Assert.True(InputActivity.Changed(remembered, body));
		body[2] = 0xFF;
		Assert.True(InputActivity.Changed(remembered, body));
	}
}

public class UsbIpExeAttacherTests
{
	[Theory]
	[InlineData("3\r\n", 3)]
	[InlineData("  12  ", 12)]
	[InlineData("", null)]
	[InlineData("error: no free port\n", null)]
	public void Port_IsParsed(string output, int? port) => Assert.Equal(port, UsbIpExeAttacher.ParsePort(output));
}
