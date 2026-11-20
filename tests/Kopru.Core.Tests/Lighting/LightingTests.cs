// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

public class LightingTests
{
	private static LightSettings Light(LightOptions options, uint rgb = 0x2F6BFF, double brightness = 1) =>
		LightSettings.FromRgb(rgb, brightness) with { Options = options };

	[Fact]
	public void Static_IsTheColourScaledByBrightness()
	{
		LightSettings light = Light(LightOptions.Default, 0xFF8000, 0.5);
		Assert.Equal(new Rgb(128, 64, 0), LightAnimator.ColourAt(light, TimeSpan.Zero));
		Assert.Equal(new Rgb(128, 64, 0), LightAnimator.ColourAt(light, TimeSpan.FromSeconds(7.3)));
		Assert.False(LightAnimator.IsAnimated(light, null));
	}

	[Fact]
	public void Breathing_GoesFromMinToMaxAndBack()
	{
		LightSettings light = Light(new LightOptions(LightMode.Breathing, BreathingPeriod: 4, BreathingMin: 0.2, BreathingMax: 1), 0xFF0000);
		Assert.Equal(new Rgb(51, 0, 0), LightAnimator.ColourAt(light, TimeSpan.Zero));
		Assert.Equal(new Rgb(255, 0, 0), LightAnimator.ColourAt(light, TimeSpan.FromSeconds(2)));
		Assert.Equal(new Rgb(153, 0, 0), LightAnimator.ColourAt(light, TimeSpan.FromSeconds(1)));
		Assert.Equal(LightAnimator.ColourAt(light, TimeSpan.FromSeconds(1)), LightAnimator.ColourAt(light, TimeSpan.FromSeconds(5)));
		Assert.True(LightAnimator.IsAnimated(light, null));
	}

	[Fact]
	public void Rainbow_CyclesTheHue_FasterWithSpeed()
	{
		LightSettings slow = Light(new LightOptions(LightMode.Rainbow, RainbowSpeed: 1));
		double cycle = LightOptions.RainbowCycleSeconds;
		Assert.Equal(new Rgb(255, 0, 0), LightAnimator.ColourAt(slow, TimeSpan.Zero));
		Assert.Equal(new Rgb(0, 255, 0), LightAnimator.ColourAt(slow, TimeSpan.FromSeconds(cycle / 3)));
		Assert.Equal(new Rgb(0, 0, 255), LightAnimator.ColourAt(slow, TimeSpan.FromSeconds(cycle * 2 / 3)));
		Assert.Equal(new Rgb(255, 0, 0), LightAnimator.ColourAt(slow, TimeSpan.FromSeconds(cycle)));
		LightSettings fast = Light(new LightOptions(LightMode.Rainbow, RainbowSpeed: 3));
		Assert.Equal(new Rgb(0, 255, 0), LightAnimator.ColourAt(fast, TimeSpan.FromSeconds(cycle / 9)));
		Assert.Equal(new Rgb(128, 0, 0), LightAnimator.ColourAt(fast with { Brightness = 0.5 }, TimeSpan.Zero));
	}

	[Theory]
	[InlineData(100, 0x20E040)]
	[InlineData(50, 0x20E040)]
	[InlineData(40, 0xFF9A00)]
	[InlineData(20, 0xFF9A00)]
	[InlineData(10, 0xFF1010)]
	[InlineData(0, 0xFF1010)]
	public void Battery_Thresholds(int percent, uint expected)
	{
		Assert.Equal(expected, LightAnimator.BatteryColour(percent).Value);
		LightSettings light = Light(new LightOptions(LightMode.Battery));
		Assert.Equal(expected, LightAnimator.ColourAt(light, TimeSpan.Zero, new BatteryStatus(percent, ChargeState.Discharging)).Value);
	}

	[Fact]
	public void Battery_PulsesRedUnderTenPercent_OnlyWhileDischarging()
	{
		LightSettings light = Light(new LightOptions(LightMode.Battery));
		BatteryStatus critical = new(5, ChargeState.Discharging);
		Assert.True(LightAnimator.IsAnimated(light, critical));
		Assert.Equal(LightAnimator.BatteryRed, LightAnimator.ColourAt(light, TimeSpan.Zero, critical));
		Rgb dim = LightAnimator.ColourAt(light, TimeSpan.FromSeconds(LightAnimator.CriticalPulseSeconds / 2), critical);
		Assert.True(dim.R < 60, $"dim {dim}");
		BatteryStatus charging = new(5, ChargeState.Charging);
		Assert.False(LightAnimator.IsAnimated(light, charging));
		Assert.Equal(LightAnimator.BatteryRed, LightAnimator.ColourAt(light, TimeSpan.FromSeconds(0.6), charging));
		Assert.False(LightAnimator.IsAnimated(light, new BatteryStatus(10, ChargeState.Discharging)));
		// Unknown battery: the user's colour.
		Assert.Equal(new Rgb(0x2F, 0x6B, 0xFF), LightAnimator.ColourAt(light, TimeSpan.Zero, null));
	}

