// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Hid;
using Kopru.Core.Health;
using Kopru.Core.Hiding;
using Kopru.Core.Instance;
using Kopru.Core.Setup;
using Kopru.Platform.Linux.Bluetooth;
using Kopru.Platform.Linux.Games;
using Kopru.Platform.Linux.Hidraw;
using Kopru.Platform.Linux.Setup;
using Kopru.Platform.Linux.UsbIp;

namespace Kopru.Core.Tests.Linux;

// The Linux app's parts: pkexec attach, the setup checks and health chips, the Steam game scan, BlueZ power off, the XDG
// folders and the one-copy guard. Pure or on fake trees, so they run on every OS.
public class LinuxAppPartsTests
{
	private sealed class Host : ILinuxHost
	{
		public bool IsRoot { get; set; }
		public bool Vhci { get; set; } = true;
		public HashSet<string> Programs { get; } = ["usbip", "pkexec", "bluetoothctl"];

		public bool DirectoryExists(string path) => Vhci && path == UsbIpLinuxCommands.VhciDirectories[0];

		public string? FindExecutable(string name) => Programs.Contains(name) ? "/usr/bin/" + name : null;
	}

	private sealed class Runner : ICommandRunner
	{
		public ConcurrentQueue<string> Calls { get; } = new();

		public Func<string, string, CommandResult> Answer { get; set; } = (_, _) => new CommandResult(0, "", "");

