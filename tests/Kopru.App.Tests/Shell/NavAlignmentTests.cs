// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The sidebar navigation rows: every icon's drawn glyph (its ink, not its layout box) sits on the same centre line as its
// label, and the selected row's accent bar is centred on the row. Measured on the rendered pixels in the owner's window
// (1152x700 layout units) at 100 % and at 225 % screen scale, in both languages. Writes nav-{100,225}-{tr,en}.png
// (the sidebar) to KOPRU_NAV_RENDER_DIR when it is set.
public sealed class NavAlignmentTests
{
	[AvaloniaTheory]
	[InlineData("tr")]
	[InlineData("en")]
	public void IconsLabelsAndIndicator_ShareEachRowsCentre(string code)
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_NAV_RENDER_DIR");
		if (outDir != null)
		{
			Directory.CreateDirectory(outDir);
		}
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = code == "tr" ? Language.Turkish : Language.English;
			var fake = new FakeControllerService(animate: false);
			var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { Language = code })));
			var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
			window.Show();
			FitCheck.Settle(window);
			Assert.Same(vm.NavItems[0], vm.SelectedNav);

			List<ListBoxItem> rows = window.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.DataContext is NavItem).ToList();
			Assert.Equal(vm.NavItems.Count, rows.Count);
			var problems = new List<string>();
			foreach (double scale in new[] { 1.0, 2.25 })
			{
				using RenderTargetBitmap frame = Render(window, scale);
				byte[] px = Pixels(frame, out int w, out _);
				foreach (ListBoxItem row in rows)
				{
					string title = ((NavItem)row.DataContext!).Title;
					Rect rowRect = InWindow(row, window);
					Control icon = row.GetVisualDescendants().OfType<Control>().First(c => c is IconSlot or Shape);
					TextBlock label = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == title);
					Rect iconRect = InWindow(icon, window), labelRect = InWindow(label, window);

					// The glyph's ink: the pixel rows in the icon's box that differ from the row's own fill just left of it.
					int x0 = (int)Math.Floor(iconRect.Left * scale), x1 = (int)Math.Ceiling(iconRect.Right * scale);
					int y0 = (int)Math.Floor((iconRect.Top - 4) * scale), y1 = (int)Math.Ceiling((iconRect.Bottom + 4) * scale);
					int top = int.MaxValue, bottom = int.MinValue;
					for (int y = y0; y < y1; y++)
					{
						int bg = ((y * w) + (int)((iconRect.Left - 3) * scale)) * 4;
						for (int x = x0; x < x1; x++)
						{
							int i = ((y * w) + x) * 4;
							if (Math.Abs(px[i] - px[bg]) + Math.Abs(px[i + 1] - px[bg + 1]) + Math.Abs(px[i + 2] - px[bg + 2]) > 60)
							{
								top = Math.Min(top, y);
								bottom = Math.Max(bottom, y);
								break;
							}
						}
					}
					Assert.True(top <= bottom, $"{title} @ {scale}: no icon drawn");
					double inkCentre = (top + bottom + 1) / 2.0 / scale;
					double labelCentre = labelRect.Center.Y, rowCentre = rowRect.Center.Y;
					if (Math.Abs(inkCentre - labelCentre) > 1)
					{
						problems.Add($"{title} @ {scale}: icon ink centre {inkCentre:F2} vs label centre {labelCentre:F2}");
					}
					if (Math.Abs(labelCentre - rowCentre) > 1)
					{
						problems.Add($"{title} @ {scale}: label centre {labelCentre:F2} vs row centre {rowCentre:F2}");
					}
					// Every icon in the same box size.
					if (Math.Abs(iconRect.Width - 20) > 0.01 || Math.Abs(iconRect.Height - 20) > 0.01)
					{
						problems.Add($"{title}: icon box {iconRect.Width}x{iconRect.Height}, not 20x20");
					}
					Border indicator = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Indicator");
					Assert.Equal(row == rows[0], indicator.IsEffectivelyVisible);
					if (indicator.IsEffectivelyVisible && Math.Abs(InWindow(indicator, window).Center.Y - rowCentre) > 0.5)
					{
						problems.Add($"{title}: indicator centre {InWindow(indicator, window).Center.Y:F2} vs row centre {rowCentre:F2}");
					}
				}
				if (outDir != null)
				{
					Rect side = InWindow(window.GetVisualDescendants().OfType<ListBox>().First(l => l.ItemsSource == vm.NavItems), window).Inflate(14);
					var crop = new PixelRect((int)(side.X * scale), (int)(side.Y * scale), (int)(side.Width * scale), (int)(side.Height * scale));
					using var cut = new RenderTargetBitmap(crop.Size, new Vector(96 * scale, 96 * scale));
					using (DrawingContext ctx = cut.CreateDrawingContext())
					{
						ctx.DrawImage(frame, new Rect(crop.X, crop.Y, crop.Width, crop.Height),
							new Rect(0, 0, crop.Width / scale, crop.Height / scale));
					}
					cut.Save(System.IO.Path.Combine(outDir, $"nav-{scale * 100:F0}-{code}.png"));
				}
			}
			window.Close();
			Assert.True(problems.Count == 0, string.Join("\n", problems));
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static Rect InWindow(Visual v, Window window)
	{
		Point p = v.TranslatePoint(default, window)!.Value;
		return new Rect(p, v.Bounds.Size);
	}

	// The window drawn at the given screen scale (as a high-DPI screen does).
	private static RenderTargetBitmap Render(Window window, double scale)
	{
		var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale));
		var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
		bitmap.Render(window);
		return bitmap;
	}

	private static byte[] Pixels(Bitmap bitmap, out int w, out int h)
	{
		w = bitmap.PixelSize.Width;
		h = bitmap.PixelSize.Height;
		byte[] px = new byte[w * h * 4];
		var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
		try
		{
			bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4);
		}
		finally
		{
			handle.Free();
		}
		return px;
	}
}
