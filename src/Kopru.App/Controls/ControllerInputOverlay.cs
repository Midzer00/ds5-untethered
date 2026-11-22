// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kopru.Core.Input;

namespace Kopru.App.Controls;

// Drawn over a ControllerArt of the same size (same uniform fit): live input on the controller's own parts. A pressed
// control looks pressed in at once (the art's own pixels of it sunk a little, darker, tinted in the accent with its
// shading kept, a soft accent glow round its edge) and fades back quickly on release; triggers press in from the low end
// with their pressure; stick caps (rendered sprites) move in their wells and point where they are pushed, and sink on
// L3/R3; touchpad fingers show as soft glowing spots. Shapes and positions come from ControllerLayout (the art's pixels).
// Frames are requested from the render loop only while a fade runs; an idle or hidden overlay costs nothing.
// Look is the art's look (PadArt): a pack variant's own pixels and stick sprites are used the same way; on the user's own
// picture nothing is drawn (its parts are not where the shapes are).
public sealed class ControllerInputOverlay : Control
{
	public static readonly StyledProperty<PadButtons> PressedProperty = AvaloniaProperty.Register<ControllerInputOverlay, PadButtons>(nameof(Pressed));
	public static readonly StyledProperty<TouchPoint> Touch1Property = AvaloniaProperty.Register<ControllerInputOverlay, TouchPoint>(nameof(Touch1));
	public static readonly StyledProperty<TouchPoint> Touch2Property = AvaloniaProperty.Register<ControllerInputOverlay, TouchPoint>(nameof(Touch2));
	public static readonly StyledProperty<bool> IsEdgeProperty = AvaloniaProperty.Register<ControllerInputOverlay, bool>(nameof(IsEdge));
	public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<ControllerInputOverlay, IBrush?>(nameof(Accent));
	public static readonly StyledProperty<PadLook?> LookProperty = AvaloniaProperty.Register<ControllerInputOverlay, PadLook?>(nameof(Look));
	// Stick deflection -1..1 (Y down, as reported); 0 = at rest.
	public static readonly StyledProperty<double> LeftStickXProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(LeftStickX));
	public static readonly StyledProperty<double> LeftStickYProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(LeftStickY));
	public static readonly StyledProperty<double> RightStickXProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(RightStickX));
	public static readonly StyledProperty<double> RightStickYProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(RightStickY));
	// Trigger pressure 0..100 %. Without it (0), a set L2/R2 button bit shows the trigger full.
	public static readonly StyledProperty<double> L2LevelProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(L2Level));
	public static readonly StyledProperty<double> R2LevelProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(R2Level));

	// Screen sizes (device-independent pixels), independent of how large the controller is drawn.
	private const double RimWidth = 1.25;
	private const double TouchGlowMin = 9;
	private const double TouchCoreMin = 2.5;
	private const double PointerWidth = 2.5;
	private const double PointerDotRadius = 3.5;
	// Art pixels.
	private const double TouchGlowRadius = 56;
	private const double TouchCoreRadius = 6;
	// How much smaller a clicked stick cap is drawn (pushed in, away from the viewer).
	private const double StickSink = 0.045;

	private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));

	private static readonly Color DefaultAccent = Color.Parse("#3B82F6");

	private readonly InputAnimator _animator = new();
	private readonly FrameRequester _frames;

	// Drawing resources, rebuilt only when the accent or the drawing scale changes.
	private Color _accent;
	private double _penScale = double.NaN;
	private IBrush _fill = Brushes.Transparent;
	private IBrush _tint = Brushes.Transparent;
	private IBrush _triggerTint = Brushes.Transparent;
	private IBrush _padTint = Brushes.Transparent;
	private IBrush _touchGlow = Brushes.Transparent;
	private IPen _rim = new Pen();
	private IPen _glowNear = new Pen();
	private IPen _glowWide = new Pen();
	private IPen _travelEdge = new Pen();
	private IPen _pointer = new Pen();
	private IPen _touchRing = new Pen();

	static ControllerInputOverlay()
	{
		AffectsRender<ControllerInputOverlay>(PressedProperty, Touch1Property, Touch2Property, IsEdgeProperty, AccentProperty, LookProperty,
			LeftStickXProperty, LeftStickYProperty, RightStickXProperty, RightStickYProperty, L2LevelProperty, R2LevelProperty);
	}

	public ControllerInputOverlay()
	{
		_frames = new FrameRequester(this);
		// The art's pixels and the stick sprites are scaled down like the art itself.
		RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
	}

	public PadButtons Pressed { get => GetValue(PressedProperty); set => SetValue(PressedProperty, value); }
	public TouchPoint Touch1 { get => GetValue(Touch1Property); set => SetValue(Touch1Property, value); }
	public TouchPoint Touch2 { get => GetValue(Touch2Property); set => SetValue(Touch2Property, value); }
	public bool IsEdge { get => GetValue(IsEdgeProperty); set => SetValue(IsEdgeProperty, value); }
	public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
	public PadLook? Look { get => GetValue(LookProperty); set => SetValue(LookProperty, value); }
	public double LeftStickX { get => GetValue(LeftStickXProperty); set => SetValue(LeftStickXProperty, value); }
	public double LeftStickY { get => GetValue(LeftStickYProperty); set => SetValue(LeftStickYProperty, value); }
	public double RightStickX { get => GetValue(RightStickXProperty); set => SetValue(RightStickXProperty, value); }
	public double RightStickY { get => GetValue(RightStickYProperty); set => SetValue(RightStickYProperty, value); }
	public double L2Level { get => GetValue(L2LevelProperty); set => SetValue(L2LevelProperty, value); }
	public double R2Level { get => GetValue(R2LevelProperty); set => SetValue(R2LevelProperty, value); }

	// The animation clock (tests use a fake one).
	public Func<TimeSpan> Clock { get; set; } = AnimationClock.Now;

	// Frames requested from the render loop so far (tests: none while idle or hidden).
	public int FrameRequests => _frames.Requests;

	internal InputAnimator Animator => _animator;

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == PressedProperty)
		{
			_animator.SetButtons(change.GetNewValue<PadButtons>(), Clock());
		}
		else if (change.Property == Touch1Property)
		{
			_animator.SetTouch(0, change.GetNewValue<TouchPoint>(), Clock());
		}
		else if (change.Property == Touch2Property)
		{
			_animator.SetTouch(1, change.GetNewValue<TouchPoint>(), Clock());
		}
	}

	public override void Render(DrawingContext context)
	{
		double scale = Math.Min(Bounds.Width / ControllerLayout.ArtWidth, Bounds.Height / ControllerLayout.ArtHeight);
		if (scale <= 0)
		{
			return;
		}
		// Exactly ControllerArt's fit, so the shapes sit on the bitmap.
		double ox = (Bounds.Width - ControllerLayout.ArtWidth * scale) / 2;
		double oy = (Bounds.Height - ControllerLayout.ArtHeight * scale) / 2;
		bool edge = IsEdge;
		PadArtSet look = PadArt.Resolve(Look, edge);
		if (look.IsOwn)
		{
			return;
		}
		EnsureResources(scale);
		TimeSpan now = Clock();
		PadButtons pressed = Pressed;
		Bitmap art = look.Body;

		using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy)))
		{
			DrawStick(context, look, ControllerLayout.LeftStick, LeftStickX, LeftStickY, now, scale);
			DrawStick(context, look, ControllerLayout.RightStick, RightStickX, RightStickY, now, scale);

			double pad = _animator.Level(PadButtons.Touchpad, now);
			if (pad > 0)
			{
				using (context.PushOpacity(pad))
				{
					Geometry padShape = ControllerArt.PadGeometry;
					using (context.PushGeometryClip(padShape))
					{
						context.FillRectangle(_padTint, padShape.Bounds);
					}
					Glow(context, padShape);
				}
			}

			foreach (ControllerRegion region in ControllerLayout.Regions)
			{
				if (!region.ShowsOn(edge))
				{
					continue;
				}
				switch (region.Kind)
				{
					case RegionKind.Button:
					case RegionKind.Bumper:
						double level = _animator.Level(region.Button, now);
						if (level > 0)
						{
							using (context.PushOpacity(level))
							{
								DrawPressed(context, art, region.Geometry, region.Bounds, region.Kind == RegionKind.Bumper ? 2 : 3, _tint);
								Glow(context, region.Geometry);
							}
						}
						break;
					case RegionKind.Trigger:
						double percent = region.Button == PadButtons.L2 ? L2Level : R2Level;
						DrawTrigger(context, art, region, InputAnimator.TriggerFill(percent, (pressed & region.Button) != 0));
						break;
				}
			}

			for (int i = 0; i < 2; i++)
			{
				(double x, double y, double level) = _animator.Touch(i, now);
				if (level <= 0)
				{
					continue;
				}
				Rect surface = ControllerLayout.TouchSurface;
				var at = new Point(surface.X + x * surface.Width, surface.Y + y * surface.Height);
				double glow = Math.Max(TouchGlowRadius, TouchGlowMin / scale);
				double core = Math.Max(TouchCoreRadius, TouchCoreMin / scale);
				using (context.PushOpacity(level))
				{
					context.DrawEllipse(_touchGlow, null, at, glow, glow);
					context.DrawEllipse(Brushes.White, null, at, core, core);
				}
			}
		}

		if (_animator.IsAnimating(now))
		{
			_frames.Request();
		}
	}

	// A part pressed in: the art's own pixels of it, sunk a few pixels and a little darker, tinted in the accent (its shading
	// still shows through). The art may have fewer pixels than the art space (a pack variant: half): its source rectangle is
	// scaled to the bitmap.
	private void DrawPressed(DrawingContext context, Bitmap art, Geometry shape, Rect bounds, double sink, IBrush tint)
	{
		Rect source = bounds.Inflate(sink + 2).Intersect(new Rect(0, 0, ControllerLayout.ArtWidth, ControllerLayout.ArtHeight));
		double density = art.Size.Width / ControllerLayout.ArtWidth;
		using (context.PushGeometryClip(shape))
		{
			context.DrawImage(art, new Rect(source.X * density, source.Y * density, source.Width * density, source.Height * density),
				source.Translate(new Vector(0, sink)));
			context.FillRectangle(Shade, source);
			context.FillRectangle(tint, source);
		}
	}

	// A soft accent glow round a pressed part's edge: two wide faint strokes under a fine one.
	private void Glow(DrawingContext context, Geometry shape)
	{
		context.DrawGeometry(null, _glowWide, shape);
		context.DrawGeometry(null, _glowNear, shape);
		context.DrawGeometry(null, _rim, shape);
	}

	// The trigger pressed in from its low end up as it is pulled (tinted and sunk, its shading kept); fully pulled it glows
	// all round.
	private void DrawTrigger(DrawingContext context, Bitmap art, ControllerRegion region, double fill)
	{
		if (fill <= 0)
		{
			return;
		}
		Rect b = region.Bounds;
		double cut = b.Bottom - fill * b.Height;
		using (context.PushClip(new Rect(b.X - 4, cut, b.Width + 8, b.Bottom - cut + 4)))
		{
			DrawPressed(context, art, region.Geometry, b, 2 * fill, _triggerTint);
		}
		if (fill >= 0.995)
		{
			Glow(context, region.Geometry);
		}
		else
		{
			context.DrawGeometry(null, _travelEdge, region.Geometry);
		}
	}

	// At rest the art's own cap shows untouched. Pushed or clicked: the stick's surroundings without the cap are drawn over
	// it, then the rendered cap (with its shadow) where it is pushed, with a pointer from its centre towards there (longer
	// the further). L3/R3 sinks the cap a little (drawn slightly smaller), darkens and tints it, and rings it in a glow.
	private void DrawStick(DrawingContext context, PadArtSet look, StickWell stick, double x, double y, TimeSpan now, double scale)
	{
		Vector offset = InputAnimator.StickOffset(x, y, stick.Travel);
		double length = offset.Length;
		bool moved = length * scale >= 0.5;
		double click = _animator.Level(stick.Button, now);
		if (!moved && click <= 0)
		{
			return;
		}
		Point cap = stick.Centre + offset;
		double half = ControllerLayout.StickSpriteHalf;
		double sunk = 1 - StickSink * click;
		context.DrawImage(stick.Button == PadButtons.L3 ? look.WellLeft : look.WellRight,
			new Rect(stick.Centre.X - half, stick.Centre.Y - half, 2 * half, 2 * half));
		context.DrawImage(look.Cap, new Rect(cap.X - half * sunk, cap.Y - half * sunk, 2 * half * sunk, 2 * half * sunk));
		double radius = stick.CapRadius * sunk;
		if (click > 0)
		{
			using (context.PushOpacity(click))
			{
				var disc = new EllipseGeometry(new Rect(cap.X - radius, cap.Y - radius, 2 * radius, 2 * radius));
				using (context.PushGeometryClip(disc))
				{
					context.FillRectangle(Shade, disc.Bounds);
					context.FillRectangle(_tint, disc.Bounds);
				}
				Glow(context, disc);
			}
		}
		if (moved)
		{
			Vector unit = offset / length;
			double reach = stick.CapRadius * 0.82 * (length / stick.Travel);
			IBrush dot = click > 0.5 ? Brushes.White : _fill;
			IPen pointer = click > 0.5 ? _touchRing : _pointer;
			context.DrawLine(pointer, cap, cap + unit * reach);
			context.DrawEllipse(dot, null, cap, PointerDotRadius / scale, PointerDotRadius / scale);
			context.DrawEllipse(dot, null, cap + unit * reach, PointerWidth / 2 / scale, PointerWidth / 2 / scale);
		}
	}

	private void EnsureResources(double scale)
	{
		Color accent = (Accent as ISolidColorBrush)?.Color ?? DefaultAccent;
		if (accent == _accent && scale == _penScale)
		{
			return;
		}
		_accent = accent;
		_penScale = scale;
		// Inside the art-pixel transform: glow widths are in art pixels (they scale with the picture); fine lines are divided
		// by the scale to stay crisp on screen, but never thinner than a few art pixels.
		Color Alpha(double a) => Color.FromArgb((byte)Math.Round(255 * a), accent.R, accent.G, accent.B);
		var solid = new SolidColorBrush(accent);
		_fill = solid;
		_tint = new SolidColorBrush(Alpha(0.38));
		_triggerTint = new SolidColorBrush(Alpha(0.45));
		_padTint = new SolidColorBrush(Alpha(0.16));
		_rim = new Pen(new SolidColorBrush(Alpha(0.95)), Math.Max(3, RimWidth / scale), lineJoin: PenLineJoin.Round);
		_glowNear = new Pen(new SolidColorBrush(Alpha(0.30)), 12, lineJoin: PenLineJoin.Round);
		_glowWide = new Pen(new SolidColorBrush(Alpha(0.12)), 28, lineJoin: PenLineJoin.Round);
		_travelEdge = new Pen(new SolidColorBrush(Alpha(0.7)), Math.Max(2, 1 / scale), lineJoin: PenLineJoin.Round);
		_pointer = new Pen(solid, PointerWidth / scale, lineCap: PenLineCap.Round);
		_touchRing = new Pen(Brushes.White, RimWidth / scale, lineCap: PenLineCap.Round);
		_touchGlow = new RadialGradientBrush
		{
			GradientStops = { new GradientStop(Alpha(0.9), 0), new GradientStop(Alpha(0.45), 0.35), new GradientStop(Alpha(0), 1) },
		};
	}
}
