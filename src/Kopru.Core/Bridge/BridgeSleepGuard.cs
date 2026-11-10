// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Bridge;

// Windows suspend / resume notifications.
public interface IPowerEvents : IDisposable
{
	// Raised before the system sleeps; the handler should finish its teardown before returning.
	event Action? Suspending;

	event Action? Resumed;
}

// Sleep and resume: the Bluetooth link and the USB/IP client don't survive sleep, so a running bridge is torn down
// cleanly before it (virtual pad unplugged, session closed) and started again after resume, once Bluetooth had time
// to come back (a controller that reconnects late is picked up by WaitingForController).
public sealed class BridgeSleepGuard : IDisposable
{
	public static readonly TimeSpan DefaultResumeDelay = TimeSpan.FromSeconds(5);

	public static readonly TimeSpan SuspendTimeout = TimeSpan.FromSeconds(3);

	private readonly IPowerEvents events;

	private readonly Func<bool> isActive;

	private readonly Func<Task> stop;

	private readonly Func<Task> start;

	private readonly TimeProvider time;

	private int wasRunning;

	private CancellationTokenSource? pendingResume;

	public BridgeSleepGuard(IPowerEvents events, BridgeHost host, TimeProvider? time = null)
		: this(events, () => host.State is not (BridgeState.Stopped or BridgeState.Error), () => host.StopAsync("system sleep"),
			() => host.StartAsync("system resume"), time)
	{
	}

	public BridgeSleepGuard(IPowerEvents events, Func<bool> isActive, Func<Task> stop, Func<Task> start, TimeProvider? time = null)
	{
		this.events = events;
		this.isActive = isActive;
		this.stop = stop;
		this.start = start;
		this.time = time ?? TimeProvider.System;
		events.Suspending += OnSuspending;
		events.Resumed += OnResumed;
	}

	public TimeSpan ResumeDelay { get; set; } = DefaultResumeDelay;

	// Completes when the restart after the last resume finished (or was skipped).
	public Task LastResume { get; private set; } = Task.CompletedTask;

	private void OnSuspending()
	{
		Interlocked.Exchange(ref pendingResume, null)?.Cancel();
		if (!isActive())
		{
			return;
		}
		Volatile.Write(ref wasRunning, 1);
		AppLog.Write("sleep: stopping the bridge");
		try
		{
			if (!Task.Run(stop).Wait(SuspendTimeout))
			{
				AppLog.Write("sleep: teardown still running when the system went to sleep");
			}
		}
		catch (AggregateException e)
		{
			AppLog.Write("sleep: teardown failed: " + e.InnerException?.Message);
		}
	}

	private void OnResumed()
	{
		if (Interlocked.Exchange(ref wasRunning, 0) == 0)
		{
			return;
		}
		CancellationTokenSource cts = new();
		Interlocked.Exchange(ref pendingResume, cts)?.Cancel();
		LastResume = RestartAsync(cts.Token);
	}

	private async Task RestartAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(ResumeDelay, time, token).ConfigureAwait(false);
			AppLog.Write("resume: starting the bridge");
			await start().ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Went to sleep again before the restart.
		}
		catch (Exception e)
		{
			AppLog.Write("resume: restart failed: " + e.Message);
		}
	}

	public void Dispose()
	{
		events.Suspending -= OnSuspending;
		events.Resumed -= OnResumed;
		Interlocked.Exchange(ref pendingResume, null)?.Cancel();
	}
}

// PowerRegisterSuspendResumeNotification with a callback: no window needed. The system waits for the callback on
// suspend, so the teardown in Suspending runs before sleep.
public sealed class WindowsPowerEvents : IPowerEvents
{
	private const uint DeviceNotifyCallback = 2;

	private const uint PbtApmSuspend = 0x4;

	private const uint PbtApmResumeAutomatic = 0x12;

	private readonly PowerCallback callback;

	private readonly GCHandle callbackHandle;

	private readonly IntPtr parameters;

	private IntPtr registration;

	private int disposed;

	private WindowsPowerEvents()
	{
		callback = OnPower;
		callbackHandle = GCHandle.Alloc(callback);
		parameters = Marshal.AllocHGlobal(IntPtr.Size * 2);
		Marshal.WriteIntPtr(parameters, Marshal.GetFunctionPointerForDelegate(callback));
		Marshal.WriteIntPtr(parameters, IntPtr.Size, IntPtr.Zero);
	}

	public event Action? Suspending;

	public event Action? Resumed;

	// Null when the notification can't be registered (not Windows, or the call failed).
	public static WindowsPowerEvents? TryCreate()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		WindowsPowerEvents events = new();
		try
		{
			if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, events.parameters, out events.registration) == 0)
			{
				return events;
			}
		}
		catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
		{
			// Too old a Windows.
		}
		AppLog.Write("sleep: could not register for suspend/resume notifications");
		events.Dispose();
		return null;
	}

	private uint OnPower(IntPtr context, uint type, IntPtr setting)
	{
		try
		{
			if (type == PbtApmSuspend)
			{
				Suspending?.Invoke();
			}
			else if (type == PbtApmResumeAutomatic)
			{
				Resumed?.Invoke();
			}
		}
		catch (Exception e)
		{
			AppLog.Write("sleep: power handler failed: " + e.Message);
		}
		return 0;
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
		{
			return;
		}
		if (registration != IntPtr.Zero)
		{
			PowerUnregisterSuspendResumeNotification(registration);
			registration = IntPtr.Zero;
		}
		Marshal.FreeHGlobal(parameters);
		callbackHandle.Free();
	}

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	private delegate uint PowerCallback(IntPtr context, uint type, IntPtr setting);

	[DllImport("powrprof.dll")]
	private static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registrationHandle);

	[DllImport("powrprof.dll")]
	private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
