// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kopru.App.Controls;

// Front view of the controller (Assets/controller.png, or the dark controller-edge.png for the Edge), scaled to fit.
// The light strips beside the touchpad come from a separate mask (controller-light.png) and are painted live in
// LightColor at LightIntensity; the player LEDs under the touchpad are drawn on top. No logos.
public sealed class ControllerArt : Control
{
	public static readonly StyledProperty<bool> IsEdgeProperty = AvaloniaProperty.Register<ControllerArt, bool>(nameof(IsEdge));
	public static readonly StyledProperty<Color> LightColorProperty =
		AvaloniaProperty.Register<ControllerArt, Color>(nameof(LightColor), Color.FromRgb(0x2F, 0x6B, 0xFF));
	public static readonly StyledProperty<double> LightIntensityProperty =
		AvaloniaProperty.Register<ControllerArt, double>(nameof(LightIntensity), 1.0);
	public static readonly StyledProperty<bool> PlayerLedsProperty = AvaloniaProperty.Register<ControllerArt, bool>(nameof(PlayerLeds), true);

	private static readonly Lazy<Bitmap> Standard = new(() => Load("controller.png"));
	private static readonly Lazy<Bitmap> Edge = new(() => Load("controller-edge.png"));
	private static readonly Lazy<Bitmap> LightMask = new(() => Load("controller-light.png"));

	// Image geometry (pixels of the asset): aspect ratio and where the player LEDs sit, under the touchpad.
	private const double ImageWidth = 2290;
	private const double ImageHeight = 1520;
	private const double LedCentreX = 1144;
	private const double LedY = 542;
	private const double LedSpacing = 34;
	private static readonly Color LightOff = Color.Parse("#3A3E44");

	static ControllerArt()
	{
		AffectsRender<ControllerArt>(IsEdgeProperty, LightColorProperty, LightIntensityProperty, PlayerLedsProperty);
	}

	// The asset is larger than most places it is shown; smooth downscaling keeps the outlines clean.
	public ControllerArt() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

	public bool IsEdge
	{
		get => GetValue(IsEdgeProperty);
		set => SetValue(IsEdgeProperty, value);
	}

	public Color LightColor
	{
		get => GetValue(LightColorProperty);
		set => SetValue(LightColorProperty, value);
	}

	// 0..1, e.g. the brightness setting.
	public double LightIntensity
	{
		get => GetValue(LightIntensityProperty);
		set => SetValue(LightIntensityProperty, value);
	}

	public bool PlayerLeds
	{
		get => GetValue(PlayerLedsProperty);
		set => SetValue(PlayerLedsProperty, value);
	}

	private static Bitmap Load(string name) => new(AssetLoader.Open(new Uri($"avares://Kopru.App/Assets/{name}")));

	protected override Size MeasureOverride(Size availableSize)
	{
		double w = double.IsInfinity(availableSize.Width)
			? (double.IsInfinity(availableSize.Height) ? ImageWidth / 6 : availableSize.Height * ImageWidth / ImageHeight)
			: availableSize.Width;
		double h = w * ImageHeight / ImageWidth;
		if (!double.IsInfinity(availableSize.Height) && h > availableSize.Height)
		{
			h = availableSize.Height;
			w = h * ImageWidth / ImageHeight;
		}
		return new Size(w, h);
	}

	public override void Render(DrawingContext context)
	{
		double scale = Math.Min(Bounds.Width / ImageWidth, Bounds.Height / ImageHeight);
		if (scale <= 0)
		{
			return;
		}
		var rect = new Rect((Bounds.Width - ImageWidth * scale) / 2, (Bounds.Height - ImageHeight * scale) / 2, ImageWidth * scale, ImageHeight * scale);
		context.DrawImage(IsEdge ? Edge.Value : Standard.Value, rect);

		// Light strips: the chosen colour through the mask, dimmed by brightness. No bloom.
		double intensity = Math.Clamp(LightIntensity, 0, 1);
		Color lit = Lerp(LightOff, LightColor, 0.25 + 0.75 * intensity);
		using (context.PushOpacityMask(new ImageBrush(LightMask.Value) { Stretch = Stretch.Fill }, rect))
		{
			context.FillRectangle(new SolidColorBrush(lit), rect);
		}

		// Player LEDs (the middle one lit when enabled).
		for (int i = 0; i < 5; i++)
		{
			bool on = PlayerLeds && i == 2;
			var led = new Rect(
				rect.X + (LedCentreX + (i - 2) * LedSpacing - 10) * scale,
				rect.Y + (LedY - 4) * scale,
				20 * scale,
				8 * scale);
			context.DrawRectangle(new SolidColorBrush(on ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(60, 255, 255, 255)), null,
				new RoundedRect(led, 4 * scale));
		}
	}

	private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
		(byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
