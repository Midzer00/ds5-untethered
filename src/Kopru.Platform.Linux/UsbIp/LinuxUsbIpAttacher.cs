// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.Versioning;
using Kopru.Core.Bridge;
using Kopru.Core.Hiding;
using Kopru.Core.UsbIp;

namespace Kopru.Platform.Linux.UsbIp;

// How the attacher gets the root rights "usbip attach" needs (it writes to the vhci_hcd driver's sysfs files).
public enum UsbIpAttachMode
{
	// Direct when running as root, else Manual.
	Auto,

	// Runs usbip itself; fails with a clear message unless running as root.
	Direct,

	// Runs "sudo usbip ..." (sudo asks for the password on the terminal). For the spike only; the app never escalates.
	Sudo,

	// Prints the exact "sudo usbip ..." command and waits until the device shows up as imported.
	Manual
}

// What the attacher needs from the system (faked in tests).
public interface ILinuxHost
{
	bool IsRoot { get; }

	bool DirectoryExists(string path);

	// Full path of a program on PATH (or in /usr/sbin, /usr/bin, /sbin, /bin); null when there is none.
	string? FindExecutable(string name);
}

[SupportedOSPlatform("linux")]
public sealed class RealLinuxHost : ILinuxHost
{
	public static RealLinuxHost Instance { get; } = new();

	public bool IsRoot => LibC.GetEffectiveUserId() == 0;

	public bool DirectoryExists(string path) => Directory.Exists(path);

	public string? FindExecutable(string name)
	{
		IEnumerable<string> path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
		foreach (string dir in path.Concat(["/usr/sbin", "/usr/bin", "/sbin", "/bin"]))
		{
			string candidate = Path.Combine(dir, name);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}
		return null;
	}
}

// The usbip command lines and what their failures mean. Arguments follow the usbip(8) man page: the global option
// --tcp-port PORT, then "attach -r HOST -b BUSID", "detach -p PORT" and "port".
public static class UsbIpLinuxCommands
{
	public const string Loopback = "127.0.0.1";

	// Present while the vhci_hcd module (the USB/IP client's virtual host controller) is loaded.
	public static readonly string[] VhciDirectories = ["/sys/devices/platform/vhci_hcd.0", "/sys/bus/platform/drivers/vhci_hcd"];

	public static string AttachArguments(int tcpPort, string busId) =>
		string.Create(CultureInfo.InvariantCulture, $"--tcp-port {tcpPort} attach -r {Loopback} -b {busId}");

	public static string DetachArguments(int port) => string.Create(CultureInfo.InvariantCulture, $"detach -p {port}");

	public const string PortArguments = "port";

	public const string DetachUnknown =
		"usbip: the port of the virtual pad is unknown; to unplug it, find it in \"usbip port\" and run \"sudo usbip detach -p <port>\"";

	public static string SudoAttach(int tcpPort, string busId) => "sudo usbip " + AttachArguments(tcpPort, busId);

	public static string SudoDetach(int port) => "sudo usbip " + DetachArguments(port);

	public const string NotInstalled =
		"usbip not found: install your distribution's usbip tools (the package is usually called usbip or linux-tools)";

	public const string VhciMissing = "the vhci_hcd kernel module is not loaded: run \"sudo modprobe vhci-hcd\" and try again";

	public static string NeedsRoot(int tcpPort, string busId) =>
		"usbip attach needs root (it writes to the vhci_hcd driver). Run this in another terminal: " + SudoAttach(tcpPort, busId) +
		" (or start the spike with sudo, or with --attach manual)";

	// Our own import among the client's: on the loopback, from our server's TCP port, with our bus id. Null when absent.
	public static int? FindImport(IReadOnlyList<UsbIpImport> imports, int tcpPort, string busId) =>
		imports.FirstOrDefault(i => i.IsLoopback && i.BusId == busId && (i.TcpPort == null || i.TcpPort == tcpPort))?.Port;

