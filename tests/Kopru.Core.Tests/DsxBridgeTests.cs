// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Mods;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// DSX mod effects on the bridge (ControllerBridgeMods): a mod's trigger effect wins over the game's, the fallback trigger
// and nothing; the game's effects come back when it ends; its light goes over the game's and the user's light; the app's
// mute LED wins over the mod's; its threshold zeroes the trigger below the point.
public partial class BridgeTests
{
	private static byte[] LastWrite(FakeLink link) => Block(link.Writes.Where(w => w[0] == 0x31).Last());

	private static byte[] RightOf(byte[] block) => block.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size).ToArray();

	private static byte[] LeftOf(byte[] block) => block.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray();

	[Fact]
	public void Mod_TriggerEffect_GoesOutAtOnce_AndWinsOverNothing()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		bridge.SetModOutput(new ModOutput { RightTrigger = TriggerEffects.Weapon(2, 6, 8) });

		byte[] sent = LastWrite(link);
		Assert.Equal(TriggerEffects.Weapon(2, 6, 8), RightOf(sent));
		Assert.Equal(TriggerEffects.Off(), LeftOf(sent));
		Assert.NotEqual(0, sent[OutputBlock.Flag0] & OutputBlock.RightTriggerEffect);
		Assert.Equal(TriggerEffects.Weapon(2, 6, 8), bridge.ControllerTriggers().Right);
	}

	[Fact]
	public void Mod_TriggerEffect_WinsOverTheFallback_OnlyOnItsSide()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { FallbackTrigger = TriggerEffects.Feedback(2, 3) };

		bridge.SetModOutput(new ModOutput { LeftTrigger = TriggerEffects.Vibration(0, 8, 30) });
		(byte[] right, byte[] left) = bridge.ControllerTriggers();
		Assert.Equal(TriggerEffects.Feedback(2, 3), right);
		Assert.Equal(TriggerEffects.Vibration(0, 8, 30), left);

		// A game report without trigger effects: the fallback on the right, the mod's on the left.
		SendOutput(pad, b => OutputBlock.SetLightbar(b, 1, 2, 3));
		byte[] sent = LastWrite(link);
		Assert.Equal(TriggerEffects.Feedback(2, 3), RightOf(sent));
		Assert.Equal(TriggerEffects.Vibration(0, 8, 30), LeftOf(sent));

		// The mod's effects end: the fallback on both.
		bridge.SetModOutput(null);
		Assert.Equal(TriggerEffects.Feedback(2, 3), LeftOf(LastWrite(link)));
	}

	[Fact]
	public void Mod_TriggerEffect_WinsOverTheGamesOwn_WhichComesBackWhenItEnds_AndTheConflictIsCounted()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		byte[] game = TriggerEffects.Feedback(1, 4);
		byte[] mod = TriggerEffects.Weapon(3, 7, 6);

		bridge.SetModOutput(new ModOutput { RightTrigger = mod });
		SendOutput(pad, b => OutputBlock.SetTriggers(b, game, game));
		byte[] sent = LastWrite(link);
		Assert.Equal(mod, RightOf(sent));
		Assert.Equal(game, LeftOf(sent));
		Assert.Equal(1, bridge.ModConflicts);
		// The game's effect still counts as the game's (native support, the fallback rule).
		Assert.True(bridge.TriggersSeen);

		bridge.SetModOutput(null);
		sent = LastWrite(link);
		Assert.Equal(game, RightOf(sent));
		Assert.Equal(game, LeftOf(sent));
	}

	[Fact]
	public void Mod_TriggerEffect_FollowsTheTriggerStrength()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { TriggerIntensity = 0.5 };

		bridge.SetModOutput(new ModOutput { RightTrigger = TriggerEffects.Feedback(0, 8) });
		Assert.Equal(TriggerEffects.Feedback(0, 4), RightOf(LastWrite(link)));
	}

	[Fact]
	public void Mod_Light_GoesOverTheGamesLight_AndTheAppsMuteLedStillWins()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		bridge.SetModOutput(new ModOutput { Lightbar = (200, 0, 0), PlayerLeds = 0x04, MicLed = 2 });
		byte[] sent = LastWrite(link);
		Assert.Equal((200, 0, 0), (sent[OutputBlock.Red], sent[OutputBlock.Red + 1], sent[OutputBlock.Red + 2]));
		Assert.Equal(0x04, sent[OutputBlock.PlayerLeds]);
		Assert.Equal(2, sent[OutputBlock.MuteLed]);

		// The game's own light: the mod's goes over it.
		SendOutput(pad, b =>
		{
			OutputBlock.SetLightbar(b, 0, 0, 255);
			OutputBlock.SetPlayerLeds(b, 0x1F, 0);
		});
		sent = LastWrite(link);
		Assert.Equal((200, 0, 0), (sent[OutputBlock.Red], sent[OutputBlock.Red + 1], sent[OutputBlock.Red + 2]));
		Assert.Equal(0x04, sent[OutputBlock.PlayerLeds]);

		// The app shows the mic mute LED itself: it wins over the mod's.
		bridge.AppMuteLed = OutputBlock.MuteLedOn;
		bridge.WriteModOutput();
		Assert.Equal(OutputBlock.MuteLedOn, LastWrite(link)[OutputBlock.MuteLed]);

		// The overlay used for the user's light writes.
		byte[] user = new byte[OutputBlock.Size];
		OutputBlock.SetLightbar(user, 9, 9, 9);
		bridge.OverlayModLight(user);
		Assert.Equal(200, user[OutputBlock.Red]);

		// Ending it reports that the light must be written again.
		Assert.True(bridge.SetModOutput(new ModOutput { RightTrigger = TriggerEffects.Off() }));
		Assert.False(bridge.SetModOutput(null));
	}

	[Fact]
	public void Mod_Light_IsLeftOut_WhileTheUsersLightIsOff_LowPower()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { ModLightAllowed = false };

		bridge.SetModOutput(new ModOutput { Lightbar = (200, 0, 0), PlayerLeds = 0x1F, MicLed = OutputBlock.MuteLedOn, RightTrigger = TriggerEffects.Feedback(0, 8) });
		byte[] sent = LastWrite(link);
		Assert.Equal(0, sent[OutputBlock.Flag1] & (OutputBlock.LightbarControl | OutputBlock.PlayerLedControl));
		Assert.Equal(0, sent[OutputBlock.Red]);
		// The triggers and the mute LED still follow the mod.
		Assert.Equal(TriggerEffects.Feedback(0, 8), RightOf(sent));
		Assert.Equal(OutputBlock.MuteLedOn, sent[OutputBlock.MuteLed]);

		byte[] user = new byte[OutputBlock.Size];
		bridge.OverlayModLight(user);
		Assert.Equal(0, user[OutputBlock.Flag1] & OutputBlock.LightbarControl);

		bridge.ModLightAllowed = true;
		bridge.OverlayModLight(user);
		Assert.Equal(200, user[OutputBlock.Red]);
	}

	[Fact]
	public void Mod_Light_DoesNotCountAsAGameLightWrite()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.SetModOutput(new ModOutput { Lightbar = (200, 0, 0) });

		// A game report without any light: the mod's light goes out with it, but it is not the game's.
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off()));
		Assert.Equal(200, LastWrite(link)[OutputBlock.Red]);
		Assert.Equal(0, bridge.GameLightWrites);
	}

	[Fact]
	public void Mod_MicLed_GoesBackToOff_WhenTheModNoLongerSetsIt()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		bridge.SetModOutput(new ModOutput { MicLed = OutputBlock.MuteLedOn });
		Assert.Equal(OutputBlock.MuteLedOn, LastWrite(link)[OutputBlock.MuteLed]);
		bridge.SetModOutput(null);
		byte[] sent = LastWrite(link);
		Assert.Equal(OutputBlock.MuteLedOff, sent[OutputBlock.MuteLed]);
		Assert.NotEqual(0, sent[OutputBlock.Flag1] & OutputBlock.MuteLedControl);
	}

	[Fact]
	public void Mod_Threshold_ZeroesTheTriggerBelowThePoint()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		session.Start();
		bridge.SetModOutput(new ModOutput { LeftThreshold = 100 });

		byte[] ReadL2(byte l2, byte seq)
		{
			byte[] bt = FakeLink.Input(l2, seq);
			bt[2 + 8] = InputTransformL2Bit;
			BinaryPrimitives.WriteUInt32LittleEndian(bt.AsSpan(74), Crc.Compute(Crc.InputSeed, bt.AsSpan(0, 74)));
			SubmitResult? result = null;
			pad.Submit(Command(seq, UsbIpCodes.DirIn, VirtualDualSense.HidInEndpoint), r => result = r);
			link.Feed(bt);
			WaitUntil(() => result != null);
			return result!.Data;
		}

		byte[] below = ReadL2(60, 3);
		Assert.Equal(0, below[1 + 4]);
		Assert.Equal(0, below[1 + 8] & InputTransformL2Bit);
		byte[] past = ReadL2(160, 4);
		Assert.Equal(160, past[1 + 4]);
		Assert.Equal(InputTransformL2Bit, past[1 + 8] & InputTransformL2Bit);
	}

	private const byte InputTransformL2Bit = Kopru.Core.Input.InputTransform.L2Bit;

	[Fact]
	public void BridgeHost_KeepsTheModOutput_AndTheTargetMapsIndexToSlot()
	{
		BridgeHost one = new(new QueueControllers(), new FakeIdentity(null), new FakeAttacher(new()));
		BridgeHost two = new(new QueueControllers(), new FakeIdentity(null), new FakeAttacher(new()));
		BridgeHostsDsxTarget target = new(i => i switch { 0 => one, 1 => two, _ => null });

		target.SetModOutput(1, new ModOutput { Lightbar = (1, 2, 3) });
		target.SetModOutput(3, new ModOutput { Lightbar = (1, 2, 3) });
		Assert.Null(one.ModOutput);
		Assert.Equal(((byte)1, (byte)2, (byte)3), two.ModOutput!.Lightbar);
		target.SetModOutput(1, ModOutput.Empty);
		Assert.Null(two.ModOutput);
		// No controller runs: nothing for the reply.
		Assert.Empty(target.Devices());
	}

	[Fact]
	public void Target_PlacesAnOutputOnceItsHostAppears()
	{
		BridgeHost one = new(new QueueControllers(), new FakeIdentity(null), new FakeAttacher(new()));
		BridgeHost? two = null;
		BridgeHostsDsxTarget target = new(i => i switch { 0 => one, 1 => two, _ => null });
		target.SetModOutput(1, new ModOutput { Lightbar = (4, 5, 6) });
		two = new(new QueueControllers(), new FakeIdentity(null), new FakeAttacher(new()));
		Assert.Null(two.ModOutput);
		target.Sync();
		Assert.Equal(((byte)4, (byte)5, (byte)6), two.ModOutput!.Lightbar);
		target.SetModOutput(1, null);
		target.Sync();
		Assert.Null(two.ModOutput);
	}
}
