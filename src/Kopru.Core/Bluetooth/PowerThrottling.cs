// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kopru.Core.Bluetooth;

// Sets the current process's power throttling state (SetProcessInformation with ProcessPowerThrottling).
public interface IProcessPowerThrottling
{
	// 0 when the state was set, else the Win32 error (never 0 on failure).
	int Set(uint controlMask, uint stateMask);
}

// PROCESS_POWER_THROTTLING_STATE: three ULONGs (12 bytes).
[StructLayout(LayoutKind.Sequential)]
public struct ProcessPowerThrottlingState
{
	public uint Version;

	public uint ControlMask;

	public uint StateMask;
}

// The app sits hidden while a game runs, and Windows may then treat it as background work: EcoQoS (efficiency cores, low
// clocks) and, for a minimized process, timer resolution requests ignored (Sleep(1) takes 15.6 ms). Both would make the
// report pacing late. Opting out (control bit set, state bit clear) keeps the process at full speed and its timer
// resolution requests honoured, visible or not.
public static class PowerThrottling
{
	// PROCESS_INFORMATION_CLASS.ProcessPowerThrottling.
	public const int ProcessPowerThrottlingClass = 4;

	// PROCESS_POWER_THROTTLING_CURRENT_VERSION.
	public const uint CurrentVersion = 1;

	// PROCESS_POWER_THROTTLING_EXECUTION_SPEED: EcoQoS.
	public const uint ExecutionSpeed = 0x1;

	// PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION (Windows 11; older Windows 10 builds reject the call with it).
	public const uint IgnoreTimerResolution = 0x4;

	// Never throttled: EcoQoS off and timer resolution kept; when Windows rejects the timer bit, EcoQoS off alone. Returns
	// the log line. Never throws.
	public static string OptOut(IProcessPowerThrottling? api = null)
	{
		try
		{
			if (api == null)
			{
				if (!OperatingSystem.IsWindows())
				{
					return "power: throttling opt-out skipped (not Windows)";
				}
				api = WindowsProcessPowerThrottling.Instance;
			}
			int error = api.Set(ExecutionSpeed | IgnoreTimerResolution, 0);
			if (error == 0)
			{
				return "power: throttling opt-out ok (execution speed, timer resolution)";
			}
			int retry = api.Set(ExecutionSpeed, 0);
			return retry == 0
				? $"power: throttling opt-out ok (execution speed; timer resolution rejected, error {error})"
				: $"power: throttling opt-out failed (error {retry})";
		}
		catch (Exception e)
		{
			return $"power: throttling opt-out failed ({e.GetType().Name})";
		}
	}

	// At startup: opts out and logs the result in one line.
	public static void OptOutAndLog(IProcessPowerThrottling? api = null) => Diagnostics.AppLog.Write(OptOut(api));
}

internal sealed class WindowsProcessPowerThrottling : IProcessPowerThrottling
{
	public static readonly WindowsProcessPowerThrottling Instance = new();

	public int Set(uint controlMask, uint stateMask)
	{
		ProcessPowerThrottlingState state = new()
		{
			Version = PowerThrottling.CurrentVersion,
			ControlMask = controlMask,
			StateMask = stateMask,
		};
		if (SetProcessInformation(GetCurrentProcess(), PowerThrottling.ProcessPowerThrottlingClass, ref state,
			(uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
		{
			return 0;
		}
		int error = Marshal.GetLastPInvokeError();
		return error != 0 ? error : -1;
	}

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetProcessInformation(IntPtr process, int informationClass,
		ref ProcessPowerThrottlingState information, uint size);
}
