// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kopru.App.Services;

// Asks Windows to restart the computer.
public interface ISystemRestart
{
	// Restart after `delay`. Throws (Win32Exception) when Windows refuses; nothing is changed then.
	void Request(TimeSpan delay);
}

// InitiateShutdownW (advapi32) with SHUTDOWN_RESTART and a grace period: the documented Win32 call. It needs the
// SeShutdownPrivilege, which every interactive user holds (disabled by default; enabling a privilege the token already
// has needs no elevation). No SHUTDOWN_FORCE_OTHERS: other programs get the normal end-session messages, and one with
// unsaved work can still ask the user.
public sealed class WindowsSystemRestart : ISystemRestart
{
	private const uint ShutdownRestart = 0x00000004;
	// SHTDN_REASON_FLAG_PLANNED | SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_INSTALLATION
	private const uint ReasonPlannedAppInstall = 0x80000000 | 0x00040000 | 0x00000002;
	private const uint TokenAdjustPrivileges = 0x0020, TokenQuery = 0x0008, SePrivilegeEnabled = 0x00000002;

	public void Request(TimeSpan delay)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException();
		}
		EnableShutdownPrivilege();
		uint seconds = (uint)Math.Clamp(Math.Round(delay.TotalSeconds), 0, 600);
		uint result = InitiateShutdown(null, null, seconds, ShutdownRestart, ReasonPlannedAppInstall);
		if (result != 0)
		{
			throw new Win32Exception((int)result);
		}
	}

	private static void EnableShutdownPrivilege()
	{
		if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		try
		{
			if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out long luid))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
			if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
			{
				// ERROR_NOT_ALL_ASSIGNED (1300): the account does not hold the privilege.
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
		}
		finally
		{
			CloseHandle(token);
		}
	}

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	private struct TokenPrivileges
	{
		public uint Count;
		public long Luid;
		public uint Attributes;
	}

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "InitiateShutdownW")]
	private static extern uint InitiateShutdown(string? machineName, string? message, uint gracePeriod, uint flags, uint reason);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state,
		uint bufferLength, IntPtr previous, IntPtr returnLength);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}

// The values of HKCU\...\RunOnce (tests use a dictionary).
public interface IRunOnceStore
{
	string? Get(string name);
	void Set(string name, string value);
	void Remove(string name);
}

public sealed class RegistryRunOnceStore : IRunOnceStore
{
	public const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

	public string? Get(string name)
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunOnceKey);
		return key?.GetValue(name) as string;
	}

	public void Set(string name, string value)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
		key.SetValue(name, value);
	}

	public void Remove(string name)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
		key?.DeleteValue(name, throwOnMissingValue: false);
	}
}

// Starts the app once at the next sign-in (after the restart), without --minimized so its window (and the setup page,
// while setup is not completed) shows. One value under our own name: registering again replaces it.
public sealed class RunOnceRegistration(IRunOnceStore store, Func<string?> exePath)
{
	public const string ValueName = "Untethered";

	public static string CommandLine(string exe) => "\"" + exe + "\"";

	// False when the exe path is unknown.
	public bool Register()
	{
		if (exePath() is not { Length: > 0 } exe)
		{
			return false;
		}
		store.Set(ValueName, CommandLine(exe));
		return true;
	}

	public void Unregister() => store.Remove(ValueName);
}
