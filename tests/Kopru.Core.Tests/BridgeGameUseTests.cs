// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// ControllerBridge.GameUseStarted: raised once when the game starts using the controller (a trigger effect, rumble, haptics
// sound), never for weak signs (light, a steady stream of the same report, silent audio) and never for the app's own tests,
// which write to the controller session directly.
public partial class BridgeTests
{
	private static IsoPacket[] AudioPackets() => Enumerable.Range(0, 10).Select(i => new IsoPacket(i * 384, 384, 0, 0)).ToArray();

	private static void SendAudio(VirtualDualSense pad, byte[] audio, uint seq = 7) =>
		pad.Submit(Command(seq, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, audio, AudioPackets()), _ => { });

	[Fact]
	public void GameUse_TriggerEffect_OnceWhenItStarts_AgainAfterOff()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		List<GameUse> uses = [];
		bridge.GameUseStarted += uses.Add;

		// Off effects and the light: nothing.
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off()));
		SendOutput(pad, b => OutputBlock.SetLightbar(b, 1, 2, 3));
		Assert.Empty(uses);

		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Feedback(2, 7), TriggerEffects.Off()));
		// Another effect while one is on (and the same report again): no new start.
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Weapon(2, 6, 8), TriggerEffects.Feedback(1, 3)));
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Weapon(2, 6, 8), TriggerEffects.Feedback(1, 3)));
		Assert.Equal([GameUse.TriggerEffect], uses);

		// Off, then on again: a new start.
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off()));
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Feedback(1, 3)));
		Assert.Equal([GameUse.TriggerEffect, GameUse.TriggerEffect], uses);
	}

	[Fact]
	public void GameUse_Rumble_WhenTheMotorsStart()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		List<GameUse> uses = [];
		bridge.GameUseStarted += uses.Add;

		SendOutput(pad, b => OutputBlock.SetRumble(b, 0, 0));
		Assert.Empty(uses);
		SendOutput(pad, b => OutputBlock.SetRumble(b, 120, 0));
		SendOutput(pad, b => OutputBlock.SetRumble(b, 200, 40));
		Assert.Equal([GameUse.Rumble], uses);
		SendOutput(pad, b => OutputBlock.SetRumble(b, 0, 0));
		SendOutput(pad, b => OutputBlock.SetRumble(b, 0, 90));
		Assert.Equal([GameUse.Rumble, GameUse.Rumble], uses);
	}

	[Fact]
	public void GameUse_Haptics_SoundOnChannels3And4_NotSilenceNorSpeakerChannels()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		List<GameUse> uses = [];
		bridge.GameUseStarted += uses.Add;

		// The audio interface streaming silence (Windows, Steam) and sound on the speaker channels: nothing.
		SendAudio(pad, HapticsConverterTests.Audio(480));
		SendAudio(pad, HapticsConverterTests.Audio(480, ch3: _ => 0.005), 8);
		SendAudio(pad, HapticsConverterTests.Audio(480, ch1: HapticsConverterTests.Tone(100, 0.9)), 9);
		Assert.Empty(uses);

		// Haptics sound: once, not for every packet that follows.
		SendAudio(pad, HapticsConverterTests.Audio(480, ch4: HapticsConverterTests.Tone(100, 0.3)), 10);
		SendAudio(pad, HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.3)), 11);
		Assert.Equal([GameUse.Haptics], uses);
	}

	// The app's own tests (rumble, haptics, trigger "try" buttons) write to the controller session, not through the
	// virtual pad: they never count as a game using the controller.
	[Fact]
	public void GameUse_NeverFromTheAppsOwnTests()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		List<GameUse> uses = [];
		bridge.GameUseStarted += uses.Add;

		session.UpdateOutput(b => OutputBlock.SetTriggers(b, TriggerEffects.Feedback(2, 7), TriggerEffects.Feedback(2, 7)));
		for (int i = 0; i < 96; i++)
		{
			session.Haptics.Push(60, 60);
		}
		session.WakeHaptics();
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: TriggerEffects.Feedback(2, 5)));
		Assert.Empty(uses);
	}

	// A reconnect or pad swap builds a new bridge: seeded with the old bridge's state (as BridgeHost does), a game still
	// holding its trigger effect and its motors does not count as starting again; a new effect after off still does.
	[Fact]
	public void GameUse_Reconnect_WithTheGameHoldingItsEffect_IsNoNewStart()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		GameUseState held;
		using (VirtualDualSense pad = Pad())
		using (ControllerBridge before = new(session, pad))
		{
			SendOutput(pad, b =>
			{
				OutputBlock.SetTriggers(b, TriggerEffects.Feedback(2, 7), TriggerEffects.Off());
				OutputBlock.SetRumble(b, 90, 0);
			});
			Assert.Equal(GameUse.TriggerEffect, before.GameUseNow);
			held = before.CaptureGameUse();
		}
		using VirtualDualSense again = Pad();
		using ControllerBridge after = new(session, again);
		after.SeedGameUse(held);
		List<GameUse> uses = [];
		after.GameUseStarted += uses.Add;
		// The game sends its held state to the new pad.
		SendOutput(again, b =>
		{
			OutputBlock.SetTriggers(b, TriggerEffects.Feedback(2, 7), TriggerEffects.Off());
			OutputBlock.SetRumble(b, 90, 0);
		});
		Assert.Empty(uses);
		SendOutput(again, b => OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off()));
		SendOutput(again, b => OutputBlock.SetTriggers(b, TriggerEffects.Weapon(2, 6, 8), TriggerEffects.Off()));
		Assert.Equal([GameUse.TriggerEffect], uses);
	}

	// Right after the pad is plugged in (QuietGameUse), a game's first reports are no new start (nothing to seed from:
	// the first connection); the level still follows them.
	[Fact]
	public void GameUse_QuietAfterThePadWasPluggedIn()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.QuietGameUse(ControllerBridge.GameUseSettle);
		List<GameUse> uses = [];
		bridge.GameUseStarted += uses.Add;
		SendOutput(pad, b => OutputBlock.SetRumble(b, 120, 0));
		SendAudio(pad, HapticsConverterTests.Audio(480, ch4: HapticsConverterTests.Tone(100, 0.3)));
		Assert.Empty(uses);
		Assert.Equal(GameUse.Rumble, bridge.GameUseNow);
	}

	[Fact]
	public void GameUse_Level_FollowsTheGame()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		Assert.Null(bridge.GameUseNow);
		SendAudio(pad, HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.3)));
		Assert.Equal(GameUse.Haptics, bridge.GameUseNow);
		SendOutput(pad, b => OutputBlock.SetRumble(b, 0, 60));
		Assert.Equal(GameUse.Rumble, bridge.GameUseNow);
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Feedback(1, 3), TriggerEffects.Off()));
		Assert.Equal(GameUse.TriggerEffect, bridge.GameUseNow);
		SendOutput(pad, b =>
		{
			OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off());
			OutputBlock.SetRumble(b, 0, 0);
		});
		// The haptics sound is still within the last 2 s.
		Assert.Equal(GameUse.Haptics, bridge.GameUseNow);
	}

	[Fact]
	public void GameUse_LogNames()
	{
		Assert.Equal("trigger effect", GameUse.TriggerEffect.LogName());
		Assert.Equal("haptics", GameUse.Haptics.LogName());
		Assert.Equal("rumble", GameUse.Rumble.LogName());
	}
}
