// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Lighting;
using Kopru.Core.Profiles;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The one game profile: whatever the game sends passes through, the classic rumble settings and the fallback trigger
// effect fill in only what it does not send.
public partial class BridgeTests
{
	private static void SetRight(byte[] block, byte[] effect)
	{
		effect.CopyTo(block, OutputBlock.RightTrigger);
		block[OutputBlock.Flag0] |= OutputBlock.RightTriggerEffect;
	}

	[Fact]
	public void Fallback_StaysUntilTheGamesFirstRealEffect_ThenNeverComesBackInTheSession()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		BridgeProfile profile = GameProfiles.For(new GameProfile(TriggerIntensity: 1, Trigger: FallbackTrigger.Feedback, FallbackStrength: 0.5), 1);
		byte[] fallback = TriggerEffects.Feedback(2, 4);
		bridge.ApplyProfile(profile);
		AssertSent(link, fallback, fallback);

		// A rumble-only game: the fallback stays.
		SendOutput(pad, b =>
		{
			b[OutputBlock.Flag0] |= OutputBlock.CompatibleVibration;
			b[OutputBlock.MotorLeft] = 100;
		});
		AssertSent(link, fallback, fallback);
		Assert.False(bridge.TriggersSeen);

		// A trigger effect turned off by the game counts as none yet: the fallback goes on that trigger.
		SendOutput(pad, b => SetRight(b, TriggerEffects.Off()));
		AssertSent(link, fallback, fallback);

		// The first real effect: the game's effect, and the other trigger is cleared of the fallback.
		byte[] weapon = TriggerEffects.Weapon(2, 6, 8);
		SendOutput(pad, b => SetRight(b, weapon));
		AssertSent(link, weapon, TriggerEffects.Off());
		Assert.True(bridge.TriggersSeen);

		// The native game switches its triggers off: they stay off.
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off()));
		AssertSent(link, TriggerEffects.Off(), TriggerEffects.Off());

		// A profile change in the same game session (a strength) keeps it so.
		bridge.ApplyProfile(profile with { GameHapticsLevel = 2 });
		AssertPair(bridge.ControllerTriggers(), TriggerEffects.Off(), TriggerEffects.Off());

		// A new game session: the fallback is back for the next game.
		bridge.ApplyProfile(profile with { GameSession = 2 });
		Assert.False(bridge.TriggersSeen);
		AssertPair(bridge.ControllerTriggers(), fallback, fallback);
	}

	[Fact]
	public void TriggersSeen_IsCarriedToTheNextBridge_OnlyInTheSameSession()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge first = new(session, pad) { GameSession = 3 };
		first.FallbackTrigger = TriggerEffects.Feedback(2, 4);
		SendOutput(pad, b => SetRight(b, TriggerEffects.Weapon(2, 6, 8)));
		Assert.True(first.TriggersSeen);
		GameUseState state = first.CaptureGameUse();

		using VirtualDualSense pad2 = Pad();
		using ControllerBridge same = new(session, pad2) { GameSession = 3 };
		same.SeedGameUse(state);
		Assert.True(same.TriggersSeen);

		using VirtualDualSense pad3 = Pad();
		using ControllerBridge other = new(session, pad3) { GameSession = 4 };
		other.SeedGameUse(state);
		Assert.False(other.TriggersSeen);
	}

	[Fact]
	public void TriggerStrength_AppliesWithoutAnyMode()
	{
		BridgeProfile p = GameProfiles.For(new ControllerSettings(TriggerStrength: 40).Game);
		Assert.Equal(0.4, p.TriggerIntensity, 3);
		// No fallback chosen: the bridge leaves the triggers to the game.
		Assert.Null(p.FallbackTrigger);
	}

	// "Use The Game's Light": the game's colour when it sends one, else the user's light; off keeps the user's light.
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void GameLight_FollowsTheOneSwitch(bool useGameLight)
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.ApplyProfile(GameProfiles.For(new GameProfile(LightFromGame: useGameLight)));
		Assert.False(bridge.GameControlsLight);
		SendOutput(pad, b =>
		{
			b[OutputBlock.Flag1] |= OutputBlock.LightbarControl;
			b[OutputBlock.Red] = 0x10;
		});
		Assert.Equal(useGameLight, bridge.GameControlsLight);
		byte[] sent = Block(link.Writes.Last(w => w[0] == 0x31));
		Assert.Equal(useGameLight, (sent[OutputBlock.Flag1] & OutputBlock.LightbarControl) != 0);
	}
}

