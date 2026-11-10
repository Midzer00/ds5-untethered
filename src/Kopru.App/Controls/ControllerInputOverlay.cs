// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kopru.Core.Input;

namespace Kopru.App.Controls;

// Drawn over a ControllerArt of the same size (same uniform fit): live input on the controller's own shapes. A pressed
// control fills in the accent with a crisp edge at once and fades out quickly on release; triggers fill with their
// pressure; stick caps move in their wells and point where they are pushed; touchpad fingers show as dots. Shapes and
// positions come from ControllerLayout (the art's pixels). Frames are requested from the render loop only while a fade
// runs; an idle or hidden overlay costs nothing.
public sealed class ControllerInputOverlay : Control
{
	public static readonly StyledProperty<PadButtons> PressedProperty = AvaloniaProperty.Register<ControllerInputOverlay, PadButtons>(nameof(Pressed));
	public static readonly StyledProperty<TouchPoint> Touch1Property = AvaloniaProperty.Register<ControllerInputOverlay, TouchPoint>(nameof(Touch1));
	public static readonly StyledProperty<TouchPoint> Touch2Property = AvaloniaProperty.Register<ControllerInputOverlay, TouchPoint>(nameof(Touch2));
	public static readonly StyledProperty<bool> IsEdgeProperty = AvaloniaProperty.Register<ControllerInputOverlay, bool>(nameof(IsEdge));
	public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<ControllerInputOverlay, IBrush?>(nameof(Accent));
	// Stick deflection -1..1 (Y down, as reported); 0 = at rest.
	public static readonly StyledProperty<double> LeftStickXProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(LeftStickX));
	public static readonly StyledProperty<double> LeftStickYProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(LeftStickY));
	public static readonly StyledProperty<double> RightStickXProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(RightStickX));
	public static readonly StyledProperty<double> RightStickYProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(RightStickY));
	// Trigger pressure 0..100 %. Without it (0), a set L2/R2 button bit shows the trigger full.
	public static readonly StyledProperty<double> L2LevelProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(L2Level));
	public static readonly StyledProperty<double> R2LevelProperty = AvaloniaProperty.Register<ControllerInputOverlay, double>(nameof(R2Level));

	// Screen sizes (device-independent pixels), independent of how large the controller is drawn.
	private const double EdgeWidth = 1.5;
	private const double TouchpadEdgeWidth = 2.5;
	private const double TouchDotRadius = 7;
	private const double PointerWidth = 2.5;
	private const double PointerDotRadius = 3.5;

	private static readonly IBrush Well = new SolidColorBrush(ControllerLayout.WellColor);
	private static readonly IBrush Cap = new SolidColorBrush(ControllerLayout.CapColor);
	private static readonly IBrush EdgeWell = new SolidColorBrush(ControllerLayout.EdgeWellColor);
	private static readonly IBrush EdgeCap = new SolidColorBrush(ControllerLayout.EdgeCapColor);
	private static readonly Color DefaultAccent = Color.Parse("#3B82F6");

	private readonly InputAnimator _animator = new();
	private readonly FrameRequester _frames;

	// Drawing resources, rebuilt only when the accent or the drawing scale changes.
	private Color _accent;
	private double _penScale = double.NaN;
	private IBrush _fill = Brushes.Transparent;
	private IBrush _tint = Brushes.Transparent;
	private IPen _edge = new Pen();
	private IPen _padEdge = new Pen();
	private IPen _fullEdge = new Pen();
	private IPen _travelEdge = new Pen();
	private IPen _pointer = new Pen();
	private IPen _touchRing = new Pen();

	static ControllerInputOverlay()
	{
		AffectsRender<ControllerInputOverlay>(PressedProperty, Touch1Property, Touch2Property, IsEdgeProperty, AccentProperty,
			LeftStickXProperty, LeftStickYProperty, RightStickXProperty, RightStickYProperty, L2LevelProperty, R2LevelProperty);
	}

	public ControllerInputOverlay()
	{
		_frames = new FrameRequester(this);
	}

	public PadButtons Pressed { get => GetValue(PressedProperty); set => SetValue(PressedProperty, value); }
	public TouchPoint Touch1 { get => GetValue(Touch1Property); set => SetValue(Touch1Property, value); }
	public TouchPoint Touch2 { get => GetValue(Touch2Property); set => SetValue(Touch2Property, value); }
	public bool IsEdge { get => GetValue(IsEdgeProperty); set => SetValue(IsEdgeProperty, value); }
	public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
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
		EnsureResources(scale);
		TimeSpan now = Clock();
		PadButtons pressed = Pressed;

		using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy)))
		{
			DrawStick(context, ControllerLayout.LeftStick, LeftStickX, LeftStickY, now, scale);
			DrawStick(context, ControllerLayout.RightStick, RightStickX, RightStickY, now, scale);

			double pad = _animator.Level(PadButtons.Touchpad, now);
			if (pad > 0)
			{
				using (context.PushOpacity(pad))
				{
					context.DrawGeometry(_tint, _padEdge, ControllerArt.PadGeometry);
				}
			}

			bool edge = IsEdge;
			foreach (ControllerRegion region in ControllerLayout.Regions)
			{
				if (region.IsEdgeOnly && !edge)
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
								context.DrawGeometry(_fill, _edge, region.Geometry);
							}
						}
						break;
					case RegionKind.Trigger:
						double percent = region.Button == PadButtons.L2 ? L2Level : R2Level;
						DrawTrigger(context, region, InputAnimator.TriggerFill(percent, (pressed & region.Button) != 0));
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
				using (context.PushOpacity(level))
				{
					context.DrawEllipse(_fill, _touchRing, at, TouchDotRadius / scale, TouchDotRadius / scale);
				}
			}
		}

		if (_animator.IsAnimating(now))
		{
			_frames.Request();
		}
	}

	// The trigger's face fills from its low end up as it is pulled; fully pulled adds a light edge.
	private void DrawTrigger(DrawingContext context, ControllerRegion region, double fill)
	{
		if (fill <= 0)
		{
			return;
		}
		Rect b = region.Bounds;
		bool full = fill >= 0.995;
		if (!full)
		{
			double cut = b.Bottom - fill * b.Height;
			using (context.PushClip(new Rect(b.X - 4, cut, b.Width + 8, b.Bottom - cut + 4)))
			{
				context.DrawGeometry(_fill, null, region.Geometry);
			}
			context.DrawGeometry(null, _travelEdge, region.Geometry);
		}
		else
		{
			context.DrawGeometry(_fill, _fullEdge, region.Geometry);
		}
	}

	// At rest the art's own cap shows untouched. Pushed: the cap is redrawn moved in its well, with a pointer from its
	// centre towards where it is pushed (longer the further). L3/R3: the cap fills in the accent.
	private void DrawStick(DrawingContext context, StickWell stick, double x, double y, TimeSpan now, double scale)
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
		if (moved)
		{
			context.DrawEllipse(IsEdge ? EdgeWell : Well, null, stick.Centre, stick.WellRadius, stick.WellRadius);
			context.DrawEllipse(IsEdge ? EdgeCap : Cap, null, cap, stick.CapRadius, stick.CapRadius);
		}
		if (click > 0)
		{
			using (context.PushOpacity(click))
			{
				context.DrawEllipse(_fill, _edge, cap, stick.CapRadius, stick.CapRadius);
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
		// Pens are used inside the art-pixel transform: widths are divided by the scale to stay fixed on screen.
		var darker = new SolidColorBrush(Color.FromRgb((byte)(accent.R * 0.72), (byte)(accent.G * 0.72), (byte)(accent.B * 0.72)));
		var solid = new SolidColorBrush(accent);
		_fill = solid;
		_tint = new SolidColorBrush(Color.FromArgb(56, accent.R, accent.G, accent.B));
		_edge = new Pen(darker, EdgeWidth / scale, lineJoin: PenLineJoin.Round);
		_padEdge = new Pen(solid, TouchpadEdgeWidth / scale, lineJoin: PenLineJoin.Round);
		_fullEdge = new Pen(Brushes.White, EdgeWidth / scale, lineJoin: PenLineJoin.Round);
		_travelEdge = new Pen(solid, 1 / scale, lineJoin: PenLineJoin.Round);
		_pointer = new Pen(solid, PointerWidth / scale, lineCap: PenLineCap.Round);
		_touchRing = new Pen(Brushes.White, EdgeWidth / scale, lineCap: PenLineCap.Round);
	}
}
