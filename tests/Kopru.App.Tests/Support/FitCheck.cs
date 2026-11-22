// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;

namespace Kopru.App.Tests;

// What the "fits one window" tests share (the controller Test tab, the Settings tabs): the window sizes, settling a
// headless window, and the checks: no page scrolling, blocks apart and inside the page column, controls inside their card,
// no text cut off.
internal static class FitCheck
{
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

	public static IEnumerable<string> Scrolls(ScrollViewer scroll, string arrangement)
	{
		if (scroll.Extent.Height > scroll.Viewport.Height + 0.5)
		{
			yield return $"scrolls: extent {scroll.Extent.Height:F1} > viewport {scroll.Viewport.Height:F1} ({arrangement})";
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

	// No visible text under `root` is trimmed, narrower than it wants to be or wider than its box (one that does not wrap).
	public static IEnumerable<string> CutText(Visual root)
	{
		foreach (TextBlock text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
		{
			if (text.TextLayout.TextLines.Any(l => l.HasCollapsed) || text.Bounds.Width + 0.5 < text.DesiredSize.Width - text.Margin.Left - text.Margin.Right
				|| text.TextLayout.Width > text.Bounds.Width - text.Padding.Left - text.Padding.Right + 0.5)
			{
				yield return $"text cut off: \"{text.Text}\"";
			}
		}
	}

	// Inside a ScrollViewer that is itself inside `card`.
	public static bool InsideList(Control c, Control card) =>
		c.GetVisualAncestors().TakeWhile(a => a != card).Any(a => a is ScrollViewer);

	public static string Describe(Control c) => c is TextBlock t ? $"text \"{t.Text}\"" : c.GetType().Name;

	public static Rect Bounds(Visual v, Visual root) =>
		v.TransformToVisual(root) is { } m ? new Rect(v.Bounds.Size).TransformToAABB(m) : default;
}
