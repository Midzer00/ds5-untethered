// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;

namespace Kopru.Core.Lighting;

// Sends the user's light while the controller is connected: ticks at about 30 Hz, but writes only when the LED state
// changed (a static light is written once), at most once per Interval, and never while the game controls the light.
// Each write changes only the light bytes of the shared output block and goes out as one ordinary output report with
// the rest of the current output (haptics mode, triggers); it never touches rumble or haptics bits.
public sealed class LightSender : IDisposable
{
	public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.0 / 30);

	// Shortest gap between two writes (a little under Interval, so timer jitter does not skip ticks).
	public static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(30);

	private readonly Action<Action<byte[]>> write;
	private readonly Func<LightSettings> light;
	private readonly Func<BatteryStatus?> battery;
	private readonly Func<bool> gameControls;
	private readonly TimeProvider time;
	private readonly object gate = new();
	private readonly DateTimeOffset started;
	private ITimer? timer;
	private LedFrame? last;
	private DateTimeOffset lastSentAt;
	private bool haveSent;
	private DateTimeOffset holdUntil;

	public LightSender(Action<Action<byte[]>> write, Func<LightSettings> light, Func<BatteryStatus?> battery, Func<bool> gameControls,
		TimeProvider? time = null)
	{
		this.write = write;
		this.light = light;
		this.battery = battery;
		this.gameControls = gameControls;
		this.time = time ?? TimeProvider.System;
		started = this.time.GetUtcNow();
	}

	public long Sent { get; private set; }

	public void Start()
	{
		lock (gate)
		{
			timer ??= time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, Interval);
		}
	}

	// Sends the next change at once (the settings changed).
	public void Invalidate()
	{
		lock (gate)
		{
			last = null;
			haveSent = false;
		}
		Tick();
	}

	// Writes nothing for a while (something else, like the profile indicator, shows on the LEDs); the full state is sent
	// again on the first tick after it.
	public void Hold(TimeSpan duration)
	{
		lock (gate)
		{
			holdUntil = time.GetUtcNow() + duration;
			last = null;
		}
	}

	// One step; true when a report was written.
	public bool Tick()
	{
		lock (gate)
		{
			if (gameControls())
			{
				// Sent again in full once the game lets go.
				last = null;
				return false;
			}
			DateTimeOffset now = time.GetUtcNow();
			if (now < holdUntil)
			{
				last = null;
				return false;
			}
			if (haveSent && now - lastSentAt < MinGap)
			{
				return false;
			}
			LedFrame frame = LightAnimator.FrameAt(light(), now - started, battery());
			if (last == frame)
			{
				return false;
			}
			try
			{
				write(frame.WriteTo);
			}
			catch (Exception)
			{
				// The controller is going away; nothing is sent after Dispose.
				return false;
			}
			last = frame;
			lastSentAt = now;
			haveSent = true;
			Sent++;
			return true;
		}
	}

	public void Dispose()
	{
		lock (gate)
		{
			timer?.Dispose();
			timer = null;
		}
	}
}