public class UnifiedProfileTests
{
	[Fact]
	public void ControllerSettings_GiveOneProfile_WithEveryValue()
	{
		ControllerSettings s = new(GameHapticsLevel: 200, RumbleLevel: 70, TriggerStrength: 60, LightFromGame: false,
			RumbleFeel: RumbleFeel.Punchy, FallbackTrigger: FallbackTrigger.Weapon, FallbackStrength: 25);
		Assert.Equal(new GameProfile(2.0, 0.6, false, 0.7, RumbleFeel.Punchy, FallbackTrigger.Weapon, 0.25), s.Game);
		// The "Game" light mode no longer lets the game's light through on its own.
		Assert.False((s with { LightOptions = new LightOptions(LightMode.Game) }).Game.LightFromGame);
	}

	[Fact]
	public void LowPower_TurnsTheGamesLightAndTheSpeakerOff()
	{
		ControllerSettings s = new ControllerSettings(LightFromGame: true) { Speaker = new SpeakerSettings(true) };
		ControllerSettings low = LowPower.Apply(s, true);
		Assert.False(low.Game.LightFromGame);
		Assert.False(low.Speaker.Enabled);
		Assert.Same(s, LowPower.Apply(s, false));
	}

	// Migration of one profile's values from the removed DualSense Mode.
	[Theory]
	// Mode on: the speaker switch becomes the mode's speaker switch, the light stays as it was.
	[InlineData(true, true, LightMode.Static, true, false, LightMode.Static, true, true)]
	[InlineData(true, false, LightMode.Rainbow, false, true, LightMode.Rainbow, false, false)]
	// Mode off: the game's light was blocked, so "use the game's light" goes off; the speaker switch stays.
	[InlineData(false, true, LightMode.Static, true, false, LightMode.Static, false, false)]
	[InlineData(false, false, LightMode.Breathing, true, true, LightMode.Breathing, false, true)]
	// Light mode Game let the game's light through in both: Static with the game's light on.
	[InlineData(true, true, LightMode.Game, false, true, LightMode.Static, true, true)]
	[InlineData(false, true, LightMode.Game, false, true, LightMode.Static, true, true)]
	public void ProfileValues_FromDualSenseMode(bool modeOn, bool modeSpeaker, LightMode mode, bool lightFromGame, bool speaker,
		LightMode expectedMode, bool expectedLight, bool expectedSpeaker)
	{
		ProfileValues before = new() { LightMode = mode, LightFromGame = lightFromGame, SpeakerEnabled = speaker, GameHapticsLevel = 120 };
		ProfileValues after = before.FromDualSenseMode(modeOn, modeSpeaker);
		Assert.Equal((expectedMode, expectedLight, expectedSpeaker), (after.LightMode, after.LightFromGame, after.SpeakerEnabled));
		Assert.Equal(120, after.GameHapticsLevel);
	}

	[Fact]
	public void ProfileValues_Defaults_SpeakerOn()
	{
		Assert.True(new ProfileValues().SpeakerEnabled);
		Assert.True(new ProfileValues().LightFromGame);
	}

	// An exported profile from an older version: the Game light mode reads, and becomes Static with the game's light.
	[Fact]
	public void OldExport_WithTheGameLightMode_ImportsAsStaticWithTheGamesLight()
	{
		const string json = """
			{ "format": "untethered-profile", "version": 1, "name": "Old",
			  "values": { "lightMode": "Game", "lightFromGame": false, "speakerEnabled": false, "gameHapticsLevel": 90 } }
			""";
		ProfileBook book = new();
		ControllerProfile p = book.Import(json, "x")!;
		Assert.Equal((LightMode.Static, true, false, 90.0), (p.Values.LightMode, p.Values.LightFromGame, p.Values.SpeakerEnabled, p.Values.GameHapticsLevel));
	}

	[Fact]
	public void SavedProfiles_WithTheGameLightMode_LoadAsStatic()
	{
		ProfileValues game = new() { LightMode = LightMode.Game, LightFromGame = false };
		ProfileBook book = ProfileBook.Load([new ControllerProfile("default", "Default", game), new ControllerProfile("p2", "Two", game)], null, null,
			new ProfileValues());
		Assert.All(book.Profiles, p => Assert.Equal((LightMode.Static, true), (p.Values.LightMode, p.Values.LightFromGame)));
	}
}
