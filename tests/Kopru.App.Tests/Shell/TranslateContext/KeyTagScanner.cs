// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// One visible text box of a shot, traced back to its catalogue key. Coordinates are window units.
internal sealed record ContextItem(string Key, Rect Bounds, double Size, int Weight, bool Wrap, string Align, double MaxW, int MaxLines,
	double LineHeight, bool Trim, bool Formatted, bool Composite);

internal sealed record ContextScan(IReadOnlyList<ContextItem> Items, IReadOnlyList<string> Untagged);

// A shown TextBlock of the plain English render: its text and the window box of each of its characters (kept as boxes: the
// TextBlock's own layout is disposed once the page changes).
internal sealed record PlainText(string Text, IReadOnlyList<Rect> CharBoxes)
{
	// The union of the boxes of characters [start, start + length).
	public Rect? Range(int start, int length)
	{
		Rect? union = null;
		for (int i = Math.Max(0, start); i < Math.Min(CharBoxes.Count, start + length); i++)
		{
			Rect r = CharBoxes[i];
			if (r.Width > 0.01 || r.Height > 0.01)
			{
				union = union is { } u ? u.Union(r) : r;
			}
		}
		return union;
	}
}

// Walks a window rendered in the key tag locale (KeyTagLocale, "qtg") and turns every visible text that carries key
// markers into ContextItems: one per key, with the bounds of that key's own part of the text. With the plain render's texts
// (PlainTexts of the same state, same tree order) the boxes are measured on the plain layout, so they sit exactly on the
// picture; the markers only say which characters belong to which key. Texts drawn by a control's own Render (no TextBlock)
// are read from a recording of that Render.
internal static class KeyTagScanner
{
	public static ContextScan Scan(Window window, IReadOnlyList<PlainText>? plain = null)
	{
		var items = new List<ContextItem>();
		var untagged = new List<string>();
		int index = 0;
		foreach (Visual visual in window.GetVisualDescendants())
		{
			if (!IsShown(visual, window))
			{
				continue;
			}
			if (visual is TextBlock text)
			{
				if (!string.IsNullOrEmpty(text.Text))
				{
					ScanTextBlock(text, window, plain != null && index < plain.Count ? plain[index] : null, items, untagged);
					index++;
				}
			}
			else if (visual is TextPresenter || visual.GetType().Assembly == typeof(App).Assembly && OverridesRender(visual))
			{
				DrawnText.Scan(visual, window, items, untagged);
			}
		}
		return new ContextScan(Dedupe(items), untagged.Distinct(StringComparer.Ordinal).ToArray());
	}

	// One entry per key and box (the same key twice in one place, e.g. a nested repeat, counts once).
	private static IReadOnlyList<ContextItem> Dedupe(List<ContextItem> items) =>
		items.GroupBy(i => (i.Key, X: Math.Round(i.Bounds.X), Y: Math.Round(i.Bounds.Y), W: Math.Round(i.Bounds.Width), H: Math.Round(i.Bounds.Height)))
			.Select(g => g.First())
			.ToArray();

	private static bool OverridesRender(Visual visual)
	{
		System.Reflection.MethodInfo? render = visual.GetType().GetMethod(nameof(Visual.Render), [typeof(DrawingContext)]);
		return render != null && render.DeclaringType != null && render.DeclaringType.Assembly == typeof(App).Assembly;
	}

	// Visible, not fully transparent, and not entirely clipped away by a clipping ancestor (a scrolled list, a closed panel).
	public static bool IsShown(Visual visual, Window window)
	{
		if (!visual.IsEffectivelyVisible || visual.Bounds.Width <= 0.5 || visual.Bounds.Height <= 0.5)
		{
			return false;
		}
		for (Visual? v = visual; v != null && v != window; v = v.GetVisualParent())
		{
			if (v.Opacity <= 0.01)
			{
				return false;
			}
		}
		return !VisibleRect(visual, new Rect(visual.Bounds.Size), window).IsEmpty();
	}

	// `local` (in the visual's own coordinates) in window coordinates, cut to every clipping ancestor and the window.
	public static Rect VisibleRect(Visual visual, Rect local, Window window)
	{
		Rect rect = ToWindow(visual, local, window);
		for (Visual? v = visual.GetVisualParent(); v != null && v != window; v = v.GetVisualParent())
		{
			if (v.ClipToBounds)
			{
				rect = Intersect(rect, ToWindow(v, new Rect(v.Bounds.Size), window));
			}
		}
		return Intersect(rect, new Rect(window.Bounds.Size));
	}

	private static Rect Intersect(Rect a, Rect b)
	{
		Rect r = a.Intersect(b);
		return r.Width <= 0.01 || r.Height <= 0.01 ? default : r;
	}

	private static bool IsEmpty(this Rect r) => r.Width <= 0.01 || r.Height <= 0.01;

