// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.Services;

// Applies values off the calling thread, one at a time and in the order posted. Values posted while one is being applied
// coalesce: only the newest is applied next. A value equal to the last one posted is ignored (nothing to change).
// Used for the speaker, mic and light settings of each bridge host, so a quick on/off/on can never end in the wrong state
// and never applies the same change twice.
public sealed class LatestValueWorker<T>
{
	private readonly Action<T> apply;
	private readonly Func<Action, Task> run;
	private readonly object gate = new();
	private readonly IEqualityComparer<T> comparer = EqualityComparer<T>.Default;
	private bool hasPosted;
	private T? posted;
	private bool hasPending;
	private T? pending;
	private Task idle = Task.CompletedTask;
	private bool running;
	private int applied;

	// run: how the worker loop is started (the thread pool by default; tests may run it inline).
	public LatestValueWorker(Action<T> apply, Func<Action, Task>? run = null)
	{
		this.apply = apply;
		this.run = run ?? Task.Run;
	}

	// How many values were applied (diagnostics and tests).
	public int Applied => Volatile.Read(ref applied);

	// The last value posted, if any.
	public bool TryGetPosted(out T? value)
	{
		lock (gate)
		{
			value = posted;
			return hasPosted;
		}
	}

	// Completes when everything posted so far has been applied.
	public Task Idle
	{
		get
		{
			lock (gate)
			{
				return idle;
			}
		}
	}

	// Queues value; force applies it even when it equals the last one posted. True when it was queued.
	public bool Post(T value, bool force = false)
	{
		lock (gate)
		{
			if (!force && hasPosted && comparer.Equals(posted!, value))
			{
				return false;
			}
			hasPosted = true;
			posted = value;
			hasPending = true;
			pending = value;
			if (running)
			{
				return true;
			}
			running = true;
			idle = run(Drain);
			return true;
		}
	}

	private void Drain()
	{
		while (true)
		{
			T value;
			lock (gate)
			{
				if (!hasPending)
				{
					running = false;
					return;
				}
				value = pending!;
				pending = default;
				hasPending = false;
			}
			try
			{
				apply(value);
			}
			catch (Exception e)
			{
				// The controller is going away; the host applies its remembered settings on the next connect.
				Kopru.Core.Diagnostics.AppLog.Write($"apply {typeof(T).Name} failed: {e.GetType().Name}: {e.Message}");
			}
			Interlocked.Increment(ref applied);
		}
	}
}
