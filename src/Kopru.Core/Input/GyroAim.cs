// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Kopru.Core.Input;

public enum GyroMode
{
	Off,
	Always,
	// Active while the physical L2 is held (or toggled by it).
	WhileL2,
	// Active while the physical R1 is held (or toggled by it).
	WhileR1
}

// Gyro aiming: the controller's turn rate moves the right stick. Sensitivity 1.0 means a turn of FullScaleDegPerSec
// deflects the stick fully; Deadzone is in degrees per second; Smoothing 0-100 %; biases are raw gyro counts
// subtracted from the reading (from the gyro calibration). Defaults are off.
public sealed record GyroSettings
{
	public static GyroSettings Default { get; } = new();

	public GyroMode Mode { get; init; } = GyroMode.Off;

	// With WhileL2/WhileR1: a press switches aiming on and off instead of holding.
	public bool Toggle { get; init; }

	public double SensitivityX { get; init; } = 1.0;

	public double SensitivityY { get; init; } = 1.0;

	public bool InvertX { get; init; }

	public bool InvertY { get; init; }

	public double Smoothing { get; init; }

	public double Deadzone { get; init; } = 1.0;

	public double BiasX { get; init; }

	public double BiasY { get; init; }

	public double BiasZ { get; init; }

	// Right stick (default, older files) or the mouse pointer (GyroMouse).
	public GyroTarget Target { get; init; } = GyroTarget.RightStick;

	public bool IsOff => Mode == GyroMode.Off;
}

// Adds the gyro turn rate to the right stick of the 63-byte input body. Keeps state (smoothing, toggle), so feed it from
// one thread; Apply allocates nothing.
//
// Body offsets (Linux drivers/hid/hid-playstation.c, struct dualsense_input_report minus the report ID):
//   15..20 gyro[3]  int16 little endian: X = pitch, Y = yaw, Z = roll
//   21..26 accel[3] int16 little endian
// Units: raw, uncalibrated counts of the IMU at its +-2000 deg/s range, about 16.4 counts per deg/s (32768 / 2000).
// Mapping: yaw (turning left is positive) moves the stick left, pitch (top tilting up is positive) moves it up; the
// stick's Y grows downwards, so both are negated. Inversion flips them.
public sealed class GyroAim
{
	public const int GyroX = 15, GyroY = 17, GyroZ = 19, MinBodySize = 27;
	public const double CountsPerDegPerSec = 32768.0 / 2000.0;
	public const double FullScaleDegPerSec = 120.0;

	private const double Center = 127.5;

	private readonly GyroSettings settings;
	private readonly double alpha;
	private bool toggled, wasPressed, active;
	private double smoothX, smoothY;

	public GyroAim(GyroSettings settings)
	{
		this.settings = settings;
		alpha = SmoothingAlpha(settings.Smoothing);
	}

	public GyroSettings Settings => settings;

	// The low-pass factor for a Smoothing percentage (1 = no smoothing).
	public static double SmoothingAlpha(double smoothing) => 1 - Math.Clamp(smoothing, 0, 100) / 100 * 0.9;

	public bool IsActive => active;

	// The offset last added to the right stick, -1..1 (for the live indicator).
	public double OffsetX { get; private set; }

	public double OffsetY { get; private set; }

	// Reads the activation button from the body as it arrived (physical buttons, before any remap).
	public void UpdateActivation(ReadOnlySpan<byte> physicalBody)
	{
		DsButton button = settings.Mode switch { GyroMode.WhileL2 => DsButton.L2, GyroMode.WhileR1 => DsButton.R1, _ => DsButton.None };
		bool pressed = button != DsButton.None && ButtonRemap.IsPressed(physicalBody, button);
		if (settings.Toggle && pressed && !wasPressed)
			toggled = !toggled;
		wasPressed = pressed;
		active = settings.Mode switch
		{
			GyroMode.Always => true,
			GyroMode.WhileL2 or GyroMode.WhileR1 => settings.Toggle ? toggled : pressed,
			_ => false
		};
	}

