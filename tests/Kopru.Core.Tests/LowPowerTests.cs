// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Lighting;

namespace Kopru.Core.Tests;

public class LowPowerTests
{
	private static readonly ControllerSettings User = new ControllerSettings(GameHapticsLevel: 150, RumbleLevel: 120, TriggerStrength: 100,
		Brightness: 80, PlayerLeds: true, IdlePowerOffMinutes: 15, LightFromGame: true)
	{
		LightOptions = new LightOptions(LightMode.Breathing, PlayerPattern: PlayerLedPattern.Two),
		Speaker = new SpeakerSettings(true, 0.7, 0.5),
		Mic = new MicSettings(true, 2, false, true),
		PowerOffWhenIdle = false
	};

	[Fact]
	public void Off_ReturnsTheSettingsUnchanged()
	{
		Assert.Same(User, LowPower.Apply(User, false));
	}

	[Fact]
	public void On_ReducesHapticsTriggersLightSoundAndIdleTime()
	{
		ControllerSettings s = LowPower.Apply(User, true);
		Assert.Equal(QuickSteps.Haptics[0], s.GameHapticsLevel);
		Assert.Equal(QuickSteps.Haptics[0], s.RumbleLevel);
		Assert.Equal(QuickSteps.Trigger[0], s.TriggerStrength);
		Assert.Equal(0, s.Brightness);
		Assert.False(s.PlayerLeds);
		Assert.False(s.LightFromGame);
		Assert.False(s.Supported.LightFromGame);
		Assert.Equal(0, s.Light.PlayerMask);
		Assert.Equal(new Rgb(0, 0, 0), LightAnimator.ColourAt(s.Light, TimeSpan.FromSeconds(2)));
		Assert.False(s.Speaker.Enabled);
		Assert.Equal(0.7, s.Speaker.SpeakerVolume);
		Assert.False(s.Mic.Enabled);
		Assert.Equal(2, s.Mic.Gain);
		Assert.Equal(LowPower.IdleMinutes, s.IdlePowerOffMinutes);
		Assert.True(s.PowerOffWhenIdle);
		// Everything else is the user's.
		Assert.Equal(User.LightbarColour, s.LightbarColour);
		Assert.Equal(User.Tuning, s.Tuning);
		Assert.Equal(User.FallbackTrigger, s.FallbackTrigger);
	}

	[Fact]
	public void On_KeepsLowerValuesOfTheUser()
	{
		ControllerSettings quiet = User with { GameHapticsLevel = 40, RumbleLevel = 20, TriggerStrength = 10, IdlePowerOffMinutes = 3 };
		ControllerSettings s = LowPower.Apply(quiet, true);
		Assert.Equal(40, s.GameHapticsLevel);
		Assert.Equal(20, s.RumbleLevel);
		Assert.Equal(10, s.TriggerStrength);
		Assert.Equal(3, s.IdlePowerOffMinutes);
	}

	[Fact]
	public void On_TurnsAnIdleTimeOfNeverIntoTheShortOne()
	{
		Assert.Equal(LowPower.IdleMinutes, LowPower.Apply(User with { IdlePowerOffMinutes = 0 }, true).IdlePowerOffMinutes);
	}

	[Fact]
	public void On_ThenOff_GivesTheUsersValuesBack()
	{
		ControllerSettings on = LowPower.Apply(User, true);
		Assert.NotEqual(User, on);
		Assert.Equal(User, LowPower.Apply(User, false));
	}

	[Theory]
	[InlineData(75, 0)]
	[InlineData(100, 1)]
	[InlineData(150, 2)]
	[InlineData(150.3, 2)]
	[InlineData(130, -1)]
	public void QuickSteps_FindTheStepOfAValue(double value, int index)
	{
		Assert.Equal(index, QuickSteps.IndexOf(QuickSteps.Haptics, value));
	}

	[Fact]
	public void QuickSteps_AreInsideTheSettingsRanges()
	{
		Assert.All(QuickSteps.Haptics, v => Assert.InRange(v, 0, 300));
		Assert.All(QuickSteps.Trigger, v => Assert.InRange(v, 0, 100));
		Assert.All(QuickSteps.Light, v => Assert.InRange(v, 0, 100));
		Assert.Contains(150, QuickSteps.Haptics);
		Assert.Equal(0, QuickSteps.Light[0]);
	}

	[Fact]
	public void Auto_TurnsOnOnceWhenTheBatteryDropsBelow20WhileDischarging()
	{
		var auto = new LowPowerAutoSwitch();
		Assert.False(auto.Update(true, 50, false, false));
		Assert.False(auto.Update(true, 20, false, false));
		Assert.True(auto.Update(true, 19, false, false));
		// Still on: nothing more to do.
		Assert.False(auto.Update(true, 15, false, true));
		// The user turned it off: it stays off for this discharge.
		Assert.False(auto.Update(true, 10, false, false));
		Assert.False(auto.Update(true, 5, false, false));
	}

	[Fact]
	public void Auto_ArmsAgainAfterTheBatteryWasBackAbove20()
	{
		var auto = new LowPowerAutoSwitch();
		Assert.True(auto.Update(true, 10, false, false));
		Assert.False(auto.Update(true, 10, true, false));
		Assert.False(auto.Update(true, 20, true, false));
		Assert.False(auto.Update(true, 60, false, false));
		Assert.True(auto.Update(true, 18, false, false));
	}

	[Fact]
	public void Auto_DoesNothingWhenOffChargingOrUnknown()
	{
		var auto = new LowPowerAutoSwitch();
		Assert.False(auto.Update(false, 10, false, false));
		Assert.False(auto.Update(true, 10, true, false));
		Assert.False(auto.Update(true, null, false, false));
		// Turned on while already low: it acts at once (still the first time in this discharge).
		Assert.True(auto.Update(true, 10, false, false));
	}

	[Fact]
	public void Auto_AlreadyOn_CountsAsThisDischargesTurn()
	{
		var auto = new LowPowerAutoSwitch();
		Assert.False(auto.Update(true, 15, false, true));
		Assert.False(auto.Update(true, 12, false, false));
	}

	[Fact]
	public void LowPowerSteps_UseTheProfileTypes()
	{
		// The supported games profile the bridge gets follows the override.
		SupportedProfile p = LowPower.Apply(User, true).Supported;
		Assert.Equal(0.75, p.GameHapticsLevel, 3);
		Assert.Equal(0.5, p.TriggerIntensity, 3);
	}
}
