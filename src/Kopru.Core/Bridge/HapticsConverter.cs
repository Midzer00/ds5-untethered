// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Haptics;

namespace Kopru.Core.Bridge;

// Turns the game's haptics audio (channels 3-4 of 4-channel 16-bit 48 kHz audio) into the controller's 3 kHz signed
// 8-bit stereo frames: two one-pole low-pass filters (~1 kHz) per side, the average of every 16 samples, a level and a
// soft limit. Keeps its state across calls, so audio cut into any chunks converts like one piece. Not thread-safe.
public sealed class HapticsConverter
{
	public const int Channels = 4;
	public const int InputRate = 48000;
	public const int OutputRate = 3000;
	public const int Decimation = InputRate / OutputRate;
	public const double CutoffHz = 1000;
	public const double DefaultLevel = 1.5;

	private const int FrameBytes = Channels * 2;

	private static readonly double Alpha = 1 - Math.Exp(-2 * Math.PI * CutoffHz / InputRate);

	private readonly byte[] partial = new byte[FrameBytes];

	private int partialLength;

	private double left1, left2, right1, right2;

	private double leftSum, rightSum;

	private int phase;

	// 1.0 plays the game's haptics as sent; above that, louder (softly limited near full scale).
	public double Level { get; set; } = DefaultLevel;

	public void Reset()
	{
		partialLength = phase = 0;
		left1 = left2 = right1 = right2 = leftSum = rightSum = 0;
	}

	// Filter state below this is set to 0 (far under one output step), so silence after a sound settles at exactly 0 and
	// the silent fast path takes over instead of decaying through denormal numbers.
	private const double Settled = 1e-9;

	// The most frames Convert can produce from this many bytes of audio (whatever is left over from the last call).
	public static int MaxFrames(int bytes) => ((bytes + FrameBytes - 1) / FrameBytes + Decimation - 1) / Decimation;

	// Converts audio and hands every finished 3 kHz frame to output.
	public void Convert(ReadOnlySpan<byte> audio, Action<sbyte, sbyte> output)
	{
		const int chunk = 32 * Decimation * FrameBytes;
		Span<sbyte> left = stackalloc sbyte[MaxFrames(chunk)];
		Span<sbyte> right = stackalloc sbyte[left.Length];
		while (!audio.IsEmpty)
		{
			ReadOnlySpan<byte> part = audio[..Math.Min(chunk, audio.Length)];
			int frames = Convert(part, left, right);
			for (int i = 0; i < frames; i++)
			{
				output(left[i], right[i]);
			}
			audio = audio[part.Length..];
		}
	}

	public void Convert(ReadOnlySpan<byte> audio, HapticsMixer mixer)
	{
		const int chunk = 32 * Decimation * FrameBytes;
		Span<sbyte> left = stackalloc sbyte[MaxFrames(chunk)];
		Span<sbyte> right = stackalloc sbyte[left.Length];
		while (!audio.IsEmpty)
		{
			ReadOnlySpan<byte> part = audio[..Math.Min(chunk, audio.Length)];
			int frames = Convert(part, left, right);
			mixer.Push(left[..frames], right[..frames]);
			audio = audio[part.Length..];
		}
	}

	// Converts audio into left/right (at least MaxFrames(audio.Length) long); returns the frames written. Allocates nothing.
	public int Convert(ReadOnlySpan<byte> audio, Span<sbyte> left, Span<sbyte> right)
	{
		if (left.Length < MaxFrames(audio.Length) || right.Length < left.Length)
		{
			throw new ArgumentException("output too short", nameof(left));
		}
		int frames = 0;
		if (partialLength > 0)
		{
			int take = Math.Min(FrameBytes - partialLength, audio.Length);
			audio[..take].CopyTo(partial.AsSpan(partialLength));
			partialLength += take;
			audio = audio[take..];
			if (partialLength < FrameBytes)
			{
				return 0;
			}
			if (Frame(partial, out sbyte l, out sbyte r))
			{
				left[frames] = l;
				right[frames] = r;
				frames++;
			}
			partialLength = 0;
		}
		while (audio.Length >= FrameBytes)
		{
			if (Frame(audio[..FrameBytes], out sbyte l, out sbyte r))
			{
				left[frames] = l;
				right[frames] = r;
				frames++;
			}
			audio = audio[FrameBytes..];
		}
		audio.CopyTo(partial);
		partialLength = audio.Length;
		return frames;
	}

	// One 48 kHz input frame; true (with the output) when it completes a 3 kHz frame.
	private bool Frame(ReadOnlySpan<byte> frame, out sbyte outLeft, out sbyte outRight)
	{
		short inLeft = BinaryPrimitives.ReadInt16LittleEndian(frame[4..]);
		short inRight = BinaryPrimitives.ReadInt16LittleEndian(frame[6..]);
		// Digital silence into settled filters stays exactly 0: skip the math (the result would be the same).
		if ((inLeft | inRight) != 0 || left1 != 0 || left2 != 0 || right1 != 0 || right2 != 0)
		{
			double left = inLeft / 32768.0;
			double right = inRight / 32768.0;
			left1 += Alpha * (left - left1);
			left2 += Alpha * (left1 - left2);
			right1 += Alpha * (right - right1);
			right2 += Alpha * (right1 - right2);
			leftSum += left2;
			rightSum += right2;
			if ((inLeft | inRight) == 0)
			{
				Settle(ref left1);
				Settle(ref left2);
				Settle(ref right1);
				Settle(ref right2);
			}
		}
		outLeft = outRight = 0;
		if (++phase < Decimation)
		{
			return false;
		}
		if (leftSum != 0 || rightSum != 0)
		{
			double level = Level;
			outLeft = ToSample(leftSum / Decimation * level);
			outRight = ToSample(rightSum / Decimation * level);
		}
		phase = 0;
		leftSum = rightSum = 0;
		return true;
	}

	private static void Settle(ref double state)
	{
		if (Math.Abs(state) < Settled)
		{
			state = 0;
		}
	}

	// tanh keeps small signals linear and bends strong ones smoothly towards full scale; anything under one step is 0.
	internal static sbyte ToSample(double value) => (sbyte)(int)(Math.Tanh(value) * 127);
}
