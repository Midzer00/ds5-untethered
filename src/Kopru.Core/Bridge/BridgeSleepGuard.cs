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
