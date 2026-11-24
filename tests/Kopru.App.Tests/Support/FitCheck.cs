// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// What the "fits one window" tests share (the controller Test tab, the Settings tabs): the window sizes, settling a
// headless window, and the checks: no page scrolling, blocks apart and inside the page column, controls inside their card,
// no text cut off.
internal static class FitCheck
{
	// Every shipped language discovered from embedded strings/<code>.json resources.
	public static IReadOnlyList<(Language Language, string Code)> ShippedLanguages() =>
		Localizer.Available.Select(l => (l, l.Code)).ToArray();

	// The three required review sizes: design size, owner's window (225% DPI layout units), and minimum window.
	public static readonly (double Width, double Height)[] ReviewSizes = [(1100, 700), (1152, 700), (960, 640)];
	// The window's minimum, common screens, ultra-wide, tall, 4:3.
	public static readonly (double Width, double Height)[] Matrix =
	[
		(960, 640), (1100, 700), (1280, 720), (1366, 768), (1536, 864), (1600, 900), (1920, 1080), (2560, 1440), (2560, 1080),
		(1100, 1000), (1400, 1050)
	];

	// The controls a card must hold inside its bounds.
	public static bool IsContent(Control c) => c is TextBlock or Button or Slider or ComboBox or ProgressBar or ToggleSwitch or ListBox or TextBox
		or StickPreview or ControllerArt;

	public static void Settle(Window window)
	{
		for (int i = 0; i < 4; i++)
		{
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
		}
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
	}

	public static IEnumerable<string> Scrolls(ScrollViewer scroll, string arrangement, bool allowVerticalSafetyNet = false)
	{
		if (scroll.Extent.Width > scroll.Viewport.Width + 0.5)
		{
			yield return $"scrolls horizontally: extent {scroll.Extent.Width:F1} > viewport {scroll.Viewport.Width:F1} ({arrangement})";
		}
		if (scroll.Extent.Height > scroll.Viewport.Height + 0.5)
		{
			bool hasBar = scroll.VerticalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible;
			if (!allowVerticalSafetyNet || !hasBar)
			{
				yield return $"scrolls: extent {scroll.Extent.Height:F1} > viewport {scroll.Viewport.Height:F1} ({arrangement})";
			}
		}
	}

	// Every block is inside the page column and at least `gap` away from every other block.
	public static IEnumerable<string> Apart(IReadOnlyList<(string Name, Rect Rect)> blocks, Rect column, double gap = 8)
	{
		foreach ((string name, Rect rect) in blocks)
		{
			if (rect.Left < column.Left - 0.5 || rect.Right > column.Right + 0.5 || rect.Width < 1 || rect.Height < 1)
			{
				yield return $"{name} {rect} outside the column {column}";
			}
		}
		double half = gap / 2 - 0.1;
		for (int i = 0; i < blocks.Count; i++)
		{
			for (int j = i + 1; j < blocks.Count; j++)
			{
				if (blocks[i].Rect.Inflate(half).Intersects(blocks[j].Rect.Inflate(half)))
				{
					yield return $"{blocks[i].Name} {blocks[i].Rect} and {blocks[j].Name} {blocks[j].Rect} are closer than {gap}";
				}
			}
		}
	}

	// Every visible control of `card` lies inside the card (`root`: the coordinate space). A list that scrolls inside the card
	// counts as one control (its rows are clipped to it); the parts of a control's own template are the control's.
	public static IEnumerable<string> Inside(Control card, Visual root)
	{
		Rect cardRect = Bounds(card, root);
		foreach (Control c in card.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.TemplatedParent == null
			&& (IsContent(c) || c is ScrollViewer) && !InsideList(c, card)))
		{
			Rect r = Bounds(c, root);
			if (r.Width > 0.5 && (r.Left < cardRect.Left - 0.5 || r.Right > cardRect.Right + 0.5 || r.Top < cardRect.Top - 0.5 || r.Bottom > cardRect.Bottom + 0.5))
			{
				yield return $"{Describe(c)} {r} outside its card {cardRect}";
			}
		}
	}

	// No visible text under `root` is cut off: trimmed text with a matching full-text ToolTip is allowed; otherwise
	// any collapsed line, narrow box or overflowing layout fails.
	public static IEnumerable<string> CutText(Visual root)
	{
		foreach (TextBlock text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
		{
			if (IsTextCutOff(text))
			{
				yield return $"text cut off: \"{text.Text}\"";
			}
		}
	}

	public static bool IsTextCutOff(TextBlock text)
	{
		double availableWidth = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
		if (text.TextTrimming != TextTrimming.None && HasMatchingToolTip(text))
		{
			// Intentional ellipsis with a matching full-text ToolTip: fits as long as the ellipsized text stays inside its box.
			return text.Bounds.Width <= 0 || text.TextLayout.Width > availableWidth + 0.5;
		}
		return text.TextLayout.TextLines.Any(l => l.HasCollapsed)
			|| text.Bounds.Width + 0.5 < text.DesiredSize.Width - text.Margin.Left - text.Margin.Right
			|| text.TextLayout.Width > availableWidth + 0.5;
	}

	public static bool HasMatchingToolTip(TextBlock text)
	{
		string? fullText = text.Text;
		if (string.IsNullOrEmpty(fullText))
		{
			return false;
		}
		if (ToolTipMatches(ToolTip.GetTip(text), fullText))
		{
			return true;
		}
		foreach (Visual ancestor in text.GetVisualAncestors())
		{
			if (ancestor is Window or UserControl or ScrollViewer)
			{
				break;
			}
			if (ancestor is Control control && ToolTipMatches(ToolTip.GetTip(control), fullText))
			{
				return true;
			}
			if (ancestor is ComboBox or Button or ToggleButton or ListBoxItem or TabItem)
			{
				break;
			}
		}
		return false;
	}

	private static bool ToolTipMatches(object? tip, string expected)
	{
		if (tip is string s)
		{
			return s.Contains(expected, StringComparison.Ordinal);
		}
		if (tip is ILogical logical)
		{
			return logical.GetSelfAndLogicalDescendants().OfType<TextBlock>()
				.Any(t => !string.IsNullOrEmpty(t.Text) && t.Text.Contains(expected, StringComparison.Ordinal));
		}
		return false;
	}

	// Inside a ScrollViewer that is itself inside `card`.
	public static bool InsideList(Control c, Control card) =>
		c.GetVisualAncestors().TakeWhile(a => a != card).Any(a => a is ScrollViewer);

	public static string Describe(Control c) => c is TextBlock t ? $"text \"{t.Text}\"" : c.GetType().Name;

	public static Rect Bounds(Visual v, Visual root) =>
		v.TransformToVisual(root) is { } m ? new Rect(v.Bounds.Size).TransformToAABB(m) : default;
}
