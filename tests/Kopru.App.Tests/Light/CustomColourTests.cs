// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

// The Light page's custom colour: the seventh swatch, its hex code and Hue/Saturation sliders, remembered across preset
// switches (one for the app, not per profile), saved, chosen on load for a colour that is no preset, and the light
// following a dragged slider at most about 30 times a second.
public class CustomColourTests
{
	private static (LightViewModel Vm, SettingsState State, FakeControllerService Fake) Create(AppSettings? saved = null)
	{
		var fake = new FakeControllerService(animate: false);
		var state = saved == null ? new SettingsState(fake) : new SettingsState(fake, new MemorySettingsStore(saved));
		return (new LightViewModel(fake, state), state, fake);
	}

	[AvaloniaFact]
	public void Custom_IsTheSeventhSwatch_EmptyUntilFirstUsed()
	{
		(LightViewModel vm, SettingsState state, _) = Create();
		Assert.Equal(6, vm.Presets.Count);
		Assert.Equal(7, vm.Swatches.Count);
		Assert.Same(vm.Custom, vm.Swatches[^1]);
		Assert.True(vm.Custom.IsCustom);
		Assert.True(vm.Custom.IsEmpty);
		Assert.Null(vm.Custom.Brush);
		Assert.Null(state.LightbarCustomColour);
		Assert.Same(vm.Presets[0], vm.SelectedPreset);
		Assert.False(vm.IsCustomSelected);
	}

	[AvaloniaFact]
	public void ChoosingCustom_StartsFromTheColourOn_ThenTheSlidersAndHexSetIt()
	{
		(LightViewModel vm, SettingsState state, FakeControllerService fake) = Create();
		vm.SelectedPreset = vm.Presets[1];
		vm.SelectedPreset = vm.Custom;
		Assert.True(vm.IsCustomSelected);
		// Its hex code stands where a preset's name does.
		Assert.Equal("#FF3B3B", vm.SelectedName);
		Assert.Equal(0xFF3B3Bu, state.LightbarCustomColour);
		Assert.Equal(0xFF3B3Bu, state.LightbarColour);
		Assert.False(vm.Custom.IsEmpty);
		Assert.Equal("#FF3B3B", vm.CustomHex);
		Assert.Equal(0, vm.CustomHue);
		Assert.Equal(77, vm.CustomSaturation);

		vm.CustomHue = 200;
		vm.CustomSaturation = 80;
		vm.FlushCustomColour();
		Assert.Equal(0x33BBFFu, state.LightbarColour);
		Assert.Equal(0x33BBFFu, fake.Settings.LightbarColour);
		Assert.Equal(0x33BBFFu, state.LightbarCustomColour);
		Assert.Equal(0x33BBFFu, vm.Custom.Rgb);
		Assert.Equal("#33BBFF", vm.CustomHex);
		Assert.Equal("#33BBFF", vm.SelectedName);
		// Whole degrees and percent; the hue wraps around.
		vm.CustomHue = 359.7;
		Assert.Equal(0, vm.CustomHue);
		vm.CustomSaturation = 80.4;
		Assert.Equal(80, vm.CustomSaturation);
		vm.CustomHue = 200;
		vm.FlushCustomColour();

		// A valid code applies at once and moves the sliders; anything else goes back to the colour.
		vm.CustomHex = "#00ff00";
		vm.CommitHex();
		Assert.Equal(0x00FF00u, state.LightbarColour);
		Assert.Equal("#00FF00", vm.CustomHex);
		Assert.Equal(120, vm.CustomHue);
		Assert.Equal(100, vm.CustomSaturation);
		vm.CustomHex = "#12";
		vm.CommitHex();
		Assert.Equal("#00FF00", vm.CustomHex);
		Assert.Equal(0x00FF00u, state.LightbarColour);
		vm.CustomHex = "zzzzzz";
		vm.CommitHexCommand.Execute(null);
		Assert.Equal("#00FF00", vm.CustomHex);
	}

