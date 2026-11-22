// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Kopru.App.Controls;

// A fixed-size ring of the latest samples (0..1), oldest first; once full, a push drops the oldest. Written and read on
// the UI thread only.
public sealed class SignalRing
{
	private readonly float[] _data;
	private int _next;

	public SignalRing(int capacity)
	{
		_data = new float[Math.Max(2, capacity)];
	}

	public int Capacity => _data.Length;
	public int Count { get; private set; }

	// Sample i, 0 the oldest.
	public float this[int i] => _data[(_next - Count + i + _data.Length) % _data.Length];

	public float Latest => Count == 0 ? 0 : this[Count - 1];

	public void Push(double value)
	{
		_data[_next] = (float)Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 1);
		_next = (_next + 1) % _data.Length;
		Count = Math.Min(Count + 1, _data.Length);
	}

	public void Clear()
	{
		_next = 0;
		Count = 0;
	}
}

public enum SignalTraceMode
{
	// Primary and Secondary as filled areas from the baseline, Secondary drawn first (under Primary).
	Areas,
	// Primary above the centre line and Secondary below it, mirrored (a stereo-like envelope).
	Mirrored,
	// Primary alone, mirrored around the centre line.
	Envelope
}

// One lane of the Overview's Signals view: the last Capacity samples of one or two SignalRings as flat translucent filled
// areas with a crisp edge in their own colour, scrolling from the right as samples arrive. The rings change in place, so a
// new Revision is what asks for a redraw; the geometries are built again only for a new revision or size, and each series
// is one StreamGeometry drawn with a pen and fill made once per colour.
public sealed class SignalTrace : Control
{
	public static readonly StyledProperty<SignalRing?> PrimaryProperty = AvaloniaProperty.Register<SignalTrace, SignalRing?>(nameof(Primary));
	public static readonly StyledProperty<SignalRing?> SecondaryProperty = AvaloniaProperty.Register<SignalTrace, SignalRing?>(nameof(Secondary));
	public static readonly StyledProperty<IBrush?> PrimaryBrushProperty = AvaloniaProperty.Register<SignalTrace, IBrush?>(nameof(PrimaryBrush));
	public static readonly StyledProperty<IBrush?> SecondaryBrushProperty = AvaloniaProperty.Register<SignalTrace, IBrush?>(nameof(SecondaryBrush));
	public static readonly StyledProperty<SignalTraceMode> ModeProperty = AvaloniaProperty.Register<SignalTrace, SignalTraceMode>(nameof(Mode));
	public static readonly StyledProperty<long> RevisionProperty = AvaloniaProperty.Register<SignalTrace, long>(nameof(Revision));

	public const byte FillAlpha = 56;
	public const double EdgeWidth = 1.4;