	// The turn rate offset (-1..1 per axis) for a body, before smoothing.
	public (double X, double Y) RawOffset(ReadOnlySpan<byte> body)
	{
		(double x, double y) = UnclampedOffset(body);
		return (Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1));
	}

	// RawOffset without the -1..1 limit (1 = FullScaleDegPerSec at sensitivity 1), for the mouse target.
	public (double X, double Y) UnclampedOffset(ReadOnlySpan<byte> body)
	{
		if (body.Length < MinBodySize)
			return (0, 0);
		double pitch = (BinaryPrimitives.ReadInt16LittleEndian(body[GyroX..]) - settings.BiasX) / CountsPerDegPerSec;
		double yaw = (BinaryPrimitives.ReadInt16LittleEndian(body[GyroY..]) - settings.BiasY) / CountsPerDegPerSec;
		double dz = Math.Max(0, settings.Deadzone);
		pitch = Deadzone(pitch, dz);
		yaw = Deadzone(yaw, dz);
		double x = -yaw * settings.SensitivityX / FullScaleDegPerSec;
		double y = -pitch * settings.SensitivityY / FullScaleDegPerSec;
		if (settings.InvertX) x = -x;
		if (settings.InvertY) y = -y;
		return (x, y);
	}

	// Adds the (smoothed) offset to the right stick and clamps. Call UpdateActivation first.
	public void Apply(Span<byte> body)
	{
		if (settings.IsOff || body.Length < MinBodySize)
			return;
		(double x, double y) = active ? RawOffset(body) : (0, 0);
		smoothX += alpha * (x - smoothX);
		smoothY += alpha * (y - smoothY);
		if (Math.Abs(smoothX) < 1e-4) smoothX = 0;
		if (Math.Abs(smoothY) < 1e-4) smoothY = 0;
		OffsetX = smoothX;
		OffsetY = smoothY;
		if ((smoothX == 0 && smoothY == 0) || settings.Target == GyroTarget.Mouse)
			return;
		body[2] = Mix(body[2], smoothX);
		body[3] = Mix(body[3], smoothY);
	}

	private static double Deadzone(double v, double dz) => Math.Abs(v) <= dz ? 0 : v - Math.Sign(v) * dz;

	private static byte Mix(byte stick, double offset)
	{
		double v = Math.Clamp((stick - Center) / Center + offset, -1, 1);
		return (byte)Math.Clamp(Math.Round(Center + v * Center, MidpointRounding.AwayFromZero), 0, 255);
	}
}

public enum GyroCalibrationStatus
{
	Success,
	Moved,
	NotEnoughSamples
}

public sealed record GyroCalibrationResult(GyroCalibrationStatus Status, double BiasX, double BiasY, double BiasZ)
{
	public bool Success => Status == GyroCalibrationStatus.Success;
}

// Averages the gyro while the controller lies still (Duration, 2 s by default) into a bias. Not thread-safe.
public sealed class GyroCalibrator
{
	public const int MinSamples = 20;

	private double sx, sy, sz;
	private int count;
	private bool moved;

	public GyroCalibrator(TimeSpan? duration = null, int maxSpread = 400)
	{
		Duration = duration ?? TimeSpan.FromSeconds(2);
		MaxSpread = maxSpread;
	}

	public TimeSpan Duration { get; }

	// Largest allowed max-min spread on any axis (raw counts, ~24 deg/s) before the run counts as moved.
	public int MaxSpread { get; }

	public TimeSpan Elapsed { get; private set; }

	public bool IsComplete => Elapsed >= Duration;

	public double Progress => Duration <= TimeSpan.Zero ? 1 : Math.Min(1, Elapsed / Duration);

	private int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;

	public void Add(ReadOnlySpan<byte> body, TimeSpan elapsed)
	{
		if (body.Length < GyroAim.MinBodySize || IsComplete)
			return;
		int x = BinaryPrimitives.ReadInt16LittleEndian(body[GyroAim.GyroX..]);
		int y = BinaryPrimitives.ReadInt16LittleEndian(body[GyroAim.GyroY..]);
		int z = BinaryPrimitives.ReadInt16LittleEndian(body[GyroAim.GyroZ..]);
		sx += x; sy += y; sz += z;
		count++;
		minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
		minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
		minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
		moved = maxX - minX > MaxSpread || maxY - minY > MaxSpread || maxZ - minZ > MaxSpread;
		Elapsed = elapsed;
	}

	public GyroCalibrationResult Finish()
	{
		if (count < MinSamples)
			return new(GyroCalibrationStatus.NotEnoughSamples, 0, 0, 0);
		if (moved)
			return new(GyroCalibrationStatus.Moved, 0, 0, 0);
		return new(GyroCalibrationStatus.Success, Math.Round(sx / count, 2), Math.Round(sy / count, 2), Math.Round(sz / count, 2));
	}
}
