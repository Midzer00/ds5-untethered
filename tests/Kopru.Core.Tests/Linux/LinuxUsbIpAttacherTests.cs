// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Hiding;
using Kopru.Core.UsbIp;
using Kopru.Platform.Linux.Hiding;
using Kopru.Platform.Linux.UsbIp;

namespace Kopru.Core.Tests.Linux;

public class LinuxUsbIpAttacherTests
{
	private const string Exe = "/usr/bin/usbip";

	// "usbip port" with our pad imported (the layout usbip-win2 copies; UsbIpPortList reads both).
	private static string PortListing(int tcpPort, string busId = "1-1", int port = 0) => $"""
		Imported USB devices
		====================
		Port {port:00}: <Port in Use> at High Speed(480Mbps)
		       Sony Interactive Entertainment : DualSense Wireless Controller (054c:0ce6)
		       3-1 -> usbip://127.0.0.1:{tcpPort}/{busId}
		           -> remote bus/dev 001/001
		""";

	private sealed class FakeHost : ILinuxHost
	{
		public bool IsRoot { get; set; }

		public bool Vhci { get; set; } = true;

		public bool Installed { get; set; } = true;

		public bool DirectoryExists(string path) => Vhci && path == UsbIpLinuxCommands.VhciDirectories[0];

		public string? FindExecutable(string name) => Installed && name == "usbip" ? Exe : null;
	}

	private sealed class FakeRunner : ICommandRunner
	{
		public ConcurrentQueue<string> Calls { get; } = new();

		public Func<string, string, CommandResult> Answer { get; set; } = (_, _) => new CommandResult(0, "", "");