	[Fact]
	public void PlayerPatterns_AreTheConsolePatterns()
	{
		Assert.Equal(0, LightAnimator.PatternMask(PlayerLedPattern.Off));
		Assert.Equal(0x04, LightAnimator.PatternMask(PlayerLedPattern.One));
		Assert.Equal(0x0A, LightAnimator.PatternMask(PlayerLedPattern.Two));
		Assert.Equal(0x15, LightAnimator.PatternMask(PlayerLedPattern.Three));
		Assert.Equal(0x1B, LightAnimator.PatternMask(PlayerLedPattern.Four));
		Assert.Equal(0x1F, LightAnimator.PatternMask(PlayerLedPattern.All));
		Assert.Equal(0, (LightSettings.FromRgb(0, 1, playerLeds: false) with { Options = new(PlayerPattern: PlayerLedPattern.All) }).PlayerMask);
	}

	[Theory]
	[InlineData(LedBrightness.High, 0)]
	[InlineData(LedBrightness.Medium, 1)]
	[InlineData(LedBrightness.Low, 2)]
	public void Frame_WritesLedBytesAndFlags_Only(LedBrightness level, byte expected)
	{
		byte[] block = new byte[OutputBlock.Size];
		block[OutputBlock.RightTrigger] = 0x21;
		block[OutputBlock.MuteLed] = 0x7E;
		new LedFrame(new Rgb(1, 2, 3), 0x15, level).WriteTo(block);
		Assert.Equal(1, block[44]);
		Assert.Equal(2, block[45]);
		Assert.Equal(3, block[46]);
		Assert.Equal(0x15, block[43]);
		Assert.Equal(expected, block[42]);
		// The mute LED is not part of the light (BridgeHost.SyncMuteLed): byte and valid bit untouched.
		Assert.Equal(0x7E, block[OutputBlock.MuteLed]);
		Assert.Equal(OutputBlock.LightbarControl | OutputBlock.PlayerLedControl, block[OutputBlock.Flag1]);
		Assert.Equal(OutputBlock.LedBrightnessControl, block[OutputBlock.Flag2]);
		// No rumble, haptics-select or trigger bits; trigger bytes left alone.
		Assert.Equal(0, block[OutputBlock.Flag0]);
		Assert.Equal(0, block[OutputBlock.MotorLeft]);
		Assert.Equal(0, block[OutputBlock.MotorRight]);
		Assert.Equal(0x21, block[OutputBlock.RightTrigger]);
		new LedFrame(new Rgb(1, 2, 3), 0, level).WriteTo(block);
		Assert.Equal(0, block[OutputBlock.PlayerLeds]);
	}

	[Fact]
	public void LightSettings_WriteTo_KeepsTheOldPlayerOneDefault()
	{
		byte[] block = new byte[OutputBlock.Size];
		LightSettings.FromRgb(0xFF8000, 0.5).WriteTo(block);
		Assert.Equal(LightSettings.PlayerOneLed, block[OutputBlock.PlayerLeds]);
		Assert.Equal(OutputBlock.LedBrightnessHigh, block[OutputBlock.LedBrightness]);
		Assert.Equal(0, block[OutputBlock.Flag1] & OutputBlock.MuteLedControl);
	}

	private sealed class Recorder
	{
		public List<byte[]> Blocks { get; } = [];

		public void Write(Action<byte[]> change)
		{
			byte[] b = new byte[OutputBlock.Size];
			change(b);
			Blocks.Add(b);
		}
	}

	[Fact]
	public void Sender_Static_WritesOnce()
	{
		ManualTime time = new();
		Recorder r = new();
		using LightSender sender = new(r.Write, () => Light(LightOptions.Default), () => null, () => false, time);
		Assert.True(sender.Tick());
		for (int i = 0; i < 100; i++)
		{
			time.Advance(TimeSpan.FromMilliseconds(33));
			Assert.False(sender.Tick());
		}
		Assert.Single(r.Blocks);
	}

