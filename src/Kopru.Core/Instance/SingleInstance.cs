// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Instance;

public enum InstanceDecision
{
	// No other copy runs: this one is the app.
	Run,
	// Another copy runs: ask it to show its window, then exit.
	ShowOtherAndExit,
	// Another copy runs and this start is the autostart (--minimized): exit without disturbing it.
	ExitQuietly
}

public static class InstancePolicy
{
	public static InstanceDecision Decide(bool firstCopy, bool minimized) =>
		firstCopy ? InstanceDecision.Run : minimized ? InstanceDecision.ExitQuietly : InstanceDecision.ShowOtherAndExit;
}

// One running copy per user session, whatever folder the exe was started from. The first copy holds a named mutex
// (also what the installer looks for, AppMutex) and waits on a named "show" event; a later start sets that event and
// waits briefly for the "shown" answer. Names have no Global\ prefix, so they live in the user's session namespace
// (no administrator rights needed; another signed-in user has their own).
public sealed class SingleInstance : IDisposable
{
	public const string DefaultName = "DS5Untethered";
	public static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromSeconds(3);

	private readonly Mutex _mutex;
	private readonly EventWaitHandle? _show;
	private readonly EventWaitHandle? _shown;
	private readonly string _name;
	private RegisteredWaitHandle? _wait;
	private Action? _onShow;
	private bool _disposed;

	private SingleInstance(string name, Mutex mutex, bool isFirst, EventWaitHandle? show, EventWaitHandle? shown)
	{
		_name = name;
		_mutex = mutex;
		IsFirst = isFirst;
		_show = show;
		_shown = shown;
	}

	// True: no other copy held the mutex when this one started.
	public bool IsFirst { get; }

	public static string MutexName(string name) => name + ".Running";
	public static string ShowEventName(string name) => name + ".Show";
	public static string ShownEventName(string name) => name + ".Shown";

	// Never throws for a running other copy: a mutex that exists but cannot be opened (e.g. made by an elevated copy)
	// counts as "another copy runs".
	public static SingleInstance Acquire(string name = DefaultName)
	{
		Mutex mutex;
		bool createdNew;
		try
		{
			mutex = new Mutex(false, MutexName(name), out createdNew);
		}
		catch (UnauthorizedAccessException)
		{
			return new SingleInstance(name, new Mutex(false), false, null, null);
		}
		if (!createdNew)
		{
			return new SingleInstance(name, mutex, false, null, null);
		}
		// The events exist as long as the first copy runs; AutoReset: one start = one show.
		var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName(name));
		var shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShownEventName(name));
		return new SingleInstance(name, mutex, true, show, shown);
	}

	// First copy: calls onShow (on a thread-pool thread) for every later start, then answers it. A start that came before
	// Listen stays pending in the event and is handled right away.
	public void Listen(Action onShow)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!IsFirst || _show == null)
		{
			throw new InvalidOperationException("Only the first copy listens.");
		}
		if (_wait != null)
		{
			throw new InvalidOperationException("Already listening.");
		}
		_onShow = onShow;
		_wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => OnShowSignalled(), null, Timeout.Infinite, executeOnlyOnce: false);
	}

	private void OnShowSignalled()
	{
		if (_disposed)
		{
			return;
		}
		try
		{
			_onShow?.Invoke();
		}
		finally
		{
			try
			{
				_shown?.Set();
			}
			catch (ObjectDisposedException)
			{
				// Exiting.
			}
		}
	}

	// Later copy: asks the first one to show its window. True when it answered within the timeout. Never touches the
	// first copy otherwise (no kill, no cleanup), so a hung first copy just stays as it is.
	public bool AskFirstToShow(TimeSpan timeout)
	{
		if (IsFirst)
		{
			throw new InvalidOperationException("The first copy has no one to ask.");
		}
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}
		DateTime deadline = DateTime.UtcNow + timeout;
		EventWaitHandle? show = null;
		EventWaitHandle? shown = null;
		try
		{
			// The first copy creates the events right after the mutex; wait for them if it is just starting.
			while (!EventWaitHandle.TryOpenExisting(ShowEventName(_name), out show) || !EventWaitHandle.TryOpenExisting(ShownEventName(_name), out shown))
			{
				show?.Dispose();
				show = null;
				if (DateTime.UtcNow >= deadline)
				{
					return false;
				}
				Thread.Sleep(50);
			}
			show.Set();
			TimeSpan left = deadline - DateTime.UtcNow;
			return shown.WaitOne(left > TimeSpan.Zero ? left : TimeSpan.Zero);
		}
		catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
		{
			return false;
		}
		finally
		{
			show?.Dispose();
			shown?.Dispose();
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_wait?.Unregister(null);
		_show?.Dispose();
		_shown?.Dispose();
		_mutex.Dispose();
	}
}
