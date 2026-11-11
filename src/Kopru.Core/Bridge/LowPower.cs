// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Lighting;

namespace Kopru.Core.Bridge;

// The three steps of the quick settings (light, normal, strong / off, low, high), in the settings' own percent units.
public static class QuickSteps
{
	// Game haptics level (0-300 %): the user's preferred level is 150 %.
	public static readonly IReadOnlyList<double> Haptics = [75, 100, 150];

	// Trigger strength of the game's effects (0-100 %).
	public static readonly IReadOnlyList<double> Trigger = [50, 75, 100];

	// Light bar brightness (0-100 %): off, low, high (80 % is the default brightness).
	public static readonly IReadOnlyList<double> Light = [0, 30, 80];

	// The step a value sits on (within half a percent), or -1 when it is between the steps.
	public static int IndexOf(IReadOnlyList<double> steps, double value)
	{
		for (int i = 0; i < steps.Count; i++)
		{
			if (Math.Abs(steps[i] - value) < 0.5)
			{
				return i;
			}
		}
		return -1;
	}
}

// Low Power mode: a runtime override on top of the settings that go to the controller. It never changes the profile;
// turning it off sends the user's own values again.
public static class LowPower
{
	// The idle power-off time while it is on (when the user's is longer or off).
	public const double IdleMinutes = 5;

	public static ControllerSettings Apply(ControllerSettings s, bool on)
	{
		if (!on)
		{
			return s;
		}
		double light = QuickSteps.Haptics[0];
		double idle = s.IdlePowerOffMinutes < 0.5 || s.IdlePowerOffMinutes > IdleMinutes ? IdleMinutes : s.IdlePowerOffMinutes;
		return s with
		{
			// Haptics and triggers at the light step (a lower value of the user's own stays lower).
			GameHapticsLevel = Math.Min(s.GameHapticsLevel, light),
			RumbleLevel = Math.Min(s.RumbleLevel, light),
			TriggerStrength = Math.Min(s.TriggerStrength, QuickSteps.Trigger[0]),
			// Light bar and player LEDs dark, also for games that set their own light.
			Brightness = 0,
			PlayerLeds = false,
			LightFromGame = false,
			LightOptions = s.LightOptions with { Mode = LightMode.Static, PlayerPattern = PlayerLedPattern.Off },
			// Speaker and controller microphone off; the mic button can not switch it back on (it may still mute a PC microphone).
			Speaker = s.Speaker with { Enabled = false },
			Mic = s.Mic with { Enabled = false },
			// The controller turns itself off sooner when it is left alone.
			IdlePowerOffMinutes = idle,
			PowerOffWhenIdle = true
		};
	}
}

// "Switch to Low Power below 20 % battery": turns Low Power on once per discharge. When the user turns it off again it
// stays off until the battery was back above the threshold.
public sealed class LowPowerAutoSwitch
{
	public const double ThresholdPercent = 20;

	private bool _armed = true;

	// Called on every battery reading (percent null: unknown). True: turn Low Power on now.
	public bool Update(bool enabled, double? percent, bool charging, bool lowPowerOn)
	{
		if (percent is not { } p)
		{
			return false;
		}
		if (p > ThresholdPercent)
		{
			_armed = true;
			return false;
		}
		if (p >= ThresholdPercent || charging || !enabled || !_armed)
		{
			return false;
		}
		_armed = false;
		return !lowPowerOn;
	}
}
