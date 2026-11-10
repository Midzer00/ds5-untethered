// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;

namespace Kopru.Core.Lighting;

// What drives the light bar. Game: the user's colour until a supported game sets its own light (the game always wins
// while the supported games profile lets the game's light through).
public enum LightMode
{
	Static,
	Breathing,
	Rainbow,
	Battery,
	Game
}

// The player LEDs under the touchpad: the console's player 1-4 patterns, or all five lit (player 5 lights all five, so
// it is the same as All).
public enum PlayerLedPattern
{
	Off,
	One,
	Two,
	Three,
	Four,
	All
}

public enum LedBrightness
{
	Low,
	Medium,
	High
}

// FollowMic is reserved for the microphone feature (treated as Off until then).
public enum MuteLedMode
{
	Off,
	On,
	FollowMic
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
	public static Rgb From(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

	public uint Value => (uint)(R << 16 | G << 8 | B);

	public Rgb Scale(double k)
	{
		k = Math.Clamp(k, 0, 1);
		return new((byte)Math.Round(R * k), (byte)Math.Round(G * k), (byte)Math.Round(B * k));
	}
}

// Effect options beside the colour and brightness. BreathingPeriod in seconds, BreathingMin/Max 0..1 of the brightness,
// RainbowSpeed 1..10 (one hue cycle takes RainbowCycleSeconds / speed).
public sealed record LightOptions(
	LightMode Mode = LightMode.Static,
	double BreathingPeriod = 4,
	double BreathingMin = 0.1,
	double BreathingMax = 1,
	double RainbowSpeed = 3,
	PlayerLedPattern PlayerPattern = PlayerLedPattern.One,
	LedBrightness PlayerBrightness = LedBrightness.High,
	MuteLedMode MuteLed = MuteLedMode.Off)
{
	public const double RainbowCycleSeconds = 30;

	public static LightOptions Default { get; } = new();
}

// One state of every LED, as written to the output block.
public readonly record struct LedFrame(Rgb Lightbar, byte PlayerMask, LedBrightness PlayerBrightness, bool MuteLed)
{
	public void WriteTo(byte[] block) => WriteTo(block.AsSpan());

	public void WriteTo(Span<byte> block)
	{
		OutputBlock.SetLightbar(block, Lightbar.R, Lightbar.G, Lightbar.B);
		OutputBlock.SetPlayerLeds(block, PlayerMask, PlayerBrightness switch
		{
			LedBrightness.Low => OutputBlock.LedBrightnessLow,
			LedBrightness.Medium => OutputBlock.LedBrightnessMedium,
			_ => OutputBlock.LedBrightnessHigh
		});
		OutputBlock.SetMuteLed(block, MuteLed ? OutputBlock.MuteLedOn : OutputBlock.MuteLedOff);
	}
}

// The light at a moment: pure functions of the settings, the time since the effect started and the battery.
public static class LightAnimator
{
	// Battery colours: green from GoodPercent, amber from LowPercent, red below; red pulses below CriticalPercent while
	// not charging.
	public const int GoodPercent = 50;
	public const int LowPercent = 20;
	public const int CriticalPercent = 10;
	public const double CriticalPulseSeconds = 1.2;
	public static readonly Rgb BatteryGood = new(0x20, 0xE0, 0x40);
	public static readonly Rgb BatteryAmber = new(0xFF, 0x9A, 0x00);
	public static readonly Rgb BatteryRed = new(0xFF, 0x10, 0x10);

	// Effects that change over time (the sender and the preview only tick for these).
	public static bool IsAnimated(LightSettings light, BatteryStatus? battery) => light.Options.Mode switch
	{
		LightMode.Breathing or LightMode.Rainbow => true,
		LightMode.Battery => battery is { } b && b.Percent < CriticalPercent && b.State == ChargeState.Discharging,
		_ => false
	};

	public static Rgb ColourAt(LightSettings light, TimeSpan t, BatteryStatus? battery = null)
	{
		LightOptions o = light.Options;
		Rgb colour = new(light.Red, light.Green, light.Blue);
		double brightness = Math.Clamp(light.Brightness, 0, 1);
		double seconds = Math.Max(0, t.TotalSeconds);
		switch (o.Mode)
		{
			case LightMode.Breathing:
			{
				double period = Math.Max(0.5, o.BreathingPeriod);
				double lo = Math.Clamp(Math.Min(o.BreathingMin, o.BreathingMax), 0, 1);
				double hi = Math.Clamp(Math.Max(o.BreathingMin, o.BreathingMax), 0, 1);
				// Starts at the low point, peaks half way through the period.
				double wave = (1 - Math.Cos(2 * Math.PI * (seconds % period) / period)) / 2;
				return colour.Scale(brightness * (lo + (hi - lo) * wave));
			}
			case LightMode.Rainbow:
			{
				double cycle = LightOptions.RainbowCycleSeconds / Math.Clamp(o.RainbowSpeed, 1, 10);
				return Hue(seconds % cycle / cycle).Scale(brightness);
			}
			case LightMode.Battery when battery is { } b:
			{
				Rgb c = BatteryColour(b.Percent);
				if (b.Percent < CriticalPercent && b.State == ChargeState.Discharging)
				{
					double wave = (1 + Math.Cos(2 * Math.PI * (seconds % CriticalPulseSeconds) / CriticalPulseSeconds)) / 2;
					return c.Scale(brightness * (0.15 + 0.85 * wave));
				}
				return c.Scale(brightness);
			}
			default:
				return colour.Scale(brightness);
		}
	}

	public static Rgb BatteryColour(int percent) =>
		percent >= GoodPercent ? BatteryGood : percent >= LowPercent ? BatteryAmber : BatteryRed;

	public static LedFrame FrameAt(LightSettings light, TimeSpan t, BatteryStatus? battery = null) =>
		new(ColourAt(light, t, battery), light.PlayerMask, light.Options.PlayerBrightness, light.Options.MuteLed == MuteLedMode.On);

	public static byte PatternMask(PlayerLedPattern pattern) => pattern switch
	{
		PlayerLedPattern.One => 0x04,
		PlayerLedPattern.Two => 0x0A,
		PlayerLedPattern.Three => 0x15,
		PlayerLedPattern.Four => 0x1B,
		PlayerLedPattern.All => 0x1F,
		_ => 0
	};

	// Fully saturated hue, 0..1 around the colour wheel.
	public static Rgb Hue(double h)
	{
		h = (h % 1 + 1) % 1 * 6;
		int sector = (int)Math.Floor(h) % 6;
		double f = h - Math.Floor(h);
		byte up = (byte)Math.Round(255 * f), down = (byte)Math.Round(255 * (1 - f));
		return sector switch
		{
			0 => new(255, up, 0),
			1 => new(down, 255, 0),
			2 => new(0, 255, up),
			3 => new(0, down, 255),
			4 => new(up, 0, 255),
			_ => new(255, 0, down)
		};
	}
}
