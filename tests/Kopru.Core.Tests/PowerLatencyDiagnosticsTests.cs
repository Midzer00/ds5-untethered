// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;
using Kopru.Core.Setup;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

internal static class TestLogSetup
{
	// Keeps the bridge's log out of the real %LOCALAPPDATA%.
	[ModuleInitializer]
	internal static void RedirectAppLog() =>
		AppLog.Directory = Path.Combine(Path.GetTempPath(), "kopru-tests-log-" + Environment.ProcessId);
}

internal sealed class FakePower : IControllerPower
{
	public ulong? Address { get; set; } = 0xA0AB51123456;

	public bool Accept { get; set; } = true;

	public ConcurrentQueue<ulong> Disconnected { get; } = new();

	public ulong? FindAddress(HidDevice device) => Address;

	public Task<bool> DisconnectAsync(ulong address, CancellationToken cancel = default)
	{
		Disconnected.Enqueue(address);
		return Task.FromResult(Accept);
	}
}

public class ControllerPowerTests
{
	private readonly ConcurrentQueue<string> log = new();

	private readonly QueueControllers controllers = new();

	private readonly ManualTime time = new();

	private readonly FakePower power = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private BridgeHost Host() =>
		new(controllers, new FakeIdentity(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
				[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00], new Dictionary<int, string>(), new Dictionary<byte, byte[]> { [0x05] = [0x05, 1] })),
			new FakeAttacher(log), time)
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			Power = power
		};

	[Fact]
	public void Address_FromBthEnumInstanceId()
	{
		Assert.Equal(0xA0AB51123456UL, BluetoothAddress.FromInstanceId(
			@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\7&1D1B1AD6&0&A0AB51123456_C00000000"));
		Assert.Null(BluetoothAddress.FromInstanceId(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\8&2A&0&0000"));
		Assert.Equal("A0:AB:51:12:34:56", BluetoothAddress.Format(0xA0AB51123456));
	}

	[Fact]
	public void Address_FromPairingFeature_IsLittleEndian()
	{
		Assert.Equal(0xA0AB51123456UL, BluetoothAddress.FromPairingFeature([0x09, 0x56, 0x34, 0x12, 0x51, 0xAB, 0xA0, 0, 0]));
		Assert.Null(BluetoothAddress.FromPairingFeature(new byte[20]));
	}

	[Fact]
	public async Task PowerOff_UnplugsThenDisconnects_AndWaitsAgain()
	{
		await using BridgeHost host = Host();
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		Assert.True(await host.PowerOffAsync());

		Assert.Equal([0xA0AB51123456UL], power.Disconnected);
		List<string> entries = [.. log];
		Assert.True(entries.IndexOf("detach 1") < entries.IndexOf("dispose a"));
		WaitUntil(() => host.State == BridgeState.WaitingForController);
		Assert.Equal(BridgeError.None, host.Status.LastError);
	}

	[Fact]
	public async Task PowerOff_WithoutAddress_Fails_AndKeepsRunning()
	{
		power.Address = null;
		await using BridgeHost host = Host();
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		Assert.False(await host.PowerOffAsync());
		Assert.Empty(power.Disconnected);
		Assert.Equal(BridgeState.Running, host.State);
	}

	[Fact]
	public async Task PowerOff_WhenNotRunning_ReturnsFalse()
	{
		await using BridgeHost host = Host();
		Assert.False(await host.PowerOffAsync());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task IdleTimeout_PowersOffOnlyWhenEnabled(bool enabled)
	{
		await using BridgeHost host = Host();
		host.IdleMinutes = 1;
		host.PowerOffWhenIdle = enabled;
		int idle = 0;
		host.IdleTimeout += () => Interlocked.Increment(ref idle);
		bool? poweredIdle = null;
		host.PoweredOff += (_, wasIdle) => poweredIdle = wasIdle;
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);

		time.Advance(TimeSpan.FromMinutes(2));
		WaitUntil(() => Volatile.Read(ref idle) == 1);
		if (enabled)
		{
			WaitUntil(() => !power.Disconnected.IsEmpty && poweredIdle == true);
			WaitUntil(() => host.State == BridgeState.WaitingForController);
		}
		else
		{
			await Task.Delay(100);
			Assert.Empty(power.Disconnected);
			Assert.Equal(BridgeState.Running, host.State);
		}
	}
}

public class LatencyMeterTests
{
	private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

	[Fact]
	public void Snapshot_SeparatesLinkDelayAndRelay()
	{
		LatencyMeter meter = new(500);
		// Reports every 4 ms by the controller's clock (3000 ticks per ms); every 10th one arrives 3 ms late.
		for (int i = 0; i < 500; i++)
		{
			double extra = i % 10 == 9 ? 3 : 0;
			long arrival = Ticks(i * 4 + extra);
			meter.Record(arrival, (byte)i, (uint)(i * 4 * 3000), arrival + Ticks(0.2));
		}

		LatencyReading reading = Assert.IsType<LatencyReading>(meter.Snapshot());

		Assert.Equal(0, reading.LinkP50Ms, 2);
		Assert.Equal(3, reading.LinkP99Ms, 1);
		Assert.Equal(0.2, reading.RelayMs, 2);
	}

	[Fact]
	public void Snapshot_KeepsOnlyTheRollingWindow()
	{
		LatencyMeter meter = new(100);
		for (int i = 0; i < 300; i++)
		{
			// The first 200 reports are badly delayed at random-ish spots; the last 100 are clean.
			double extra = i < 200 && i % 3 == 0 ? 8 : 0;
			long arrival = Ticks(i * 4 + extra);
			meter.Record(arrival, (byte)i, (uint)(i * 4 * 3000), arrival + Ticks(0.05));
		}

		LatencyReading reading = Assert.IsType<LatencyReading>(meter.Snapshot());

		Assert.True(reading.LinkP99Ms < 0.5, reading.LinkP99Ms.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	[Fact]
	public void Snapshot_IsNullWithTooFewReports()
	{
		LatencyMeter meter = new();
		meter.Record(0, 0, 0, 1);
		Assert.Null(meter.Snapshot());
	}
}

[CollectionDefinition("AppLog", DisableParallelization = true)]
public class AppLogCollection;

[Collection("AppLog")]
public class DiagnosticsExportTests : IDisposable
{
	private readonly string dir = Path.Combine(Path.GetTempPath(), "kopru-diag-" + Guid.NewGuid().ToString("N"));

	public DiagnosticsExportTests() => Directory.CreateDirectory(dir);

	public void Dispose() => Directory.Delete(dir, true);

	[Fact]
	public void Scrub_MasksAddressesAndUserNames()
	{
		string text = """
			mac A0:AB:51:12:34:56 and a0-ab-51-12-34-56
			BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\7&1D1B1AD6&0&A0AB51123456_C00000000
			C:\Users\alice\AppData\Local\Untethered and {"p":"C:\\Users\\alice\\x"} and /Users/alice/x
			""";

		string scrubbed = DiagnosticsExport.Scrub(text);

		Assert.DoesNotContain("A0:AB", scrubbed, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("a0-ab", scrubbed, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("A0AB51123456", scrubbed, StringComparison.Ordinal);
		Assert.DoesNotContain("alice", scrubbed, StringComparison.Ordinal);
		Assert.Contains("00805F9B34FB", scrubbed, StringComparison.Ordinal);
		Assert.Contains(@"C:\Users\<user>\AppData", scrubbed, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CreateZip_HasAllParts_Scrubbed()
	{
		string settings = Path.Combine(dir, "settings.json");
		await File.WriteAllTextAsync(settings, """{"capture":"C:\\Users\\bob\\captures\\a.json","Mac":"A0:AB:51:12:34:56"}""");
		string hiding = Path.Combine(dir, "hiding-state.json");
		await File.WriteAllTextAsync(hiding, """{"hidden":["HID\\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\\8&2A&0&0000"]}""");
		string logs = Path.Combine(dir, "logs");
		Directory.CreateDirectory(logs);
		await File.WriteAllLinesAsync(Path.Combine(logs, AppLog.FileName), Enumerable.Range(0, 50).Select(i => $"line {i} C:\\Users\\bob\\x"));
		HidDevice device = new(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0df2#8&2a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
			0x054C, 0x0DF2, 78, 547, 64);
		SystemStatus status = new(new ComponentInfo(true, "1.0", @"C:\Program Files\USBip", true), ComponentInfo.Missing, RadioState.On, [device], false);
		string zipPath = Path.Combine(dir, "out", "diag.zip");

		await DiagnosticsExport.CreateZipAsync(zipPath, new DiagnosticsSources
		{
			SystemCheck = () => status,
			SettingsPath = settings,
			HidingStatePath = hiding,
			LogDirectory = logs,
			LogLines = 10,
			AdapterResultPath = Path.Combine(dir, "missing.json"),
			Controller = ControllerInfo.From(device, [0x20, 1, 2])
		});

		using ZipArchive zip = ZipFile.OpenRead(zipPath);
		Dictionary<string, string> entries = zip.Entries.ToDictionary(e => e.FullName, e =>
		{
			using StreamReader r = new(e.Open());
			return r.ReadToEnd();
		});
		Assert.Equal(["controller.txt", "hiding-state.json", "log.txt", "settings.json", "system.txt"], entries.Keys.Order(StringComparer.Ordinal));
		Assert.Contains("DS5 Untethered", entries["system.txt"], StringComparison.Ordinal);
		Assert.Contains(".NET", entries["system.txt"], StringComparison.Ordinal);
		Assert.Contains("USB/IP: installed True", entries["system.txt"], StringComparison.Ordinal);
		Assert.Contains("PID 0DF2", entries["controller.txt"], StringComparison.Ordinal);
		Assert.Contains("Edge: True", entries["controller.txt"], StringComparison.Ordinal);
		Assert.Contains("Bluetooth", entries["controller.txt"], StringComparison.Ordinal);
		Assert.Contains("200102", entries["controller.txt"], StringComparison.Ordinal);
		string[] logLines = entries["log.txt"].Split(Environment.NewLine);
		Assert.Equal(10, logLines.Length);
		Assert.StartsWith("line 40", logLines[0], StringComparison.Ordinal);
		Assert.All(entries.Values, v =>
		{
			Assert.DoesNotContain("bob", v, StringComparison.Ordinal);
			Assert.DoesNotContain("A0:AB:51", v, StringComparison.Ordinal);
		});
	}

	[Fact]
	public void AppLog_RollsOverAtSizeCap()
	{
		string saved = AppLog.Directory;
		long savedMax = AppLog.MaxBytes;
		try
		{
			AppLog.Directory = dir;
			AppLog.MaxBytes = 200;
			for (int i = 0; i < 20; i++)
			{
				AppLog.Write($"message {i}");
			}
			Assert.True(new FileInfo(Path.Combine(dir, AppLog.FileName)).Length <= 200);
			Assert.True(File.Exists(Path.Combine(dir, AppLog.PreviousFileName)));
			Assert.EndsWith("message 19", AppLog.Tail(1)[0], StringComparison.Ordinal);
		}
		finally
		{
			AppLog.Directory = saved;
			AppLog.MaxBytes = savedMax;
		}
	}
}
