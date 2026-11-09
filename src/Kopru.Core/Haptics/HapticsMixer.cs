// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Haptics;

// Frames waiting for the haptics stream (3 kHz, signed 8-bit stereo). Holds at most MaxFrames: audio that runs ahead
// drops its oldest frames, so latency stays low; audio that runs behind plays as silence. Thread-safe.
public sealed class HapticsMixer
{
	public const int MaxFrames = 32 * 6;

	private readonly object gate = new();

	private readonly (sbyte Left, sbyte Right)[] ring = new (sbyte, sbyte)[MaxFrames];

	private int head;

	private int count;

	private int peakLeft, peakRight;

	public int Buffered
	{
		get
		{
			lock (gate)
			{
				return count;
			}
		}
	}

	public void Push(sbyte left, sbyte right)
	{
		lock (gate)
		{
			if (count == MaxFrames)
			{
				head = (head + 1) % MaxFrames;
				count--;
			}
			ring[(head + count) % MaxFrames] = (left, right);
			count++;
		}
	}

	// Takes the next frames (silence where none are buffered); returns whether any of them is not silent.
	public bool Take(Span<sbyte> left, Span<sbyte> right)
	{
		bool sound = false;
		lock (gate)
		{
			for (int i = 0; i < left.Length; i++)
			{
				(sbyte l, sbyte r) = (0, 0);
				if (count > 0)
				{
					(l, r) = ring[head];
					head = (head + 1) % MaxFrames;
					count--;
				}
				left[i] = l;
				right[i] = r;
				peakLeft = Math.Max(peakLeft, Math.Abs((int)l));
				peakRight = Math.Max(peakRight, Math.Abs((int)r));
				sound |= l != 0 || r != 0;
			}
		}
		return sound;
	}

	// Loudest frame per side (0..1) taken for sending since the last call; for a live output meter.
	public (double Left, double Right) TakePeaks()
	{
		lock (gate)
		{
			(double, double) peaks = (Math.Min(peakLeft, 127) / 127.0, Math.Min(peakRight, 127) / 127.0);
			peakLeft = peakRight = 0;
			return peaks;
		}
	}
}
