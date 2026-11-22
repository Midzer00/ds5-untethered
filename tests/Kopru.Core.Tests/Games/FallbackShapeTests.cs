// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Profiles;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

// The fallback trigger effect's shape: where it starts on the trigger's travel, the weapon's end, the vibration's
// frequency. The default shape gives exactly the bytes the app always sent.
public class FallbackShapeTests
{
	// The effects as they were hard-coded before the shape existed: zone 2, the weapon to zone 6, 30 Hz.
	private static byte[] Before(FallbackTrigger trigger, double strength)
	{
		int step = Math.Clamp((int)Math.Round(8 * strength), 1, 8);
		return trigger switch
		{
			FallbackTrigger.Feedback => TriggerEffects.Feedback(2, step),
			FallbackTrigger.Weapon => TriggerEffects.Weapon(2, 6, step),
			FallbackTrigger.Vibration => TriggerEffects.Vibration(2, step, 30),
			_ => TriggerEffects.Off()
		};
	}

	[Fact]
	public void DefaultShape_GivesByteIdenticalEffects_AtEveryStrength()
	{
		foreach (FallbackTrigger trigger in Enum.GetValues<FallbackTrigger>())
		{
			for (int percent = 0; percent <= 100; percent += 5)
			{
				byte[] before = Before(trigger, percent / 100.0);
				Assert.Equal(before, GameProfiles.Effect(trigger, percent / 100.0, FallbackShape.Default));
				Assert.Equal(before, GameProfiles.Effect(trigger, percent / 100.0, new FallbackShape()));
				Assert.Equal(before, GameProfiles.Effect(trigger, percent / 100.0, null));
				Assert.Equal(before, GameProfiles.Effect(trigger, percent / 100.0));
				Assert.Equal(before, new ControllerSettings(FallbackTrigger: trigger, FallbackStrength: percent).Game.TriggerEffect ?? TriggerEffects.Off());
			}
		}
		// And as literal bytes at the default strength (50 %: step 4) and full strength.
		Assert.Equal([0x21, 0xFC, 0x03, 0xC0, 0xB6, 0x6D, 0x1B, 0, 0, 0, 0], GameProfiles.Effect(FallbackTrigger.Feedback, 0.5, FallbackShape.Default));
		Assert.Equal([0x25, 0x44, 0x00, 0x03, 0, 0, 0, 0, 0, 0, 0], GameProfiles.Effect(FallbackTrigger.Weapon, 0.5, FallbackShape.Default));
		Assert.Equal([0x26, 0xFC, 0x03, 0xC0, 0xFF, 0xFF, 0x3F, 0, 0, 30, 0], GameProfiles.Effect(FallbackTrigger.Vibration, 1, FallbackShape.Default));
	}

	[Fact]
	public void Defaults_AreZone2_WeaponTo6_30Hz()
	{
		FallbackShape d = FallbackShape.Default;
		Assert.Equal((2, 2, 6, 2, 30), (d.FeedbackStart, d.WeaponStart, d.WeaponEnd, d.VibrationStart, d.VibrationFrequency));
		Assert.Same(d, d.Normalized());
	}

	[Fact]
	public void Feedback_StartsAtTheChosenZone()
	{
		// From zone 0 at full strength: every zone, step 8.
		Assert.Equal([0x21, 0xFF, 0x03, 0xFF, 0xFF, 0xFF, 0x3F, 0, 0, 0, 0],
			GameProfiles.Effect(FallbackTrigger.Feedback, 1, new FallbackShape { FeedbackStart = 0 }));
		// From zone 9 at the lowest strength: only the last zone, step 1.
		Assert.Equal([0x21, 0x00, 0x02, 0, 0, 0, 0, 0, 0, 0, 0],
			GameProfiles.Effect(FallbackTrigger.Feedback, 0.1, new FallbackShape { FeedbackStart = 9 }));
		Assert.Equal(TriggerEffects.Feedback(5, 4), GameProfiles.Effect(FallbackTrigger.Feedback, 0.5, new FallbackShape { FeedbackStart = 5 }));
	}

	[Fact]
	public void Weapon_UsesItsStartAndEnd()
	{
		// Zones 5 and 8, strength 75 % (step 6).
		Assert.Equal([0x25, 0x20, 0x01, 0x05, 0, 0, 0, 0, 0, 0, 0],
			GameProfiles.Effect(FallbackTrigger.Weapon, 0.75, new FallbackShape { WeaponStart = 5, WeaponEnd = 8 }));
		Assert.Equal(TriggerEffects.Weapon(7, 8, 8), GameProfiles.Effect(FallbackTrigger.Weapon, 1, new FallbackShape { WeaponStart = 7, WeaponEnd = 8 }));
		// The other effects' fields do not change it.
		Assert.Equal(Before(FallbackTrigger.Weapon, 0.5),
			GameProfiles.Effect(FallbackTrigger.Weapon, 0.5, new FallbackShape { FeedbackStart = 7, VibrationStart = 0, VibrationFrequency = 55 }));
	}

