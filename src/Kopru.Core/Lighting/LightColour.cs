// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Lighting;

// The custom light bar colour as the Light page edits it: a hex code (#RRGGBB) and hue/saturation at full value, picked on a
// colour disc.
public static class LightColour
{
	public const double MaxHue = 359;
	public const double MaxSaturation = 100;

	// "#RRGGBB" (upper case).
	public static string ToHex(uint rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

	// "#RRGGBB" or "RRGGBB", any case, spaces around ignored; false for anything else.
	public static bool TryParseHex(string? text, out uint rgb)
	{
		rgb = 0;
		string s = (text ?? string.Empty).Trim();
		if (s.StartsWith('#'))
		{
			s = s[1..];
		}
		if (s.Length != 6 || !s.All(Uri.IsHexDigit))
		{
			return false;
		}
		rgb = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		return true;
	}

	// The colour at full value for a hue (degrees, wraps around) and saturation (0-100 %, clamped).
	public static uint FromHueSaturation(double hueDegrees, double saturationPercent)
	{
		double h = double.IsFinite(hueDegrees) ? (hueDegrees % 360 + 360) % 360 : 0;
		double s = Math.Clamp(double.IsFinite(saturationPercent) ? saturationPercent : 0, 0, MaxSaturation) / MaxSaturation;
		double sector = h / 60;
		double x = s * (1 - Math.Abs(sector % 2 - 1));
		double m = 1 - s;
		(double r, double g, double b) = (int)sector switch
		{
			0 => (s, x, 0.0),
			1 => (x, s, 0.0),
			2 => (0.0, s, x),
			3 => (0.0, x, s),
			4 => (x, 0.0, s),
			_ => (s, 0.0, x)
		};
		return (uint)(Channel(r + m) << 16 | Channel(g + m) << 8 | Channel(b + m));
	}

	// The hue (0-360 degrees, 0 for greys) and saturation (0-100 %) of a colour; its value is left out.
	public static (double Hue, double Saturation) ToHueSaturation(uint rgb)
	{
		double r = (rgb >> 16 & 0xFF) / 255.0, g = (rgb >> 8 & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
		double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
		if (delta <= 0)
		{
			return (0, 0);
		}
		double hue = max == r ? 60 * ((g - b) / delta % 6)
			: max == g ? 60 * ((b - r) / delta + 2)
			: 60 * ((r - g) / delta + 4);
		return ((hue + 360) % 360, delta / max * MaxSaturation);
	}

	// The colour disc: hue around the circle (0 at the top, clockwise), saturation from 0 at the centre to 100 at the rim.
	// x and y are relative to the centre in units of the radius (y down); a point outside the disc counts as on its rim.
	public static (double Hue, double Saturation) FromDisc(double x, double y)
	{
		double d = Math.Sqrt(x * x + y * y);
		if (!double.IsFinite(d) || d <= 0)
		{
			return (0, 0);
		}
		double hue = (Math.Atan2(x, -y) * 180 / Math.PI + 360) % 360;
		return (hue, Math.Min(1, d) * MaxSaturation);
	}

	// Where a hue and saturation sit on the disc (relative to the centre in units of the radius, y down).
	public static (double X, double Y) ToDisc(double hueDegrees, double saturationPercent)
	{
		double a = (double.IsFinite(hueDegrees) ? hueDegrees : 0) * Math.PI / 180;
		double r = Math.Clamp(double.IsFinite(saturationPercent) ? saturationPercent : 0, 0, MaxSaturation) / MaxSaturation;
		return (r * Math.Sin(a), -r * Math.Cos(a));
	}

	private static int Channel(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
}