	// Something drawn over the text (a dialog over the page, its dimmed backdrop) is the topmost visual at the middle and
	// both ends of its box. A text is never covered by its own parents or parts, nor by anything fully transparent (a
	// tab's hidden bold copy that keeps its width).
	public static bool IsCovered(Visual text, Rect box, Window window)
	{
		Point[] probes = [box.Center, new(box.Left + Math.Min(2, box.Width / 2), box.Center.Y), new(box.Right - Math.Min(2, box.Width / 2), box.Center.Y)];
		foreach (Point probe in probes)
		{
			Visual? top = window.GetVisualsAt(probe, v => v.IsEffectivelyVisible && v.Opacity > 0.01).FirstOrDefault(Paints);
			if (top == null || top == text || text.IsVisualAncestorOf(top) || top.IsVisualAncestorOf(text) || !IsDrawnAbove(top, text))
			{
				return false;
			}
		}
		return true;
	}

	// `a` is drawn after (over) `b`: at their nearest common ancestor, a's branch has a higher ZIndex or comes later.
	// (The hit test can miss the text itself, between its glyphs, and return what lies behind it.)
	private static bool IsDrawnAbove(Visual a, Visual b)
	{
		List<Visual> bChain = b.GetSelfAndVisualAncestors().ToList();
		Visual aBranch = a;
		Visual? common = a.GetVisualParent();
		while (common != null && !bChain.Contains(common))
		{
			aBranch = common;
			common = common.GetVisualParent();
		}
		if (common == null)
		{
			return true;
		}
		Visual bBranch = bChain[bChain.IndexOf(common) - 1];
		List<Visual> children = common.GetVisualChildren().ToList();
		int za = aBranch.ZIndex;
		int zb = bBranch.ZIndex;
		return za != zb ? za > zb : children.IndexOf(aBranch) > children.IndexOf(bBranch);
	}

	// Whether a visual that takes the pointer there also draws something (a transparent hit area, such as the title bar's
	// drag strip or a row's background, does not cover anything).
	private static bool Paints(Visual visual) => visual switch
	{
		TextBlock text => !string.IsNullOrEmpty(text.Text),
		Border border => Opaque(border.Background),
		Panel panel => Opaque(panel.Background),
		ContentPresenter presenter => Opaque(presenter.Background),
		TemplatedControl templated => Opaque(templated.Background),
		Decorator or LayoutTransformControl => false,
		_ => true
	};

	private static bool Opaque(IBrush? brush) => brush switch
	{
		null => false,
		ISolidColorBrush solid => solid.Color.A > 3 && solid.Opacity > 0.01,
		_ => brush.Opacity > 0.01
	};

	public static Rect ToWindow(Visual visual, Rect local, Window window) =>
		visual.TransformToVisual(window) is { } m ? local.TransformToAABB(m) : default;

	public static double ScaleToWindow(Visual visual, Window window) =>
		visual.TransformToVisual(window) is { } m ? Math.Sqrt(Math.Abs(m.M11 * m.M22 - m.M12 * m.M21)) : 1;

	// Every shown TextBlock with text, in tree order (the order Scan and LayoutSignature walk them).
	public static IReadOnlyList<PlainText> PlainTexts(Window window) =>
		window.GetVisualDescendants().OfType<TextBlock>()
			.Where(t => !string.IsNullOrEmpty(t.Text) && IsShown(t, window))
			.Select(t => new PlainText(t.Text!, CharBoxes(t, window)))
			.ToArray();

	private static Rect[] CharBoxes(TextBlock text, Window window)
	{
		TextLayout layout = text.TextLayout;
		Point origin = TextOrigin(text, layout);
		Matrix toWindow = text.TransformToVisual(window) ?? Matrix.Identity;
		var boxes = new Rect[text.Text!.Length];
		for (int i = 0; i < boxes.Length; i++)
		{
			boxes[i] = Union(layout.HitTestTextRange(i, 1), origin)?.TransformToAABB(toWindow) ?? default;
		}
		return boxes;
	}

