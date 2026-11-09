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

	// Converts audio and hands every finished 3 kHz frame to output.
	public void Convert(ReadOnlySpan<byte> audio, Action<sbyte, sbyte> output)
	{
		if (partialLength > 0)
		{
			int take = Math.Min(FrameBytes - partialLength, audio.Length);
			audio[..take].CopyTo(partial.AsSpan(partialLength));
			partialLength += take;
			audio = audio[take..];
			if (partialLength < FrameBytes)
			{
				return;
			}
			Frame(partial, output);
			partialLength = 0;
		}
		while (audio.Length >= FrameBytes)
		{
			Frame(audio[..FrameBytes], output);
			audio = audio[FrameBytes..];
		}
		audio.CopyTo(partial);
		partialLength = audio.Length;
	}

	public void Convert(ReadOnlySpan<byte> audio, HapticsMixer mixer) => Convert(audio, mixer.Push);

	private void Frame(ReadOnlySpan<byte> frame, Action<sbyte, sbyte> output)
	{
		double left = BinaryPrimitives.ReadInt16LittleEndian(frame[4..]) / 32768.0;
		double right = BinaryPrimitives.ReadInt16LittleEndian(frame[6..]) / 32768.0;
		left1 += Alpha * (left - left1);
		left2 += Alpha * (left1 - left2);
		right1 += Alpha * (right - right1);
		right2 += Alpha * (right1 - right2);
		leftSum += left2;
		rightSum += right2;
		if (++phase < Decimation)
		{
			return;
		}
		double level = Level;
		output(ToSample(leftSum / Decimation * level), ToSample(rightSum / Decimation * level));
		phase = 0;
		leftSum = rightSum = 0;
	}

	// tanh keeps small signals linear and bends strong ones smoothly towards full scale; anything under one step is 0.
	internal static sbyte ToSample(double value) => (sbyte)(int)(Math.Tanh(value) * 127);
}
