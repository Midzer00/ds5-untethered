// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kopru.App.Controls;

// The two links of the game panel's settings chain (game -> virtual controller -> controller), drawn under three
// equal columns of nodes: each link runs on the pictures' centre line, stops short of the pictures and ends in a
// small arrow head (the direction the settings flow). IsLive false (the game is not running): a dashed hairline —
// set, waiting for the game; IsLive true: a 2 px accent line — applied now.
public sealed class GameChainLines : Control
{
	public static readonly StyledProperty<bool> IsLiveProperty = AvaloniaProperty.Register<GameChainLines, bool>(nameof(IsLive));
	public static readonly StyledProperty<IBrush?> LiveBrushProperty = AvaloniaProperty.Register<GameChainLines, IBrush?>(nameof(LiveBrush));
	public static readonly StyledProperty<IBrush?> IdleBrushProperty = AvaloniaProperty.Register<GameChainLines, IBrush?>(nameof(IdleBrush));

	// Half the width of each node's picture (the game and the virtual controller's tiles, the controller's art), so the
	// links stop 10 px short of them, and the pictures' centre line.
	private static readonly double[] NodeHalf = [32, 32, 78];
	public const double CentreY = 52;

	static GameChainLines()
	{
		AffectsRender<GameChainLines>(IsLiveProperty, LiveBrushProperty, IdleBrushProperty);
	}

	public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
	public IBrush? LiveBrush { get => GetValue(LiveBrushProperty); set => SetValue(LiveBrushProperty, value); }
	public IBrush? IdleBrush { get => GetValue(IdleBrushProperty); set => SetValue(IdleBrushProperty, value); }

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, column = w / 3;
		bool on = IsLive;
		IBrush liveBrush = LiveBrush ?? Brushes.Transparent, idleBrush = IdleBrush ?? Brushes.Gray;
		var pen = new Pen(on ? liveBrush : idleBrush, on ? 2 : 1, on ? null : new DashStyle([4, 3], 0),
			lineCap: on ? PenLineCap.Round : PenLineCap.Flat, lineJoin: PenLineJoin.Round);
		var head = new Pen(on ? liveBrush : idleBrush, on ? 2 : 1, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
		double y = on ? CentreY : Math.Floor(CentreY) + 0.5;
		for (int link = 0; link < 2; link++)
		{
			double a = Math.Round(column * (link + 0.5) + NodeHalf[link] + 10);
			double b = Math.Round(column * (link + 1.5) - NodeHalf[link + 1] - 10);
			context.DrawLine(pen, new Point(a, y), new Point(b - 1, y));
			// The arrow head points at the next node: the direction the settings flow (from the game to the controller).
			var arrow = new StreamGeometry();
			using (StreamGeometryContext g = arrow.Open())
			{
				g.BeginFigure(new Point(b - 6, y - 5), false);
				g.LineTo(new Point(b, y));
				g.LineTo(new Point(b - 6, y + 5));
				g.EndFigure(false);
			}
			context.DrawGeometry(null, head, arrow);
		}
	}
}