	// A one-line reason for a failed attach.
	public static string Explain(CommandResult result, bool isRoot, int tcpPort, string busId)
	{
		string text = (result.Error + " " + result.Output).Trim();
		if (Contains(text, "Permission denied") || Contains(text, "Operation not permitted") || (!isRoot && result.ExitCode != 0 && text.Length == 0))
		{
			return NeedsRoot(tcpPort, busId);
		}
		if (Contains(text, "vhci") && (Contains(text, "open") || Contains(text, "not") || Contains(text, "No such")))
		{
			return VhciMissing;
		}
		if (Contains(text, "Connection refused") || Contains(text, "tcp connect"))
		{
			return string.Create(CultureInfo.InvariantCulture,
				$"usbip could not reach our USB/IP server on {Loopback}:{tcpPort} (exit {result.ExitCode}): {text}");
		}
		return string.Create(CultureInfo.InvariantCulture, $"usbip attach failed (exit {result.ExitCode}): {text}");
	}

	private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}

// Plugs our USB/IP server's virtual pad into Linux with the usbip command line client (vhci_hcd). Nothing here runs as
// root by itself: Direct needs the whole process to be root, Sudo asks sudo, Manual only prints the command.
public sealed class LinuxUsbIpAttacher : IUsbIpAttacher
{
	private readonly ICommandRunner runner;
	private readonly ILinuxHost host;
	private readonly Action<string> notice;
	private readonly object gate = new();
	private TaskCompletionSource? imported;

	public LinuxUsbIpAttacher(ICommandRunner runner, ILinuxHost host, UsbIpAttachMode mode = UsbIpAttachMode.Auto, Action<string>? notice = null)
	{
		this.runner = runner;
		this.host = host;
		this.notice = notice ?? (_ => { });
		Mode = mode;
	}

	public UsbIpAttachMode Mode { get; }

	// How long Manual waits for the user to run the command.
	public TimeSpan ManualTimeout { get; init; } = TimeSpan.FromMinutes(5);

	// How often Manual looks at "usbip port" (and Direct after attaching).
	public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

	// Direct and Sudo: how often "usbip port" is asked for the new port after a successful attach.
	public int ListRetries { get; init; } = 10;

	public UsbIpAttachMode EffectiveMode => Mode == UsbIpAttachMode.Auto ? (host.IsRoot ? UsbIpAttachMode.Direct : UsbIpAttachMode.Manual) : Mode;

	public bool VhciLoaded => UsbIpLinuxCommands.VhciDirectories.Any(host.DirectoryExists);

	// Our USB/IP server saw the client import the device (BridgeHost.PadServing -> UsbIpServer.Imported). Lets Manual go
	// on even when "usbip port" can't be read.
	public void NotifyImported()
	{
		lock (gate)
		{
			imported?.TrySetResult();
		}
	}

