// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Kopru.Core.Hid;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// Where the virtual pad's identity (descriptors, feature reports) comes from.
public interface IIdentitySource
{
	// Null when there is none yet; throws when there is one but it can't be read.
	DeviceIdentity? Load();
}

// Finds and opens the Bluetooth DualSense.
public interface IControllerSource
{
	// Null while no Bluetooth DualSense can be opened.
	IHidLink? TryOpen();

	// Every Bluetooth DualSense present now, in a stable order (see BridgeManager).
	IReadOnlyList<ControllerPresence> Enumerate() => [];

	// Opens the controller with that id; null while it can't be opened.
	IHidLink? TryOpen(string id) => null;
}

// Plugs the USB/IP device served on the loopback into Windows and unplugs it.
public interface IUsbIpAttacher
{
	// Returns the virtual hub port. Throws FileNotFoundException when the USB/IP client is not installed.
	Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel);

	Task DetachAsync(int port);

	// The devices the client has imported (all of them, not only ours). Throws when the listing failed.
	Task<IReadOnlyList<UsbIp.UsbIpImport>> ListAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<UsbIp.UsbIpImport>>([]);
}

// The newest captures/*.json (made by "Kopru.Probe capture"), looked up in the app's data folder, the working directory,
// then next to the app.
public sealed class CaptureIdentitySource : IIdentitySource
{
	private readonly string[] directories;

	public CaptureIdentitySource(params string[] directories)
	{
		this.directories = directories.Length > 0 ? directories :
			[AppPaths.Captures, Path.Combine(Environment.CurrentDirectory, "captures"), Path.Combine(AppContext.BaseDirectory, "captures")];
	}

	public static string? NewestCapture(string directory) =>
		Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault() : null;

	public DeviceIdentity? Load()
	{
		foreach (string directory in directories)
		{
			string? file = NewestCapture(directory);
			if (file != null)
			{
				return DeviceIdentity.FromCaptureFile(file);
			}
		}
		return null;
	}
}

public sealed class WindowsControllerSource : IControllerSource, IReadOnlyControllerSource
{
	public IHidLink? TryOpenReadOnly()
	{
		try
		{
			HidDevice? bt = WindowsHid.FindDualSenses().FirstOrDefault(d => d.Transport == Transport.Bluetooth);
			return bt == null ? null : WindowsHid.OpenReadOnly(bt);
		}
		catch (Exception)
		{
			return null;
		}
	}

	// The device instance id (stable while paired), or the interface path when it can't be read.
	public static string IdOf(HidDevice device) => DeviceInstance.FromInterfacePath(device.Path) ?? device.Path;

	public IReadOnlyList<ControllerPresence> Enumerate()
	{
		try
		{
			return [.. WindowsHid.FindDualSenses().Where(d => d.Transport == Transport.Bluetooth).Select(d => new ControllerPresence(IdOf(d), d))];
		}
		catch (Exception)
		{
			return [];
		}
	}

	public IHidLink? TryOpen(string id)
	{
		try
		{
			HidDevice? bt = WindowsHid.FindDualSenses().FirstOrDefault(d => d.Transport == Transport.Bluetooth && IdOf(d) == id);
			return bt == null ? null : WindowsHid.Open(bt);
		}
		catch (Exception)
		{
			return null;
		}
	}

	public IHidLink? TryOpen()
	{
		try
		{
			HidDevice? bt = WindowsHid.FindDualSenses().FirstOrDefault(d => d.Transport == Transport.Bluetooth);
			return bt == null ? null : WindowsHid.Open(bt);
		}
		catch (Exception)
		{
			// Hidden (HidHide), in use or vanished between listing and opening: try again on the next poll.
			return null;
		}
	}
}

// usbip-win2's command line client.
public sealed class UsbIpExeAttacher : IUsbIpAttacher
{
	public const string DefaultPath = @"C:\Program Files\USBip\usbip.exe";

	private readonly string? path;

	public UsbIpExeAttacher(string? path = null)
	{
		this.path = path;
	}

	public static string? Locate()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		try
		{
			return Locate(new Setup.WindowsInstallRegistry(), Environment.GetEnvironmentVariable("PATH"));
		}
		catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
		{
			return null;
		}
	}

	// The same place the setup check looks first (SystemCheck.CheckUsbIp: the uninstall entry's install folder), then the
	// default install folder, then PATH.
	public static string? Locate(Setup.IInstallRegistry registry, string? pathVariable)
	{
		if (registry.FindProgram(Setup.SystemCheck.UsbIpProgramPrefix) is { } program && program.Location is { Length: > 0 } folder)
		{
			try
			{
				string installed = Path.Combine(folder, Setup.SystemCheck.UsbIpExe);
				if (registry.FileExists(installed))
				{
					return installed;
				}
			}
			catch (ArgumentException)
			{
				// A malformed install location.
			}
		}
		if (registry.FileExists(DefaultPath))
		{
			return DefaultPath;
		}
		foreach (string dir in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			try
			{
				string candidate = Path.Combine(dir.Trim(), Setup.SystemCheck.UsbIpExe);
				if (registry.FileExists(candidate))
				{
					return candidate;
				}
			}
			catch (ArgumentException)
			{
				// A malformed PATH entry.
			}
		}
		return null;
	}

	// "--terse" prints just the port number.
	public static int? ParsePort(string output)
	{
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port > 0)
			{
				return port;
			}
		}
		return null;
	}

	public async Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel)
	{
		string exe = Executable();
		(int exit, string output, string error) = await RunAsync(exe,
			string.Create(CultureInfo.InvariantCulture, $"-t {tcpPort} attach -r 127.0.0.1 -b {busId} --once --terse"), cancel);
		int? port = ParsePort(output);
		if (exit != 0 || port == null)
		{
			throw new InvalidOperationException($"usbip attach failed (exit {exit}): {error.Trim()} {output.Trim()}");
		}
		return port.Value;
	}

	public async Task DetachAsync(int port)
	{
		await RunAsync(Executable(), string.Create(CultureInfo.InvariantCulture, $"detach -p {port}"), CancellationToken.None);
	}

	public async Task<IReadOnlyList<UsbIp.UsbIpImport>> ListAsync(CancellationToken cancel)
	{
		(int exit, string output, string error) = await RunAsync(Executable(), "port", cancel);
		if (exit != 0)
		{
			throw new InvalidOperationException($"usbip port failed (exit {exit}): {error.Trim()} {output.Trim()}");
		}
		return UsbIp.UsbIpPortList.Parse(output);
	}

	private string Executable() => path ?? Locate() ?? throw new FileNotFoundException("usbip.exe not found", DefaultPath);

	private static async Task<(int Exit, string Output, string Error)> RunAsync(string exe, string arguments, CancellationToken cancel)
	{
		using Process process = Process.Start(new ProcessStartInfo(exe, arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		})!;
		Task<string> output = process.StandardOutput.ReadToEndAsync(cancel);
		Task<string> error = process.StandardError.ReadToEndAsync(cancel);
		await process.WaitForExitAsync(cancel);
		return (process.ExitCode, await output, await error);
	}
}
