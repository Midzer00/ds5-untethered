// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Diagnostics;
using Microsoft.Win32;

namespace Kopru.Core.Updates;

// Whether this copy was installed by the installer (it can then update itself in place) or unzipped somewhere (it gets
// the download page instead).
public interface IInstallKind
{
	bool IsInstalled { get; }
}

// Installed: the installer's uninstall entry (HKCU, per-user install) points at the folder this exe runs from, and the
// uninstaller sits next to it. A zip copy, or a copy of the install folder elsewhere, fails one of the two.
public sealed class InnoInstallKind(string exeFolder, Func<string?> readInstallLocation, Func<string, bool> fileExists) : IInstallKind
{
	// tools/installer/DS5Untethered.iss AppId; Inno Setup names the uninstall key "<AppId>_is1".
	public const string AppId = "{4B19CEA3-5728-4B63-AA22-BC72FDA10B4B}";
	public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";
	public const string Uninstaller = "unins000.exe";

	public bool IsInstalled
	{
		get
		{
			string? location = SafeRead();
			return !string.IsNullOrWhiteSpace(location) && SameFolder(location, exeFolder)
				&& fileExists(Path.Combine(exeFolder, Uninstaller));
		}
	}

	public static IInstallKind CreateDefault()
	{
#if DEBUG
		if (Environment.GetEnvironmentVariable(UpdateEndpoints.InstalledOverrideVariable) == "1")
		{
			return new Fixed(true);
		}
#endif
		string folder = AppContext.BaseDirectory;
		return new InnoInstallKind(folder, ReadRegistry, File.Exists);
	}

	public static bool SameFolder(string a, string b)
	{
		try
		{
			return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private string? SafeRead()
	{
		try
		{
			return readInstallLocation();
		}
		catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
		{
			return null;
		}
	}

	private static string? ReadRegistry()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey);
		return key?.GetValue("InstallLocation") as string;
	}

	public sealed class Fixed(bool installed) : IInstallKind
	{
		public bool IsInstalled { get; } = installed;
	}
}

// Starts a program (tests record the call instead).
public interface IProcessLauncher
{
	void Start(string path, IReadOnlyList<string> arguments);
}

public sealed class ShellProcessLauncher : IProcessLauncher
{
	public void Start(string path, IReadOnlyList<string> arguments)
	{
		var info = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty };
		foreach (string a in arguments)
		{
			info.ArgumentList.Add(a);
		}
		using Process? _ = Process.Start(info);
	}
}

public enum InstallOutcome
{
	// The installer runs and the app was told to exit.
	Started,
	// The file no longer matched the manifest and was deleted.
	Rejected,
	// Windows refused to start it.
	LaunchFailed
}

// Hands over to the installer: the downloaded file is checked once more, the virtual controller is stopped the way
// exit stops it, the single-instance lock is handed to the installer's wait (see SingleInstance.HandOverToUpdate), the
// installer starts silently and the app exits. The installer waits until this process is gone before it replaces files
// and starts the new version when it is done (tools/installer/DS5Untethered.iss, /UPDATE=1).
public sealed class UpdateInstaller(UpdateDownloader downloader, IProcessLauncher launcher, Func<Task> stopBridge, Func<Action?> handOver, Action exit)
{
	// Inno Setup command line (https://jrsoftware.org/ishelp/topic_setupcmdline.htm): no wizard, no message boxes, no
	// reboot, no "This will install..." prompt, no cancel button; /LANG picks the language (and skips the language
	// dialog); /UPDATE=1 is this app's own switch, read by the script with {param:UPDATE}; /LOG keeps a log next to it.
	public static IReadOnlyList<string> Arguments(string language, string logPath) =>
		["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/NOCANCEL", "/UPDATE=1", "/LANG=" + (language == "tr" ? "tr" : "en"), "/LOG=" + logPath];

	public async Task<InstallOutcome> InstallAsync(string installer, UpdateAsset asset, string language)
	{
		if (!await downloader.VerifyAsync(installer, asset).ConfigureAwait(false))
		{
			return InstallOutcome.Rejected;
		}
		await stopBridge().ConfigureAwait(false);
		Action? takeBack = handOver();
		try
		{
			launcher.Start(installer, Arguments(language, Path.Combine(downloader.Folder, "install.log")));
		}
		catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
		{
			AppLog.Write($"update: installer did not start ({e.GetType().Name})");
			takeBack?.Invoke();
			return InstallOutcome.LaunchFailed;
		}
		AppLog.Write("update: starting installer, app exits");
		exit();
		return InstallOutcome.Started;
	}
}