		public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> argumentList, CancellationToken cancellationToken = default)
		{
			string arguments = string.Join(" ", argumentList);
			Calls.Enqueue(fileName + " " + arguments);
			return Task.FromResult(Answer(fileName, arguments));
		}
	}

	private sealed class Radio(RadioState state) : IBluetoothRadio
	{
		public RadioState Query() => state;
	}

	private static string PortListing(int tcpPort) => $"""
		Imported USB devices
		====================
		Port 00: <Port in Use> at High Speed(480Mbps)
		       Sony Interactive Entertainment : DualSense Wireless Controller (054c:0ce6)
		       1-1 -> usbip://127.0.0.1:{tcpPort}/1-1
		           -> remote bus/dev 001/001
		""";

	[Fact]
	public async Task Pkexec_AttachesThroughPkexec_AndDetachesThroughItOnlyWhenThePortStaysOurs()
	{
		Host host = new();
		Runner runner = new() { Answer = (_, args) => new CommandResult(0, args == "port" ? PortListing(40123) : "", "") };
		LinuxUsbIpAttacher attacher = new(runner, host, UsbIpAttachMode.Pkexec)
		{
			PollInterval = TimeSpan.FromMilliseconds(5),
			DetachSettle = TimeSpan.FromMilliseconds(5)
		};

		Assert.Equal(0, await attacher.AttachAsync(40123, "1-1", CancellationToken.None));
		await attacher.DetachAsync(0);
		await attacher.LastDetachCheck;

		Assert.Contains("/usr/bin/pkexec /usr/bin/usbip --tcp-port 40123 attach -r 127.0.0.1 -b 1-1", runner.Calls);
		// The listing still shows our import after the server closed: the fallback detaches it.
		Assert.Contains("/usr/bin/pkexec /usr/bin/usbip detach -p 0", runner.Calls);
	}

	[Fact]
	public async Task Pkexec_ReleasedPort_IsNotDetached_NoSecondPrompt()
	{
		Host host = new();
		bool attached = false;
		Runner runner = new()
		{
			Answer = (file, args) =>
			{
				if (args.Contains("attach"))
				{
					attached = true;
				}
				return new CommandResult(0, args == "port" && attached ? PortListing(40123) : "", "");
			}
		};
		LinuxUsbIpAttacher attacher = new(runner, host, UsbIpAttachMode.Pkexec)
		{
			PollInterval = TimeSpan.FromMilliseconds(5),
			DetachSettle = TimeSpan.FromMilliseconds(5)
		};
		Assert.Equal(0, await attacher.AttachAsync(40123, "1-1", CancellationToken.None));
		attached = false;
		await attacher.DetachAsync(0);
		await attacher.LastDetachCheck;
		Assert.DoesNotContain(runner.Calls, c => c.Contains("detach"));
	}

	[Theory]
	[InlineData("1-1", true)]
	[InlineData("2-1.4", true)]
	[InlineData("1-1; reboot", false)]
	[InlineData("-1", false)]
	[InlineData("", false)]
	public void BusIds_AreValidated(string busId, bool valid) => Assert.Equal(valid, UsbIpLinuxCommands.IsValidBusId(busId));

	[Fact]
	public async Task Pkexec_Attach_RefusesAStrangeBusId() =>
		await Assert.ThrowsAsync<ArgumentException>(() => new LinuxUsbIpAttacher(new Runner(), new Host(), UsbIpAttachMode.Pkexec)
			.AttachAsync(40123, "1-1 --other", CancellationToken.None));

	[Fact]
	public void Pkexec_AsRoot_RunsUsbIpDirectly() =>
		Assert.Equal(UsbIpAttachMode.Direct, new LinuxUsbIpAttacher(new Runner(), new Host { IsRoot = true }, UsbIpAttachMode.Pkexec).EffectiveMode);

	[Fact]
	public async Task Pkexec_DismissedDialog_SaysSo()
	{
		Runner runner = new() { Answer = (file, _) => new CommandResult(file.EndsWith("pkexec") ? UsbIpLinuxCommands.PkexecDismissed : 0, "", "") };
		LinuxUsbIpAttacher attacher = new(runner, new Host(), UsbIpAttachMode.Pkexec);
		var e = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => attacher.AttachAsync(40123, "1-1", CancellationToken.None));
		Assert.Equal(UsbIpLinuxCommands.PkexecCancelled, e.Message);
	}

	[Fact]
	public async Task Pkexec_Missing_IsReported()
	{
		Host host = new();
		host.Programs.Remove("pkexec");
		LinuxUsbIpAttacher attacher = new(new Runner(), host, UsbIpAttachMode.Pkexec);
		await Assert.ThrowsAsync<FileNotFoundException>(() => attacher.AttachAsync(40123, "1-1", CancellationToken.None));
	}

	// A fake /sys/class/hidraw with one Bluetooth DualSense node.
	private static string FakeHidraw()
	{
		string root = Path.Combine(Path.GetTempPath(), "kopru-linux-" + Guid.NewGuid().ToString("N"));
		string device = Path.Combine(root, "hidraw3", "device");
		Directory.CreateDirectory(device);
		File.WriteAllText(Path.Combine(device, "uevent"), "DRIVER=playstation\nHID_ID=0005:0000054C:00000CE6\nHID_NAME=DualSense Wireless Controller\nHID_UNIQ=02:00:00:00:00:01\n");
		return root;
	}

	[Fact]
	public void SystemCheck_ListsWhatIsMissing_WithFixes()
	{
		string root = FakeHidraw();
		try
		{
			Host host = new() { Vhci = false };
			host.Programs.Remove("pkexec");
			LinuxSystemCheck check = new(host, new HidrawFinder(root), _ => false, new Radio(RadioState.On));
			SystemStatus status = check.Run();

			Assert.True(status.IsLinux);
			Assert.True(status.ControllerOnBluetooth);
			var missing = status.Requirements!.Where(r => !r.Ready).Select(r => r.Id).ToList();
			Assert.Equal([SetupRequirementId.VhciModule, SetupRequirementId.Pkexec, SetupRequirementId.HidrawAccess], missing);
			Assert.Contains("modprobe vhci-hcd", status.Requirements!.First(r => r.Id == SetupRequirementId.VhciModule).FixCommands);
			Assert.Contains("054C:0CE6", status.Requirements!.First(r => r.Id == SetupRequirementId.HidrawAccess).FixCommands);

			SetupWizard wizard = new(check.Run, new NoLinks()) { LanguageChosen = true };
			SetupStep system = wizard.Steps.First(s => s.Id == SetupStepId.Drivers);
			Assert.Equal(StepStatus.Needed, system.Status);
			Assert.Contains(system.Actions, a => a.Kind == SetupActionKind.CopyText && a.Target!.Contains("modprobe"));
			Assert.DoesNotContain(wizard.Steps.SelectMany(s => s.Actions), a => a.Kind == SetupActionKind.OpenLink);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void SystemCheck_AllThere_IsDone()
	{
		string root = FakeHidraw();
		try
		{
			LinuxSystemCheck check = new(new Host(), new HidrawFinder(root), _ => true, new Radio(RadioState.On));
			SetupWizard wizard = new(check.Run, new NoLinks()) { LanguageChosen = true };
			Assert.Equal(StepStatus.Done, wizard.Steps.First(s => s.Id == SetupStepId.Drivers).Status);
			Assert.Equal(StepStatus.Done, wizard.Steps.First(s => s.Id == SetupStepId.PairController).Status);
			Assert.True(wizard.CanFinish);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	private sealed class NoLinks : IUrlLauncher
	{
		public void Open(string target)
		{
		}
	}

	private static HealthEnvironment Linux(params SetupRequirement[] requirements) =>
		new(HealthProbe.LinuxUsbIp(requirements), ComponentInfo.Missing, RadioState.On, [new PairedController("DualSense", 0, false, false)], null, null,
			requirements);

	private static SetupRequirement Req(SetupRequirementId id, bool ready) => new(id, ready, _ => id + " missing");

	[Fact]
	public void Health_Linux_BlocksOnlyOnTheUsbIpParts_AndNeverOnHiding()
	{
		HealthEnvironment ok = Linux(Req(SetupRequirementId.VhciModule, true), Req(SetupRequirementId.UsbIpTool, true),
			Req(SetupRequirementId.Pkexec, true), Req(SetupRequirementId.HidrawAccess, false));
		HealthReport report = HealthReport.Build(ok, HealthRuntime.Idle, DateTimeOffset.UnixEpoch);
		Assert.Null(report.BlockingIssue);
		Assert.Equal(HealthLevel.Warning, report.Chip(HealthChipKind.Driver).Level);
		Assert.Equal(HealthLevel.Unknown, report.Chip(HealthChipKind.HidHide).Level);

		HealthEnvironment noVhci = Linux(Req(SetupRequirementId.VhciModule, false), Req(SetupRequirementId.UsbIpTool, true));
		HealthReport blocked = HealthReport.Build(noVhci, HealthRuntime.Idle, DateTimeOffset.UnixEpoch);
		Assert.Equal(BlockingKind.DriverMissing, blocked.BlockingIssue?.Kind);
		Assert.Equal("VhciModule missing", blocked.BlockingIssue?.Reason);
	}

	[Theory]
	[InlineData("PATH=/usr/bin\0SteamAppId=1091500\0", 1091500L)]
	[InlineData("STEAM_COMPAT_APP_ID=2050650\0", 2050650L)]
	[InlineData("SteamAppId=0\0SteamGameId=553850\0", 553850L)]
	[InlineData("HOME=/home/someone\0", null)]
	public void SteamAppId_FromTheEnvironment(string environ, long? expected) =>
		Assert.Equal(expected, LinuxProcessSource.ParseSteamAppId(environ));

	[Fact]
	public void WindowsExe_FromAProtonCommandLine()
	{
		var exe = LinuxProcessSource.WindowsExe("/usr/bin/wine64-preloader\0Z:\\games\\steamapps\\common\\Game\\bin\\Game.exe\0-dx12\0");
		Assert.Equal(("Game.exe", "/games/steamapps/common/Game/bin/Game.exe"), exe);
		Assert.Null(LinuxProcessSource.WindowsExe("C:\\windows\\system32\\explorer.exe\0"));
		Assert.Null(LinuxProcessSource.WindowsExe("/usr/bin/bash\0-c\0true\0"));
	}

	[Fact]
	public void SteamGames_ScanAFakeProc()
	{
		string proc = Path.Combine(Path.GetTempPath(), "kopru-proc-" + Guid.NewGuid().ToString("N"));
		try
		{
			void Process(int pid, string cmdline, string environ)
			{
				string dir = Path.Combine(proc, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
				Directory.CreateDirectory(dir);
				File.WriteAllText(Path.Combine(dir, "cmdline"), cmdline);
				File.WriteAllText(Path.Combine(dir, "environ"), environ);
				File.WriteAllText(Path.Combine(dir, "comm"), cmdline.Split('\0')[0].Split('/')[^1]);
			}
			Process(100, "/usr/bin/bash\0", "HOME=/home/someone\0");
			Process(200, "/usr/bin/wineserver\0", "SteamAppId=1091500\0");
			Process(300, "/usr/bin/wine64-preloader\0Z:\\g\\Cyberpunk2077.exe\0", "SteamAppId=1091500\0");
			Process(400, "/home/someone/game/NativeGame.x86_64\0", "SteamAppId=1245620\0");
			long now = 0;
			// Process 400 is another user's: skipped without reading it.
			LinuxProcessSource source = new(proc, canRead: path => !path.Contains("400"), clockMs: () => now);
			var games = source.SteamGames();
			Assert.Single(games);
			Assert.Equal("Cyberpunk2077.exe", games[0].Exe);
			Assert.Equal(1091500L, games[0].SteamAppId);
			Assert.Equal(300, source.Foreground()?.Id);

			// Scanned again only after the interval: a game that starts meanwhile shows up then.
			Process(500, "/usr/bin/wine64-preloader\0Z:\\g\\Other.exe\0", "STEAM_COMPAT_APP_ID=2050650\0");
			Assert.Single(source.SteamGames());
			now += (long)LinuxProcessSource.ScanInterval.TotalMilliseconds;
			Assert.Equal([500, 300], source.SteamGames().Select(g => g.Id));
		}
		finally
		{
			Directory.Delete(proc, true);
		}
	}

	[Fact]
	public async Task BluezPower_ReadsTheAddressFromUevent_AndDisconnects()
	{
		string root = FakeHidraw();
		try
		{
			Runner runner = new() { Answer = (_, _) => new CommandResult(0, "Successful disconnected", "") };
			BluezControllerPower power = new(runner, new Host(), root);
			ulong? address = power.FindAddress(new HidDevice("/dev/hidraw3", HidDevice.SonyVendorId, 0x0CE6, 78, 78, 64));
			Assert.Equal(0x020000000001UL, address);
			Assert.True(await power.DisconnectAsync(address!.Value));
			Assert.Equal("/usr/bin/bluetoothctl disconnect 02:00:00:00:00:01", Assert.Single(runner.Calls));
			Assert.Null(BluezControllerPower.ParseAddress("not an address"));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void XdgFolders_FollowTheSpec()
	{
		Dictionary<string, string?> env = new() { ["XDG_CONFIG_HOME"] = "/cfg", ["XDG_STATE_HOME"] = "relative/ignored" };
		Assert.Equal("/cfg", AppPaths.XdgConfigHome(k => env.GetValueOrDefault(k), "/home/someone"));
		Assert.Equal(Path.Combine("/home/someone", ".local", "state"), AppPaths.XdgStateHome(k => env.GetValueOrDefault(k), "/home/someone"));
		Assert.Equal(Path.Combine("/home/someone", ".config"), AppPaths.XdgConfigHome(_ => null, "/home/someone"));
	}

	[Fact]
	public void UnixSingleInstance_SecondCopyAsksTheFirstToShow()
	{
		// Short: a Unix domain socket path has a small length limit.
		string dir = Path.Combine(Path.GetTempPath(), "ki" + Guid.NewGuid().ToString("N")[..8]);
		try
		{
			using UnixSingleInstance first = UnixSingleInstance.Acquire(dir);
			Assert.True(first.IsFirst);
			int shown = 0;
			first.Listen(() => Interlocked.Increment(ref shown));

			using UnixSingleInstance second = UnixSingleInstance.Acquire(dir);
			Assert.False(second.IsFirst);
			Assert.True(second.AskFirstToShow(TimeSpan.FromSeconds(5)));
			Assert.Equal(1, Volatile.Read(ref shown));
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void UdpPortTable_ReadsProcNetUdp()
	{
		const string Header = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops\n";
		string table = Header +
			"  12: 0100007F:1A85 00000000:0000 07 00000000:00000000 00:00000000 00000000  1000        0 12345 2 0000000000000000 0\n";
		Assert.True(Kopru.Core.Mods.UdpPortTable.IsBound(table, 0x1A85));
		Assert.False(Kopru.Core.Mods.UdpPortTable.IsBound(table, 6969));
		Assert.Null(Kopru.Core.Mods.UdpPortTable.IsBound(Header, 6969));
	}
}