		public Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Calls.Enqueue(fileName + " " + arguments);
			return Task.FromResult(Answer(fileName, arguments));
		}
	}

	private readonly FakeHost host = new();
	private readonly FakeRunner runner = new();
	private readonly ConcurrentQueue<string> notices = new();

	private LinuxUsbIpAttacher Attacher(UsbIpAttachMode mode) => new(runner, host, mode, notices.Enqueue)
	{
		PollInterval = TimeSpan.FromMilliseconds(5),
		ManualTimeout = TimeSpan.FromSeconds(5),
		ListRetries = 3
	};

	[Fact]
	public void Commands_FollowTheManPage()
	{
		Assert.Equal("--tcp-port 40123 attach -r 127.0.0.1 -b 1-1", UsbIpLinuxCommands.AttachArguments(40123, "1-1"));
		Assert.Equal("detach -p 3", UsbIpLinuxCommands.DetachArguments(3));
		Assert.Equal("sudo usbip --tcp-port 40123 attach -r 127.0.0.1 -b 1-2", UsbIpLinuxCommands.SudoAttach(40123, "1-2"));
	}

	[Fact]
	public void FindImport_TakesOnlyOurLoopbackImport()
	{
		IReadOnlyList<UsbIpImport> imports = UsbIpPortList.Parse(PortListing(40123, "1-1", 2) + "\n" + PortListing(40999, "1-2", 5)
			.Replace("127.0.0.1", "192.168.0.9"));
		Assert.Equal(2, UsbIpLinuxCommands.FindImport(imports, 40123, "1-1"));
		Assert.Null(UsbIpLinuxCommands.FindImport(imports, 40123, "1-2"));
		Assert.Null(UsbIpLinuxCommands.FindImport(imports, 40999, "1-2"));
		Assert.Null(UsbIpLinuxCommands.FindImport(imports, 1, "1-1"));
	}

	[Theory]
	[InlineData("usbip: error: open vhci_driver", true, "vhci_hcd")]
	[InlineData("libusbip: error: write: Permission denied", true, "needs root")]
	[InlineData("", false, "needs root")]
	[InlineData("usbip: error: tcp connect: Connection refused", true, "could not reach")]
	[InlineData("usbip: error: something else", true, "attach failed (exit 1)")]
	public void Explain_NamesTheCause(string error, bool root, string expected)
	{
		string text = UsbIpLinuxCommands.Explain(new CommandResult(1, "", error), root, 40123, "1-1");
		Assert.Contains(expected, text);
	}

	[Fact]
	public async Task NotInstalled_IsAFileNotFound_SoTheBridgeSaysUsbIpMissing()
	{
		host.Installed = false;
		await Assert.ThrowsAsync<FileNotFoundException>(() => Attacher(UsbIpAttachMode.Direct).AttachAsync(40123, "1-1", CancellationToken.None));
		Assert.Empty(runner.Calls);
	}

	[Fact]
	public async Task VhciNotLoaded_SaysHowToLoadIt()
	{
		host.Vhci = false;
		InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(
			() => Attacher(UsbIpAttachMode.Direct).AttachAsync(40123, "1-1", CancellationToken.None));
		Assert.Contains("modprobe vhci-hcd", e.Message);
		Assert.Empty(runner.Calls);
	}

	[Fact]
	public async Task Direct_WithoutRoot_PrintsTheExactSudoCommand_AndRunsNothing()
	{
		InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(
			() => Attacher(UsbIpAttachMode.Direct).AttachAsync(40123, "1-1", CancellationToken.None));
		Assert.Contains("sudo usbip --tcp-port 40123 attach -r 127.0.0.1 -b 1-1", e.Message);
		Assert.Empty(runner.Calls);
	}

	[Fact]
	public async Task Direct_AsRoot_AttachesThenFindsThePort()
	{
		host.IsRoot = true;
		runner.Answer = (_, args) => args == "port" ? new CommandResult(0, PortListing(40123, "1-1", 4), "") : new CommandResult(0, "", "");

		int port = await Attacher(UsbIpAttachMode.Auto).AttachAsync(40123, "1-1", CancellationToken.None);

		Assert.Equal(4, port);
		Assert.Equal([$"{Exe} --tcp-port 40123 attach -r 127.0.0.1 -b 1-1", $"{Exe} port"], runner.Calls);
	}

	[Fact]
	public async Task Direct_FailedAttach_IsExplained()
	{
		host.IsRoot = true;
		runner.Answer = (_, _) => new CommandResult(1, "", "usbip: error: open vhci_driver");
		InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(
			() => Attacher(UsbIpAttachMode.Direct).AttachAsync(40123, "1-1", CancellationToken.None));
		Assert.Equal(UsbIpLinuxCommands.VhciMissing, e.Message);
	}

	[Fact]
	public async Task Direct_AttachedButNeverListed_Fails()
	{
		host.IsRoot = true;
		runner.Answer = (_, _) => new CommandResult(0, "", "");
		InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(
			() => Attacher(UsbIpAttachMode.Direct).AttachAsync(40123, "1-1", CancellationToken.None));
		Assert.Contains("not in \"usbip port\"", e.Message);
		Assert.Equal(3, runner.Calls.Count(c => c.EndsWith(" port", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task Sudo_RunsUsbipThroughSudo()
	{
		runner.Answer = (_, args) => args == "port" ? new CommandResult(0, PortListing(40123), "") : new CommandResult(0, "", "");

		await Attacher(UsbIpAttachMode.Sudo).AttachAsync(40123, "1-1", CancellationToken.None);
		await Attacher(UsbIpAttachMode.Sudo).DetachAsync(0);

		Assert.Contains($"sudo {Exe} --tcp-port 40123 attach -r 127.0.0.1 -b 1-1", runner.Calls);
		Assert.Contains($"sudo {Exe} detach -p 0", runner.Calls);
	}

	[Fact]
	public async Task Manual_PrintsTheCommand_AndWaitsUntilThePadIsListed()
	{
		int polls = 0;
		runner.Answer = (_, _) => Interlocked.Increment(ref polls) < 3 ? new CommandResult(0, "", "") : new CommandResult(0, PortListing(40123, "1-1", 1), "");

		int port = await Attacher(UsbIpAttachMode.Auto).AttachAsync(40123, "1-1", CancellationToken.None);

		Assert.Equal(1, port);
		Assert.Contains(notices, n => n.Contains("sudo usbip --tcp-port 40123 attach -r 127.0.0.1 -b 1-1", StringComparison.Ordinal));
		// Never an attach of its own: only listings.
		Assert.All(runner.Calls, c => Assert.Equal($"{Exe} port", c));
	}

	[Fact]
	public async Task Manual_GoesOnWhenTheServerSawTheImport_EvenIfTheListingFails()
	{
		runner.Answer = (_, _) => new CommandResult(2, "", "cannot read");
		LinuxUsbIpAttacher attacher = Attacher(UsbIpAttachMode.Manual);

		Task<int> attach = attacher.AttachAsync(40123, "1-1", CancellationToken.None);
		Assert.True(SpinWait.SpinUntil(() => runner.Calls.Count >= 2, 5000));
		attacher.NotifyImported();

		Assert.Equal(IUsbIpAttacher.UnknownPort, await attach);
		Assert.Contains(UsbIpLinuxCommands.DetachUnknown, notices);
		Assert.Single(notices, n => n.Contains("\"usbip port\" failed", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Manual_TimesOut()
	{
		LinuxUsbIpAttacher attacher = new(runner, host, UsbIpAttachMode.Manual, notices.Enqueue)
		{
			PollInterval = TimeSpan.FromMilliseconds(5),
			ManualTimeout = TimeSpan.FromMilliseconds(50)
		};
		await Assert.ThrowsAsync<TimeoutException>(() => attacher.AttachAsync(40123, "1-1", CancellationToken.None));
	}

	[Fact]
	public async Task Manual_StopsWhenCancelled()
	{
		using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(50));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Attacher(UsbIpAttachMode.Manual).AttachAsync(40123, "1-1", cancel.Token));
	}

	[Fact]
	public async Task PortZero_IsARealPort_FoundAndDetached()
	{
		host.IsRoot = true;
		runner.Answer = (_, args) => args == "port" ? new CommandResult(0, PortListing(40123, "1-1", 0), "") : new CommandResult(0, "", "");
		LinuxUsbIpAttacher attacher = Attacher(UsbIpAttachMode.Direct);

		Assert.Equal(0, await attacher.AttachAsync(40123, "1-1", CancellationToken.None));
		await attacher.DetachAsync(0);

		Assert.Contains($"{Exe} detach -p 0", runner.Calls);
	}

	[Theory]
	[InlineData(UsbIpAttachMode.Direct)]
	[InlineData(UsbIpAttachMode.Sudo)]
	[InlineData(UsbIpAttachMode.Manual)]
	public async Task AnUnknownPort_IsNeverDetached_OnlyExplained(UsbIpAttachMode mode)
	{
		host.IsRoot = true;
		await Attacher(mode).DetachAsync(IUsbIpAttacher.UnknownPort);

		Assert.Empty(runner.Calls);
		Assert.Equal([UsbIpLinuxCommands.DetachUnknown], notices);
	}

	[Fact]
	public async Task Manual_Detach_OnlyPrintsTheCommand()
	{
		await Attacher(UsbIpAttachMode.Manual).DetachAsync(3);
		Assert.Empty(runner.Calls);
		Assert.Contains(notices, n => n.Contains("sudo usbip detach -p 3", StringComparison.Ordinal));
	}

	[Fact]
	public async Task List_ParsesThePorts_AndAFailedListingThrows()
	{
		runner.Answer = (_, _) => new CommandResult(0, PortListing(40123, "1-3", 7), "");
		UsbIpImport import = Assert.Single(await Attacher(UsbIpAttachMode.Manual).ListAsync(CancellationToken.None));
		Assert.Equal(new UsbIpImport(7, "127.0.0.1", 40123, "1-3"), import);

		runner.Answer = (_, _) => new CommandResult(1, "", "boom");
		await Assert.ThrowsAsync<InvalidOperationException>(() => Attacher(UsbIpAttachMode.Manual).ListAsync(CancellationToken.None));
	}

	[Fact]
	public async Task LinuxHiding_IsNotInstalled_AndLogsOnce()
	{
		List<string> lines = [];
		LinuxHiding hiding = new(lines.Add);

		await hiding.BeginAsync();
		await hiding.HideAsync("x");
		await hiding.RestoreAsync();

		Assert.False(hiding.IsInstalled);
		Assert.Equal([LinuxHiding.NotImplemented], lines);
	}
}
