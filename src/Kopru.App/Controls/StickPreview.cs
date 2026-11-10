// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Kopru.App.Controls;

// Stick position preview: the outer limit circle, a crosshair, the inner deadzone ring and the outer deadzone (dashed).
// Live, the position the game receives is the accent dot with a short fading trail: hollow while the raw stick is inside
// the inner deadzone (the game ignores it), solid outside. The raw stick position is the small grey dot when it differs. Positions are
// -1..1 (Y down, as reported); deadzones are percent. Lines are snapped to device pixels at any display scaling; the
// trail asks the render loop for frames only while it fades.
public sealed class StickPreview : Control
{
	public static readonly StyledProperty<double> RawXProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(RawX));
	public static readonly StyledProperty<double> RawYProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(RawY));
	public static readonly StyledProperty<double> TunedXProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(TunedX));
	public static readonly StyledProperty<double> TunedYProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(TunedY));
	public static readonly StyledProperty<double> InnerDeadzoneProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(InnerDeadzone));
	public static readonly StyledProperty<double> OuterDeadzoneProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(OuterDeadzone));
	public static readonly StyledProperty<bool> IsLiveProperty = AvaloniaProperty.Register<StickPreview, bool>(nameof(IsLive));
	public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<StickPreview, IBrush?>(nameof(Accent));

	public static readonly TimeSpan TrailLength = TimeSpan.FromMilliseconds(180);
	private const int TrailCapacity = 24;
	private const double DotRadius = 5;

	private static readonly IBrush Face = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
	private static readonly IBrush DeadzoneFill = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255));
	private static readonly IBrush Faint = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
	private static readonly IBrush Ring = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
	private static readonly IBrush DeadzoneRing = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
	private static readonly IBrush Dashed = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
	private static readonly IBrush Output = new SolidColorBrush(Color.FromArgb(170, 163, 168, 176));

	private readonly FrameRequester _frames;
	private readonly (double X, double Y, TimeSpan At)[] _trail = new (double, double, TimeSpan)[TrailCapacity];
	private int _trailStart;
	private int _trailCount;

	// Pens, rebuilt only when the display scaling or the accent changes.
	private double _penScaling = double.NaN;
	private IBrush? _penAccent;
	private double _line = 1;
	private IPen _faintPen = new Pen();
	private IPen _ringPen = new Pen();
	private IPen _deadzonePen = new Pen();
	private IPen _dashedPen = new Pen();
	private IPen _trailPen = new Pen();
	private IPen _hollowPen = new Pen();

	static StickPreview()
	{
		AffectsRender<StickPreview>(RawXProperty, RawYProperty, TunedXProperty, TunedYProperty, InnerDeadzoneProperty,
			OuterDeadzoneProperty, IsLiveProperty, AccentProperty);
	}

	public StickPreview() => _frames = new FrameRequester(this);

	public double RawX { get => GetValue(RawXProperty); set => SetValue(RawXProperty, value); }
	public double RawY { get => GetValue(RawYProperty); set => SetValue(RawYProperty, value); }
	public double TunedX { get => GetValue(TunedXProperty); set => SetValue(TunedXProperty, value); }
	public double TunedY { get => GetValue(TunedYProperty); set => SetValue(TunedYProperty, value); }
	public double InnerDeadzone { get => GetValue(InnerDeadzoneProperty); set => SetValue(InnerDeadzoneProperty, value); }
	public double OuterDeadzone { get => GetValue(OuterDeadzoneProperty); set => SetValue(OuterDeadzoneProperty, value); }
	public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
	public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

	// The animation clock (tests use a fake one).
	public Func<TimeSpan> Clock { get; set; } = AnimationClock.Now;

	// Frames requested from the render loop so far (tests: none while idle or hidden).
	public int FrameRequests => _frames.Requests;

	// Trail points still visible at a time (tests).
	public int TrailPoints(TimeSpan now)
	{
		int count = 0;
		for (int i = 0; i < _trailCount; i++)
		{
			if (now - Sample(i).At < TrailLength)
			{
				count++;
			}
		}
		return count;
	}

	// Inside the inner deadzone the dot is drawn hollow (the game ignores that range).
	public static bool IsInsideDeadzone(double x, double y, double innerPercent)
	{
		double r = Math.Sqrt(x * x + y * y);
		return r <= Math.Clamp(innerPercent, 0, 95) / 100;
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == TunedXProperty || change.Property == TunedYProperty)
		{
			AddSample(TunedX, TunedY, Clock());
		}
		else if (change.Property == IsLiveProperty)
		{
			_trailCount = 0;
		}
	}

	private ref (double X, double Y, TimeSpan At) Sample(int i) => ref _trail[(_trailStart + i) % TrailCapacity];

	private void AddSample(double x, double y, TimeSpan now)
	{
		// X and Y arrive as two property changes of one report: merge samples a few ms apart.
		if (_trailCount > 0 && now - Sample(_trailCount - 1).At < TimeSpan.FromMilliseconds(4))
		{
			ref var last = ref Sample(_trailCount - 1);
			last = (x, y, last.At);
			return;
		}
		if (_trailCount == TrailCapacity)
		{
			_trailStart = (_trailStart + 1) % TrailCapacity;
			_trailCount--;
		}
		Sample(_trailCount) = (x, y, now);
		_trailCount++;
	}

	public override void Render(DrawingContext context)
	{
		double size = Math.Min(Bounds.Width, Bounds.Height);
		if (size <= 8)
		{
			return;
		}
		double scaling = (VisualRoot as IRenderRoot)?.RenderScaling ?? 1;
		IBrush accent = Accent ?? Brushes.DodgerBlue;
		EnsurePens(scaling, accent);

		// Centre and radii on the device-pixel grid so the 1 px cross and rings land on whole pixels.
		var center = new Point(PixelSnap.Line(Bounds.Width / 2, _line, scaling), PixelSnap.Line(Bounds.Height / 2, _line, scaling));
		double radius = PixelSnap.Point(Math.Floor(size / 2) - 5, scaling);
		context.DrawEllipse(Face, _ringPen, center, radius, radius);
		context.DrawLine(_faintPen, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
		context.DrawLine(_faintPen, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));

		double inner = PixelSnap.Point(Math.Clamp(InnerDeadzone, 0, 95) / 100 * radius, scaling);
		if (inner > 0.5)
		{
			context.DrawEllipse(DeadzoneFill, _deadzonePen, center, inner, inner);
		}
		double outer = PixelSnap.Point((1 - Math.Clamp(OuterDeadzone, 0, 95) / 100) * radius, scaling);
		if (outer < radius - 0.5)
		{
			context.DrawEllipse(null, _dashedPen, center, outer, outer);
		}
		if (!IsLive)
		{
			return;
		}

		TimeSpan now = Clock();
		Point Map(double x, double y) => new(center.X + Clamp(x) * radius, center.Y + Clamp(y) * radius);

		// Trail: segments between recent samples, fading with age.
		bool fading = false;
		for (int i = 1; i < _trailCount; i++)
		{
			(double x0, double y0, _) = Sample(i - 1);
			(double x1, double y1, TimeSpan at) = Sample(i);
			double age = (now - at).TotalMilliseconds / TrailLength.TotalMilliseconds;
			if (age >= 1)
			{
				continue;
			}
			fading = true;
			using (context.PushOpacity(0.55 * (1 - Math.Max(0, age))))
			{
				context.DrawLine(_trailPen, Map(x0, y0), Map(x1, y1));
			}
		}

		// Raw stick position (grey), when it is not hidden under the output dot.
		Point raw = Map(RawX, RawY);
		Point tuned = Map(TunedX, TunedY);
		if (Math.Abs(tuned.X - raw.X) + Math.Abs(tuned.Y - raw.Y) > 1.5)
		{
			context.DrawEllipse(Output, null, raw, 3.5, 3.5);
		}
		if (!fading)
		{
			// At rest: on the pixel grid, crisp.
			tuned = new Point(PixelSnap.Point(tuned.X, scaling), PixelSnap.Point(tuned.Y, scaling));
		}
		if (IsInsideDeadzone(Clamp(RawX), Clamp(RawY), InnerDeadzone))
		{
			context.DrawEllipse(null, _hollowPen, tuned, DotRadius - 0.75, DotRadius - 0.75);
		}
		else
		{
			context.DrawEllipse(accent, null, tuned, DotRadius, DotRadius);
		}

		if (fading)
		{
			_frames.Request();
		}
	}

	private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;

	private void EnsurePens(double scaling, IBrush accent)
	{
		if (scaling == _penScaling && ReferenceEquals(accent, _penAccent))
		{
			return;
		}
		_penScaling = scaling;
		_penAccent = accent;
		_line = PixelSnap.Thickness(1, scaling);
		_faintPen = new Pen(Faint, _line);
		_ringPen = new Pen(Ring, _line);
		_deadzonePen = new Pen(DeadzoneRing, _line);
		_dashedPen = new Pen(Dashed, _line, new DashStyle([3, 3], 0));
		_trailPen = new Pen(accent, 2, lineCap: PenLineCap.Round);
		_hollowPen = new Pen(accent, PixelSnap.Thickness(1.5, scaling));
	}
}