	[AvaloniaFact]
	public void TheCustomColour_IsRememberedAcrossPresetSwitches()
	{
		(LightViewModel vm, SettingsState state, _) = Create();
		vm.SelectedPreset = vm.Custom;
		vm.CustomHex = "#123456";
		vm.CommitHex();
		vm.SelectedPreset = vm.Presets[2];
		Assert.Equal(vm.Presets[2].Rgb, state.LightbarColour);
		Assert.Equal(0x123456u, state.LightbarCustomColour);
		Assert.Equal(0x123456u, vm.Custom.Rgb);
		Assert.False(vm.IsCustomSelected);
		vm.SelectedPreset = vm.Custom;
		Assert.Equal(0x123456u, state.LightbarColour);
		Assert.Equal("#123456", vm.CustomHex);
	}

	[AvaloniaFact]
	public void Load_ChoosesCustom_ForAColourThatIsNoPreset_AndKeepsTheSavedCustom()
	{
		(LightViewModel vm, SettingsState state, _) = Create(new AppSettings { LightbarColour = 0x123456 });
		Assert.Same(vm.Custom, vm.SelectedPreset);
		Assert.Equal(0x123456u, state.LightbarColour);
		Assert.Equal(0x123456u, vm.Custom.Rgb);
		Assert.Equal("#123456", vm.CustomHex);

		// A preset colour on with a saved custom colour: the preset is chosen, Custom shows the saved one.
		(LightViewModel withSaved, SettingsState savedState, _) = Create(new AppSettings { LightbarColour = 0xFF3B3B, LightbarCustomColour = 0xABCDEF });
		Assert.Same(withSaved.Presets[1], withSaved.SelectedPreset);
		Assert.Equal(0xABCDEFu, withSaved.Custom.Rgb);
		withSaved.SelectedPreset = withSaved.Custom;
		Assert.Equal(0xABCDEFu, savedState.LightbarColour);
	}

	[AvaloniaFact]
	public void SavedAndLoaded_OnceForTheApp_NotPerProfile()
	{
		string path = Path.Combine(Path.GetTempPath(), $"untethered-customcolour-{Guid.NewGuid():N}.json");
		try
		{
			var fake = new FakeControllerService(animate: false);
			var state = new SettingsState(fake, new JsonSettingsStore(path));
			var vm = new LightViewModel(fake, state);
			ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues())!;
			vm.SelectedPreset = vm.Custom;
			vm.CustomHex = "#336699";
			vm.CommitHex();
			Assert.Equal(0x336699u, state.Profiles.Default.Values.LightbarColour);

			// Another profile with a preset: the preset shows, Custom still holds the app's custom colour.
			state.ActivateProfile(racing.Id);
			Assert.Same(vm.Presets[0], vm.SelectedPreset);
			Assert.Equal(0x336699u, vm.Custom.Rgb);
			state.ActivateProfile(ProfileBook.DefaultId);
			Assert.Same(vm.Custom, vm.SelectedPreset);
			state.SaveNow();

			var again = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.Equal(0x336699u, again.LightbarCustomColour);
			Assert.Equal(0x336699u, again.LightbarColour);
		}
		finally
		{
			File.Delete(path);
		}