	[Fact]
	public void Vibration_UsesItsStartAndFrequency()
	{
		// Zones 7-9 at step 2 (25 %), 55 Hz.
		Assert.Equal([0x26, 0x80, 0x03, 0x00, 0x00, 0x20, 0x09, 0, 0, 55, 0],
			GameProfiles.Effect(FallbackTrigger.Vibration, 0.25, new FallbackShape { VibrationStart = 7, VibrationFrequency = 55 }));
		Assert.Equal(TriggerEffects.Vibration(0, 8, 5), GameProfiles.Effect(FallbackTrigger.Vibration, 1, new FallbackShape { VibrationStart = 0, VibrationFrequency = 5 }));
	}

	[Fact]
	public void OutOfRange_IsClampedBeforeEncoding()
	{
		FallbackShape wild = new() { FeedbackStart = -3, WeaponStart = 0, WeaponEnd = 1, VibrationStart = 20, VibrationFrequency = 500 };
		FallbackShape n = wild.Normalized();
		Assert.Equal((0, 2, 3, 9, 60), (n.FeedbackStart, n.WeaponStart, n.WeaponEnd, n.VibrationStart, n.VibrationFrequency));
		Assert.Equal(TriggerEffects.Weapon(2, 3, 4), GameProfiles.Effect(FallbackTrigger.Weapon, 0.5, wild));
		Assert.Equal(TriggerEffects.Vibration(9, 4, 60), GameProfiles.Effect(FallbackTrigger.Vibration, 0.5, wild));
		Assert.Equal(5, new FallbackShape { VibrationFrequency = 0 }.Normalized().VibrationFrequency);
		// An end at or before the start moves to just after it.
		Assert.Equal((6, 7), (new FallbackShape { WeaponStart = 6, WeaponEnd = 4 }.Normalized().WeaponStart, new FallbackShape { WeaponStart = 6, WeaponEnd = 4 }.Normalized().WeaponEnd));
	}

	[Fact]
	public void WeaponStartAndEnd_PushEachOther()
	{
		FallbackShape s = FallbackShape.Default;
		// Start up to the end: the end moves up with it.
		s = s.WithWeaponStart(6);
		Assert.Equal((6, 7), (s.WeaponStart, s.WeaponEnd));
		s = s.WithWeaponStart(7);
		Assert.Equal((7, 8), (s.WeaponStart, s.WeaponEnd));
		// Past its range: clamped.
		s = s.WithWeaponStart(9);
		Assert.Equal((7, 8), (s.WeaponStart, s.WeaponEnd));
		// End down to the start: the start moves down with it.
		s = s.WithWeaponEnd(5);
		Assert.Equal((4, 5), (s.WeaponStart, s.WeaponEnd));
		s = s.WithWeaponEnd(1);
		Assert.Equal((2, 3), (s.WeaponStart, s.WeaponEnd));
		// Apart again: the other one stays.
		s = s.WithWeaponEnd(8);
		Assert.Equal((2, 8), (s.WeaponStart, s.WeaponEnd));
		s = s.WithWeaponStart(4);
		Assert.Equal((4, 8), (s.WeaponStart, s.WeaponEnd));
	}

	[Fact]
	public void ProfileValues_RoundTripTheShape_AndOldProfilesReadTheDefault()
	{
		FallbackShape shape = new() { FeedbackStart = 4, WeaponStart = 3, WeaponEnd = 7, VibrationStart = 6, VibrationFrequency = 45 };
		Assert.Equal(FallbackShape.Default, new ProfileValues().FallbackShape);
		ProfileBook book = new();
		ControllerProfile p = book.Create("Shooter", new ProfileValues { FallbackTrigger = FallbackTrigger.Weapon, FallbackShape = shape })!;
		ControllerProfile imported = new ProfileBook().Import(book.Export(p.Id), "file")!;
		Assert.Equal(shape, imported.Values.FallbackShape);

		// The settings file's own serializer (PascalCase) too, and a shape missing some fields.
		string json = JsonSerializer.Serialize(new ProfileValues { FallbackShape = shape });
		Assert.Equal(shape, JsonSerializer.Deserialize<ProfileValues>(json)!.FallbackShape);
		Assert.Equal(new FallbackShape { VibrationFrequency = 50 },
			JsonSerializer.Deserialize<ProfileValues>("""{ "FallbackShape": { "VibrationFrequency": 50 } }""")!.FallbackShape);

		const string old = """{ "format": "untethered-profile", "version": 1, "name": "Old", "values": { "fallbackTrigger": "Vibration" } }""";
		Assert.Equal(FallbackShape.Default, new ProfileBook().Import(old, "file")!.Values.FallbackShape);
		Assert.Equal(FallbackShape.Default, JsonSerializer.Deserialize<ProfileValues>("""{ "RumbleLevel": 80 }""")!.FallbackShape);
	}

	[Fact]
	public void ControllerSettings_CarryTheShapeToTheBridge()
	{
		ControllerSettings s = new(FallbackTrigger: FallbackTrigger.Vibration, FallbackStrength: 100)
		{
			FallbackShape = new FallbackShape { VibrationStart = 5, VibrationFrequency = 40 }
		};
		Assert.Equal(TriggerEffects.Vibration(5, 8, 40), s.Game.TriggerEffect);
		Assert.Equal(TriggerEffects.Vibration(5, 8, 40), GameProfiles.For(s.Game).FallbackTrigger);
		// Off stays off whatever the shape.
		Assert.Null((s with { FallbackTrigger = FallbackTrigger.Off }).Game.TriggerEffect);
	}
}
