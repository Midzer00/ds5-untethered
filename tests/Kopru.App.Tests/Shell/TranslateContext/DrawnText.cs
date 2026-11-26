// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Text a control draws in its own Render (a diagram's labels, a plot's words): the Render is recorded into a DrawingGroup
// and every glyph run in it is decoded; a key's box is the union of the runs that carry it.
internal static class DrawnText
{
	public static void Scan(Visual visual, Window window, List<ContextItem> items, List<string> untagged)
	{
		var group = new DrawingGroup();
		try
		{
			using DrawingContext context = group.Open();
			visual.Render(context);
		}
		catch (NotImplementedException)
		{
			// The recording context cannot hold every drawing (bitmaps): such a control (the controller art) draws no text.
			return;
		}
		var runs = new List<(GlyphRun Run, Matrix Transform)>();
		Collect(group, Matrix.Identity, runs);
		double scale = KeyTagScanner.ScaleToWindow(visual, window);
		foreach ((GlyphRun run, Matrix transform) in runs)
		{
			string chars = run.Characters.ToString();
			KeyTagDecoding decoding = KeyTagLocale.Decode(chars);
			if (decoding.Spans.Count == 0)
			{
				if (decoding.Clean.Any(char.IsLetter))
				{
					untagged.Add(decoding.Clean);
				}
				continue;
			}
			Rect local = RunBox(run).TransformToAABB(transform);
			Rect shown = KeyTagScanner.VisibleRect(visual, local, window);
			if (shown.Width <= 0.01 || shown.Height <= 0.01 || KeyTagScanner.IsCovered(visual, shown, window))
			{
				continue;
			}
			double size = Math.Round(run.FontRenderingEmSize * scale, 2);
			double lineHeight = Math.Round(local.Height * scale, 2);
			int weight = (int)run.GlyphTypeface.Weight;
			foreach (string key in decoding.Spans.Select(s => s.Key).Distinct(StringComparer.Ordinal))
			{
				items.Add(new ContextItem(key, shown, size, weight, false, "left", Math.Round(shown.Width, 1), 1, lineHeight, false,
					KeyTagScanner.IsFormatted(key), decoding.Spans.Count > 1));
			}
		}
	}

	// The run's line box: its advance width from the baseline origin, from the ascent to the descent.
	private static Rect RunBox(GlyphRun run)
	{
		Rect bounds = run.Bounds;
		return new Rect(run.BaselineOrigin.X + bounds.X, run.BaselineOrigin.Y + bounds.Y, bounds.Width, bounds.Height);
	}

	private static void Collect(Drawing drawing, Matrix transform, List<(GlyphRun, Matrix)> runs)
	{
		switch (drawing)
		{
			case DrawingGroup group:
				Matrix inner = group.Transform is { } t ? t.Value * transform : transform;
				foreach (Drawing child in group.Children)
				{
					Collect(child, inner, runs);
				}
				break;
			case GlyphRunDrawing glyphs when glyphs.GlyphRun is { } run:
				runs.Add((run, transform));
				break;
		}
	}
}
