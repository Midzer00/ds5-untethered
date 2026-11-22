// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Profiles;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// Custom Trigger: per profile and per trigger, an effect over whatever the game sends (Game's Effect: the game's own,
// then the fallback when the game sends none). Its bytes come from the effect, its shape and its own Force; the trigger
// strength does not scale it.
public partial class BridgeTests
{
	private static CustomTrigger Custom(FallbackTrigger effect, FallbackShape? shape = null) => new() { Effect = effect, Shape = shape ?? FallbackShape.Default };

	[Fact]
	public void CustomTrigger_Bytes_ForEachEffect_WithItsForce()
	{
		Assert.Null(CustomTrigger.Default.Bytes());
		Assert.False(CustomTrigger.Default.IsSet);
		Assert.Equal(FallbackShape.DefaultForce, FallbackShape.Default.Force);
		// Resistance (Feedback) from zone 4 at full force: step 8.
		Assert.Equal(TriggerEffects.Feedback(4, 8), Custom(FallbackTrigger.Feedback, new FallbackShape { FeedbackStart = 4, Force = 100 }).Bytes());
		Assert.Equal([0x21, 0xF0, 0x03, 0x00, 0xF0, 0xFF, 0x3F, 0, 0, 0, 0], Custom(FallbackTrigger.Feedback, new FallbackShape { FeedbackStart = 4, Force = 100 }).Bytes());
		// Weapon zones 3 to 7 at 25 % (step 2).
		Assert.Equal([0x25, 0x88, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0], Custom(FallbackTrigger.Weapon, new FallbackShape { WeaponStart = 3, WeaponEnd = 7, Force = 25 }).Bytes());
		// Vibration from zone 6 at 75 % (step 6), 45 Hz.
		Assert.Equal(TriggerEffects.Vibration(6, 6, 45), Custom(FallbackTrigger.Vibration, new FallbackShape { VibrationStart = 6, VibrationFrequency = 45, Force = 75 }).Bytes());
		// Force 0 still plays the smallest step (as Effect Strength does); the default (50 %) is step 4.
		Assert.Equal(TriggerEffects.Feedback(2, 1), Custom(FallbackTrigger.Feedback, new FallbackShape { Force = 0 }).Bytes());
		Assert.Equal(TriggerEffects.Weapon(2, 6, 4), Custom(FallbackTrigger.Weapon).Bytes());
		// Out of range: clamped.
		Assert.Equal(TriggerEffects.Feedback(2, 8), Custom(FallbackTrigger.Feedback, new FallbackShape { Force = 400 }).Bytes());
		Assert.Equal(CustomTrigger.Default, new CustomTrigger { Effect = (FallbackTrigger)77 }.Normalized());
	}

	[Fact]
	public void CustomTrigger_IsPerTrigger_AndStoredPerProfile_OldProfilesReadGamesEffect()
	{
		CustomTrigger left = Custom(FallbackTrigger.Weapon, new FallbackShape { WeaponStart = 4, WeaponEnd = 8, Force = 80 });
		CustomTrigger right = Custom(FallbackTrigger.Vibration, new FallbackShape { VibrationFrequency = 55, Force = 30 });
		ProfileBook book = new();
		ControllerProfile p = book.Create("Shooter", new ProfileValues { LeftCustomTrigger = left, RightCustomTrigger = right })!;
		ControllerProfile imported = new ProfileBook().Import(book.Export(p.Id), "file")!;
		Assert.Equal(left, imported.Values.LeftCustomTrigger);
		Assert.Equal(right, imported.Values.RightCustomTrigger);
		string json = JsonSerializer.Serialize(new ProfileValues { LeftCustomTrigger = left });
		ProfileValues again = JsonSerializer.Deserialize<ProfileValues>(json)!;
		Assert.Equal(left, again.LeftCustomTrigger);
		Assert.Equal(CustomTrigger.Default, again.RightCustomTrigger);

		// Older profiles (and files): Game's Effect on both, the fallback as it was.
		const string old = """{ "format": "untethered-profile", "version": 1, "name": "Old", "values": { "fallbackTrigger": "Weapon", "fallbackStrength": 75 } }""";
		ProfileValues oldValues = new ProfileBook().Import(old, "file")!.Values;
		Assert.Equal((CustomTrigger.Default, CustomTrigger.Default, FallbackTrigger.Weapon), (oldValues.LeftCustomTrigger, oldValues.RightCustomTrigger, oldValues.FallbackTrigger));
		ProfileValues missingForce = JsonSerializer.Deserialize<ProfileValues>("""{ "LeftCustomTrigger": { "Effect": 1 } }""")!;
		Assert.Equal(TriggerEffects.Feedback(2, 4), missingForce.LeftCustomTrigger.Bytes());

		// Through the settings to the bridge's profile.
		BridgeProfile bridge = GameProfiles.For(new ControllerSettings { LeftCustomTrigger = left, RightCustomTrigger = right }.Game);
		Assert.Equal(left.Bytes(), bridge.CustomLeftTrigger);
		Assert.Equal(right.Bytes(), bridge.CustomRightTrigger);
		Assert.True(bridge.SetsTriggers);
		BridgeProfile none = GameProfiles.For(new ControllerSettings().Game);
		Assert.Null(none.CustomLeftTrigger);
		Assert.Null(none.CustomRightTrigger);
		Assert.False(none.SetsTriggers);
		// Equal by content, different by content.
		Assert.True(bridge.SameAs(GameProfiles.For(new ControllerSettings { LeftCustomTrigger = left, RightCustomTrigger = right }.Game)));
		Assert.False(bridge.SameAs(GameProfiles.For(new ControllerSettings { LeftCustomTrigger = left }.Game)));
	}