	public async Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel)
	{
		string exe = host.FindExecutable("usbip") ?? throw new FileNotFoundException(UsbIpLinuxCommands.NotInstalled, "usbip");
		if (!VhciLoaded)
		{
			throw new InvalidOperationException(UsbIpLinuxCommands.VhciMissing);
		}
		UsbIpAttachMode mode = EffectiveMode;
		if (mode == UsbIpAttachMode.Manual)
		{
			return await WaitForManualAttachAsync(exe, tcpPort, busId, cancel).ConfigureAwait(false);
		}
		if (mode == UsbIpAttachMode.Direct && !host.IsRoot)
		{
			throw new InvalidOperationException(UsbIpLinuxCommands.NeedsRoot(tcpPort, busId));
		}
		string arguments = UsbIpLinuxCommands.AttachArguments(tcpPort, busId);
		CommandResult result = mode == UsbIpAttachMode.Sudo
			? await runner.RunAsync("sudo", exe + " " + arguments, cancel).ConfigureAwait(false)
			: await runner.RunAsync(exe, arguments, cancel).ConfigureAwait(false);
		if (result.ExitCode != 0)
		{
			throw new InvalidOperationException(UsbIpLinuxCommands.Explain(result, host.IsRoot || mode == UsbIpAttachMode.Sudo, tcpPort, busId));
		}
		for (int attempt = 0; attempt < Math.Max(1, ListRetries); attempt++)
		{
			if (attempt > 0)
			{
				await Task.Delay(PollInterval, cancel).ConfigureAwait(false);
			}
			try
			{
				if (UsbIpLinuxCommands.FindImport(await ListWithAsync(exe, cancel).ConfigureAwait(false), tcpPort, busId) is int port)
				{
					return port;
				}
			}
			catch (InvalidOperationException)
			{
				// Listing failed; try again.
			}
		}
		throw new InvalidOperationException($"usbip attach reported success but bus id {busId} is not in \"usbip port\"");
	}

	private async Task<int> WaitForManualAttachAsync(string exe, int tcpPort, string busId, CancellationToken cancel)
	{
		TaskCompletionSource seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (gate)
		{
			imported = seen;
		}
		notice("usbip: run this in another terminal to plug in the virtual pad: " + UsbIpLinuxCommands.SudoAttach(tcpPort, busId));
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		timeout.CancelAfter(ManualTimeout);
		bool listingFailed = false;
		try
		{
			while (true)
			{
				try
				{
					if (UsbIpLinuxCommands.FindImport(await ListWithAsync(exe, timeout.Token).ConfigureAwait(false), tcpPort, busId) is int port)
					{
						return port;
					}
				}
				catch (InvalidOperationException e)
				{
					if (!listingFailed)
					{
						listingFailed = true;
						notice("usbip: \"usbip port\" failed (" + e.Message + "); waiting for the server to see the import instead");
					}
				}
				if (seen.Task.IsCompleted)
				{
					// Imported, but the port number is unknown: say how to find it; the bridge records nothing.
					notice(UsbIpLinuxCommands.DetachUnknown);
					return IUsbIpAttacher.UnknownPort;
				}
				await Task.WhenAny(seen.Task, Task.Delay(PollInterval, timeout.Token)).ConfigureAwait(false);
				timeout.Token.ThrowIfCancellationRequested();
			}
		}
		catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
		{
			throw new TimeoutException($"usbip: the virtual pad was not attached within {ManualTimeout.TotalSeconds:F0} s");
		}
		finally
		{
			lock (gate)
			{
				if (imported == seen)
				{
					imported = null;
				}
			}
		}
	}

	// A negative port (IUsbIpAttacher.UnknownPort) is never passed to usbip; the person is told how to find it instead.
	// Port 0 is a real port: Linux numbers the vhci_hcd ports from 0.
	public async Task DetachAsync(int port)
	{
		string exe = host.FindExecutable("usbip") ?? throw new FileNotFoundException(UsbIpLinuxCommands.NotInstalled, "usbip");
		if (port < 0)
		{
			notice(UsbIpLinuxCommands.DetachUnknown);
			return;
		}
		switch (EffectiveMode)
		{
			case UsbIpAttachMode.Manual:
				notice("usbip: unplug the virtual pad with: " + UsbIpLinuxCommands.SudoDetach(port));
				return;
			case UsbIpAttachMode.Sudo:
				await runner.RunAsync("sudo", exe + " " + UsbIpLinuxCommands.DetachArguments(port)).ConfigureAwait(false);
				return;
			default:
				await runner.RunAsync(exe, UsbIpLinuxCommands.DetachArguments(port)).ConfigureAwait(false);
				return;
		}
	}

	public async Task<IReadOnlyList<UsbIpImport>> ListAsync(CancellationToken cancel)
	{
		string exe = host.FindExecutable("usbip") ?? throw new FileNotFoundException(UsbIpLinuxCommands.NotInstalled, "usbip");
		return await ListWithAsync(exe, cancel).ConfigureAwait(false);
	}

	// "usbip port" needs no root: it reads the driver's status files.
	private async Task<IReadOnlyList<UsbIpImport>> ListWithAsync(string exe, CancellationToken cancel)
	{
		CommandResult result = await runner.RunAsync(exe, UsbIpLinuxCommands.PortArguments, cancel).ConfigureAwait(false);
		if (result.ExitCode != 0)
		{
			throw new InvalidOperationException($"usbip port failed (exit {result.ExitCode}): {result.Error.Trim()} {result.Output.Trim()}");
		}
		return UsbIpPortList.Parse(result.Output);
	}
}
