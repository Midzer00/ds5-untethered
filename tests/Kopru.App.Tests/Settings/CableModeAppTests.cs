// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Views;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

// "When The Cable Is Plugged In" (Controller page, Outputs tab, its own card): Charge Only by default — wired mode is
// opt-in (an app setting: saved, pushed, not per profile); the service applies it to the cable switch live.
public class CableModeAppTests
{
	private readonly TestControllers controllers = new();

	[Fact]
	public void Defaults_ChargeOnlyInTheAppAndTheSettingsRecord_AndPushed()
	{
		FakeControllerService fake = new();
		SettingsState settings = new(fake);
		Assert.Equal(CableMode.ChargeOnly, settings.CableMode);
		Assert.Equal(CableMode.ChargeOnly, SettingsState.DefaultCableMode);
		Assert.Null(new AppSettings().CableMode);
		Assert.Equal(CableMode.ChargeOnly, new ControllerSettings().CableMode);
		Assert.Equal(CableMode.ChargeOnly, fake.Settings.CableMode);
	}

	// A saved file without the key (no choice made yet, or written while the default was Wired) gives Charge Only;
	// only a stored Wired turns wired mode on.
	[Fact]
	public void Loading_WithoutTheKey_GivesChargeOnly_AStoredWiredGivesWired()
	{
		FakeControllerService fake = new();
		var settings = new SettingsState(fake, new MemorySettingsStore(new AppSettings()));
		Assert.Equal(CableMode.ChargeOnly, settings.CableMode);
		Assert.Equal(CableMode.ChargeOnly, fake.Settings.CableMode);

		FakeControllerService wiredFake = new();
		var wired = new SettingsState(wiredFake, new MemorySettingsStore(new AppSettings { CableMode = CableMode.Wired }));
		Assert.Equal(CableMode.Wired, wired.CableMode);
		Assert.Equal(CableMode.Wired, wiredFake.Settings.CableMode);
	}

	[Fact]
	public void Changing_Pushes_AndSaves()
	{
		FakeControllerService fake = new();
		SettingsState settings = new(fake);
		settings.CableMode = CableMode.Wired;
		Assert.Equal(CableMode.Wired, fake.Settings.CableMode);
		Assert.Equal(CableMode.Wired, settings.ToAppSettings().CableMode);
		settings.CableMode = CableMode.ChargeOnly;
		Assert.Equal(CableMode.ChargeOnly, fake.Settings.CableMode);
		Assert.Null(settings.ToAppSettings().CableMode);
	}

	[Fact]
	public void Loading_StoredChargeOnly_GivesChargeOnly()
	{
		FakeControllerService fake = new();
		var settings = new SettingsState(fake, new MemorySettingsStore(new AppSettings { CableMode = CableMode.ChargeOnly }));
		Assert.Equal(CableMode.ChargeOnly, settings.CableMode);
		Assert.Equal(CableMode.ChargeOnly, fake.Settings.CableMode);
	}

