// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Games;

namespace Kopru.Core.Tests;

public class DualSenseModeTests
{
	private static ControllerSettings User(bool speaker) => new ControllerSettings(GameHapticsLevel: 120, TriggerStrength: 60, LightFromGame: false)
	{
		Speaker = new SpeakerSettings(speaker, 0.7, 0.5),
		Mic = new MicSettings(false, 2, false, true)
	};

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Off_ReturnsTheUsersSettingsUnchanged(bool speaker)
	{
		ControllerSettings user = User(speaker);
		Assert.Same(user, DualSenseMode.Apply(user, false));
		Assert.Same(user, DualSenseMode.Apply(user, false, new DualSenseModeChoices(SpeakerOff: true)));
	}

	[Fact]
	public void On_TurnsTheSpeakerOn_AndKeepsEverythingElseTheUsers()
	{
		ControllerSettings user = User(false);
		ControllerSettings s = DualSenseMode.Apply(user, true);
		Assert.True(s.Speaker.Enabled);
		Assert.Equal(0.7, s.Speaker.SpeakerVolume);
		Assert.Equal(0.5, s.Speaker.HeadphoneVolume);
		// Strengths, the microphone and the user's light choice are not part of the set.
		Assert.Equal(user with { Speaker = user.Speaker with { Enabled = true } }, s);
		Assert.False(s.Mic.Enabled);
		Assert.Equal(120, s.GameHapticsLevel);
		Assert.Equal(60, s.TriggerStrength);
	}

	[Fact]
	public void On_WithTheSpeakerAlreadyOn_ReturnsTheSameSettings()
	{
		ControllerSettings user = User(true);
		Assert.Same(user, DualSenseMode.Apply(user, true));
	}

	[Fact]
	public void On_SpeakerTurnedOffByHand_StaysOff()
	{
		Assert.False(DualSenseMode.Apply(User(true), true, new DualSenseModeChoices(SpeakerOff: true)).Speaker.Enabled);
		Assert.False(DualSenseMode.Apply(User(false), true, new DualSenseModeChoices(SpeakerOff: true)).Speaker.Enabled);
	}

	// Every combination: Low Power wins, then the hand-made choice while the mode is on, then the mode, then the user's switch.
	[Theory]
	[MemberData(nameof(AllCombinations))]
	public void Effective_Precedence(bool userSpeaker, bool modeOn, bool speakerOff, bool lowPower)
	{
		bool expected = !lowPower && (modeOn ? !speakerOff : userSpeaker);
		DualSenseModeChoices choices = new(speakerOff);
		ControllerSettings s = DualSenseMode.Effective(User(userSpeaker), modeOn, choices, lowPower);
		Assert.Equal(expected, s.Speaker.Enabled);
		Assert.False(s.Mic.Enabled);
		Assert.Equal(DualSenseMode.SpeakerShown(userSpeaker, modeOn, choices) && !lowPower, s.Speaker.Enabled);
		if (lowPower)
		{
			// Low Power still dims the rest on top of the mode.
			Assert.Equal(LowPower.Apply(DualSenseMode.Apply(User(userSpeaker), modeOn, choices), true), s);
			Assert.False(s.LightFromGame);
		}
	}

	public static TheoryData<bool, bool, bool, bool> AllCombinations()
	{
		TheoryData<bool, bool, bool, bool> data = [];
		foreach (bool a in new[] { false, true })
		{
			foreach (bool b in new[] { false, true })
			{
				foreach (bool c in new[] { false, true })
				{
					foreach (bool d in new[] { false, true })
					{
						data.Add(a, b, c, d);
					}
				}
			}
		}
		return data;
	}

	[Fact]
	public void TurningTheModeOff_GivesTheUsersOwnSpeakerBack()
	{
		ControllerSettings user = User(false);
		Assert.True(DualSenseMode.Effective(user, true, null, false).Speaker.Enabled);
		Assert.Equal(user, DualSenseMode.Effective(user, false, null, false));
	}

	[Fact]
	public void TheGamesHapticsTriggersAndLight_FollowTheModeThroughTheProfile()
	{
		ControllerSettings user = new(GameHapticsLevel: 150, TriggerStrength: 80, LightFromGame: true, FallbackTrigger: FallbackTrigger.Weapon);
		BridgeProfile on = GameProfiles.For(GameMode.Supported, user.Supported, user.Unsupported);
		BridgeProfile off = GameProfiles.For(GameMode.Unsupported, user.Supported, user.Unsupported);
		Assert.Equal(1.5, on.GameHapticsLevel);
		Assert.Equal(0.8, on.TriggerIntensity, 3);
		Assert.True(on.LightFromGame);
		Assert.Null(on.FallbackTrigger);
		Assert.False(off.LightFromGame);
		Assert.NotNull(off.FallbackTrigger);
	}
}

public class BridgeAutoStartTests
{
	[Fact]
	public void StartsOnce_WhenEnabledReadyAndConnected()
	{
		BridgeAutoStart auto = new();
		Assert.True(auto.Update(true, true, true, false));
		// The start is under way (not running yet): no second start.
		Assert.False(auto.Update(true, true, true, false));
		Assert.False(auto.Update(true, true, true, true));
	}

	[Fact]
	public void WaitsForTheControllerAndForSetup()
	{
		BridgeAutoStart auto = new();
		Assert.False(auto.Update(true, true, false, false));
		Assert.False(auto.Update(true, false, true, false));
		Assert.True(auto.Update(true, true, true, false));
	}

	[Fact]
	public void Disabled_NeverStarts()
	{
		BridgeAutoStart auto = new();
		Assert.False(auto.Update(false, true, true, false));
		Assert.False(auto.Update(false, true, false, false));
		Assert.False(auto.Update(false, true, true, false));
	}

	[Fact]
	public void AStopByHand_IsNotUndone_UntilTheControllerConnectsAgain()
	{
		BridgeAutoStart auto = new();
		Assert.True(auto.Update(true, true, true, false));
		Assert.False(auto.Update(true, true, true, true));
		// Stopped by hand.
		Assert.False(auto.Update(true, true, true, false));
		Assert.False(auto.Update(true, true, true, false));
		// The controller goes away and comes back: started again.
		Assert.False(auto.Update(true, true, false, false));
		Assert.True(auto.Update(true, true, true, false));
	}

	[Fact]
	public void ABridgeStartedByHand_IsNotStartedAgainAfterAStop()
	{
		BridgeAutoStart auto = new();
		Assert.False(auto.Update(true, true, true, true));
		Assert.False(auto.Update(true, true, true, false));
	}

	[Fact]
	public void ReconnectWhileRunning_StartsNothing()
	{
		BridgeAutoStart auto = new();
		Assert.True(auto.Update(true, true, true, false));
		Assert.False(auto.Update(true, true, true, true));
		Assert.False(auto.Update(true, true, false, true));
		Assert.False(auto.Update(true, true, true, true));
	}
}
