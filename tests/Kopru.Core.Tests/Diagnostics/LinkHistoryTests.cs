// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Tests;

// The last minute of the link the bridge keeps for the Overview's chart: a second at a time with its counts, oldest first,
// a fixed size, a snapshot that later samples never change, and a fresh start on every connect.
public class LinkHistoryTests
{
	private static readonly LatencyReading Reading = new(1.2, 4.5, 0.05);

	[Fact]
	public void KeepsTheLastMinute_OldestFirst()
	{
		var history = new LinkHistory();
		Assert.Empty(history.Recent);
		for (int s = 0; s < LinkHistory.Seconds + 5; s++)
		{
			history.Add(new LinkCounts(700 + s, 0, 0, 0), Reading with { LinkP99Ms = s }, 700 + s);
		}
		IReadOnlyList<LinkSecondPoint> recent = history.Recent;
		Assert.Equal(LinkHistory.Seconds, recent.Count);
		Assert.Equal(5, recent[0].WorstMs);
		Assert.Equal(LinkHistory.Seconds + 4, recent[^1].WorstMs);
		Assert.Equal(1.2, recent[^1].TypicalMs);
		Assert.Equal(700 + LinkHistory.Seconds + 4, recent[^1].RateHz);
		Assert.Equal(700 + LinkHistory.Seconds + 4, recent[^1].Reports);
	}

	// The per-second counts behind the chart's ticks: a second with lost reports or a long pause has trouble.
	[Fact]
	public void EverySecond_CarriesItsCounts_ForTheTicks()
	{
		var history = new LinkHistory();
		history.Add(new LinkCounts(700, 0, 0, 0), Reading, 700);
		history.Add(new LinkCounts(690, 4, 0, 0), Reading, 690);
		history.Add(new LinkCounts(650, 0, 2, 1), Reading, 650);
		IReadOnlyList<LinkSecondPoint> recent = history.Recent;
		Assert.Equal([false, true, true], recent.Select(p => p.HasTrouble));
		Assert.Equal(4, recent[1].Lost);
		Assert.Equal(2, recent[2].LongGaps);
	}

	// A second without a delay reading yet is kept as a gap (NaN), not as a zero.
	[Fact]
	public void ASecondWithoutAReading_IsAGap()
	{
		var history = new LinkHistory();
		history.Add(new LinkCounts(5, 0, 0, 0), null, 5);
		LinkSecondPoint point = Assert.Single(history.Recent);
		Assert.False(point.HasDelay);
		Assert.True(double.IsNaN(point.TypicalMs));
	}

	[Fact]
	public void Snapshot_IsNotChangedByLaterSamples_AndResetStartsOver()
	{
		var history = new LinkHistory();
		history.Add(new LinkCounts(700, 0, 0, 0), Reading, 700);
		IReadOnlyList<LinkSecondPoint> before = history.Recent;
		history.Add(new LinkCounts(700, 1, 0, 0), Reading, 700);
		Assert.Single(before);
		Assert.Equal(2, history.Recent.Count);
		history.Reset();
		Assert.Empty(history.Recent);
		history.Add(new LinkCounts(600, 0, 0, 0), Reading, 600);
		Assert.Equal(600, Assert.Single(history.Recent).RateHz);
	}

	// Without a running bridge there is no history in the status.
	[Fact]
	public void Status_HasNoHistory_ByDefault() =>
		Assert.Null(new BridgeStatus(BridgeState.Stopped, null, 0, 0, BridgeError.None, null).LinkSeconds);
}
