// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Every page at the smallest window (1100x700) and a large one (1680x1050), in Turkish and English, with the bridge running
// and a game connected (so the live parts are filled). Writes {page}-{small|large}-{tr|en}.png to KOPRU_PAGE_RENDER_DIR when
// it is set (the design pass compares these before/after); otherwise only checks that every page renders.
public sealed class PageSizeRenderTests
{
	public static readonly (double Width, double Height, string Name)[] Sizes = [(1100, 700, "small"), (1400, 800, "medium"), (1680, 1050, "large"), (2560, 1440, "xl")];

	[AvaloniaFact]
	public void RenderPages_AtBothSizes()
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
				var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new Kopru.App.Services.AppSettings
				{
					GameListCheckedAt = DateTimeOffset.Now,
					Language = code,
					AlwaysGames = ["Returnal"],
					UserGames = [new Kopru.Core.Games.UserGame("Indie Game", @"C:\Games\Indie\Indie.exe")]
				})));
				vm.Overview.ToggleBridgeCommand.Execute(null);
				fake.Tick();
				foreach ((double width, double height, string size) in Sizes)
				{
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					foreach (NavItem item in vm.NavItems)
					{
						vm.SelectedNav = item;
						Settle();
						using var frame = window.CaptureRenderedFrame();
						Assert.NotNull(frame);
						if (!string.IsNullOrEmpty(outDir))
						{
							frame!.Save(Path.Combine(outDir, $"{Slug(item.Page)}-{size}-{code}.png"));
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

	// File name for a page: its view model's name without "ViewModel", in kebab case (OverviewViewModel -> overview).
	public static string Slug(object page)
	{
		string name = page.GetType().Name.Replace("PageViewModel", "").Replace("ViewModel", "");
		return string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
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
