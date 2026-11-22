// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kopru.Core.Lighting;

namespace Kopru.App.Controls;

// The custom light colour's disc: hue around the circle (red at the top, clockwise), saturation from white at the centre to
// the full colour at the rim (LightColour.FromDisc). The one place the app draws a colour gradient: the owner asked for a
// colour disc. The disc is a bitmap made at the screen's pixel size and kept until the size or the scaling changes; a
// round marker (a white ring with a dark edge, filled with the colour) shows the chosen point. A press or a drag anywhere
// on the disc picks the point under it (outside the rim: the rim); the arrow keys nudge it (left/right: hue, up/down:
// saturation).
public sealed class ColourWheel : Control
{
	public const double MarkerRadius = 8;
	public const double HueStep = 5;
	public const double SaturationStep = 5;

	public static readonly StyledProperty<double> HueProperty =
		AvaloniaProperty.Register<ColourWheel, double>(nameof(Hue), defaultBindingMode: BindingMode.TwoWay);
	public static readonly StyledProperty<double> SaturationProperty =
		AvaloniaProperty.Register<ColourWheel, double>(nameof(Saturation), LightColour.MaxSaturation, defaultBindingMode: BindingMode.TwoWay);

	private static readonly IPen MarkerEdge = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 4.5);
	private static readonly IPen MarkerRing = new Pen(Brushes.White, 2.5);

	private WriteableBitmap? _disc;
	private int _discPixels;
	private bool _dragging;

	static ColourWheel()
	{
		AffectsRender<ColourWheel>(HueProperty, SaturationProperty);
		FocusableProperty.OverrideDefaultValue<ColourWheel>(true);
	}

	public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }
	public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }

	// The disc's radius in a control of this size: the marker stays inside the control at the rim.
	public static double DiscRadius(Size size) => Math.Max(0, Math.Min(size.Width, size.Height) / 2 - MarkerRadius);

	public static Point Centre(Size size) => new(size.Width / 2, size.Height / 2);

	// Where the marker sits for a hue and saturation.
	public static Point MarkerPosition(Size size, double hue, double saturation)
	{
		(double x, double y) = LightColour.ToDisc(hue, saturation);
		double r = DiscRadius(size);
		Point c = Centre(size);
		return new Point(c.X + x * r, c.Y + y * r);
	}

	// The hue and saturation at a point of the control (outside the rim: on the rim). The centre has no hue: null then.
	public static (double? Hue, double Saturation) HueSaturationAt(Size size, Point p)
	{
		double r = DiscRadius(size);
		if (r <= 0)
		{
			return (null, 0);
		}
		Point c = Centre(size);
		double x = (p.X - c.X) / r, y = (p.Y - c.Y) / r;
		(double hue, double saturation) = LightColour.FromDisc(x, y);
		return (saturation <= 0 ? null : hue, saturation);
	}

	// True where a press starts picking: on the disc (and the marker's edge around it).
	public static bool IsOnDisc(Size size, Point p)
	{
		Point c = Centre(size);
		return Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y)) <= DiscRadius(size) + MarkerRadius;
	}

	public override void Render(DrawingContext context)
	{
		// Clear behind the whole control so the pointer reaches it anywhere in its box.
		context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
		double r = DiscRadius(Bounds.Size);
		if (r <= 0)
		{
			return;
		}
		double scale = PixelScale();
		int pixels = Math.Max(1, (int)Math.Ceiling(2 * r * scale));
		if (_disc == null || _discPixels != pixels)
		{
			_disc?.Dispose();
			_disc = BuildDisc(pixels, 2 * r);
			_discPixels = pixels;
		}
		Point c = Centre(Bounds.Size);
		context.DrawImage(_disc, new Rect(0, 0, pixels, pixels), new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));

		Point m = MarkerPosition(Bounds.Size, Hue, Saturation);
		uint rgb = LightColour.FromHueSaturation(Hue, Saturation);
		var fill = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
		double ring = MarkerRadius - 2;
		context.DrawEllipse(fill, MarkerEdge, m, ring, ring);
		context.DrawEllipse(null, MarkerRing, m, ring, ring);
	}

	// Device pixels per layout unit here: the screen's scaling times any scale on the way (the pop-up's content scale).
	private double PixelScale()
	{
		TopLevel? top = TopLevel.GetTopLevel(this);
		double transform = top != null && this.TransformToVisual(top) is { } m ? Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12) : 1;
		return (top?.RenderScaling ?? 1) * (transform > 0 ? transform : 1);
	}

	// The disc at `pixels` x `pixels` (premultiplied BGRA), its edge smoothed over one pixel; size: its size in the layout.
	internal static WriteableBitmap BuildDisc(int pixels, double size)
	{
		double dpi = 96 * pixels / Math.Max(1, size);
		var bitmap = new WriteableBitmap(new PixelSize(pixels, pixels), new Vector(dpi, dpi), PixelFormat.Bgra8888, AlphaFormat.Premul);
		double radius = pixels / 2.0;
		int[] row = new int[pixels];
		using ILockedFramebuffer frame = bitmap.Lock();
		for (int j = 0; j < pixels; j++)
		{
			double y = (j + 0.5 - radius) / radius;
			for (int i = 0; i < pixels; i++)
			{
				double x = (i + 0.5 - radius) / radius;
				double d = Math.Sqrt(x * x + y * y);
				double alpha = Math.Clamp((1 - d) * radius + 0.5, 0, 1);
				if (alpha <= 0)
				{
					row[i] = 0;
					continue;
				}
				(double hue, double saturation) = LightColour.FromDisc(x, y);
				uint rgb = LightColour.FromHueSaturation(hue, saturation);
				int a = (int)Math.Round(alpha * 255);
				int r = (int)((rgb >> 16 & 0xFF) * alpha + 0.5), g = (int)((rgb >> 8 & 0xFF) * alpha + 0.5), b = (int)((rgb & 0xFF) * alpha + 0.5);
				row[i] = a << 24 | r << 16 | g << 8 | b;
			}
			Marshal.Copy(row, 0, frame.Address + j * frame.RowBytes, pixels);
		}
		return bitmap;
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		Point p = e.GetPosition(this);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsOnDisc(Bounds.Size, p))
		{
			return;
		}
		_dragging = true;
		e.Pointer.Capture(this);
		Focus();
		PickAt(p);
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (_dragging)
		{
			PickAt(e.GetPosition(this));
			e.Handled = true;
		}
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (_dragging)
		{
			_dragging = false;
			e.Pointer.Capture(null);
			e.Handled = true;
		}
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		_dragging = false;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		switch (e.Key)
		{
			case Key.Left:
				Hue = ((Hue - HueStep) % 360 + 360) % 360;
				break;
			case Key.Right:
				Hue = (Hue + HueStep) % 360;
				break;
			case Key.Up:
				Saturation = Math.Min(LightColour.MaxSaturation, Saturation + SaturationStep);
				break;
			case Key.Down:
				Saturation = Math.Max(0, Saturation - SaturationStep);
				break;
			default:
				return;
		}
		e.Handled = true;
	}

	// The point under the pointer: its hue (the centre keeps the hue it had) and saturation.
	public void PickAt(Point p)
	{
		(double? hue, double saturation) = HueSaturationAt(Bounds.Size, p);
		if (hue is double h)
		{
			Hue = h;
		}
		Saturation = saturation;
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		_disc?.Dispose();
		_disc = null;
		_discPixels = 0;
	}
}
