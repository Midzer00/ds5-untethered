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
	Manual,

	// Runs "pkexec usbip ..." (the desktop app): polkit asks for the password in a graphical dialog, or not at all with
	// the rule in docs/linux/README.md. Runs usbip directly when the process is root already.
	Pkexec
}

// What the attacher needs from the system (faked in tests).
public interface ILinuxHost
{
	bool IsRoot { get; }

	bool DirectoryExists(string path);

	// Full path of a system program in /usr/sbin, /usr/bin, /sbin or /bin; null when there is none. Never the user's PATH:
	// usbip runs as root through pkexec, so a program a user could place earlier on PATH must never be picked.
	string? FindExecutable(string name);
}

[SupportedOSPlatform("linux")]
public sealed class RealLinuxHost : ILinuxHost
{
	public static RealLinuxHost Instance { get; } = new();

	public bool IsRoot => LibC.GetEffectiveUserId() == 0;

	public bool DirectoryExists(string path) => Directory.Exists(path);

	public static readonly string[] SystemDirectories = ["/usr/sbin", "/usr/bin", "/sbin", "/bin"];

	public string? FindExecutable(string name)
	{
		if (name.Contains('/'))
		{
			return null;
		}
		foreach (string dir in SystemDirectories)
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

	public static string AttachArguments(int tcpPort, string busId) => string.Join(' ', AttachArgumentList(tcpPort, busId));

	public static string DetachArguments(int port) => string.Join(' ', DetachArgumentList(port));

	// The same as separate arguments (what is actually run; nothing is split or interpreted).
	public static string[] AttachArgumentList(int tcpPort, string busId) =>
		["--tcp-port", tcpPort.ToString(CultureInfo.InvariantCulture), "attach", "-r", Loopback, "-b", busId];

	public static string[] DetachArgumentList(int port) => ["detach", "-p", port.ToString(CultureInfo.InvariantCulture)];

	public const string PortArguments = "port";

	// A USB bus id as usbip names it ("1-1", "2-1.4"); anything else is never passed on.
	public static bool IsValidBusId(string? busId) =>
		busId != null && System.Text.RegularExpressions.Regex.IsMatch(busId, @"^\d+-\d+(\.\d+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	public const string DetachUnknown =
		"usbip: the port of the virtual pad is unknown; to unplug it, find it in \"usbip port\" and run \"sudo usbip detach -p <port>\"";

	public static string SudoAttach(int tcpPort, string busId) => "sudo usbip " + AttachArguments(tcpPort, busId);

	public static string SudoDetach(int port) => "sudo usbip " + DetachArguments(port);

	// pkexec's own exit codes (pkexec(1)): the dialog was dismissed, or the user is not allowed.
	public const int PkexecDismissed = 126;
	public const int PkexecNotAuthorized = 127;

	public const string PkexecMissing =
		"pkexec not found: install polkit (the package is usually called polkit or policykit-1) to plug in the virtual pad from the app";

	public const string PkexecCancelled = "usbip attach was not authorized (the password dialog was closed or polkit refused it)";

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
	public static string Explain(CommandResult result, bool isRoot, int tcpPort, string busId, bool pkexec = false)
	{
		string text = (result.Error + " " + result.Output).Trim();
		if (pkexec && result.ExitCode is PkexecDismissed or PkexecNotAuthorized)
		{
			return PkexecCancelled;
		}
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
// root by itself: Direct needs the whole process to be root, Sudo asks sudo, Pkexec asks polkit (the desktop app), Manual
// only prints the command. Pkexec never asks again to unplug: closing our server's connection makes vhci_hcd release the
// port, and only a port that is still ours after DetachSettle is detached through pkexec (to be confirmed on hardware).
public sealed class LinuxUsbIpAttacher : IUsbIpAttacher
{
	private readonly ICommandRunner runner;
	private readonly ILinuxHost host;
	private readonly Action<string> notice;
	private readonly object gate = new();
	private readonly Dictionary<int, int> serverPorts = [];
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

	// Pkexec: how long after an unplug the port is looked at again (our server has closed the connection by then).
	public TimeSpan DetachSettle { get; init; } = TimeSpan.FromSeconds(5);

	// The longest a pkexec detach (the fallback) may wait for its password dialog.
	public TimeSpan DetachTimeout { get; init; } = TimeSpan.FromMinutes(2);

	// The fallback check of the last unplug (tests wait for it).
	public Task LastDetachCheck { get; private set; } = Task.CompletedTask;

	public UsbIpAttachMode EffectiveMode => Mode switch
	{
		UsbIpAttachMode.Auto => host.IsRoot ? UsbIpAttachMode.Direct : UsbIpAttachMode.Manual,
		UsbIpAttachMode.Pkexec when host.IsRoot => UsbIpAttachMode.Direct,
		_ => Mode
	};

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

	private string UsbIp() => host.FindExecutable("usbip") ?? throw new FileNotFoundException(UsbIpLinuxCommands.NotInstalled, "usbip");

	public async Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel)
	{
		if (!UsbIpLinuxCommands.IsValidBusId(busId) || tcpPort is <= 0 or > 65535)
		{
			throw new ArgumentException($"usbip: refusing bus id {busId} / TCP port {tcpPort}");
		}
		string exe = UsbIp();
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
		CommandResult result = await RunElevatedAsync(mode, exe, UsbIpLinuxCommands.AttachArgumentList(tcpPort, busId), cancel).ConfigureAwait(false);
		if (mode == UsbIpAttachMode.Pkexec && result.ExitCode is UsbIpLinuxCommands.PkexecDismissed or UsbIpLinuxCommands.PkexecNotAuthorized)
		{
			throw new UnauthorizedAccessException(UsbIpLinuxCommands.PkexecCancelled);
		}
		if (result.ExitCode != 0)
		{
			throw new InvalidOperationException(UsbIpLinuxCommands.Explain(result, host.IsRoot || mode is UsbIpAttachMode.Sudo or UsbIpAttachMode.Pkexec,
				tcpPort, busId, pkexec: mode == UsbIpAttachMode.Pkexec));
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
					lock (gate)
					{
						serverPorts[port] = tcpPort;
					}
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
		string exe = UsbIp();
		if (port < 0)
		{
			notice(UsbIpLinuxCommands.DetachUnknown);
			return;
		}
		int? serverPort;
		lock (gate)
		{
			serverPort = serverPorts.Remove(port, out int tcp) ? tcp : null;
		}
		switch (EffectiveMode)
		{
			case UsbIpAttachMode.Manual:
				notice("usbip: unplug the virtual pad with: " + UsbIpLinuxCommands.SudoDetach(port));
				return;
			case UsbIpAttachMode.Pkexec:
				// No password prompt: the bridge closes our server's connection right after this, and vhci_hcd then lets the
				// port go. Only if it is still ours a little later is it detached through pkexec.
				LastDetachCheck = Task.Run(() => DetachIfStillAttachedAsync(exe, port, serverPort));
				return;
			default:
				CommandResult result = await RunElevatedAsync(EffectiveMode, exe, UsbIpLinuxCommands.DetachArgumentList(port), CancellationToken.None)
					.ConfigureAwait(false);
				if (result.ExitCode != 0)
				{
					notice("usbip: detach failed (exit " + result.ExitCode + "); unplug the virtual pad with: " + UsbIpLinuxCommands.SudoDetach(port));
				}
				return;
		}
	}

	// The fallback: the port still holds the import from our (now closed) server's TCP port. Never touches a port another
	// import took over meanwhile (a new virtual pad gets a new server port), nor one whose server port is unknown.
	private async Task DetachIfStillAttachedAsync(string exe, int port, int? serverPort)
	{
		try
		{
			await Task.Delay(DetachSettle).ConfigureAwait(false);
			IReadOnlyList<UsbIpImport> imports = await ListWithAsync(exe, CancellationToken.None).ConfigureAwait(false);
			bool stillOurs = serverPort is int tcp && imports.Any(i => i.Port == port && i.IsLoopback && i.TcpPort == tcp);
			if (!stillOurs)
			{
				if (serverPort == null && imports.Any(i => i.Port == port && i.IsLoopback))
				{
					notice("usbip: port " + port + " may still be in use; unplug it with: " + UsbIpLinuxCommands.SudoDetach(port));
				}
				return;
			}
			notice("usbip: port " + port + " was not released when our server closed; detaching it through pkexec");
			using CancellationTokenSource timeout = new(DetachTimeout);
			CommandResult result = await RunElevatedAsync(UsbIpAttachMode.Pkexec, exe, UsbIpLinuxCommands.DetachArgumentList(port), timeout.Token)
				.ConfigureAwait(false);
			if (result.ExitCode != 0)
			{
				notice("usbip: detach failed (exit " + result.ExitCode + "); unplug the virtual pad with: " + UsbIpLinuxCommands.SudoDetach(port));
			}
		}
		catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or IOException or System.ComponentModel.Win32Exception)
		{
			notice("usbip: could not check port " + port + " after unplugging (" + e.Message + ")");
		}
	}

	// Sudo: "sudo usbip ...". Pkexec: "pkexec usbip ..." (the full path, as pkexec wants). Direct: usbip itself. Every
	// argument is passed on its own; cancelling ends the program (and its password dialog).
	private Task<CommandResult> RunElevatedAsync(UsbIpAttachMode mode, string exe, IReadOnlyList<string> arguments, CancellationToken cancel)
	{
		switch (mode)
		{
			case UsbIpAttachMode.Sudo:
				return runner.RunAsync("sudo", [exe, .. arguments], cancel);
			case UsbIpAttachMode.Pkexec:
				string pkexec = host.FindExecutable("pkexec") ?? throw new FileNotFoundException(UsbIpLinuxCommands.PkexecMissing, "pkexec");
				return runner.RunAsync(pkexec, [exe, .. arguments], cancel);
			default:
				return runner.RunAsync(exe, arguments, cancel);
		}
	}

	public async Task<IReadOnlyList<UsbIpImport>> ListAsync(CancellationToken cancel)
	{
		return await ListWithAsync(UsbIp(), cancel).ConfigureAwait(false);
	}

	// "usbip port" needs no root: it reads the driver's status files.
	private async Task<IReadOnlyList<UsbIpImport>> ListWithAsync(string exe, CancellationToken cancel)
	{
		CommandResult result = await runner.RunAsync(exe, [UsbIpLinuxCommands.PortArguments], cancel).ConfigureAwait(false);
		if (result.ExitCode != 0)
		{
			throw new InvalidOperationException($"usbip port failed (exit {result.ExitCode}): {result.Error.Trim()} {result.Output.Trim()}");
		}
		return UsbIpPortList.Parse(result.Output);
	}
}