	private static readonly IPen BaselinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1);

	private (IBrush Fill, IPen Edge)? _primaryPaint, _secondaryPaint;
	private StreamGeometry? _primaryGeometry, _secondaryGeometry;
	private long _builtRevision = -1;
	private Size _builtSize;

	static SignalTrace()
	{
		AffectsRender<SignalTrace>(PrimaryProperty, SecondaryProperty, PrimaryBrushProperty, SecondaryBrushProperty, ModeProperty, RevisionProperty);
	}

	public SignalRing? Primary { get => GetValue(PrimaryProperty); set => SetValue(PrimaryProperty, value); }
	public SignalRing? Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
	public IBrush? PrimaryBrush { get => GetValue(PrimaryBrushProperty); set => SetValue(PrimaryBrushProperty, value); }
	public IBrush? SecondaryBrush { get => GetValue(SecondaryBrushProperty); set => SetValue(SecondaryBrushProperty, value); }
	public SignalTraceMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
	public long Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

	// How many times the geometries were built (tests: a redraw without a new revision reuses them).
	public int Builds { get; private set; }

	public StreamGeometry? PrimaryGeometry => _primaryGeometry;
	public StreamGeometry? SecondaryGeometry => _secondaryGeometry;

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == PrimaryBrushProperty)
		{
			_primaryPaint = null;
		}
		else if (change.Property == SecondaryBrushProperty)
		{
			_secondaryPaint = null;
		}
		else if (change.Property == PrimaryProperty || change.Property == SecondaryProperty || change.Property == ModeProperty)
		{
			_builtRevision = -1;
		}
	}

	// The vertical extent of a series' sample: the baseline (or centre line) and the edge it reaches (tests).
	public (double Base, double Edge) Extent(double value, bool secondary)
	{
		double h = Bounds.Height;
		double v = Math.Clamp(value, 0, 1);
		switch (Mode)
		{
			case SignalTraceMode.Areas:
				return (h - 1, h - 1 - v * (h - 2));
			default:
				double mid = h / 2;
				double reach = mid - 1;
				return (mid, secondary && Mode == SignalTraceMode.Mirrored ? mid + v * reach : mid - v * reach);
		}
	}

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, h = Bounds.Height;
		if (w < 4 || h < 4)
		{
			return;
		}
		if (_builtRevision != Revision || _builtSize != Bounds.Size)
		{
			Build();
		}
		double line = Mode == SignalTraceMode.Areas ? Math.Round(h - 1) + 0.5 : Math.Round(h / 2) + 0.5;
		context.DrawLine(BaselinePen, new Point(0, line), new Point(w, line));
		if (_secondaryGeometry != null && Mode != SignalTraceMode.Envelope)
		{
			(IBrush fill, IPen edge) = _secondaryPaint ??= Paint(SecondaryBrush);
			context.DrawGeometry(fill, edge, _secondaryGeometry);
		}
		if (_primaryGeometry != null)
		{
			(IBrush fill, IPen edge) = _primaryPaint ??= Paint(PrimaryBrush);
			context.DrawGeometry(fill, edge, _primaryGeometry);
		}
	}

	private void Build()
	{
		_builtRevision = Revision;
		_builtSize = Bounds.Size;
		Builds++;
		_primaryGeometry = Primary is { Count: >= 2 } p ? Shape(p, false) : null;
		_secondaryGeometry = Mode != SignalTraceMode.Envelope && Secondary is { Count: >= 2 } s ? Shape(s, true) : null;
	}

	// One closed figure: along the baseline (or centre line) under the samples, up along the samples, and back. Envelope
	// mode mirrors the samples around the centre line instead of closing on it.
	private StreamGeometry Shape(SignalRing ring, bool secondary)
	{
		double w = Bounds.Width;
		double step = w / (ring.Capacity - 1);
		int n = ring.Count;
		double X(int i) => w - (n - 1 - i) * step;
		var g = new StreamGeometry();
		using StreamGeometryContext ctx = g.Open();
		if (Mode == SignalTraceMode.Envelope)
		{
			double mid = Bounds.Height / 2;
			ctx.BeginFigure(new Point(X(0), Extent(ring[0], false).Edge), true);
			for (int i = 1; i < n; i++)
			{
				ctx.LineTo(new Point(X(i), Extent(ring[i], false).Edge));
			}
			for (int i = n - 1; i >= 0; i--)
			{
				ctx.LineTo(new Point(X(i), 2 * mid - Extent(ring[i], false).Edge));
			}
			ctx.EndFigure(true);
			return g;
		}
		double baseY = Extent(0, secondary).Base;
		ctx.BeginFigure(new Point(X(0), baseY), true);
		for (int i = 0; i < n; i++)
		{
			ctx.LineTo(new Point(X(i), Extent(ring[i], secondary).Edge));
		}
		ctx.LineTo(new Point(X(n - 1), baseY));
		ctx.EndFigure(true);
		return g;
	}

	private static (IBrush Fill, IPen Edge) Paint(IBrush? brush)
	{
		Color c = brush is ISolidColorBrush s ? s.Color : Colors.White;
		return (new ImmutableSolidColorBrush(Color.FromArgb(FillAlpha, c.R, c.G, c.B)),
			new ImmutablePen(new ImmutableSolidColorBrush(c), EdgeWidth, lineJoin: PenLineJoin.Round));
	}
}
