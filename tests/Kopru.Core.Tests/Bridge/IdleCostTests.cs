// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Lighting;
using Kopru.Core.Setup;
using Kopru.Core.Steam;
using Xunit;

namespace Kopru.Core.Tests;

// The app's recurring background work stays small: Steam's config files are parsed only when they change, Steam's
// process is not looked up on every check, slow health facts are not read on every refresh, only Sony devices are opened
// when the HID devices are listed, and a steady light does not tick at 30 Hz.
[Trait("Category", "Windows")]
public sealed class IdleCostTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "kopru-idle-" + Guid.NewGuid().ToString("N"));

	public IdleCostTests()
	{
		Directory.CreateDirectory(Path.Combine(_dir, "config"));
		File.WriteAllText(Path.Combine(_dir, "steam.exe"), "");
	}

	public void Dispose() => Directory.Delete(_dir, true);

	private sealed class FileSteam(string path) : ISteamEnvironment
	{
		public string? SteamPath { get; } = path;
		public bool IsSteamRunning => true;
		public int Reads { get; private set; }
		public bool FileExists(string p) => File.Exists(p);

		public string ReadAllText(string p)
		{
			Reads++;
			return File.ReadAllText(p);
		}

		public IEnumerable<string> GetDirectories(string p) => Directory.Exists(p) ? Directory.GetDirectories(p) : [];
		public DateTime GetLastWriteTimeUtc(string p) => File.GetLastWriteTimeUtc(p);
		public long GetFileLength(string p) => new FileInfo(p).Length;
	}

	private void WriteConfig(string global, DateTime at)
	{
		string file = Path.Combine(_dir, "config", "config.vdf");
		File.WriteAllText(file, $"\"InstallConfigStore\" {{ \"Software\" {{ \"Valve\" {{ \"Steam\" {{ \"SteamController_PSSupport\" \"{global}\" }} }} }} }}");
		File.SetLastWriteTimeUtc(file, at);
	}

	private void WriteLocal(string perApp, DateTime at)
	{
		string dir = Path.Combine(_dir, "userdata", "12345", "config");
		Directory.CreateDirectory(dir);
		string file = Path.Combine(dir, "localconfig.vdf");
		File.WriteAllText(file, $"\"UserLocalConfigStore\" {{ \"Apps\" {{ \"1091500\" {{ \"UseSteamControllerConfig\" \"{perApp}\" }} }} }}");
		File.SetLastWriteTimeUtc(file, at);
	}

	[Fact]
	public void SteamConfig_IsParsedOnce_WhileTheFilesAreUnchanged_AndAgainAfterAChange()
	{
		DateTime t0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
		WriteConfig("2", t0);
		WriteLocal("1", t0);
		FileSteam env = new(_dir);
		SteamInputCheck check = new(env);

		Assert.Equal(SteamInputStatus.OnGlobally, check.Check("1091500").Status);
		Assert.Equal(2, check.Parses);
		for (int i = 0; i < 10; i++)
		{
			Assert.Equal(SteamInputStatus.OnGlobally, check.Check("1091500").Status);
		}
		Assert.Equal(2, check.Parses);
		Assert.Equal(2, env.Reads);

		// The user switches Steam Input off for the game: localconfig.vdf changes, config.vdf does not.
		WriteLocal("0", t0.AddMinutes(1));
		Assert.Equal(SteamInputStatus.Off, check.Check("1091500").Status);
		Assert.Equal(3, check.Parses);
		// The per-app value of another game still follows the global one.
		Assert.Equal(SteamInputStatus.OnGlobally, check.Check("999").Status);
		Assert.Equal(3, check.Parses);

		// Global off (same size, newer time).
		WriteConfig("0", t0.AddMinutes(2));
		Assert.Equal(SteamInputStatus.Off, check.Check("999").Status);
		Assert.Equal(4, check.Parses);
	}

	[Fact]
	public void SteamConfig_ABrokenFile_IsNotParsedAgainUntilItChanges()
	{
		DateTime t0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
		string file = Path.Combine(_dir, "config", "config.vdf");
		File.WriteAllText(file, "\"InstallConfigStore\" {");
		File.SetLastWriteTimeUtc(file, t0);
		SteamInputCheck check = new(new FileSteam(_dir));
		Assert.Equal(SteamInputStatus.Unknown, check.Check().Status);
		Assert.Equal(SteamInputStatus.Unknown, check.Check().Status);
		Assert.Equal(1, check.Parses);
		WriteConfig("1", t0.AddMinutes(1));
		Assert.Equal(SteamInputStatus.OnGlobally, check.Check().Status);
		Assert.Equal(2, check.Parses);
	}

	[Fact]
	public void ProcessPresence_ListsAtMostOncePerInterval_WhileNotRunning_AndWatchesTheFoundOne()
	{
		long now = 0;
		bool running = false;
		bool alive = true;
		ProcessPresence steam = new(() => running ? () => alive : null, () => now);

		Assert.False(steam.IsRunning);
		Assert.Equal(1, steam.Scans);
		// The game timer asks every second or ten: no new listing within the interval.
		for (int i = 0; i < 5; i++)
		{
			now += 10_000;
			Assert.False(steam.IsRunning);
		}
		Assert.Equal(1, steam.Scans);

		// Steam started: found at the next listing (at most ScanInterval later).
		running = true;
		now += 10_000;
		Assert.True(steam.IsRunning);
		Assert.Equal(2, steam.Scans);
		// While it runs, only its handle is asked.
		for (int i = 0; i < 20; i++)
		{
			now += 10_000;
			Assert.True(steam.IsRunning);
		}
		Assert.Equal(2, steam.Scans);

		// It exits: seen at once, and the processes are listed again right then (a restart is found).
		alive = false;
		running = false;
		Assert.False(steam.IsRunning);
		Assert.Equal(3, steam.Scans);
		Assert.False(steam.IsRunning);
		Assert.Equal(3, steam.Scans);
	}

	private sealed class CountingSystem : IInstallRegistry, IBluetoothRadio, IPairedControllers, IRestartProbe
	{
		public int ProgramLookups { get; private set; }
		public int RadioQueries { get; private set; }
		public int PairedLookups { get; private set; }
		public int RestartChecks { get; private set; }
		public bool UsbIp { get; set; } = true;
		public RadioState Radio { get; set; } = RadioState.On;
		public IReadOnlyList<PairedController> Paired { get; set; } = [];

		public (string? Version, string? Location)? FindProgram(string prefix)
		{
			ProgramLookups++;
			return prefix == SystemCheck.UsbIpProgramPrefix ? UsbIp ? ("1.0", @"C:\fake\") : null : ("1.6", null);
		}

		public bool ServiceExists(string serviceName) => true;
		public bool FileExists(string path) => true;

		public RadioState Query()
		{
			RadioQueries++;
			return Radio;
		}

		public IReadOnlyList<PairedController> Find()
		{
			PairedLookups++;
			return Paired;
		}

		public bool? IsRestartPending()
		{
			RestartChecks++;
			return false;
		}
	}

	[Fact]
	public void HealthProbe_ReadsTheSlowFacts_OnlyOnAFullRead_OrOncePerInterval()
	{
		long now = 0;
		CountingSystem system = new();
		HealthProbe probe = new(system, system, system, system, null, () => now);

		HealthEnvironment first = probe.Read(full: false, out bool readStatic);
		Assert.True(readStatic);
		Assert.True(first.UsbIp.Ready);
		Assert.Equal(1, probe.StaticReads);
		int programLookups = system.ProgramLookups;

		// The 5 s refresh for a minute: radio and paired list every time, the registry walks not.
		system.Paired = [new PairedController("DualSense Wireless Controller", 1, true, false)];
		for (int i = 0; i < 11; i++)
		{
			now += 5_000;
			HealthEnvironment env = probe.Read(full: false, out readStatic);
			Assert.False(readStatic);
			Assert.Single(env.Paired);
		}
		Assert.Equal(1, probe.StaticReads);
		Assert.Equal(programLookups, system.ProgramLookups);
		Assert.Equal(1, system.RestartChecks);
		Assert.Equal(12, system.RadioQueries);
		Assert.Equal(12, system.PairedLookups);

		// Once the interval passed they are read again.
		now += 5_000;
		probe.Read(full: false, out readStatic);
		Assert.True(readStatic);
		Assert.Equal(2, probe.StaticReads);

		// On demand (an install step, a bridge event): at once, with the new state.
		system.UsbIp = false;
		Assert.True(probe.Read(full: false).UsbIp.Ready);
		Assert.False(probe.Read(full: true).UsbIp.Ready);
		Assert.Equal(3, probe.StaticReads);
	}

	[Theory]
	[InlineData(@"\\?\hid#vid_054c&pid_0ce6&mi_03#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true)]
	[InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&2f3e4d5c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true)]
	[InlineData(@"\\?\HID#{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0DF2#9&1&0&0000#{4D1E55B2-F16F-11CF-88CB-001111000030}", true)]
	[InlineData(@"\\?\hid#vid_046d&pid_c52b&mi_01&col01#8&2a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false)]
	[InlineData(@"\\?\hid#vid_054c&pid_09cc&mi_03#7&3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false)]
	[InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002045e_pid&0b13#9&3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false)]
	[InlineData(@"\\?\hid#converted_device&col01#1&2d595ca7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true)]
	public void HidListing_OpensOnlyDevicesThatMayBeADualSense(string path, bool opened) =>
		Assert.Equal(opened, WindowsHid.MayBeDualSense(path));

	private static LightSettings Light(LightOptions options) => LightSettings.FromRgb(0x2F6BFF, 0.8) with { Options = options };

	[Fact]
	public void LightSender_TicksFast_OnlyWhileTheLightChangesOverTime()
	{
		ManualTime time = new();
		LightOptions options = LightOptions.Default;
		BatteryStatus? battery = new BatteryStatus(60, ChargeState.Discharging);
		bool game = false;
		using LightSender sender = new(_ => { }, () => Light(options), () => battery, () => game, time);

		Assert.Equal(LightSender.SteadyInterval, sender.WantedInterval());
		options = new LightOptions(LightMode.Breathing);
		Assert.Equal(LightSender.Interval, sender.WantedInterval());
		options = new LightOptions(LightMode.Rainbow);
		Assert.Equal(LightSender.Interval, sender.WantedInterval());
		// A game driving the light: nothing to animate meanwhile.
		game = true;
		Assert.Equal(LightSender.SteadyInterval, sender.WantedInterval());
		game = false;

		// The battery colour is steady until it pulses (critical and discharging).
		options = new LightOptions(LightMode.Battery);
		Assert.Equal(LightSender.SteadyInterval, sender.WantedInterval());
		battery = new BatteryStatus(0, ChargeState.Discharging);
		Assert.Equal(LightSender.Interval, sender.WantedInterval());

		// A hold (profile indicator) ends on time.
		options = LightOptions.Default;
		sender.Hold(TimeSpan.FromSeconds(1.5));
		Assert.Equal(LightSender.Interval, sender.WantedInterval());
		time.Advance(TimeSpan.FromSeconds(2));
		Assert.Equal(LightSender.SteadyInterval, sender.WantedInterval());
	}

	[Fact]
	public void BackgroundWork_LogLine_HasEveryCounter()
	{
		BackgroundWork.Counts c = new(12, 7, 2, 1, 30, 3);
		Assert.Equal("background work: last 600 s, hid listings 12, health refreshes 7 (full 2), steam config parses 1, state refreshes 30, settings saves 3",
			BackgroundWork.Format(c, TimeSpan.FromSeconds(600)));
		Assert.Equal(new BackgroundWork.Counts(2, 2, 1, 0, 10, 1), c - new BackgroundWork.Counts(10, 5, 1, 1, 20, 2));
	}
}
