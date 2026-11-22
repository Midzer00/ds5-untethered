// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Lighting;
using Kopru.Core.Protocol;

namespace Kopru.App.Tests;

// The controller drawings show the light the controller has (IControllerService.ControllerLight), not the configured
// colour: the game's colour, the app's static colour, Low Power's dark light bar, another controller's light; the strip is
// off with no controller; nothing is pushed to the drawings while the snapshot is unchanged.
public class LiveLightTests
{
	private static LightOutput Light(byte r, byte g, byte b, byte leds = 0x04, long changes = 1) =>
		new(r, g, b, leds, OutputBlock.LedBrightnessHigh, changes);

	[AvaloniaFact]
	public void Drawings_FollowTheControllersLight()
	{
		var fake = new FakeControllerService(animate: false) { ControllerLight = Light(0xFF, 0x00, 0xFF) };
		var vm = new MainWindowViewModel(fake);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.Navigate(vm.Overview);
		// The configured colour is something else: the drawings must not show it.
		vm.Settings.LightbarColour = 0x2F6BFF;
		vm.Settings.Live.Refresh();
		FitCheck.Settle(window);
		ControllerArt[] arts = [.. window.GetVisualDescendants().OfType<ControllerArt>().Where(a => a.IsEffectivelyVisible)];
		Assert.True(arts.Length >= 2, "the sidebar card and the Overview drawing");
		void Expect(Color c, int leds)
		{
			vm.Settings.Live.Refresh();
			FitCheck.Settle(window);
			Assert.All(arts, a => Assert.Equal(c, a.LightColor));
			Assert.All(arts, a => Assert.Equal(leds, a.PlayerLedMask));
		}

		// The game's colour (magenta) while the user's is blue.
		Expect(Color.FromRgb(0xFF, 0, 0xFF), 0x04);
		// The app's own static colour, as sent (brightness included).
		fake.ControllerLight = Light(0x26, 0x56, 0xCC, 0x04, 2);
		Expect(Color.FromRgb(0x26, 0x56, 0xCC), 0x04);
		// Low Power: the light bar dark, the player LEDs off.
		fake.ControllerLight = Light(0, 0, 0, 0, 3);
		Expect(Colors.Black, 0);
		// Another controller selected: its own light and LEDs (slot 2).
		fake.ControllerLight = Light(0x00, 0xC0, 0x40, 0x0A, 7);
		Expect(Color.FromRgb(0, 0xC0, 0x40), 0x0A);
		// No controller: the strip is off.
		fake.IsConnected = false;
		Expect(Colors.Black, 0);
		window.Close();
	}

	// For review (KOPRU_LIVE_LIGHT_RENDER_DIR): the Overview at 1920x1080 in Turkish with a game's colour on the controller
	// (the user's is blue), and with Low Power (light bar dark).
	[AvaloniaFact]
	public void SaveReviewRenders()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_LIVE_LIGHT_RENDER_DIR");
		if (string.IsNullOrEmpty(dir))
		{
			return;
		}
		Directory.CreateDirectory(dir);
		Kopru.Core.Localization.Language? before = Kopru.Core.Localization.Localizer.Chosen;
		try
		{
			Kopru.Core.Localization.Localizer.Chosen = Kopru.Core.Localization.Language.Turkish;
			var fake = new FakeControllerService(animate: false) { ControllerLight = Light(0xFF, 0x30, 0x10) };
			var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = "tr" })));
			vm.Overview.ToggleBridgeCommand.Execute(null);
			fake.Tick();
			var window = new MainWindow { DataContext = vm, Width = 1920, Height = 1080 };
			window.Show();
			vm.Navigate(vm.Overview);
			foreach ((LightOutput light, string name) in new[] { (Light(0xFF, 0x30, 0x10), "game-colour"), (Light(0, 0, 0, 0, 2), "low-power") })
			{
				fake.ControllerLight = light;
				vm.Settings.Live.Refresh();
				FitCheck.Settle(window);
				Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
				frame!.Save(Path.Combine(dir, $"overview-{name}-1920x1080.png"));
			}
			window.Close();
		}
		finally
		{
			Kopru.Core.Localization.Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public void UnchangedSnapshot_PushesNothing()
	{
		var fake = new FakeControllerService(animate: false);
		var live = new LiveLight(fake);
		Assert.True(live.Refresh());
		int updates = live.Updates;
		int notified = 0;
		live.PropertyChanged += (_, _) => notified++;
		for (int i = 0; i < 20; i++)
		{
			Assert.False(live.Refresh());
		}
		Assert.Equal(updates, live.Updates);
		Assert.Equal(0, notified);

		// A new frame (breathing): one push.
		fake.ControllerLight = fake.ControllerLight! with { R = 0x10, Changes = fake.ControllerLight.Changes + 1 };
		Assert.True(live.Refresh());
		Assert.Equal(updates + 1, live.Updates);
		Assert.True(notified > 0);
	}
}
