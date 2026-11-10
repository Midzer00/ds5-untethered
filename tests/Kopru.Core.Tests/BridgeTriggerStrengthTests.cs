// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

public partial class BridgeTests
{
	private static (byte[] Right, byte[] Left) SentTriggers(FakeLink link)
	{
		byte[] sent = Block(link.Writes.Last(w => w[0] == 0x31));
		return (sent.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size).ToArray(),
			sent.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray());
	}

	private static void AssertPair((byte[] Right, byte[] Left) actual, byte[] right, byte[] left)
	{
		Assert.Equal(right, actual.Right);
		Assert.Equal(left, actual.Left);
	}

	private static void AssertSent(FakeLink link, byte[] right, byte[] left) => AssertPair(SentTriggers(link), right, left);

	[Fact]
	public void TriggerStrength_ScalesBothGameTriggers_AndAChangeResendsThemAtOnce()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.5));

		byte[] right = TriggerScaleTests.Zoned(0x21, [0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
		byte[] left = [0x01, 0x40, 200, 0, 0, 0, 0, 0, 0, 0, 0];
		SendOutput(pad, b => OutputBlock.SetTriggers(b, right, left));
		AssertSent(link, TriggerEffects.Scale(right, 0.5), TriggerEffects.Scale(left, 0.5));
		Assert.Equal(new byte[] { 0x01, 0x40, 100, 0, 0, 0, 0, 0, 0, 0, 0 },SentTriggers(link).Left);

		// The setting changes while the game holds the effect: sent again without a new game report.
		int writes = link.Writes.Count;
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.75));
		Assert.True(link.Writes.Count > writes);
		AssertSent(link, TriggerEffects.Scale(right, 0.75), TriggerEffects.Scale(left, 0.75));

		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 1));
		AssertSent(link, right, left); // 100 %: exactly the game's bytes

		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0));
		AssertSent(link, TriggerEffects.Off(), TriggerEffects.Off());

		// The game's next (repeated) report is let through and scaled the same way.
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.5));
		SendOutput(pad, b => OutputBlock.SetTriggers(b, right, left));
		AssertSent(link, TriggerEffects.Scale(right, 0.5), TriggerEffects.Scale(left, 0.5));
		Assert.Equal(0, bridge.UnknownTriggerEffects);
	}

	[Fact]
	public void TriggerStrength_KeepsEachTriggersLastEffect_AcrossReportsThatSetOnlyOne()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Weapon(2, 6, 8), TriggerEffects.Feedback(1, 8)));
		SendOutput(pad, b => OutputBlock.SetLightbar(b, 1, 2, 3)); // no trigger flags
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.5));
		AssertSent(link, TriggerEffects.Weapon(2, 6, 4), TriggerEffects.Feedback(1, 4));
		AssertPair(bridge.ControllerTriggers(), TriggerEffects.Weapon(2, 6, 4), TriggerEffects.Feedback(1, 4));
	}

	[Fact]
	public void TriggerStrength_UnknownMode_PassesThroughUnscaled_AndIsCounted()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.5));

		byte[] unknown = [0x7E, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
		SendOutput(pad, b => OutputBlock.SetTriggers(b, unknown, TriggerEffects.Feedback(0, 8)));
		AssertSent(link, unknown, TriggerEffects.Feedback(0, 4));
		Assert.Equal(1, bridge.UnknownTriggerEffects);
	}

	[Fact]
	public void TriggerStrength_NotAppliedInUnsupportedMode_FallbackAsBefore()
	{
		// Unsupported mode runs at intensity 1 with the fallback effect: game effects (if any) pass unchanged.
		BridgeProfile profile = Games.GameProfiles.For(Games.GameMode.Unsupported, new Games.SupportedProfile(TriggerIntensity: 0.5),
			new Games.UnsupportedProfile(Trigger: Games.FallbackTrigger.Feedback, FallbackStrength: 0.5));
		Assert.Equal(1, profile.TriggerIntensity);

		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.ApplyProfile(profile);
		AssertSent(link, TriggerEffects.Feedback(2, 4), TriggerEffects.Feedback(2, 4));
		byte[] weapon = TriggerEffects.Weapon(2, 6, 8);
		SendOutput(pad, b => OutputBlock.SetTriggers(b, weapon, TriggerEffects.Off()));
		AssertSent(link, weapon, TriggerEffects.Feedback(2, 4));
	}
}
