// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Platform.Linux.Diagnostics;

// One measuring window of the virtual pad's isochronous OUT audio (what the game plays to the pad: haptics on channels
// 3-4, speaker on 1-2).
public readonly record struct IsoAudioWindow(double Seconds, long Transfers, long Bytes, double AudioMs, double MaxGapMs, int Dropouts, int Starts)
{
	public bool Active => Transfers > 0;

	public double TransfersPerSecond => Seconds > 0 ? Transfers / Seconds : 0;

	// Milliseconds of audio that arrived per second of wall time: about 1000 while a game streams steadily.
	public double AudioMsPerSecond => Seconds > 0 ? AudioMs / Seconds : 0;
}

// Counts the audio transfers the virtual pad receives and finds dropouts: a pause between two transfers longer than
// DropoutGapMs but shorter than StreamStopMs (a longer pause is the game closing and reopening its audio stream, counted
// as a new start). Thread-safe; times are in milliseconds of any monotonic clock.
public sealed class IsoAudioMeter(int bytesPerMs, double dropoutGapMs = 50, double streamStopMs = 1000)
{
	private readonly object gate = new();
	private double? last;
	private double windowStart = double.NaN;
	private long transfers, bytes;
	private double maxGap;
	private int dropouts, starts;

	public double DropoutGapMs => dropoutGapMs;

	public double StreamStopMs => streamStopMs;

	public long TotalTransfers { get; private set; }

	public long TotalBytes { get; private set; }

	public int TotalDropouts { get; private set; }

	public int TotalStarts { get; private set; }

	// One transfer of `length` bytes arrived at nowMs. Returns the gap when it ended a dropout, else null.
	public double? Record(double nowMs, int length)
	{
		lock (gate)
		{
			if (double.IsNaN(windowStart))
			{
				windowStart = nowMs;
			}
			double? dropout = null;
			if (last is not { } previous || nowMs - previous >= streamStopMs)
			{
				starts++;
				TotalStarts++;
			}
			else
			{
				double gap = nowMs - previous;
				maxGap = Math.Max(maxGap, gap);
				if (gap > dropoutGapMs)
				{
					dropouts++;
					TotalDropouts++;
					dropout = gap;
				}
			}
			last = nowMs;
			transfers++;
			bytes += length;
			TotalTransfers++;
			TotalBytes += length;
			return dropout;
		}
	}

	// What arrived since the last call (or the first transfer), and starts a new window at nowMs.
	public IsoAudioWindow TakeWindow(double nowMs)
	{
		lock (gate)
		{
			double seconds = double.IsNaN(windowStart) ? 0 : Math.Max(0, (nowMs - windowStart) / 1000.0);
			IsoAudioWindow window = new(seconds, transfers, bytes, bytesPerMs > 0 ? (double)bytes / bytesPerMs : 0, maxGap, dropouts, starts);
			windowStart = nowMs;
			transfers = bytes = 0;
			maxGap = 0;
			dropouts = starts = 0;
			return window;
		}
	}
}
