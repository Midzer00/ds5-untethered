// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Audio;

// What one audio haptics capture run saw, for its session summary in the log: the captured sound (before the DSP: level,
// how much of it was digital silence), the haptics frames the DSP made of it, and what became of them at the controller.
// With these, a summary says by itself where nothing happened: no sound captured, sound too quiet for the gate, or frames
// that never reached the controller. Written on the capture thread (allocates nothing), read when the run has stopped.
public sealed class AudioHapticsStats
{
	// Captured sound.
	public long InputFrames { get; private set; }
	public long SilentInputFrames { get; private set; }
	public int InputRate { get; private set; }
	public int InputChannels { get; private set; }

	// Largest absolute sample (0..1 and above for float overs) and the mean square over all samples.
	public double InputPeak { get; private set; }
	public double InputRms => inputSamples > 0 ? Math.Sqrt(inputSquares / inputSamples) : 0;

	// Haptics frames the DSP made (whatever became of them), how many were not zero, and the largest |sample| (0..127).
	public long OutputFrames { get; private set; }
	public long NonZeroFrames { get; private set; }
	public int OutputPeak { get; private set; }

	// What became of them: mixed into the controller's haptics (and how many of those were not zero), dropped while the
	// game sent its own haptics, dropped without a controller.
	public long PlayedFrames { get; private set; }
	public long NonZeroPlayedFrames { get; private set; }
	public long PausedFrames { get; private set; }
	public long NoControllerFrames { get; private set; }

	private double inputSquares;
	private long inputSamples;

	public void Clear()
	{
		InputFrames = SilentInputFrames = 0;
		InputRate = InputChannels = 0;
		InputPeak = 0;
		inputSquares = 0;
		inputSamples = 0;
		OutputFrames = NonZeroFrames = 0;
		OutputPeak = 0;
		PlayedFrames = NonZeroPlayedFrames = PausedFrames = NoControllerFrames = 0;
	}

	// A block of captured sound (interleaved, whole frames).
	public void AddInput(ReadOnlySpan<float> samples, int channels, int sampleRate)
	{
		if (channels < 1)
		{
			return;
		}
		InputRate = sampleRate;
		InputChannels = channels;
		int frames = samples.Length / channels;
		double peak = InputPeak, squares = 0;
		long silent = 0;
		for (int f = 0, i = 0; f < frames; f++)
		{
			bool zero = true;
			for (int c = 0; c < channels; c++, i++)
			{
				double s = samples[i];
				if (s != 0)
				{
					zero = false;
					squares += s * s;
					double a = Math.Abs(s);
					if (a > peak)
					{
						peak = a;
					}
				}
			}
			if (zero)
			{
				silent++;
			}
		}
		InputPeak = double.IsFinite(peak) ? peak : InputPeak;
		if (double.IsFinite(squares))
		{
			inputSquares += squares;
		}
		inputSamples += (long)frames * channels;
		InputFrames += frames;
		SilentInputFrames += silent;
	}

	// A block of haptics frames from the DSP and what the controller did with it.
	public void AddOutput(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right, AudioHapticsPush result)
	{
		int n = Math.Min(left.Length, right.Length);
		int nonZero = 0, peak = OutputPeak;
		for (int i = 0; i < n; i++)
		{
			int a = Math.Max(Math.Abs((int)left[i]), Math.Abs((int)right[i]));
			if (a != 0)
			{
				nonZero++;
				if (a > peak)
				{
					peak = a;
				}
			}
		}
		OutputPeak = peak;
		OutputFrames += n;
		NonZeroFrames += nonZero;
		switch (result)
		{
			case AudioHapticsPush.Played:
				PlayedFrames += n;
				NonZeroPlayedFrames += nonZero;
				break;
			case AudioHapticsPush.NativeHaptics:
				PausedFrames += n;
				break;
			default:
				NoControllerFrames += n;
				break;
		}
	}

	// The summary line (without the "audio haptics: " prefix). bandPeak: the DSP's loudest envelope (0..1 of full scale)
	// of the band it keeps, which is what the gate compares with.
	public string Summary(TimeSpan active, double bandPeak)
	{
		CultureInfo inv = CultureInfo.InvariantCulture;
		double inSeconds = InputRate > 0 ? InputFrames / (double)InputRate : 0;
		string format = InputRate > 0 ? string.Create(inv, $"{InputRate} Hz {InputChannels} ch") : "no format";
		return string.Create(inv,
			$"session summary: active {active.TotalSeconds:F0} s; " +
			$"sound in: {InputFrames} frames ({inSeconds:F1} s, {format}), peak {Db(InputPeak)} dBFS, rms {Db(InputRms)} dBFS, silent {Percent(SilentInputFrames, InputFrames)} %; " +
			$"band peak {Db(bandPeak)} dBFS (gate opens at {AudioHapticsDsp.GateOpenDb:0}); " +
			$"haptics out: {OutputFrames} frames ({OutputFrames / (double)AudioHapticsDsp.OutputRate:F1} s), peak {Math.Min(OutputPeak, 127) * 100 / 127} %, non-zero {NonZeroFrames} ({Percent(NonZeroFrames, OutputFrames)} %); " +
			$"{PlayedFrames} frames played ({NonZeroPlayedFrames} non-zero), {PausedFrames} paused for native haptics, {NoControllerFrames} without a controller");
	}

	// One plain sentence on what the numbers mean: where the sound stopped on its way to the controller, if it did.
	public string Verdict(double strength)
	{
		if (InputFrames == 0)
		{
			return "no sound arrived from the capture at all";
		}
		if (InputPeak == 0)
		{
			return "the captured sound was digital silence the whole time (the game played nothing, or muted itself, e.g. while in the background)";
		}
		if (NonZeroFrames == 0)
		{
			return strength <= 0
				? "nothing was felt: strength is 0 %"
				: string.Create(CultureInfo.InvariantCulture, $"the sound never got over the gate ({AudioHapticsDsp.GateOpenDb:0} dBFS in the band): too quiet, or nothing in the band");
		}
		if (NonZeroPlayedFrames == 0)
		{
			return PausedFrames > 0 && NoControllerFrames == 0
				? "haptics were made but none played: the game sent its own haptics the whole time"
				: "haptics were made but none reached the controller";
		}
		return string.Create(CultureInfo.InvariantCulture, $"haptics played for {NonZeroPlayedFrames / (double)AudioHapticsDsp.OutputRate:F1} s");
	}

	// "-inf" for nothing at all.
	public static string Db(double level) =>
		level > 0 ? (20 * Math.Log10(level)).ToString("F1", CultureInfo.InvariantCulture) : "-inf";

	private static long Percent(long part, long whole) => whole > 0 ? (long)Math.Round(part * 100.0 / whole) : 0;
}
