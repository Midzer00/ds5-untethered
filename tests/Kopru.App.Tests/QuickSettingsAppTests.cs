// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The Overview's quick settings card and Low Power mode (an override that never touches the profile).
public class QuickSettingsAppTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create(AppSettings? saved = null, MemorySettingsStore? store = null)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store ?? (saved == null ? null : new MemorySettingsStore(saved))));
		return (vm, fake);
	}

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	[AvaloniaFact]
	public void Steps_EditTheSameSettingsAsTheDetailedPages()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		QuickSettingsViewModel q = vm.Overview.Quick;
		// Defaults: haptics 150 % (strong), triggers 100 % (strong), brightness 80 % (high).
		Assert.Equal(2, q.HapticsIndex);
		Assert.Equal(2, q.TriggerIndex);
		Assert.Equal(2, q.LightIndex);
		Assert.False(q.HasHapticsCustom);

		q.HapticsIndex = 0;
		q.TriggerIndex = 1;
		q.LightIndex = 0;
		Assert.Equal(QuickSteps.Haptics[0], vm.Settings.GameHapticsLevel);
		Assert.Equal(QuickSteps.Trigger[1], vm.Settings.TriggerStrength);
		Assert.Equal(0, vm.Settings.Brightness);
		Assert.Equal(QuickSteps.Haptics[0], fake.Settings.GameHapticsLevel);
		Assert.Equal(QuickSteps.Trigger[1], fake.Settings.TriggerStrength);

		// A cleared selection leaves the value alone.
		q.HapticsIndex = -1;
		Assert.Equal(QuickSteps.Haptics[0], vm.Settings.GameHapticsLevel);
	}

	[AvaloniaFact]
	public void ValueBetweenSteps_SelectsNoneAndShowsCustom()
	{
		WithLanguage(Language.English, () =>
		{
			(MainWindowViewModel vm, _) = Create();
			QuickSettingsViewModel q = vm.Overview.Quick;
			var changed = new List<string?>();
			q.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
			// Changed on another page (the haptics slider).
			vm.Settings.GameHapticsLevel = 130;
			Assert.Contains(nameof(QuickSettingsViewModel.HapticsIndex), changed);
			Assert.Equal(-1, q.HapticsIndex);
			Assert.True(q.HasHapticsCustom);
			Assert.Equal("Custom · 130%", q.HapticsCustom);
			Assert.Equal("Özel · %130", Text.QuickCustom(130, Language.Turkish));
		});
	}

	[AvaloniaFact]
	public void SpeakerAndMicSwitches_FollowTheSettings()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		QuickSettingsViewModel q = vm.Overview.Quick;
		q.SpeakerOn = true;
		q.MicTile = true;
		Assert.True(vm.Settings.SpeakerEnabled);
		Assert.True(vm.Settings.MicEnabled);
		Assert.True(fake.Settings.Speaker.Enabled);
		Assert.True(fake.Settings.Mic.Enabled);
		vm.Settings.MicEnabled = false;
		Assert.False(q.MicTile);
	}

	// The Speaker tile shows the speaker as DualSense Mode makes it (SpeakerActive), next to the shortcuts tile.
	[AvaloniaFact]
	public void SpeakerTile_FollowsDualSenseMode_AndKeepsTheUsersOwnSwitch()
	{
		(MainWindowViewModel vm, _) = Create();
		QuickSettingsViewModel q = vm.Overview.Quick;
		vm.Overview.IsSupportedModeOn = false;
		Settle();
		vm.Settings.SpeakerEnabled = false;
		Assert.False(q.SpeakerOn);

		var changed = new List<string?>();
		q.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		vm.Overview.IsSupportedModeOn = true;
		Settle();
		Assert.True(vm.Settings.SpeakerActive);
		Assert.True(q.SpeakerOn);
		Assert.Contains(nameof(QuickSettingsViewModel.SpeakerOn), changed);

		// Off by hand while the mode is on: only this session's choice, the user's own switch stays.
		q.SpeakerOn = false;
		Assert.False(vm.Settings.SpeakerActive);
		Assert.False(vm.Settings.SpeakerEnabled);
		vm.Overview.IsSupportedModeOn = false;
		Settle();
		Assert.False(q.SpeakerOn);
		Assert.True(q.ShortcutsOn);
	}

	[AvaloniaFact]
	public void MouseModeTile_IsTheDesktopModeSwitch_BothWays()
	{
		(MainWindowViewModel vm, _) = Create();
		QuickSettingsViewModel q = vm.Overview.Quick;
		var changed = new List<string?>();
		q.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		Assert.False(q.MouseModeOn);
		q.MouseModeOn = true;
		Assert.True(vm.Settings.DesktopModeOn);
		// Turned off elsewhere (the Settings group, the tray, PS + Options): the tile follows.
		vm.Settings.DesktopModeOn = false;
		Assert.False(q.MouseModeOn);
		Assert.Contains(nameof(QuickSettingsViewModel.MouseModeOn), changed);
		// Low Power does not lock it.
		vm.Settings.LowPowerOn = true;
		q.MouseModeOn = true;
		Assert.True(vm.Settings.DesktopModeOn);
	}

	[AvaloniaFact]
	public void ShortcutsTile_IsTheShortcutsSwitch_BothWays_AndReachesTheController()
	{
		var store = new MemorySettingsStore(new AppSettings());
		(MainWindowViewModel vm, FakeControllerService fake) = Create(store: store);
		QuickSettingsViewModel q = vm.Overview.Quick;
		var changed = new List<string?>();
		q.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		Assert.True(q.ShortcutsOn);
		Assert.True(fake.Settings.ShortcutsEnabled);

		q.ShortcutsOn = false;
		Assert.False(vm.Settings.ShortcutsEnabled);
		Assert.False(fake.Settings.ShortcutsEnabled);
		vm.Settings.SaveNow();
		Assert.False(store.Saved!.ShortcutsEnabled);

		// Switched on the Settings page: the tile follows.
		vm.Settings.ShortcutsPanel.Settings.ShortcutsEnabled = true;
		Assert.True(q.ShortcutsOn);
		Assert.Contains(nameof(QuickSettingsViewModel.ShortcutsOn), changed);

		// Low Power does not lock it.
		vm.Settings.LowPowerOn = true;
		q.ShortcutsOn = false;
		Assert.False(fake.Settings.ShortcutsEnabled);
	}

	[AvaloniaFact]
	public void ShortcutsTileButton_OpensTheShortcutsGroup_WithoutSwitching()
	{
		(MainWindowViewModel vm, _) = Create();
		QuickSettingsViewModel q = vm.Overview.Quick;
		string? requested = null;
		vm.SettingsPage.SectionRequested += s => requested = s;
		q.EditShortcutsCommand.Execute(null);
		Assert.Same(vm.SettingsPage, vm.CurrentPage);
		Assert.Equal(SettingsSections.Shortcuts, requested);
		Assert.True(q.ShortcutsOn);
	}

	// The tile's small button scrolls the Settings page to the shortcuts group (headless window).
	[AvaloniaFact]
	public void ShortcutsTileButton_ScrollsTheSettingsPageToTheGroup()
	{
		(MainWindowViewModel vm, _) = Create();
		var window = new Kopru.App.Views.MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Settle();
		Avalonia.Controls.Button button = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>()
			.Single(b => b.Classes.Contains("tileAction"));
		button.Command!.Execute(null);
		for (int i = 0; i < 5; i++)
		{
			Settle();
			Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		}
		Settle();
		var scroll = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ScrollViewer>()
			.First(s => s.Classes.Contains("page") && s.IsEffectivelyVisible);
		Assert.True(scroll.Offset.Y > 100, $"offset {scroll.Offset.Y}");
		Assert.False(vm.SettingsPage.HasPendingSection);
		window.Close();
	}

	[AvaloniaFact]
	public void DualSenseModeTile_SharesTheOverviewSwitch()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		bool before = vm.Overview.IsSupportedModeOn;
		vm.Overview.IsSupportedModeOn = !before;
		Assert.Equal(!before, fake.Games.IsSupportedModeOn);
		vm.Overview.IsSupportedModeOn = before;
		Assert.Equal(before, fake.Games.IsSupportedModeOn);
	}

	[AvaloniaFact]
	public void EveryQuickSetting_HasATooltip_InBothLanguages_AndSaysWhenLowPowerOverridesIt()
	{
		foreach (Language language in new[] { Language.Turkish, Language.English })
		{
			WithLanguage(language, () =>
			{
				(MainWindowViewModel vm, _) = Create();
				QuickSettingsViewModel q = vm.Overview.Quick;
				string[] tips = [q.LowPowerTip, q.SupportedModeTip, q.MouseModeTip, q.SpeakerTip, q.MicTip, q.ShortcutsTip, q.EditShortcutsTip, q.HapticsTip,
					q.TriggerTip, q.LightTip, q.ProfileTip, q.MicButtonTip, q.Audio.OutputTip, q.Audio.InputTip, vm.Overview.Live.LeftHapticsTip, vm.Overview.Live.RightHapticsTip, vm.Overview.Live.TriggersTip];
				Assert.All(tips, t => Assert.False(string.IsNullOrWhiteSpace(t)));
				Assert.Equal(tips.Length, tips.Distinct().Count());
				Assert.DoesNotContain(Text.QuickOverriddenByLowPower(), q.SpeakerTip);

				var changed = new List<string?>();
				q.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
				vm.Settings.LowPowerOn = true;
				Assert.Contains(nameof(QuickSettingsViewModel.SpeakerTip), changed);
				foreach (string tip in new[] { q.SpeakerTip, q.MicTip, q.HapticsTip, q.TriggerTip, q.LightTip })
				{
					Assert.Contains(Text.QuickOverriddenByLowPower(), tip);
				}
				Assert.DoesNotContain(Text.QuickOverriddenByLowPower(), q.MouseModeTip);
				Assert.DoesNotContain(Text.QuickOverriddenByLowPower(), q.ShortcutsTip);
				Assert.Equal(Text.QuickLowPowerShort(), q.LowPowerState);
			});
		}
		Assert.NotEqual(Text.QuickMouseModeTip(Language.Turkish), Text.QuickMouseModeTip(Language.English));
	}

	[AvaloniaFact]
	public void HeroBattery_ShowsTheSidebarReading_ChargingAndLow()
	{
		WithLanguage(Language.English, () =>
		{
			(MainWindowViewModel vm, FakeControllerService fake) = Create();
			fake.BatteryPercent = 78;
			fake.IsCharging = false;
			fake.RaiseStateChanged();
			Settle();
			Assert.True(vm.Overview.ShowsBattery);
			Assert.Equal(vm.BatteryShort, vm.Overview.BatteryText);
			Assert.Equal("78%", vm.Overview.BatteryText);
			Assert.False(vm.Overview.IsBatteryLow);
			Assert.Equal(Text.HeroBatteryTip(), vm.Overview.BatteryTip);

			fake.IsCharging = true;
			fake.RaiseStateChanged();
			Settle();
			Assert.Equal("78% · Charging", vm.Overview.BatteryText);
			Assert.Equal(Text.HeroBatteryChargingTip(), vm.Overview.BatteryTip);

			fake.IsCharging = false;
			fake.BatteryPercent = 15;
			fake.RaiseStateChanged();
			Settle();
			Assert.True(vm.Overview.IsBatteryLow);

			fake.IsConnected = false;
			fake.RaiseStateChanged();
			Settle();
			Assert.False(vm.Overview.ShowsBattery);
		});
		Assert.Equal("%78", Text.BatteryValue(78, Language.Turkish));
	}

	[AvaloniaFact]
	public void Profile_PickedInTheCard_ActivatesIt()
	{
		(MainWindowViewModel vm, _) = Create();
		var racing = vm.Settings.Profiles.Create("Racing", vm.Settings.CaptureValues() with { GameHapticsLevel = 100 })!;
		Settle();
		vm.Overview.SelectedProfile = vm.Overview.Profiles.Single(p => p.Id == racing.Id);
		Assert.Equal(racing.Id, vm.Settings.Profiles.Active.Id);
		Assert.Equal(1, vm.Overview.Quick.HapticsIndex);
	}

	[AvaloniaFact]
	public void AdvancedLink_OpensSettings()
	{
		(MainWindowViewModel vm, _) = Create();
		vm.Overview.Quick.AdvancedCommand.Execute(null);
		Assert.Same(vm.SettingsPage, vm.CurrentPage);
	}

	[AvaloniaFact]
	public void Disconnected_CardStaysUsableWithANote()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		Assert.False(vm.Overview.Quick.ShowsDisconnectedNote);
		fake.IsConnected = false;
		fake.RaiseStateChanged();
		Settle();
		Assert.True(vm.Overview.Quick.ShowsDisconnectedNote);
		vm.Overview.Quick.HapticsIndex = 1;
		Assert.Equal(100, vm.Settings.GameHapticsLevel);
	}

	[AvaloniaFact]
	public void LowPower_OverridesWhatTheControllerGets_NeverTheProfile()
	{
		var store = new MemorySettingsStore(new AppSettings());
		(MainWindowViewModel vm, FakeControllerService fake) = Create(store: store);
		vm.Settings.SpeakerEnabled = true;
		vm.Settings.MicEnabled = true;
		vm.Settings.GameHapticsLevel = 130;
		ControllerSettings before = fake.Settings;
		var profileBefore = vm.Settings.Profiles.Active.Values;
		QuickSettingsViewModel q = vm.Overview.Quick;

		q.LowPowerOn = true;
		Assert.True(vm.Settings.LowPowerOn);
		Assert.False(q.CanEditOverridden);
		Assert.Equal(QuickSteps.Haptics[0], fake.Settings.GameHapticsLevel);
		Assert.Equal(QuickSteps.Trigger[0], fake.Settings.TriggerStrength);
		Assert.Equal(0, fake.Settings.Brightness);
		Assert.False(fake.Settings.PlayerLeds);
		Assert.False(fake.Settings.Speaker.Enabled);
		Assert.False(fake.Settings.Mic.Enabled);
		Assert.Equal(LowPower.IdleMinutes, fake.Settings.IdlePowerOffMinutes);
		// The card and the profile keep the user's own values.
		Assert.Equal(130, vm.Settings.GameHapticsLevel);
		Assert.True(q.SpeakerOn);
		// The microphone tile shows the controller mic as it is (held off), the user's switch stays on.
		Assert.True(vm.Settings.MicEnabled);
		Assert.False(q.MicTile);
		Assert.False(q.CanPressMic);
		Assert.Equal(-1, q.HapticsIndex);
		Assert.Equal(profileBefore, vm.Settings.Profiles.Active.Values);
		Assert.Equal(0, vm.Overview.ArtLightIntensity);

		q.LowPowerOn = false;
		Assert.Equal(before, fake.Settings);
		Assert.Equal(profileBefore, vm.Settings.Profiles.Active.Values);

		// Only the switch is saved.
		q.LowPowerOn = true;
		vm.Settings.SaveNow();
		Assert.True(store.Saved!.LowPowerOn);
		Assert.Equal(130, store.Saved.GameHapticsLevel);
	}

	[AvaloniaFact]
	public void LowPower_IsLoadedFromTheSavedSettings()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create(new AppSettings { LowPowerOn = true, GameHapticsLevel = 150 });
		Assert.True(vm.Overview.Quick.LowPowerOn);
		Assert.Equal(150, vm.Settings.GameHapticsLevel);
		Assert.Equal(QuickSteps.Haptics[0], fake.Settings.GameHapticsLevel);
	}

	[AvaloniaFact]
	public void LowPower_AppliesToControllersWithTheirOwnProfile()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		var racing = vm.Settings.Profiles.Create("Racing", vm.Settings.CaptureValues() with { Brightness = 100 })!;
		vm.Settings.AssignControllerProfile("pad-2", racing.Id);
		Assert.Equal(100, fake.ControllerProfiles["pad-2"]!.Brightness);
		vm.Settings.LowPowerOn = true;
		Assert.Equal(0, fake.ControllerProfiles["pad-2"]!.Brightness);
	}

	[AvaloniaFact]
	public void AutoLowPower_TurnsOnBelow20WhileDischarging_OncePerDischarge()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.SettingsPage.Settings.AutoLowPower = true;
		fake.BatteryPercent = 25;
		fake.RaiseStateChanged();
		Settle();
		Assert.False(vm.Settings.LowPowerOn);

		fake.BatteryPercent = 18;
		fake.RaiseStateChanged();
		Settle();
		Assert.True(vm.Settings.LowPowerOn);

		// Turned off by hand: stays off for this discharge.
		vm.Overview.Quick.LowPowerOn = false;
		fake.BatteryPercent = 12;
		fake.RaiseStateChanged();
		Settle();
		Assert.False(vm.Settings.LowPowerOn);

		// Charged above 20 % and down again: on once more.
		fake.BatteryPercent = 60;
		fake.RaiseStateChanged();
		fake.BatteryPercent = 19;
		fake.RaiseStateChanged();
		Settle();
		Assert.True(vm.Settings.LowPowerOn);
	}

	[AvaloniaFact]
	public void AutoLowPower_IsOffByDefault_AndNotWhileCharging()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		Assert.False(vm.Settings.AutoLowPower);
		fake.BatteryPercent = 10;
		fake.RaiseStateChanged();
		Settle();
		Assert.False(vm.Settings.LowPowerOn);

		fake.IsCharging = true;
		vm.Settings.AutoLowPower = true;
		fake.RaiseStateChanged();
		Settle();
		Assert.False(vm.Settings.LowPowerOn);
	}

	[AvaloniaFact]
	public void Header_IsOnePlainLine_AndHealthCollapsesWhenAllIsWell()
	{
		WithLanguage(Language.English, () =>
		{
			(MainWindowViewModel vm, FakeControllerService fake) = Create();
			fake.Health = OverviewHealthTests.Report();
			fake.RaiseStateChanged();
			Settle();
			Assert.True(vm.Overview.IsHealthAllGood);
			Assert.False(vm.Overview.ShowsHealthChips);
			Assert.StartsWith("All good · ", vm.Overview.HealthSummary);

			fake.Health = OverviewHealthTests.Report(driver: false);
			fake.RaiseStateChanged();
			Settle();
			Assert.False(vm.Overview.IsHealthAllGood);
			Assert.True(vm.Overview.ShowsHealthChips);
		});
	}

	private static void WithLanguage(Language language, Action action)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = language;
			action();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}
}
