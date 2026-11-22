// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Lighting;

namespace Kopru.Core.Tests;

// The custom light bar colour: hex codes in and out, hue and saturation at full value.
public class LightColourTests
{
	[Theory]
	[InlineData(0x000000u, "#000000")]
	[InlineData(0x2F6BFFu, "#2F6BFF")]
	[InlineData(0xFFFFFFu, "#FFFFFF")]
	[InlineData(0xFF00ABCDu, "#00ABCD")]
	public void ToHex_IsHashAndSixUpperCaseDigits(uint rgb, string hex) => Assert.Equal(hex, LightColour.ToHex(rgb));

	[Theory]
	[InlineData("#2F6BFF", 0x2F6BFFu)]
	[InlineData("2f6bff", 0x2F6BFFu)]
	[InlineData("  #aBcDeF ", 0xABCDEFu)]
	[InlineData("#000000", 0u)]
	public void TryParseHex_TakesSixDigits_WithOrWithoutHash(string text, uint rgb)
	{
		Assert.True(LightColour.TryParseHex(text, out uint parsed));
		Assert.Equal(rgb, parsed);
		Assert.Equal(rgb, LightColour.TryParseHex(LightColour.ToHex(rgb), out uint again) ? again : 1u);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("#")]
	[InlineData("#FFF")]
	[InlineData("#12345")]
	[InlineData("#1234567")]
	[InlineData("#GG0000")]
	[InlineData("##123456")]
	[InlineData("12 345")]
	[InlineData("-12345")]
	public void TryParseHex_RefusesAnythingElse(string? text) => Assert.False(LightColour.TryParseHex(text, out _));

	[Theory]
	[InlineData(0, 100, 0xFF0000u)]
	[InlineData(60, 100, 0xFFFF00u)]
	[InlineData(120, 100, 0x00FF00u)]
	[InlineData(180, 100, 0x00FFFFu)]
	[InlineData(240, 100, 0x0000FFu)]
	[InlineData(300, 100, 0xFF00FFu)]
	[InlineData(359, 100, 0xFF0004u)]
	[InlineData(360, 100, 0xFF0000u)]
	[InlineData(200, 0, 0xFFFFFFu)]
	[InlineData(60, 50, 0xFFFF80u)]
	[InlineData(200, 80, 0x33BBFFu)]
	[InlineData(30, 100, 0xFF8000u)]
	public void FromHueSaturation_IsAtFullValue(double hue, double saturation, uint rgb) =>
		Assert.Equal(rgb, LightColour.FromHueSaturation(hue, saturation));

	[Fact]
	public void FromHueSaturation_ClampsAndWraps()
	{
		Assert.Equal(0xFF0000u, LightColour.FromHueSaturation(0, 150));
		Assert.Equal(0xFFFFFFu, LightColour.FromHueSaturation(90, -5));
		Assert.Equal(LightColour.FromHueSaturation(300, 100), LightColour.FromHueSaturation(-60, 100));
		Assert.Equal(0xFFFFFFu, LightColour.FromHueSaturation(double.NaN, double.NaN));
	}

	[Theory]
	[InlineData(0xFF0000u, 0, 100)]
	[InlineData(0x00FF00u, 120, 100)]
	[InlineData(0x0000FFu, 240, 100)]
	[InlineData(0xFF00FFu, 300, 100)]
	[InlineData(0xFFFFFFu, 0, 0)]
	[InlineData(0x808080u, 0, 0)]
	[InlineData(0x000000u, 0, 0)]
	[InlineData(0xFFFF80u, 60, 49.8)]
	[InlineData(0x7F0000u, 0, 100)]
	public void ToHueSaturation_LeavesTheValueOut(uint rgb, double hue, double saturation)
	{
		(double h, double s) = LightColour.ToHueSaturation(rgb);
		Assert.Equal(hue, h, 1);
		Assert.Equal(saturation, s, 1);
	}

	[Fact]
	public void HueSaturation_RoundTrip_AtFullValue()
	{
		for (int hue = 0; hue < 360; hue += 7)
		{
			for (int saturation = 0; saturation <= 100; saturation += 5)
			{
				uint rgb = LightColour.FromHueSaturation(hue, saturation);
				(double h, double s) = LightColour.ToHueSaturation(rgb);
				Assert.Equal(rgb, LightColour.FromHueSaturation(h, s));
			}
		}
	}

	// The colour disc: hue around it (0 at the top, clockwise, y down), saturation from the centre to the rim.
	[Theory]
	[InlineData(0, -1, 0, 100)]
	[InlineData(1, 0, 90, 100)]
	[InlineData(0, 1, 180, 100)]
	[InlineData(-1, 0, 270, 100)]
	[InlineData(0.5, 0, 90, 50)]
	[InlineData(0, -0.25, 0, 25)]
	[InlineData(3, 0, 90, 100)]
	[InlineData(-2, -2, 315, 100)]
	public void FromDisc_HueAroundSaturationOut_ClampedToTheRim(double x, double y, double hue, double saturation)
	{
		(double h, double s) = LightColour.FromDisc(x, y);
		Assert.Equal(hue, h, 6);
		Assert.Equal(saturation, s, 6);
	}

	[Fact]
	public void Disc_CentreIsWhite_AndPointsRoundTrip()
	{
		Assert.Equal((0.0, 0.0), LightColour.FromDisc(0, 0));
		Assert.Equal((0.0, 0.0), LightColour.FromDisc(double.NaN, 1));
		Assert.Equal(0xFFFFFFu, LightColour.FromHueSaturation(0, LightColour.FromDisc(0, 0).Saturation));
		for (int hue = 0; hue < 360; hue += 13)
		{
			for (int saturation = 1; saturation <= 100; saturation += 11)
			{
				(double x, double y) = LightColour.ToDisc(hue, saturation);
				Assert.Equal(saturation / 100.0, Math.Sqrt(x * x + y * y), 9);
				(double h, double s) = LightColour.FromDisc(x, y);
				Assert.Equal(hue, h, 6);
				Assert.Equal(saturation, s, 6);
			}
		}
		// Saturation past 100 % stays on the rim.
		(double rx, double ry) = LightColour.ToDisc(45, 250);
		Assert.Equal(1, Math.Sqrt(rx * rx + ry * ry), 9);
	}
}