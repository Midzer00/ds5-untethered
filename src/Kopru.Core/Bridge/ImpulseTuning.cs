// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Kopru.Core.Bridge;

// EXPERIMENTAL (the Xbox One pad): the knobs of the impulse trigger emulation (docs/research/xbox-one-impulse-triggers.md,
// section 7). A real impulse trigger is a small ERM motor: its drive level (PWM %) sets its speed, and its speed sets both
// the vibration frequency (rises in proportion) and the strength. A level is played as the DualSense trigger's vibration
// effect with
//
//   x         = level / 100 (after the motor's inertia, ImpulseMotor)
//   amplitude = round(8 x x ^ AmplitudeCurve), 1-8 (at least 1 for any level the motor still turns at)
//   frequency = MinHz + (MaxHz - MinHz) x q, where q = x ^ FrequencyCurve rounded to eighths (FrequencySteps)
//
// AttackMs / DecayMs are the motor's spin-up / spin-down time constants (time to cover 63 % of a step).
//
// This file has no dependencies outside the BCL: tools/Kopru.WgiProbe compiles it too (its tuning sliders write the
// file the app watches, ImpulseTuningFile, and show the mapping with the same code).
public sealed record ImpulseTuning
{
	// The tuning file the WGI probe writes and the app reads while the experimental Xbox One pad is on:
	// %LOCALAPPDATA%\Untethered\impulse-tuning.json (the app's data folder, AppPaths.DataDirectory).
	public const string FileName = "impulse-tuning.json";

	public static string DefaultPath { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Untethered", FileName);

	// Frequency moves in eighths of its range, like the amplitude's eight steps: a level change inside a step writes
	// nothing new to the controller.
	public const int FrequencySteps = 8;

	// Below this level (percent) a decaying motor counts as stopped: a real ERM stalls well before its drive reaches 0,
	// so the inertia's exponential tail does not drag on at amplitude 1.
	public const double StallLevel = 10;

	// Levels under this (percent) are no vibration at all.
	public const double OffLevel = 0.5;

	public int MinHz { get; init; } = 30;

	public int MaxHz { get; init; } = 90;

	public double FrequencyCurve { get; init; } = 1.0;

	public double AmplitudeCurve { get; init; } = 0.8;

	public double AttackMs { get; init; } = 25;

	public double DecayMs { get; init; } = 35;

	// The default: grounded in the motor data in the research doc (frequency proportional to drive over a ~3:1 range,
	// 25-35 ms spin-up / spin-down of a small ERM), scaled into the band the DualSense trigger motor plays well.
	public static ImpulseTuning XboxLike { get; } = new();

	// Gentler: a lower, narrower pitch range, weak low levels, slower spin-up and a longer tail.
	public static ImpulseTuning Soft { get; } = new() { MinHz = 25, MaxHz = 60, FrequencyCurve = 1.0, AmplitudeCurve = 1.2, AttackMs = 45, DecayMs = 80 };

	// Punchier: a higher pitch that climbs early, strong low levels, near-instant start and stop.
	public static ImpulseTuning Sharp { get; } = new() { MinHz = 45, MaxHz = 130, FrequencyCurve = 0.8, AmplitudeCurve = 0.6, AttackMs = 10, DecayMs = 15 };

	public static IReadOnlyList<(string Name, ImpulseTuning Tuning)> Presets { get; } = [("Xbox-like", XboxLike), ("Soft", Soft), ("Sharp", Sharp)];

	// Every value in range: Hz 1-255 with MinHz <= MaxHz, curves 0.2-3, times 0-500 ms (NaN counts as the default).
	public ImpulseTuning Normalized()
	{
		int min = Math.Clamp(MinHz, 1, 255);
		int max = Math.Clamp(MaxHz, 1, 255);
		return new ImpulseTuning
		{
			MinHz = Math.Min(min, max),
			MaxHz = Math.Max(min, max),
			FrequencyCurve = Clamp(FrequencyCurve, 0.2, 3, XboxLike.FrequencyCurve),
			AmplitudeCurve = Clamp(AmplitudeCurve, 0.2, 3, XboxLike.AmplitudeCurve),
			AttackMs = Clamp(AttackMs, 0, 500, XboxLike.AttackMs),
			DecayMs = Clamp(DecayMs, 0, 500, XboxLike.DecayMs)
		};
	}

	// The vibration for a level (percent; above 100 counts as 100): amplitude 1-8 and frequency in Hz, or (0, 0) for
	// no vibration (level under OffLevel).
	public (int Amplitude, int Hz) Map(double level)
	{
		if (double.IsNaN(level) || level < OffLevel)
		{
			return (0, 0);
		}
		double x = Math.Min(level, 100) / 100;
		int amplitude = Math.Clamp((int)Math.Round(8 * Math.Pow(x, AmplitudeCurve), MidpointRounding.AwayFromZero), 1, 8);
		double q = Math.Round(Math.Pow(x, FrequencyCurve) * FrequencySteps, MidpointRounding.AwayFromZero) / FrequencySteps;
		int hz = Math.Clamp((int)Math.Round(MinHz + (MaxHz - MinHz) * q, MidpointRounding.AwayFromZero), 1, 255);
		return (amplitude, hz);
	}

	// "30-90 Hz, frequency curve 1, amplitude curve 0.8, attack 25 ms, decay 35 ms" (logs, the probe).
	public string Describe() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
		$"{MinHz}-{MaxHz} Hz, frequency curve {FrequencyCurve:0.##}, amplitude curve {AmplitudeCurve:0.##}, attack {AttackMs:0} ms, decay {DecayMs:0} ms");

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

	// Missing values keep their defaults; the result is normalized. Throws JsonException on malformed text.
	public static ImpulseTuning FromJson(string json) =>
		(JsonSerializer.Deserialize<ImpulseTuning>(json, JsonOptions) ?? throw new JsonException("empty tuning")).Normalized();

	// Written whole under a temporary name, then moved over the file, so a reader never sees half a file.
	public void Save(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string temp = path + ".tmp";
		File.WriteAllText(temp, ToJson());
		File.Move(temp, path, overwrite: true);
	}

	private static double Clamp(double v, double min, double max, double fallback) => double.IsNaN(v) ? fallback : Math.Clamp(v, min, max);
}
