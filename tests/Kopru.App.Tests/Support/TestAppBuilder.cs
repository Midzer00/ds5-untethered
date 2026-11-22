// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Kopru.App;

[assembly: AvaloniaTestApplication(typeof(Kopru.App.Tests.TestAppBuilder))]

namespace Kopru.App.Tests;

public static class TestAppBuilder
{
	public static AppBuilder BuildAvaloniaApp()
	{
		// KOPRU_TEST_CULTURE (e.g. tr-TR, en-US) runs the suite under that culture, like the Core tests.
		string? name = Environment.GetEnvironmentVariable("KOPRU_TEST_CULTURE");
		if (!string.IsNullOrWhiteSpace(name))
		{
			CultureInfo culture = CultureInfo.GetCultureInfo(name);
			CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
			CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
		}
		return AppBuilder.Configure<App>()
			.UseSkia()
			.WithInterFont()
			.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
	}
}
