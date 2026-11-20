// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Lighting;

namespace Kopru.App.ViewModels;

// Light effect settings (Light page). PlayerLeds (the on/off switch elsewhere) and PlayerLedPattern stay in step:
// pattern Off is the switch off, the switch on brings back player 1.
public sealed partial class SettingsState
{
	[ObservableProperty]
	private LightMode _lightMode;

	// Breathing: one cycle in seconds, the low and high points in percent of the brightness.
	[ObservableProperty]
	private double _breathingPeriod = 4;

	[ObservableProperty]
	private double _breathingMin = 10;

	[ObservableProperty]
	private double _breathingMax = 100;

	// Rainbow: 1 (slow) .. 10 (fast).
	[ObservableProperty]
	private double _rainbowSpeed = 3;

	[ObservableProperty]
	private PlayerLedPattern _playerLedPattern = PlayerLedPattern.One;

	[ObservableProperty]
	private LedBrightness _playerLedBrightness = LedBrightness.High;

	public LightOptions LightOptions => new(LightMode, BreathingPeriod, Math.Clamp(BreathingMin / 100, 0, 1), Math.Clamp(BreathingMax / 100, 0, 1),
		RainbowSpeed, PlayerLedPattern, PlayerLedBrightness);

	partial void OnPlayerLedPatternChanged(PlayerLedPattern value) => PlayerLeds = value != PlayerLedPattern.Off;

	partial void OnPlayerLedsChanged(bool value)
	{
		if (!value)
		{
			PlayerLedPattern = PlayerLedPattern.Off;
		}
		else if (PlayerLedPattern == PlayerLedPattern.Off)
		{
			PlayerLedPattern = PlayerLedPattern.One;
		}
	}

	private void LoadLight(AppSettings saved)
	{
		LightMode = Enum.IsDefined(saved.LightMode) ? saved.LightMode : LightMode.Static;
		BreathingPeriod = Math.Clamp(saved.BreathingPeriod, 1, 10);
		BreathingMin = Math.Clamp(saved.BreathingMin, 0, 100);
		BreathingMax = Math.Clamp(saved.BreathingMax, 0, 100);
		RainbowSpeed = Math.Clamp(saved.RainbowSpeed, 1, 10);
		PlayerLedPattern = saved.PlayerLedPattern ?? (saved.PlayerLeds ? PlayerLedPattern.One : PlayerLedPattern.Off);
		PlayerLedBrightness = saved.PlayerLedBrightness;
	}

	private AppSettings WithLight(AppSettings s)
	{
		s.LightMode = LightMode;
		s.BreathingPeriod = BreathingPeriod;
		s.BreathingMin = BreathingMin;
		s.BreathingMax = BreathingMax;
		s.RainbowSpeed = RainbowSpeed;
		s.PlayerLedPattern = PlayerLedPattern;
		s.PlayerLedBrightness = PlayerLedBrightness;
		return s;
	}
}