	[Fact]
	public void CustomTrigger_OverridesTheGamesEffect_OnItsTriggerOnly_EveryReport_AndIsNotScaled()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		byte[] custom = TriggerEffects.Vibration(5, 6, 40);
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.5) { CustomRightTrigger = custom });
		// Nothing from the game yet: the custom effect is on the right trigger at once; the left one stays off.
		AssertSent(link, custom, TriggerEffects.Off());

		// The game sets both triggers, report after report: the right keeps the custom effect (not scaled), the left gets the
		// game's at the trigger strength.
		byte[] gameRight = TriggerEffects.Weapon(2, 6, 8), gameLeft = TriggerEffects.Feedback(1, 8);
		for (int i = 0; i < 3; i++)
		{
			SendOutput(pad, b => OutputBlock.SetTriggers(b, gameRight, gameLeft));
			AssertSent(link, custom, TriggerEffects.Feedback(1, 4));
		}
		// A report without trigger flags still carries it.
		SendOutput(pad, b => OutputBlock.SetLightbar(b, 1, 2, 3));
		Assert.Equal(custom, SentTriggers(link).Right);
		AssertPair(bridge.ControllerTriggers(), custom, TriggerEffects.Feedback(1, 4));

		// Back to Game's Effect: the game's own effect again (at the trigger strength).
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.5));
		AssertSent(link, TriggerEffects.Weapon(2, 6, 4), TriggerEffects.Feedback(1, 4));
	}

	[Fact]
	public void CustomTrigger_Precedence_PerTrigger_CustomThenGameThenFallback()
	{
		// The left trigger has a custom effect, the right one Game's Effect; the profile has a fallback for games that send none.
		byte[] fallback = GameProfiles.Effect(FallbackTrigger.Feedback, 0.5);
		byte[] custom = TriggerEffects.Weapon(3, 7, 6);
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback) { CustomLeftTrigger = custom });
		// The game sends nothing: custom on the left, the fallback on the right.
		AssertSent(link, fallback, custom);

		// The game sets the right trigger: its own effect there; the left keeps the custom one.
		byte[] gameRight = TriggerEffects.Vibration(1, 8, 20);
		SendOutput(pad, b => OutputBlock.SetTriggers(b, gameRight, TriggerEffects.Feedback(0, 8)));
		AssertSent(link, gameRight, custom);

		// The game turns its triggers off: the right is off (the fallback ends with the game's first effect), the left
		// keeps the custom effect.
		SendOutput(pad, b => OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off()));
		AssertSent(link, TriggerEffects.Off(), custom);
	}

	[Fact]
	public void CustomTrigger_StaysOverAModsEffect()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		byte[] custom = TriggerEffects.Feedback(6, 3);
		bridge.ApplyProfile(new BridgeProfile { CustomRightTrigger = custom });
		bridge.SetModOutput(new Kopru.Core.Mods.ModOutput { RightTrigger = TriggerEffects.Weapon(2, 5, 8), LeftTrigger = TriggerEffects.Feedback(0, 8) });
		AssertSent(link, custom, TriggerEffects.Feedback(0, 8));
	}
}
