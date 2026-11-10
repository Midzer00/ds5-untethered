// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Instance;

namespace Kopru.Core.Tests;

public class SingleInstanceTests
{
	// A fresh name per test: the real kernel objects are used, never the app's own name.
	private static string UniqueName() => "DS5UntetheredTest." + Guid.NewGuid().ToString("N");

	[Theory]
	[InlineData(true, false, InstanceDecision.Run)]
	[InlineData(true, true, InstanceDecision.Run)]
	[InlineData(false, false, InstanceDecision.ShowOtherAndExit)]
	[InlineData(false, true, InstanceDecision.ExitQuietly)]
	public void Policy(bool firstCopy, bool minimized, InstanceDecision expected) =>
		Assert.Equal(expected, InstancePolicy.Decide(firstCopy, minimized));

	[Fact]
	public void SecondCopyIsNotFirst()
	{
		string name = UniqueName();
		using SingleInstance first = SingleInstance.Acquire(name);
		using SingleInstance second = SingleInstance.Acquire(name);
		Assert.True(first.IsFirst);
		Assert.False(second.IsFirst);
	}

	[Fact]
	public void FirstAgainAfterTheFirstCopyExits()
	{
		string name = UniqueName();
		SingleInstance first = SingleInstance.Acquire(name);
		first.Dispose();
		using SingleInstance next = SingleInstance.Acquire(name);
		Assert.True(next.IsFirst);
	}

	[Fact]
	public void SecondStartShowsTheFirstCopy()
	{
		string name = UniqueName();
		using SingleInstance first = SingleInstance.Acquire(name);
		int shows = 0;
		first.Listen(() => Interlocked.Increment(ref shows));
		using SingleInstance second = SingleInstance.Acquire(name);

		Assert.True(second.AskFirstToShow(TimeSpan.FromSeconds(5)));
		Assert.Equal(1, Volatile.Read(ref shows));
	}

	[Fact]
	public void EveryLaterStartShowsAgain()
	{
		string name = UniqueName();
		using SingleInstance first = SingleInstance.Acquire(name);
		int shows = 0;
		first.Listen(() => Interlocked.Increment(ref shows));
		for (int i = 0; i < 3; i++)
		{
			using SingleInstance later = SingleInstance.Acquire(name);
			Assert.True(later.AskFirstToShow(TimeSpan.FromSeconds(5)));
		}
		Assert.Equal(3, Volatile.Read(ref shows));
	}

	[Fact]
	public void NoAnswerTimesOutWithoutSideEffects()
	{
		string name = UniqueName();
		using SingleInstance first = SingleInstance.Acquire(name);
		using SingleInstance second = SingleInstance.Acquire(name);

		// The first copy is not listening yet (still starting, or hung).
		Assert.False(second.AskFirstToShow(TimeSpan.FromMilliseconds(200)));
		Assert.True(first.IsFirst);

		// The request stays pending and is handled once the first copy listens.
		using var shown = new ManualResetEventSlim();
		first.Listen(shown.Set);
		Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public void MutexWithoutEventsTimesOut()
	{
		string name = UniqueName();
		// Something holds the mutex but never made the events (e.g. a copy that is just starting).
		using var mutex = new Mutex(false, SingleInstance.MutexName(name));
		using SingleInstance second = SingleInstance.Acquire(name);
		Assert.False(second.IsFirst);
		Assert.False(second.AskFirstToShow(TimeSpan.FromMilliseconds(200)));
	}

	[Fact]
	public void OnlyTheFirstCopyListensAndOnlyLaterCopiesAsk()
	{
		string name = UniqueName();
		using SingleInstance first = SingleInstance.Acquire(name);
		using SingleInstance second = SingleInstance.Acquire(name);
		Assert.Throws<InvalidOperationException>(() => second.Listen(() => { }));
		Assert.Throws<InvalidOperationException>(() => first.AskFirstToShow(TimeSpan.Zero));
	}

	[Fact]
	public void MutexNameIsWhatTheInstallerChecks() =>
		Assert.Equal("DS5Untethered.Running", SingleInstance.MutexName(SingleInstance.DefaultName));
}
