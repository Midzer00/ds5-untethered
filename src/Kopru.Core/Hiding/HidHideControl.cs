// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Hid;
using Microsoft.Win32;

namespace Kopru.Core.Hiding;

public sealed record CommandResult(int ExitCode, string Output, string Error);

// Runs an external program; faked in tests.
public interface ICommandRunner
{
	Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
	public async Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
	{
		var info = new ProcessStartInfo(fileName, arguments)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		using Process process = Process.Start(info) ?? throw new InvalidOperationException(fileName);
		Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
		Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
	}
}

public sealed class HidHideException(string message) : Exception(message);

public sealed record HidHideState(bool CloakOn, IReadOnlyList<string> HiddenDevices, IReadOnlyList<string> AllowedApps);

// Drives HidHideCLI.exe. Its list commands print lines in its own command syntax, e.g.
//   --cloak-state -> "--cloak-on" / "--cloak-off"
//   --dev-list    -> --dev-hide "HID\{00001124-...}\..."   (one per hidden device)
//   --app-list    -> --app-reg "C:\...\App.exe"             (one per allowed app)
public sealed class HidHideControl(ICommandRunner runner, string? cliPath)
{
	public const string DefaultCliPath = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
	private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

	public string? CliPath { get; } = cliPath;

	public bool IsInstalled => CliPath is not null;

	// Uses the HidHide install location from the uninstall registry entry, then the default path.
	public static HidHideControl CreateDefault(ICommandRunner? runner = null) => new(runner ?? new ProcessCommandRunner(), LocateCli());

	public static string? LocateCli()
	{
		if (OperatingSystem.IsWindows())
		{
			try
			{
				foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
				{
					using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
					using RegistryKey? uninstall = root.OpenSubKey(UninstallKey);
					if (uninstall is null)
					{
						continue;
					}
					foreach (string name in uninstall.GetSubKeyNames())
					{
						using RegistryKey? entry = uninstall.OpenSubKey(name);
						if (entry?.GetValue("DisplayName") is string display && display.Equals("HidHide", StringComparison.OrdinalIgnoreCase) &&
							entry.GetValue("InstallLocation") is string location)
						{
							string candidate = Path.Combine(location, Environment.Is64BitOperatingSystem ? "x64" : "x86", "HidHideCLI.exe");
							if (File.Exists(candidate))
							{
								return candidate;
							}
						}
					}
				}
			}
			catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
			{
				// Fall through to the default path.
			}
		}
		return File.Exists(DefaultCliPath) ? DefaultCliPath : null;
	}

	// Quotes one argument for the command line. Windows paths cannot contain '"'.
	public static string Quote(string value)
	{
		if (value.Contains('"'))
		{
			throw new ArgumentException(value, nameof(value));
		}
		return "\"" + value + "\"";
	}

	public async Task<HidHideState> GetStateAsync(CancellationToken cancellationToken = default)
	{
		string cloak = await RunAsync("--cloak-state", cancellationToken).ConfigureAwait(false);
		string devices = await RunAsync("--dev-list", cancellationToken).ConfigureAwait(false);
		string apps = await RunAsync("--app-list", cancellationToken).ConfigureAwait(false);
		return new HidHideState(
			cloak.Contains("--cloak-on", StringComparison.OrdinalIgnoreCase),
			ParseList(devices, "--dev-hide"),
			ParseList(apps, "--app-reg"));
	}

	public static IReadOnlyList<string> ParseList(string output, string command)
	{
		var items = new List<string>();
		foreach (string raw in output.Split('\n'))
		{
			string line = raw.Trim();
			if (!line.StartsWith(command, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string value = line[command.Length..].Trim().Trim('"');
			if (value.Length > 0)
			{
				items.Add(value);
			}
		}
		return items;
	}

	// Refuses anything that is not a Bluetooth HID collection, so the virtual (USB) pad is never hidden.
	public Task HideAsync(string instanceId, CancellationToken cancellationToken = default)
	{
		if (!DeviceInstance.IsBluetoothHid(instanceId) || DeviceInstance.IsUsbDevice(instanceId))
		{
			throw new ArgumentException(instanceId, nameof(instanceId));
		}
		return RunAsync("--dev-hide " + Quote(instanceId), cancellationToken);
	}

	public Task UnhideAsync(string instanceId, CancellationToken cancellationToken = default) =>
		RunAsync("--dev-unhide " + Quote(instanceId), cancellationToken);

	public Task AllowAppAsync(string exePath, CancellationToken cancellationToken = default) =>
		RunAsync("--app-reg " + Quote(exePath), cancellationToken);

	public Task DisallowAppAsync(string exePath, CancellationToken cancellationToken = default) =>
		RunAsync("--app-unreg " + Quote(exePath), cancellationToken);

	public Task CloakOnAsync(CancellationToken cancellationToken = default) => RunAsync("--cloak-on", cancellationToken);

	public Task CloakOffAsync(CancellationToken cancellationToken = default) => RunAsync("--cloak-off", cancellationToken);

	private async Task<string> RunAsync(string arguments, CancellationToken cancellationToken)
	{
		if (CliPath is null)
		{
			throw new HidHideException(Localization.Text.HidHideNotInstalled());
		}
		CommandResult result;
		try
		{
			result = await runner.RunAsync(CliPath, arguments, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
		{
			throw new HidHideException(Localization.Text.HidHideCommandFailed(arguments, e.Message));
		}
		if (result.ExitCode != 0)
		{
			string detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
			throw new HidHideException(Localization.Text.HidHideCommandFailed(arguments, detail));
		}
		return result.Output;
	}
}
