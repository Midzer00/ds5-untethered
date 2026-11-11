// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The controller page's two tabs at the smallest and a large window, in Turkish and English: both tabs render, nothing on
// them is wider than the page column, and with KOPRU_PAGE_RENDER_DIR set each is saved as controller-{test|remap}-{size}-{lang}.png
// (and the whole scrolled page as ...-full-... so the parts below the fold can be reviewed too).
public sealed class ControllerPageRenderTests
{
	[AvaloniaFact]
	public void BothTabs_RenderInsideThePageColumn_AtBothSizes()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		if (!string.IsNullOrEmpty(outDir))
		{
			Directory.CreateDirectory(outDir);
		}
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				var fake = new FakeControllerService(animate: false);
				var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code })));
				vm.Overview.ToggleBridgeCommand.Execute(null);
				fake.Tick();
				foreach ((double width, double height, string size) in PageSizeRenderTests.Sizes)
				{
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					foreach ((int tab, string name) in new[] { (ControllerPageViewModel.TestTab, "test"), (ControllerPageViewModel.RemapTab, "remap") })
					{
						vm.OpenController(tab);
						Settle();
						ControllerView page = window.GetVisualDescendants().OfType<ControllerView>().Single();
						StackPanel column = page.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("page"));
						Assert.True(column.Bounds.Width <= column.MaxWidth + 0.5, $"{name} {size}: column {column.Bounds.Width}");
						using (var frame = window.CaptureRenderedFrame())
						{
							Assert.NotNull(frame);
							if (!string.IsNullOrEmpty(outDir))
							{
								frame!.Save(Path.Combine(outDir, $"controller-{name}-{size}-{code}.png"));
							}
						}
						if (!string.IsNullOrEmpty(outDir))
						{
							// The whole page: grow the window to the page's height for one capture
							ScrollViewer scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First();
							window.Height = height + Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
							Settle();
							using var full = window.CaptureRenderedFrame();
							full!.Save(Path.Combine(outDir, $"controller-{name}-full-{size}-{code}.png"));
							window.Height = height;
							Settle();
						}
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Thread.Sleep(320);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}
}
