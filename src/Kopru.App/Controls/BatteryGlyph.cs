// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kopru.App.Controls;

// Small horizontal battery: an outline with a terminal nub, filled to Level (0..100).
// Low charge (20 % or less) fills amber, critical (10 % or less) red, charging green; unknown shows the empty outline.
// The thresholds match the low and critical battery notifications.
public sealed class BatteryGlyph : Control
{
	public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<BatteryGlyph, double>(nameof(Level));
	public static readonly StyledProperty<bool> IsChargingProperty = AvaloniaProperty.Register<BatteryGlyph, bool>(nameof(IsCharging));
	public static readonly StyledProperty<bool> IsKnownProperty = AvaloniaProperty.Register<BatteryGlyph, bool>(nameof(IsKnown), true);

	private static readonly IBrush Outline = new SolidColorBrush(Color.Parse("#A3A8B0"));
	private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#D5D9DE"));
	private static readonly IBrush Low = new SolidColorBrush(Color.Parse("#C9A24A"));
	private static readonly IBrush Critical = new SolidColorBrush(Color.Parse("#E5534B"));
	private static readonly IBrush Charging = new SolidColorBrush(Color.Parse("#4FA877"));

	static BatteryGlyph()
	{
		AffectsRender<BatteryGlyph>(LevelProperty, IsChargingProperty, IsKnownProperty);
	}

	public double Level
	{
		get => GetValue(LevelProperty);
		set => SetValue(LevelProperty, value);
	}

	public bool IsCharging
	{
		get => GetValue(IsChargingProperty);
		set => SetValue(IsChargingProperty, value);
	}

	public bool IsKnown
	{
		get => GetValue(IsKnownProperty);
		set => SetValue(IsKnownProperty, value);
	}

	protected override Size MeasureOverride(Size availableSize) => new(22, 11);

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, h = Bounds.Height;
		if (w < 6 || h < 4)
		{
			return;
		}
		double nub = Math.Max(2, w * 0.1);
		var body = new Rect(0.5, 0.5, w - nub - 1, h - 1);
		context.DrawRectangle(null, new Pen(Outline, 1), new RoundedRect(body, 2.5));
		context.DrawRectangle(Outline, null, new RoundedRect(new Rect(body.Right + 0.5, h * 0.3, nub - 0.5, h * 0.4), 1));
		if (!IsKnown)
		{
			return;
		}
		double level = Math.Clamp(Level, 0, 100) / 100;
		var inner = new Rect(body.X + 2, body.Y + 2, Math.Max(0, (body.Width - 4) * level), body.Height - 4);
		IBrush fill = IsCharging ? Charging : level <= 0.10 ? Critical : level <= 0.20 ? Low : Normal;
		context.DrawRectangle(fill, null, new RoundedRect(inner, 1.2));
	}
}