	private static void ScanTextBlock(TextBlock text, Window window, PlainText? plain, List<ContextItem> items, List<string> untagged)
	{
		string? value = text.Text;
		if (string.IsNullOrEmpty(value))
		{
			return;
		}
		KeyTagDecoding decoding = KeyTagLocale.Decode(value);
		if (decoding.Spans.Count == 0)
		{
			if (decoding.Clean.Any(char.IsLetter))
			{
				untagged.Add(decoding.Clean);
			}
			return;
		}

		TextLayout layout = text.TextLayout;
		Point origin = TextOrigin(text, layout);
		double scale = ScaleToWindow(text, window);
		TextStyle style = Style(text, layout, scale);
		bool composite = decoding.Spans.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() > 1;

		bool onPlain = plain != null && plain.Text == decoding.Clean;
		Rect clip = VisibleRect(text, new Rect(text.Bounds.Size), window);
		foreach (KeyTagSpan span in decoding.Spans)
		{
			Rect? box = onPlain
				? plain!.Range(span.CleanStart, span.CleanLength)
				: Union(layout.HitTestTextRange(span.TaggedStart, span.TaggedLength), origin) is { } local ? ToWindow(text, local, window) : null;
			if (box is not { } found || found.Width <= 0.01)
			{
				continue;
			}
			Rect shown = Intersect(found, clip);
			if (shown.IsEmpty() || IsCovered(text, shown, window))
			{
				continue;
			}
			items.Add(new ContextItem(span.Key, shown, style.Size, style.Weight, style.Wrap, style.Align, style.MaxW, style.MaxLines, style.LineHeight,
				style.Trim, IsFormatted(span.Key), composite));
		}
	}

	// The union of a text range's line boxes, moved to where the layout is drawn.
	private static Rect? Union(IEnumerable<Rect> rects, Point origin)
	{
		Rect? union = null;
		foreach (Rect r in rects)
		{
			if (r.Width <= 0.01 && r.Height <= 0.01)
			{
				continue;
			}
			Rect shifted = r.Translate(new Vector(origin.X, origin.Y));
			union = union is { } u ? u.Union(shifted) : shifted;
		}
		return union;
	}

	private static readonly Dictionary<string, bool> FormattedCache = new(StringComparer.Ordinal);

	// The English value has ICU placeholders or plural blocks.
	public static bool IsFormatted(string key)
	{
		lock (FormattedCache)
		{
			if (!FormattedCache.TryGetValue(key, out bool formatted))
			{
				formatted = StringCatalog.English.Messages.TryGetValue(key, out IcuMessage? message) && message.Placeholders.Count > 0;
				FormattedCache[key] = formatted;
			}
			return formatted;
		}
	}

	// Where TextBlock draws its layout (TextBlock.Render): the padding, and a vertical shift when the box is shorter than the text.
	public static Point TextOrigin(TextBlock text, TextLayout layout)
	{
		Thickness padding = text.Padding;
		double top = padding.Top;
		double textHeight = layout.Height;
		if (text.Bounds.Height < textHeight)
		{
			top += text.VerticalAlignment switch
			{
				VerticalAlignment.Center => (text.Bounds.Height - textHeight) / 2,
				VerticalAlignment.Bottom => text.Bounds.Height - textHeight,
				_ => 0
			};
		}
		return new Point(padding.Left, top);
	}

	internal readonly record struct TextStyle(double Size, int Weight, bool Wrap, string Align, double MaxW, int MaxLines, double LineHeight, bool Trim);

	private static TextStyle Style(TextBlock text, TextLayout layout, double scale)
	{
		bool wrap = text.TextWrapping != TextWrapping.NoWrap;
		bool trim = text.TextTrimming != TextTrimming.None;
		double lineHeight = !double.IsNaN(text.LineHeight) && text.LineHeight > 0
			? text.LineHeight
			: layout.TextLines.Count > 0 ? layout.TextLines[0].Height : text.FontSize * 1.2;
		Thickness padding = text.Padding;
		double own = Math.Max(0, text.Bounds.Width - padding.Left - padding.Right);
		// A one-line text needs its desired width; what it may grow into is found from there.
		double needed = Math.Max(0, text.DesiredSize.Width - text.Margin.Left - text.Margin.Right - padding.Left - padding.Right);
		(double room, string align) = wrap || trim ? (own, Align(text.TextAlignment, text.HorizontalAlignment)) : Room(text, needed);
		if (!double.IsInfinity(text.MaxWidth) && text.MaxWidth > 0)
		{
			room = Math.Min(room, text.MaxWidth - padding.Left - padding.Right);
		}
		int maxLines = 1;
		if (wrap)
		{
			int fit = (int)Math.Floor((SlotHeight(text) - padding.Top - padding.Bottom) / lineHeight + 0.05);
			maxLines = text.MaxLines > 0 ? text.MaxLines : Math.Max(Math.Max(1, layout.TextLines.Count), fit);
		}
		return new TextStyle(Math.Round(text.FontSize * scale, 2), text.FontWeight is var w ? (int)w : 400, wrap, align,
			Math.Round(room * scale, 1), maxLines, Math.Round(lineHeight * scale, 2), trim);
	}

	private static string Align(TextAlignment alignment, HorizontalAlignment own) => alignment switch
	{
		TextAlignment.Center => "center",
		TextAlignment.Right or TextAlignment.End => "right",
		TextAlignment.Justify => "justify",
		_ => own switch
		{
			HorizontalAlignment.Center => "center",
			HorizontalAlignment.Right => "right",
			_ => "left"
		}
	};