	[Fact]
	public void Sender_Animated_IsRateLimited_ToAbout30Hz()
	{
		ManualTime time = new();
		Recorder r = new();
		using LightSender sender = new(r.Write, () => Light(new LightOptions(LightMode.Rainbow, RainbowSpeed: 10)), () => null, () => false, time);
		Assert.True(sender.Tick());
		Assert.False(sender.Tick());
		time.Advance(TimeSpan.FromMilliseconds(10));
		Assert.False(sender.Tick());
		// Ticks every millisecond for one second: no more than 1000 / MinGap writes.
		for (int i = 0; i < 1000; i++)
		{
			time.Advance(TimeSpan.FromMilliseconds(1));
			sender.Tick();
		}
		Assert.InRange(r.Blocks.Count, 30, 35);
		Assert.Equal(r.Blocks.Count, sender.Sent);
	}

	[Fact]
	public void Sender_Hold_WritesNothing_ThenSendsTheFullStateAgain()
	{
		ManualTime time = new();
		Recorder r = new();
		using LightSender sender = new(r.Write, () => Light(new LightOptions(LightMode.Rainbow, RainbowSpeed: 10)), () => null, () => false, time);
		Assert.True(sender.Tick());
		sender.Hold(TimeSpan.FromSeconds(1.5));
		for (int i = 0; i < 40; i++)
		{
			time.Advance(TimeSpan.FromMilliseconds(33));
			Assert.False(sender.Tick());
		}
		time.Advance(TimeSpan.FromMilliseconds(300));
		Assert.True(sender.Tick());
		Assert.Equal(2, r.Blocks.Count);
	}

	[Fact]
	public void Sender_GameWins_ThenSendsAgain()
	{
		ManualTime time = new();
		Recorder r = new();
		bool game = true;
		using LightSender sender = new(r.Write, () => Light(LightOptions.Default), () => null, () => game, time);
		Assert.False(sender.Tick());
		time.Advance(TimeSpan.FromSeconds(1));
		Assert.False(sender.Tick());
		Assert.Empty(r.Blocks);
		game = false;
		Assert.True(sender.Tick());
		game = true;
		time.Advance(TimeSpan.FromSeconds(1));
		Assert.False(sender.Tick());
		game = false;
		// The same static frame is written again once the game lets go.
		Assert.True(sender.Tick());
		Assert.Equal(2, r.Blocks.Count);
	}

	[Fact]
	public void Sender_Invalidate_SendsTheChangeAtOnce()
	{
		ManualTime time = new();
		Recorder r = new();
		uint colour = 0xFF0000;
		using LightSender sender = new(r.Write, () => Light(LightOptions.Default, colour), () => null, () => false, time);
		sender.Tick();
		colour = 0x00FF00;
		sender.Invalidate();
		Assert.Equal(2, r.Blocks.Count);
		Assert.Equal(0xFF, r.Blocks[1][OutputBlock.Red + 1]);
	}

	[Fact]
	public void Sender_ThroughTheSession_KeepsHapticsModeAndSendsNoRumble()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		session.UpdateOutput(b => OutputBlock.SetTriggers(b, TriggerEffects.Feedback(1, 4), TriggerEffects.Feedback(1, 4)));
		int before = link.Writes.Count;
		ManualTime time = new();
		using LightSender sender = new(session.UpdateOutput,
			() => Light(new LightOptions(LightMode.Static, PlayerPattern: PlayerLedPattern.All, PlayerBrightness: LedBrightness.Low), 0x102030),
			() => null, () => false, time);
		Assert.True(sender.Tick());
		Assert.Equal(before + 1, link.Writes.Count);
		byte[] block = link.Writes.Last().AsSpan(3, OutputBlock.Size).ToArray();
		Assert.Equal(0, block[OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect));
		Assert.Equal(0, block[OutputBlock.Flag2] & OutputBlock.ImprovedRumble);
		Assert.Equal(0, block[OutputBlock.MotorLeft] | block[OutputBlock.MotorRight]);
		byte haptics = OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl;
		Assert.Equal(haptics, block[OutputBlock.Flag1] & haptics);
		Assert.NotEqual(0, block[OutputBlock.Flag0] & OutputBlock.RightTriggerEffect);
		Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, block.AsSpan(OutputBlock.Red, 3).ToArray());
		Assert.Equal(0x1F, block[OutputBlock.PlayerLeds]);
		Assert.Equal(OutputBlock.LedBrightnessLow, block[OutputBlock.LedBrightness]);
		Assert.Equal(0, block[OutputBlock.Flag1] & OutputBlock.MuteLedControl);
	}
}
