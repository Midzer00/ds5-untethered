// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// With KOPRU_UI_SHOTS_DIR set: the Overview (Bluetooth, charging, wired, no controller, a game running with Close Game and its
// question; 1100x700, 1152x700, 960x640), its sidebar cards, and the game panel's three tabs (1100x700, Turkish) as PNGs there,
// for review. Otherwise it does nothing. The App tests' log is redirected (TestLogSetup).
public sealed class UiShotsRenderTests
{
	[AvaloniaFact]
	public async Task RenderShots()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_UI_SHOTS_DIR") is { Length: > 0 } d ? d : null;
		if (dir == null)
		{
			return;
		}
		Directory.CreateDirectory(dir);
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			foreach ((string name, double w, double h) in OverviewShots)
			{
				(MainWindowViewModel ovm, MainWindow owindow, FakeControllerService ofake) = OpenOverview(name, w, h);
				Save(owindow, Path.Combine(dir, $"overview-{name}-{w:0}x{h:0}.png"));
				if (w == 1100 && name is "bt" or "charge" or "wired" or "not-connected")
				{
					SaveSidebar(owindow, Path.Combine(dir, $"sidebar-{name}.png"));
				}
				if (name == "game")
				{
					// The close request left the game open: the header asks in the button's place.
					ofake.Watcher.Source.LeavesOnCloseRequest = false;
					ovm.BackToGame.Delay = _ => Task.CompletedTask;
					await ovm.BackToGame.CloseGameCommand.ExecuteAsync(null);
					Settle();
					Save(owindow, Path.Combine(dir, $"overview-game-confirm-{w:0}x{h:0}.png"));
				}
				owindow.Close();
			}
			foreach (bool running in new[] { false, true })
			{
				(MainWindowViewModel gvm, MainWindow gwindow) = await OpenSample(running, 1100, 700);
				GamePanelViewModel panel = gvm.SupportedGames.Panel;
				(int Segment, string Name)[] shots = running
					? [(GamePanelViewModel.GameSegment, "game-oyun-running")]
					: [(GamePanelViewModel.GameSegment, "game-oyun"), (GamePanelViewModel.VibrationSegment, "game-titresim"), (GamePanelViewModel.TriggersSegment, "game-tetikler")];
				foreach ((int segment, string name) in shots)
				{
					panel.SelectedSegment = segment;
					Settle();
					Save(gwindow, Path.Combine(dir, name + ".png"));
				}
				gwindow.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The game panel with the design mockup's sample data: a made-up catalog game, "Örnek Oyun" (found by a fake Steam
	// library, so Hide shows), with a profile of its own named after it, Game Haptics 150 %, sound to haptics on,
	// Resistance (20 %, 50 %) on the left trigger and Weapon (30 %, 60 %, 70 %) on the right one; running: its exe in front.
	internal static async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenSample(bool running, double width, double height)
	{
		const string Name = "Örnek Oyun", Exe = "ornek-oyun.exe";
		var fake = new FakeControllerService(animate: false);
		var catalog = new GameCatalog([new CatalogGame(Name, [Exe], 900001, true, true, "high")]);
		fake.Games.SetCatalog(catalog);
		fake.Watcher.Catalog = catalog;
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Watcher.Exe = running ? Exe : null;
		fake.Games.Tick();
		var folders = new FakeGameFolders(@"C:\Steam\steamapps\common\Ornek Oyun");
		folders.File(@"C:\Steam\steamapps\appmanifest_900001.acf",
			$"\"AppState\"\n{{\n\t\"appid\"\t\t\"900001\"\n\t\"name\"\t\t\"{Name}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Ornek Oyun\"\n}}\n");
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = "tr" }),
			InstalledGames: FakeFoundGames.Installed(folders)));
		await vm.SupportedGames.ScanTask;
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == Name));
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		panel.UseOwnSettingsCommand.Execute(null);
		panel.SaveProfileNameCommand.Execute(null);
		panel.Feel.GameHapticsLevel = 150;
		panel.AudioHaptics.On = true;
		panel.Feel.LeftTrigger.EffectIndex = 1;
		panel.Feel.LeftTrigger.ResistanceStart = 20;
		panel.Feel.LeftTrigger.Force = 50;
		panel.Feel.RightTrigger.EffectIndex = 2;
		panel.Feel.RightTrigger.WeaponStart = 30;
		panel.Feel.RightTrigger.WeaponEnd = 60;
		panel.Feel.RightTrigger.Force = 70;
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		Settle();
		return (vm, window);
	}

	// The Overview shots: the four link states at 1100x700, a game running (Close Game in the header), and the narrow sizes.
	private static readonly (string Name, double W, double H)[] OverviewShots =
	[
		("bt", 1100, 700), ("charge", 1100, 700), ("wired", 1100, 700), ("not-connected", 1100, 700), ("game", 1100, 700),
		("waiting", 1100, 700), ("virtual-off", 1100, 700), ("cable-only", 1100, 700),
		("bt", 1152, 700), ("wired", 1152, 700), ("bt", 960, 640), ("wired", 960, 640), ("not-connected", 960, 640),
	];

	// The Overview in one state with the design study's sample data (direction A's scenarios): "bt" (a good link), "charge"
	// (the cable only charging), "wired" (the cable as the link: 0,5 ms, 250 reports/s), "not-connected" (paired, asleep,
	// last battery 62 % two hours ago), "waiting" (the virtual controller on, no controller), "virtual-off" (connected, the
	// virtual controller off), "cable-only" (Bluetooth lost on the cable) and "game" (a made-up game running, "Örnek Oyun").
	// No real game is in front otherwise. language: "tr" or "en".
	internal static (MainWindowViewModel Vm, MainWindow Window, FakeControllerService Fake) OpenOverview(string state, double width, double height, string language = "tr")
	{
		var fake = new FakeControllerService(animate: false);
		fake.Watcher.Exit("Cyberpunk2077.exe");
		if (state == "game")
		{
			var catalog = new GameCatalog([new CatalogGame("Örnek Oyun", ["ornek-oyun.exe"], 900001, true, true, "high")]);
			fake.Games.SetCatalog(catalog);
			fake.Watcher.Catalog = catalog;
			fake.Watcher.Exe = "ornek-oyun.exe";
		}
		fake.Games.Tick();
		// Nothing pressed on the drawing (the fake's default input holds the d-pad up).
		fake.RawInput = [128, 128, 128, 128, 0, 0, 0, 8, 0];
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = language })));
		bool connected = state is "bt" or "charge" or "wired" or "game" or "virtual-off";
		if (connected)
		{
			vm.Settings.MicEnabled = true;
			fake.CablePluggedIn = state is "charge" or "wired";
			fake.IsCharging = state is "charge" or "wired";
			fake.BatteryPercent = state switch { "charge" => 64, "wired" => 100, _ => 78 };
			fake.IsWired = state == "wired";
			// The controller as the service lists it, so the sidebar offers the rename pencil.
			fake.ControllerList.Add(new ControllerEntry("pad-1", 1, "DualSense", false, true, fake.BatteryPercent, fake.IsCharging, Kopru.Core.Bridge.BridgeState.Running));
		}
		else
		{
			fake.IsConnected = false;
			fake.Health = Report(state == "cable-only");
			if (state != "cable-only")
			{
				fake.LastKnownBattery = new Kopru.Core.Health.LastKnownBattery(62, false, DateTimeOffset.Now.AddHours(-2));
			}
		}
		if (connected && state != "virtual-off")
		{
			vm.Overview.ToggleBridgeCommand.Execute(null);
			fake.Tick();
			bool wired = state == "wired";
			(double[] worst, double[] typical, bool[] trouble) = wired ? Chart(0.5, 0.9, 33) : Chart(1.2, 3.4, 11, troubleCount: 3, spikeMs: 5.5);
			for (int i = 0; i < worst.Length; i++)
			{
				fake.AddSecond(typical[i], worst[i], wired ? 250 : 718, lost: trouble[i] ? 3 : 0, longGaps: trouble[i] && i % 3 == 0 ? 1 : 0);
			}
			fake.Latency = wired ? new(0.5, 0.9, 0.05) : new(1.2, 3.4, 0.05);
			fake.InputRateHz = wired ? 250 : 718;
			fake.LinkQuality = wired ? null : new Kopru.Core.Diagnostics.LinkQualityReading(Kopru.Core.Diagnostics.LinkQuality.Good, 3.4, 0, 0, 718);
		}
		else if (state == "waiting")
		{
			vm.Overview.ToggleBridgeCommand.Execute(null);
		}
		fake.RaiseStateChanged();
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		vm.SelectedNav = vm.NavItems[0];
		Settle();
		vm.Overview.Live.Sample();
		Settle();
		return (vm, window, fake);
	}

	// A health report with everything installed and the controller paired: asleep (no device), or on a USB cable with the
	// radio off (the real "on a cable" state).
	private static Kopru.Core.Health.HealthReport Report(bool usb)
	{
		Kopru.Core.Hid.HidDevice[] devices = usb ? [new Kopru.Core.Hid.HidDevice(@"\?\hid#vid_054c&pid_0ce6&mi_03#usb_fake", 0x054C, 0x0CE6, 64, 64, 64)] : [];
		return Kopru.Core.Health.HealthReport.Build(
			new Kopru.Core.Health.HealthEnvironment(new Kopru.Core.Setup.ComponentInfo(true, "1.0", null, true), new Kopru.Core.Setup.ComponentInfo(true, "1.6", null, true),
				usb ? Kopru.Core.Setup.RadioState.Off : Kopru.Core.Setup.RadioState.On, [new Kopru.Core.Health.PairedController("DualSense", 1, false, false)], null, null),
			new Kopru.Core.Health.HealthRuntime(devices, false, Kopru.Core.Bridge.BridgeState.Stopped, Kopru.Core.Bridge.BridgeError.None, null),
			DateTimeOffset.Now);
	}

	// A minute of chart history, the design study's generator: typical and worst wobble around the given numbers (never a
	// flat line); troubleCount seconds get a trouble mark and a spike up to roughly spikeMs.
	private static (double[] Worst, double[] Typical, bool[] Trouble) Chart(double typical, double worst, int seed, int troubleCount = 0, double spikeMs = 0)
	{
		var rnd = new Random(seed);
		var w = new double[60];
		var t = new double[60];
		var tr = new bool[60];
		for (int i = 0; i < 60; i++)
		{
			double wave = Math.Sin(i / 2.3 + seed);
			t[i] = Math.Max(0.2, typical + typical * 0.18 * wave + (rnd.NextDouble() - 0.5) * typical * 0.2);
			w[i] = Math.Max(t[i] + 0.4, worst + worst * 0.18 * wave + (rnd.NextDouble() - 0.5) * worst * 0.22);
		}
		for (int n = 0; n < troubleCount; n++)
		{
			int i = rnd.Next(60);
			tr[i] = true;
			w[i] = Math.Max(w[i], spikeMs * (0.85 + 0.3 * rnd.NextDouble()));
		}
		return (w, t, tr);
	}

	// The sidebar card with 8 px around it, as the design study crops it.
	private static void SaveSidebar(MainWindow window, string path)
	{
		Border card = FindSidebarCard(window);
		Point p = card.TranslatePoint(new Point(0, 0), window)!.Value;
		Point q = card.TranslatePoint(new Point(card.Bounds.Width, card.Bounds.Height), window)!.Value;
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using var frame = window.CaptureRenderedFrame()!;
		Rect bounds = new Rect(p, q).Inflate(8).Intersect(new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height));
		var px = new PixelRect((int)Math.Floor(bounds.X), (int)Math.Floor(bounds.Y), (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
		using var crop = new RenderTargetBitmap(px.Size);
		using (var ctx = crop.CreateDrawingContext())
		{
			ctx.DrawImage(frame, new Rect(px.X, px.Y, px.Width, px.Height), new Rect(0, 0, px.Width, px.Height));
		}
		crop.Save(path);
	}

	// The sidebar's device card: the nearest Border around the sidebar's controller picture.
	private static Border FindSidebarCard(MainWindow window) =>
		window.GetVisualDescendants().OfType<Kopru.App.Controls.ControllerArt>().First()
			.GetVisualAncestors().OfType<Border>().First(b => b.Bounds.Width > 150);

	private static void Save(MainWindow window, string path)
	{
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using var frame = window.CaptureRenderedFrame();
		frame!.Save(path);
	}

	private static void Settle()
	{
		for (int i = 0; i < 3; i++)
		{
			Dispatcher.UIThread.RunJobs();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			Thread.Sleep(120);
		}
	}
}