	private static double SlotHeight(Layoutable element)
	{
		Rect? slot = LayoutInformation.GetPreviousArrangeBounds(element);
		double height = slot is { } s ? s.Height - element.Margin.Top - element.Margin.Bottom : element.Bounds.Height;
		if (!double.IsNaN(element.Height))
		{
			height = element.Height;
		}
		return Math.Max(element.Bounds.Height, Math.Min(height, element.MaxHeight));
	}

	// The width a one-line text may grow to: its own width plus the first spare room found walking up through the parents
	// that size to their content (a button in a row, an Auto grid column), stopping at a parent that fixes the width.
	private static (double Room, string Align) Room(TextBlock text, double own)
	{
		string align = Align(text.TextAlignment, text.HorizontalAlignment);
		Layoutable element = text;
		Layoutable child = text;
		for (int depth = 0; depth < 12; depth++)
		{
			double slack = Slack(element);
			if (slack > 0.5)
			{
				return (own + slack, element == text ? align : GrowthSide(element, child, text));
			}
			if (!double.IsNaN(element.Width) || element.GetVisualParent() is not Layoutable parent || parent is Window or ScrollContentPresenter
				|| !SizesToContent(parent, element))
			{
				break;
			}
			child = element;
			element = parent;
		}
		return (own, align);
	}

	// Which edge of the text stays put when it grows into `container`'s spare room ("right": it grows to the left).
	// In a grid, toward its star columns; otherwise from where the text sits in the container.
	private static string GrowthSide(Layoutable container, Layoutable child, TextBlock text)
	{
		if (container is Grid grid && grid.ColumnDefinitions.Count > 1 && child is Control c)
		{
			int column = Grid.GetColumn(c);
			List<int> stars = Enumerable.Range(0, grid.ColumnDefinitions.Count).Where(i => grid.ColumnDefinitions[i].Width.IsStar).ToList();
			if (stars.Count > 0 && stars.All(i => i < column))
			{
				return "right";
			}
			if (stars.Count > 0 && stars.All(i => i > column))
			{
				return "left";
			}
		}
		if (text.TransformToVisual(container) is not { } m)
		{
			return "left";
		}
		Rect box = new Rect(text.Bounds.Size).TransformToAABB(m);
		double left = box.Left;
		double right = container.Bounds.Width - box.Right;
		if (right < 1 && left > 1)
		{
			return "right";
		}
		return left > 1 && right > 1 && Math.Abs(left - right) < 2 ? "center" : "left";
	}

	// Room the element was given but does not need.
	private static double Slack(Layoutable element)
	{
		if (LayoutInformation.GetPreviousArrangeBounds(element) is not { } slot)
		{
			return 0;
		}
		Thickness m = element.Margin;
		double room = slot.Width - m.Left - m.Right;
		if (!double.IsNaN(element.Width))
		{
			room = Math.Min(room, element.Width);
		}
		room = Math.Min(room, element.MaxWidth);
		double need = element.DesiredSize.Width - m.Left - m.Right;
		return room - need;
	}

	// The parent gives the child a width that follows the child's own size (so the child may grow inside the parent's room).
	private static bool SizesToContent(Layoutable parent, Layoutable child) => parent switch
	{
		StackPanel stack => stack.Orientation == Orientation.Horizontal || child.HorizontalAlignment != HorizontalAlignment.Stretch,
		WrapPanel => true,
		Grid grid => GridColumnIsAuto(grid, child),
		DockPanel => DockPanel.GetDock((Control)child) is Dock.Left or Dock.Right,
		Panel panel when panel.GetType() == typeof(Panel) => true,
		Decorator or ContentPresenter or TemplatedControl => true,
		_ => false
	};

	private static bool GridColumnIsAuto(Grid grid, Layoutable child)
	{
		if (grid.ColumnDefinitions.Count == 0)
		{
			return true;
		}
		int column = Grid.GetColumn((Control)child);
		int span = Grid.GetColumnSpan((Control)child);
		for (int c = column; c < Math.Min(grid.ColumnDefinitions.Count, column + span); c++)
		{
			if (!grid.ColumnDefinitions[c].Width.IsAuto)
			{
				return false;
			}
		}
		return true;
	}

	// The window-relative box of every shown TextBlock, in tree order: what must match between the tagged and clean renders.
	public static IReadOnlyList<(string Text, Rect Box, Size Layout)> LayoutSignature(Window window) =>
		window.GetVisualDescendants().OfType<TextBlock>()
			.Where(t => !string.IsNullOrEmpty(t.Text) && IsShown(t, window))
			.Select(t => (KeyTagLocale.Strip(t.Text!), ToWindow(t, new Rect(t.Bounds.Size), window), new Size(t.TextLayout.Width, t.TextLayout.Height)))
			.ToArray();
}
