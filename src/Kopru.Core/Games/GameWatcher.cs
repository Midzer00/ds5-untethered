// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kopru.Core.Games;

// The app in the foreground: its exe file name and, when it is a known game, the catalog entry.
public sealed record ForegroundGame(string Exe, CatalogGame? Match)
{
	// The catalog name, or the exe name without ".exe".
	public string Name => Match?.Name ?? DisplayName(Exe);

	public static string DisplayName(string exe) =>
		exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
}

// Reports which app is in the foreground; GameModeController polls it about once a second.
public interface IGameWatcher
{
	// Null when nothing usable is in the foreground (the desktop, the taskbar, this app...).
	ForegroundGame? Poll();
}

// Watches the foreground window: GetForegroundWindow, GetWindowThreadProcessId, then the process's exe name. Ignores
// this app and the Windows shell.
public sealed class GameWatcher(GameCatalog catalog, Func<string?>? foregroundExe = null) : IGameWatcher
{
	// Shell and system processes that are never games.
	public static readonly IReadOnlySet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
		"LockApp.exe", "TextInputHost.exe", "dwm.exe", "ApplicationFrameHost.exe", "SystemSettings.exe", "Taskmgr.exe",
		"ShellHost.exe", "WidgetBoard.exe", "Widgets.exe"
	};

	private readonly Func<string?> foregroundExe = foregroundExe ?? ForegroundExe;

	private readonly string ownExe = GameCatalog.Normalize(Environment.ProcessPath);

	public GameCatalog Catalog { get; } = catalog;

	public ForegroundGame? Poll()
	{
		string exe;
		try
		{
			exe = GameCatalog.Normalize(foregroundExe());
		}
		catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
		{
			return null;
		}
		if (exe.Length == 0 || Ignored.Contains(exe) || string.Equals(exe, ownExe, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return new ForegroundGame(exe, Catalog.Find(exe));
	}

	// The exe file name of the foreground window's process, or null.
	public static string? ForegroundExe()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		IntPtr window = GetForegroundWindow();
		if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0
			|| pid == (uint)Environment.ProcessId)
		{
			return null;
		}
		using Process process = Process.GetProcessById((int)pid);
		// MainModule needs access rights the process may not grant; the process name is always readable.
		return process.ProcessName + ".exe";
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
