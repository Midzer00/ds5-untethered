// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Kopru.App.Services;

namespace Kopru.App.Controls;

// A game's picture at the start of a supported games row: a rounded tile in the Steam header shape (460x215). It loads only
// while the row is on screen (after a short pause, so rows flicked past during a fast scroll ask for nothing) and stops
// loading when the row scrolls away or is reused for another game. Until a picture is there, and for games without one,
// it shows a quiet tile with a faint controller outline. An exe icon is drawn small in the middle of the tile.
public sealed class GameThumbnail : Control
{
	public static readonly StyledProperty<GameImageHandle?> SourceProperty = AvaloniaProperty.Register<GameThumbnail, GameImageHandle?>(nameof(Source));

	public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = AvaloniaProperty.Register<GameThumbnail, CornerRadius>(nameof(CornerRadius), new CornerRadius(8));

	// The pause before a load starts for a row that just came into view.
	public static TimeSpan SettleDelay { get; set; } = TimeSpan.FromMilliseconds(90);

	// Fallbacks for the App.axaml brushes (QuietFillBrush, HairlineBrush, MutedBrush).
	private static readonly IBrush TileFallback = new SolidColorBrush(Color.Parse("#14FFFFFF"));
	private static readonly IBrush EdgeFallback = new SolidColorBrush(Color.Parse("#14FFFFFF"));
	private static readonly IBrush GlyphFallback = new SolidColorBrush(Color.Parse("#8A909A"));

	private CancellationTokenSource? loading;
	private GameThumb? thumb;

	static GameThumbnail()
	{
		AffectsRender<GameThumbnail>(CornerRadiusProperty);
	}

	public GameImageHandle? Source
	{
		get => GetValue(SourceProperty);
		set => SetValue(SourceProperty, value);
	}

	public CornerRadius CornerRadius
	{
		get => GetValue(CornerRadiusProperty);
		set => SetValue(CornerRadiusProperty, value);
	}

	// What is shown now (tests).
	public GameThumb? Shown => thumb;

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == SourceProperty)
		{
			Stop();
			thumb = null;
			InvalidateVisual();
			Start();
		}
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		Start();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		Stop();
		base.OnDetachedFromVisualTree(e);
	}

	protected override void OnSizeChanged(SizeChangedEventArgs e)
	{
		base.OnSizeChanged(e);
		if (thumb == null)
		{
			Start();
		}
	}

	private int PixelWidth()
	{
		double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
		return (int)Math.Ceiling(Math.Max(Bounds.Width, Width is double w && !double.IsNaN(w) ? w : 0) * scale);
	}

	private void Start()
	{
		if (Source is not GameImageHandle handle || thumb != null || loading != null || VisualRoot == null)
		{
			return;
		}
		int width = PixelWidth();
		if (width <= 0)
		{
			return;
		}
		if (handle.Service.Cached(handle.Request, width) is GameThumb hit)
		{
			Show(hit);
			return;
		}
		var cancel = new CancellationTokenSource();
		loading = cancel;
		_ = LoadAsync(handle, width, cancel);
	}

	private async Task LoadAsync(GameImageHandle handle, int width, CancellationTokenSource cancel)
	{
		try
		{
			if (SettleDelay > TimeSpan.Zero)
			{
				await Task.Delay(SettleDelay, cancel.Token);
			}
			GameThumb result = await handle.Service.LoadAsync(handle.Request, width, cancel.Token);
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (!cancel.IsCancellationRequested && ReferenceEquals(Source, handle))
				{
					loading = null;
					Show(result);
				}
			});
		}
		catch (OperationCanceledException)
		{
			// Scrolled away or reused for another game.
		}
		finally
		{
			cancel.Dispose();
		}
	}

	private void Stop()
	{
		if (loading != null)
		{
			loading.Cancel();
			loading = null;
		}
	}

	private void Show(GameThumb result)
	{
		thumb = result;
		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		var bounds = new Rect(Bounds.Size);
		if (bounds.Width < 4 || bounds.Height < 4)
		{
			return;
		}
		CornerRadius r = CornerRadius;
		var shape = new RoundedRect(bounds, r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft);
		GameThumb? shown = thumb;
		if (shown is { Image: { } image, IsIcon: false })
		{
			using (context.PushClip(shape))
			{
				Size size = image.Size;
				// Fills the tile (the header shape matches; anything else is cropped around the middle).
				double scale = Math.Max(bounds.Width / size.Width, bounds.Height / size.Height);
				double w = size.Width * scale, h = size.Height * scale;
				context.DrawImage(image, new Rect(size), new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h));
			}
			context.DrawRectangle(null, new Pen(Brush("HairlineBrush", EdgeFallback), 1), shape.Deflate(0.5, 0.5));
			return;
		}
		context.DrawRectangle(Brush("QuietFillBrush", TileFallback), null, shape);
		if (shown is { Image: { } icon, IsIcon: true })
		{
			double side = Math.Min(32, bounds.Height - 12);
			context.DrawImage(icon, new Rect(icon.Size), new Rect((bounds.Width - side) / 2, (bounds.Height - side) / 2, side, side));
			return;
		}
		// The placeholder: the gamepad outline from the icon set (24x24 box), faint, in the middle.
		double glyph = Math.Min(22, bounds.Height - 16);
		if (glyph >= 10)
		{
			double k = glyph / 24;
			using (context.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation((bounds.Width - glyph) / 2, (bounds.Height - glyph) / 2)))
			{
				using (context.PushOpacity(0.45))
				{
					context.DrawGeometry(null, new Pen(Brush("MutedBrush", GlyphFallback), 1.5 / k, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), IconPaths.Gamepad);
				}
			}
		}
	}

	private IBrush Brush(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : fallback;
}
