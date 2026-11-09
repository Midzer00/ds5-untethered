// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

namespace Kopru.Core.Input;

public enum DriftCalibrationStatus
{
	Success,
	SticksMoved,
	NotEnoughSamples
}

// Center offsets in raw units (subtract from readings) and suggested inner deadzones in percent.
public sealed record DriftCalibrationResult(DriftCalibrationStatus Status, double LeftX, double LeftY, double RightX,
	double RightY, double SuggestedLeftDeadzone, double SuggestedRightDeadzone)
{
	public bool Success => Status == DriftCalibrationStatus.Success;

	public string Message(Language? l = null) => Status switch
	{
		DriftCalibrationStatus.Success => Text.CalibrationDone(l),
		DriftCalibrationStatus.SticksMoved => Text.CalibrationSticksMoved(l),
		_ => Text.CalibrationNotEnoughData(l)
	};
}

// Collects raw (untransformed) input bodies while the user leaves both sticks alone. Feed Add with the time since
// calibration started; once IsComplete, call Finish. Not thread-safe; feed it from one thread.
public sealed class DriftCalibrator
{
	public const int MinSamples = 50;

	private readonly List<(byte LX, byte LY, byte RX, byte RY)> samples = new(4096);

	public DriftCalibrator(TimeSpan duration, int maxSpread = 16)
	{
		Duration = duration;
		MaxSpread = maxSpread;
	}

	public TimeSpan Duration { get; }

	// Largest allowed max-min spread per axis (raw units) before the run counts as "moved".
	public int MaxSpread { get; }

	public TimeSpan Elapsed { get; private set; }

	public bool IsComplete => Elapsed >= Duration;

	public double Progress => Duration <= TimeSpan.Zero ? 1 : Math.Min(1, Elapsed / Duration);

	public void Add(ReadOnlySpan<byte> body, TimeSpan elapsed)
	{
		if (body.Length < 4 || IsComplete)
			return;
		samples.Add((body[0], body[1], body[2], body[3]));
		Elapsed = elapsed;
	}

	public DriftCalibrationResult Finish()
	{
		if (samples.Count < MinSamples)
			return new(DriftCalibrationStatus.NotEnoughSamples, 0, 0, 0, 0, 0, 0);
		var (lx, ly, lMoved, lDz) = Analyze(s => s.LX, s => s.LY);
		var (rx, ry, rMoved, rDz) = Analyze(s => s.RX, s => s.RY);
		return lMoved || rMoved
			? new(DriftCalibrationStatus.SticksMoved, 0, 0, 0, 0, 0, 0)
			: new(DriftCalibrationStatus.Success, lx, ly, rx, ry, lDz, rDz);
	}

	private (double X, double Y, bool Moved, double Deadzone) Analyze(Func<(byte LX, byte LY, byte RX, byte RY), byte> getX,
		Func<(byte LX, byte LY, byte RX, byte RY), byte> getY)
	{
		double sx = 0, sy = 0;
		int minX = 255, maxX = 0, minY = 255, maxY = 0;
		foreach (var s in samples)
		{
			int x = getX(s), y = getY(s);
			sx += x; sy += y;
			minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
			minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
		}
		if (maxX - minX > MaxSpread || maxY - minY > MaxSpread)
			return (0, 0, true, 0);
		double mx = sx / samples.Count, my = sy / samples.Count, noise = 0;
		foreach (var s in samples)
			noise = Math.Max(noise, Math.Sqrt(Math.Pow(getX(s) - mx, 2) + Math.Pow(getY(s) - my, 2)));
		// Noise radius as a percent of the stick radius, plus a 2 % margin.
		double deadzone = Math.Round(Math.Min(30, noise / 127.5 * 100 + 2), 1);
		return (Math.Round(mx - 127.5, 2), Math.Round(my - 127.5, 2), false, deadzone);
	}
}