	[Fact]
	public void JsonStore_MissingPropertyReadsAsUnset_AndWiredRoundTrips()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "settings.json");
			File.WriteAllText(path, "{}");
			var store = new JsonSettingsStore(path);
			Assert.Null(store.Load()!.CableMode);
			Assert.Equal(CableMode.ChargeOnly, new SettingsState(new FakeControllerService(), store).CableMode);
			store.Save(new AppSettings { CableMode = CableMode.Wired });
			Assert.Equal(CableMode.Wired, store.Load()!.CableMode);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	// The default stays out of the file (missing = the app's default, so a later change of it still moves users who
	// never chose); a chosen Wired is written.
	[Fact]
	public void TheDefault_IsNotWrittenToTheFile()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "settings.json");
			var store = new JsonSettingsStore(path);
			var fake = new FakeControllerService();
			var settings = new SettingsState(fake);
			store.Save(settings.ToAppSettings());
			Assert.DoesNotContain("CableMode", File.ReadAllText(path));
			settings.CableMode = CableMode.Wired;
			store.Save(settings.ToAppSettings());
			Assert.Contains("CableMode", File.ReadAllText(path));
			Assert.Equal(CableMode.Wired, store.Load()!.CableMode);
			settings.CableMode = CableMode.ChargeOnly;
			store.Save(settings.ToAppSettings());
			Assert.DoesNotContain("CableMode", File.ReadAllText(path));
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	// An app setting: a profile switch must not touch it.
	[AvaloniaFact]
	public void SwitchingProfiles_DoesNotChangeIt()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues())!;
		state.CableMode = CableMode.Wired;
		state.ActivateProfile(racing.Id);
		Assert.Equal(CableMode.Wired, state.CableMode);
		state.ActivateProfile(ProfileBook.DefaultId);
		Assert.Equal(CableMode.Wired, state.CableMode);
	}

	[Fact]
	public void Service_AppliesItToTheCableSwitch_Live()
	{
		BridgeHost host = new(controllers, new TestIdentity(true), new TestAttacher());
		using RealControllerService service = new(host, new TestHiding(), controllers, a => a(), _ => null, timers: false) { DsxPort = 0 };

		service.ApplySettings(new ControllerSettings { CableMode = CableMode.Wired });
		Assert.Equal(CableMode.Wired, service.CableMode);

		service.ApplySettings(new ControllerSettings());
		Assert.Equal(CableMode.ChargeOnly, service.CableMode);
	}

	[AvaloniaFact]
	public void Picker_TwoChoices_InOrder_FollowsTheSetting_BothWays()
	{
		var fake = new FakeControllerService(animate: false);
		var settings = new SettingsState(fake);
		var vm = new ControllerPageViewModel(fake, settings, live: false);

		Assert.Equal(2, vm.CableModes.Count);
		Assert.Equal(CableMode.Wired, vm.CableModes[0].Mode);
		Assert.Equal(CableMode.ChargeOnly, vm.CableModes[1].Mode);
		// The default (Charge Only) is selected; wired mode is the user's pick.
		Assert.Same(vm.CableModes[1], vm.SelectedCableMode);

		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			Assert.Equal("Use The Cable (Experimental)", vm.CableModes[0].Name);
			Assert.Equal("Charge Only", vm.CableModes[1].Name);
			Localizer.Chosen = Language.Turkish;
			Assert.Equal("Kablo Üzerinden Çalış (Deneysel)", vm.CableModes[0].Name);
			Assert.Equal("Sadece Şarj Et", vm.CableModes[1].Name);
		}
		finally
		{
			Localizer.Chosen = before;
		}

		vm.SelectedCableMode = vm.CableModes[0];
		Assert.Equal(CableMode.Wired, settings.CableMode);
		settings.CableMode = CableMode.ChargeOnly;
		Assert.Same(vm.CableModes[1], vm.SelectedCableMode);
	}

	[Fact]
	public void Texts_InBothLanguages()
	{
		Assert.Equal("Kablo Takılınca", Text.CableModeLabel(Language.Turkish));
		Assert.Equal("When The Cable Is Plugged In", Text.CableModeLabel(Language.English));
		Assert.Equal("Kablo Üzerinden Çalış (Deneysel)", Text.CableModeWired(Language.Turkish));
		Assert.Equal("Use The Cable (Experimental)", Text.CableModeWired(Language.English));
		Assert.Equal("Sadece Şarj Et", Text.CableModeChargeOnly(Language.Turkish));
		Assert.Equal("Charge Only", Text.CableModeChargeOnly(Language.English));
		Assert.Equal("Kablo takılıyken kumanda kablo üzerinden çalışır, Bluetooth bağlantısı kesilir.", Text.CableModeShort(Language.Turkish));
		Assert.Equal("While the cable is in, the app runs the controller over it and disconnects Bluetooth.", Text.CableModeShort(Language.English));
		Assert.Equal("Kablo Üzerinden Çalış (Deneysel) seçiliyken kumandanın tüm işlevleri USB üzerinden çalışır ve Bluetooth bağlantısı kesilir. Kabloyu çıkarınca yeniden bağlanmak için ø tuşuna bas. Sorun olursa Sadece Şarj Et’i seç. Kablo yalnızca şarj eder, kumanda Bluetooth üzerinden çalışmaya devam eder.", Text.CableModeHint(Language.Turkish));
		Assert.Equal("Use The Cable: while the cable is in, input, triggers, light, haptics, speaker and microphone go over USB and Bluetooth is disconnected; after unplugging, press the ø button to reconnect. If anything misbehaves choose Charge Only: the cable only charges and everything stays on Bluetooth.", Text.CableModeHint(Language.English));
		Assert.Equal("USB Kablosu (Deneysel)", Text.ConnectionWired(Language.Turkish));
		Assert.Equal("USB Cable (Experimental)", Text.ConnectionWired(Language.English));
		Assert.Equal("Kablo", Text.CardCable(Language.Turkish));
		Assert.Equal("Cable", Text.CardCable(Language.English));
	}

	// The row is on the Controller page (Outputs tab) in its own card and no longer on Settings → Notifications:
	// the picker renders on the Controller page and is visible in both languages.
	[AvaloniaFact]
	public void Picker_IsOnTheControllerPage_InBothLanguages()
	{
		foreach (Language language in new[] { Language.Turkish, Language.English })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				var fake = new FakeControllerService(animate: false);
				var settings = new SettingsState(fake);
				var vm = new ControllerPageViewModel(fake, settings, live: false)
				{
					SelectedTab = ControllerPageViewModel.OutputsTab,
				};
				var window = new Window { Content = new ControllerView { DataContext = vm }, Width = 1100, Height = 700 };
				window.Show();
				FitCheck.Settle(window);

				Border panel = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CablePanel");
				ComboBox picker = panel.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "CableModePicker");
				Assert.True(picker.IsEffectivelyVisible);
				List<TextBlock> texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();
				Assert.Contains(texts, t => t.Text == Text.CardCable(language));
				Assert.Contains(texts, t => t.Text == Text.CableModeLabel(language));
				window.Close();

				var settingsVm = new SettingsViewModel(fake, settings)
				{
					SelectedTab = SettingsViewModel.NotificationsTab,
				};
				var settingsWindow = new Window { Content = new SettingsView { DataContext = settingsVm }, Width = 1100, Height = 700 };
				settingsWindow.Show();
				FitCheck.Settle(settingsWindow);
				Assert.Empty(settingsWindow.GetVisualDescendants().OfType<ComboBox>().Where(c => c.Name == "CableModePicker"));
				settingsWindow.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}
}
