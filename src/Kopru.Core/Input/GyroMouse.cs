// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Input;

// Where gyro aiming goes: added to the right stick (games see it) or moving the Windows mouse pointer.
public enum GyroTarget
{
	RightStick,
	Mouse
}

// Gyro aiming with GyroTarget.Mouse: the controller's turn rate moves the pointer through an IInputSink, with the gyro
// settings' sensitivity, inversion, deadzone, smoothing and activation mode. The right stick stays untouched (GyroAim
// skips the stick mix for this target). Apply runs on the input thread only.
public sealed class GyroMouse
{
	// Pointer pixels per degree turned at sensitivity 1.0.
	public const double PixelsPerDegree = 12;

	private readonly TimeProvider time;
	private volatile IInputSink sink;
	private long lastTimestamp;
	private bool hasTimestamp;
	private double smoothX, smoothY, remX, remY;

	public GyroMouse(IInputSink? sink = null, TimeProvider? time = null)
	{
		this.sink = sink ?? NullInputSink.Instance;
		this.time = time ?? TimeProvider.System;
	}

	public IInputSink Sink
	{
		get => sink;
		set => sink = value ?? NullInputSink.Instance;
	}

	public void Apply(GyroAim? aim, ReadOnlySpan<byte> body)
	{
		long now = time.GetTimestamp();
		TimeSpan elapsed = hasTimestamp ? time.GetElapsedTime(lastTimestamp, now) : TimeSpan.Zero;
		lastTimestamp = now;
		hasTimestamp = true;
		Apply(aim, body, elapsed);
	}

	// Call after the aim's UpdateActivation (InputTransform.Apply does it).
	public void Apply(GyroAim? aim, ReadOnlySpan<byte> body, TimeSpan elapsed)
	{
		if (aim == null || aim.Settings.Target != GyroTarget.Mouse || !aim.IsActive || body.Length < GyroAim.MinBodySize)
		{
			smoothX = smoothY = remX = remY = 0;
			return;
		}
		(double x, double y) = aim.UnclampedOffset(body);
		double alpha = GyroAim.SmoothingAlpha(aim.Settings.Smoothing);
		smoothX += alpha * (x - smoothX);
		smoothY += alpha * (y - smoothY);
		double dt = Math.Clamp(elapsed.TotalSeconds, 0, 0.05);
		double scale = GyroAim.FullScaleDegPerSec * PixelsPerDegree * dt;
		remX += smoothX * scale;
		remY += smoothY * scale;
		int px = (int)Math.Truncate(remX), py = (int)Math.Truncate(remY);
		remX -= px;
		remY -= py;
		if (px != 0 || py != 0)
		{
			sink.MoveMouse(px, py);
		}
	}
}