		// An older settings file: no custom colour yet.
		Assert.Null(new SettingsState(new FakeControllerService(animate: false), new MemorySettingsStore(new AppSettings())).LightbarCustomColour);
	}

	[AvaloniaFact]
	public void DraggingASlider_UpdatesTheLightAtMost30TimesASecond_AndTheLastValueArrives()
	{
		(LightViewModel vm, SettingsState state, FakeControllerService fake) = Create();
		vm.SelectedPreset = vm.Custom;
		int before = fake.TestCount;
		for (int hue = 1; hue <= 40; hue++)
		{
			vm.CustomHue = hue;
		}
		// The swatch and the code follow every step; the light did not take all forty.
		Assert.Equal(LightColour.ToHex(LightColour.FromHueSaturation(40, vm.CustomSaturation)), vm.CustomHex);
		Assert.True(fake.TestCount - before < 10, $"{fake.TestCount - before} light updates");
		vm.FlushCustomColour();
		Assert.False(vm.HasPendingCustomColour);
		Assert.Equal(LightColour.FromHueSaturation(40, vm.CustomSaturation), state.LightbarColour);

		// After the interval the next step goes out at once.
		Thread.Sleep(LightViewModel.LiveInterval + TimeSpan.FromMilliseconds(20));
		int count = fake.TestCount;
		vm.CustomHue = 90;
		Assert.False(vm.HasPendingCustomColour);
		Assert.Equal(count + 1, fake.TestCount);
		Assert.Equal(LightColour.FromHueSaturation(90, vm.CustomSaturation), state.LightbarColour);
	}

	// On the page: the hex code stands where a preset's name does; a click on Custom (also when it is already chosen) opens
	// the colour disc with the hex box under it, anchored to the swatch; a press and drag on the disc picks the colour; the
	// card is as tall as with a preset.
	[AvaloniaFact]
	public void Page_ClickingCustom_OpensTheDisc_AndTheDiscPicksTheColour()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = "en" })));
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.Navigate(vm.Light);
		FitCheck.Settle(window);
		LightView page = window.GetVisualDescendants().OfType<LightView>().Single();
		Control Named(string name) => page.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
		ListBox swatches = (ListBox)Named("ColourSwatches");
		Assert.Equal(7, swatches.GetVisualDescendants().OfType<ListBoxItem>().Count());
		ScrollViewer card = Named("OwnLightCard").GetVisualDescendants().OfType<ScrollViewer>().First();
		double presetHeight = card.Extent.Height;
		Assert.False(page.IsCustomColourOpen);

		void Click(Control target)
		{
			Point p = FitCheck.Bounds(target, window).Center;
			window.MouseDown(p, MouseButton.Left);
			window.MouseUp(p, MouseButton.Left);
			FitCheck.Settle(window);
		}
		Control custom = swatches.ContainerFromItem(vm.Light.Custom)!;
		Click(custom);
		Assert.Same(vm.Light.Custom, vm.Light.SelectedPreset);
		Assert.True(page.IsCustomColourOpen);
		Assert.Equal(LightColour.ToHex(vm.Settings.LightbarColour), ((TextBlock)Named("ColourName")).Text);
		Assert.Equal(presetHeight, card.Extent.Height, 1);
		Assert.Equal(vm.Light.CustomHex, page.CustomHexBox.Text);
		Assert.True(Named("BrightnessSection").IsEffectivelyVisible);
		Assert.Empty(FlyoutPlacementProblems(page, window));

		// A press on the disc's right edge: hue 90, full saturation; a drag down to the centre's level and past the rim stays on it.
		ColourWheel wheel = page.ColourWheel;
		Rect disc = FitCheck.Bounds(wheel, window);
		double r = ColourWheel.DiscRadius(wheel.Bounds.Size) * disc.Width / wheel.Bounds.Width;
		window.MouseDown(new Point(disc.Center.X + r * 0.5, disc.Center.Y), MouseButton.Left);
		FitCheck.Settle(window);
		Assert.Equal(90, vm.Light.CustomHue);
		Assert.Equal(50, vm.Light.CustomSaturation);
		window.MouseMove(new Point(disc.Center.X, disc.Center.Y + r * 3));
		window.MouseUp(new Point(disc.Center.X, disc.Center.Y + r * 3), MouseButton.Left);
		FitCheck.Settle(window);
		vm.Light.FlushCustomColour();
		Assert.Equal(180, vm.Light.CustomHue);
		Assert.Equal(100, vm.Light.CustomSaturation);
		Assert.Equal(LightColour.FromHueSaturation(180, 100), vm.Settings.LightbarColour);
		Assert.Equal("#00FFFF", page.CustomHexBox.Text);
		Assert.Equal("#00FFFF", ((TextBlock)Named("ColourName")).Text);

		// Typing an invalid code and leaving the box puts the colour back.
		page.CustomHexBox.Text = "#nope";
		Assert.Equal("#nope", vm.Light.CustomHex);
		page.CustomHexBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
		FitCheck.Settle(window);
		Assert.Equal("#00FFFF", page.CustomHexBox.Text);
		Assert.Empty(FitCheck.CutText(page.CustomColourPanel));

		// Closed, then a click on Custom while it is chosen opens it again.
		page.CustomColourFlyout.Hide();
		FitCheck.Settle(window);
		Assert.False(page.IsCustomColourOpen);
		Click(custom);
		Assert.True(page.IsCustomColourOpen);
		page.CustomColourFlyout.Hide();

		// A preset's click opens nothing; its name shows again.
		Click(swatches.ContainerFromItem(vm.Light.Presets[3])!);
		Assert.False(page.IsCustomColourOpen);
		Assert.Equal(vm.Light.Presets[3].Name, ((TextBlock)Named("ColourName")).Text);
		window.Close();
	}

	// The disc: where the marker sits for a colour, the colour under a point, the rim's clamp, the arrow keys.
	[AvaloniaFact]
	public void Disc_MarkerAndPoints_RoundTrip()
	{
		var size = new Size(196, 196);
		double r = ColourWheel.DiscRadius(size);
		Assert.Equal(90, r);
		Point centre = ColourWheel.Centre(size);
		Assert.Equal(centre, ColourWheel.MarkerPosition(size, 123, 0));
		Assert.Equal(new Point(98, 8), ColourWheel.MarkerPosition(size, 0, 100));
		Point right = ColourWheel.MarkerPosition(size, 90, 50);
		Assert.Equal(98 + 45, right.X, 6);
		Assert.Equal(98, right.Y, 6);
		for (int hue = 0; hue < 360; hue += 15)
		{
			for (int saturation = 5; saturation <= 100; saturation += 19)
			{
				(double? h, double s) = ColourWheel.HueSaturationAt(size, ColourWheel.MarkerPosition(size, hue, saturation));
				Assert.Equal(hue, h!.Value, 6);
				Assert.Equal(saturation, s, 6);
			}
		}
		// Past the rim: on the rim; the centre has no hue.
		(double? outHue, double outSat) = ColourWheel.HueSaturationAt(size, new Point(98, 196 + 50));
		Assert.Equal(180, outHue!.Value, 6);
		Assert.Equal(100, outSat);
		Assert.Null(ColourWheel.HueSaturationAt(size, centre).Hue);
		Assert.True(ColourWheel.IsOnDisc(size, new Point(98, 10)));
		Assert.False(ColourWheel.IsOnDisc(size, new Point(2, 2)));

		var wheel = new ColourWheel { Width = 196, Height = 196, Hue = 40, Saturation = 60 };
		var window = new Window { Content = wheel, Width = 300, Height = 300 };
		window.Show();
		FitCheck.Settle(window);
		wheel.PickAt(centre);
		Assert.Equal(40, wheel.Hue);
		Assert.Equal(0, wheel.Saturation);
		wheel.Saturation = 50;
		foreach ((Key key, double hue, double saturation) in new[] { (Key.Right, 45.0, 50.0), (Key.Left, 40.0, 50.0), (Key.Up, 40.0, 55.0), (Key.Down, 40.0, 50.0) })
		{
			wheel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
			Assert.Equal((hue, saturation), (wheel.Hue, wheel.Saturation));
		}
		wheel.Hue = 2;
		wheel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
		Assert.Equal(357, wheel.Hue);
		window.Close();
	}

	// The disc's bitmap: white at the centre, the full hue at the rim (red at the top), clear outside, at the screen's pixels.
	[AvaloniaFact]
	public void DiscBitmap_IsWhiteInTheMiddle_FullColourAtTheRim()
	{
		using var bitmap = ColourWheel.BuildDisc(200, 100);
		Assert.Equal(new PixelSize(200, 200), bitmap.PixelSize);
		Assert.Equal(new Size(100, 100), bitmap.Size);
		int[] pixels = new int[200 * 200];
		using (var frame = bitmap.Lock())
		{
			for (int y = 0; y < 200; y++)
			{
				System.Runtime.InteropServices.Marshal.Copy(frame.Address + y * frame.RowBytes, pixels, y * 200, 200);
			}
		}
		uint At(int x, int y) => (uint)pixels[y * 200 + x];
		Assert.Equal(0xFFFFFFFFu, At(100, 100) | 0x00030303);
		uint top = At(100, 3);
		Assert.Equal(0xFFu, top >> 24);
		Assert.True((top >> 16 & 0xFF) == 0xFF && (top >> 8 & 0xFF) < 0x20 && (top & 0xFF) < 0x20, $"{top:X8}");
		Assert.Equal(0u, At(0, 0));
		Assert.Equal(0u, At(199, 199));
	}
	// The pop-up opens above the swatches, its right edge on the Custom swatch's, inside the settings column and the
	// window, never over the swatches, the colour's title and name, Brightness or the preview (both sizes, both languages).
	[AvaloniaFact]
	public void ColourPopUp_OpensAboveTheSwatches_InsideTheColumn()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in new[] { (1100.0, 700.0), (1152.0, 700.0) })
				{
					(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.Navigate(vm.Light);
					vm.Light.SelectedPreset = vm.Light.Custom;
					FitCheck.Settle(window);
					LightView page = window.GetVisualDescendants().OfType<LightView>().Single();
					page.OpenCustomColour();
					FitCheck.Settle(window);
					problems.AddRange(FlyoutPlacementProblems(page, window).Select(p => $"{code} {width}x{height}: {p}"));
					page.CustomColourFlyout.Hide();
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// The pop-up's box (its presenter, border included) in window coordinates.
	internal static Rect FlyoutRect(LightView page, Window window)
	{
		Control box = page.CustomColourPanel.GetVisualAncestors().OfType<FlyoutPresenter>().FirstOrDefault() ?? page.CustomColourPanel;
		return FitCheck.Bounds(box, window);
	}

	private static IEnumerable<string> FlyoutPlacementProblems(LightView page, Window window)
	{
		if (!page.IsCustomColourOpen)
		{
			yield return "not open";
			yield break;
		}
		Control Named(string name) => page.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
		var swatches = (ListBox)Named("ColourSwatches");
		Rect box = FlyoutRect(page, window);
		Rect custom = FitCheck.Bounds(swatches.ContainerFromItem(((LightViewModel)page.DataContext!).Custom)!, window);
		Rect row = FitCheck.Bounds(swatches, window);
		Rect name = FitCheck.Bounds(Named("ColourName"), window);
		Rect title = FitCheck.Bounds(page.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == ((LightViewModel)page.DataContext!).ColourLabel), window);
		Rect brightness = FitCheck.Bounds(Named("BrightnessSection"), window);
		Rect column = FitCheck.Bounds(Named("OwnLightCard"), window).Union(FitCheck.Bounds(Named("GameLightCard"), window));
		Rect preview = FitCheck.Bounds(page.GetVisualDescendants().OfType<ControllerArt>().Single(), window);
		if (box.Bottom > row.Top + 0.5) yield return $"pop-up {box} reaches the swatches {row}";
		if (Math.Abs(box.Right - custom.Right) > 1) yield return $"pop-up {box} does not end on the Custom swatch {custom}";
		if (box.Intersects(title)) yield return $"pop-up {box} covers the colour's title {title}";
		if (box.Intersects(name)) yield return $"pop-up {box} covers the colour's name {name}";
		if (box.Intersects(brightness)) yield return $"pop-up {box} covers Brightness {brightness}";
		if (box.Intersects(preview)) yield return $"pop-up {box} covers the preview {preview}";
		if (box.Left < column.Left - 0.5 || box.Right > column.Right + 0.5) yield return $"pop-up {box} outside the settings column {column}";
		if (box.Top < 0 || box.Bottom > window.Bounds.Height || box.Right > window.Bounds.Width) yield return $"pop-up {box} outside the window {window.Bounds}";
	}
}