// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Input;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

public class ProfilesAppTests
{
	private static string GameKey(FakeControllerService fake) => fake.Games.ActiveGame!.Key;

	[AvaloniaFact]
	public void OldSettings_BecomeTheDefaultProfile_AndAreSavedAsProfiles()
	{
		var store = new MemorySettingsStore(new AppSettings { GameHapticsLevel = 120, Brightness = 40, RumbleLevel = 70 });
		var state = new SettingsState(new FakeControllerService(animate: false), store);
		ControllerProfile only = Assert.Single(state.Profiles.Profiles);
		Assert.True(only.IsDefault);
		Assert.Equal(120, only.Values.GameHapticsLevel);
		Assert.Equal(40, only.Values.Brightness);
		Assert.Equal(70, only.Values.RumbleLevel);

		AppSettings saved = state.ToAppSettings();
		Assert.Equal(ProfileBook.DefaultId, saved.ActiveProfile);
		Assert.Equal(120, Assert.Single(saved.Profiles!).Values.GameHapticsLevel);
		Assert.True(saved.ShortcutsEnabled);
	}

	[AvaloniaFact]
	public void Edits_GoToTheActiveProfile_AndSwitchingLoadsAndPushesValues()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues())!;
		Assert.True(state.ActivateProfile(racing.Id));
		state.GameHapticsLevel = 220;
		state.LightbarColour = 0xFF0000;
		Assert.Equal(220, state.Profiles.Find(racing.Id)!.Values.GameHapticsLevel);
		Assert.Equal(150, state.Profiles.Default.Values.GameHapticsLevel);
		Assert.Equal("Racing", state.ActiveProfileName);

		state.ActivateProfile(ProfileBook.DefaultId);
		Assert.Equal(150, state.GameHapticsLevel);
		Assert.Equal(0x2F6BFFu, state.LightbarColour);
		Assert.Equal(150, fake.Settings.GameHapticsLevel);
		Assert.Equal(Text.ProfileDefault(), state.ActiveProfileName);

		state.ActivateProfile(racing.Id);
		Assert.Equal(220, fake.Settings.GameHapticsLevel);
		Assert.Equal(0xFF0000u, fake.Settings.LightbarColour);
	}

	[AvaloniaFact]
	public void Profiles_CarryRemapGyroAndLightEffects()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues())!;
		state.ActivateProfile(racing.Id);
		state.Remap = state.Remap.With(DsButton.Cross, DsButton.Circle);
		state.Gyro = state.Gyro with { Mode = GyroMode.Always, SensitivityX = 2 };
		state.LightMode = Kopru.Core.Lighting.LightMode.Rainbow;
		state.PlayerLedPattern = Kopru.Core.Lighting.PlayerLedPattern.Three;
		state.MuteLed = Kopru.Core.Lighting.MuteLedMode.On;

		state.ActivateProfile(ProfileBook.DefaultId);
		Assert.True(state.Remap.IsIdentity);
		Assert.True(state.Gyro.IsOff);
		Assert.Equal(Kopru.Core.Lighting.LightMode.Static, state.LightMode);
		Assert.Equal(Kopru.Core.Lighting.PlayerLedPattern.One, state.PlayerLedPattern);
		Assert.Equal(Kopru.Core.Lighting.MuteLedMode.Off, state.MuteLed);

		state.ActivateProfile(racing.Id);
		Assert.Equal(DsButton.Circle, state.Remap.TargetOf(DsButton.Cross));
		Assert.Equal(GyroMode.Always, state.Gyro.Mode);
		Assert.Equal(Kopru.Core.Lighting.LightMode.Rainbow, state.LightMode);
		Assert.Equal(Kopru.Core.Lighting.PlayerLedPattern.Three, state.PlayerLedPattern);
		Assert.Equal(DsButton.Circle, fake.Settings.Remap.TargetOf(DsButton.Cross));
		Assert.Equal(Kopru.Core.Lighting.LightMode.Rainbow, fake.Settings.LightOptions.Mode);
	}

	[AvaloniaFact]
	public void OldProfileWithoutNewFields_LoadsDefaults_AndKeepsPlayerLedsOff()
	{
		var state = new SettingsState(new FakeControllerService(animate: false));
		ControllerProfile old = state.Profiles.Create("Old", new ProfileValues { PlayerLeds = false })!;
		state.ActivateProfile(old.Id);
		Assert.False(state.PlayerLeds);
		Assert.Equal(Kopru.Core.Lighting.PlayerLedPattern.Off, state.PlayerLedPattern);
		Assert.True(state.Remap.IsIdentity);
	}

	[AvaloniaFact]
	public void Profiles_SurviveSaveAndReload()
	{
		var store = new MemorySettingsStore();
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake, store);
		ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues() with { GameHapticsLevel = 90 })!;
		state.Profiles.AssignGame(GameKey(fake), racing.Id);
		state.ActivateProfile(racing.Id);
		state.ShortcutsEnabled = false;
		state.SaveNow();

		var again = new SettingsState(new FakeControllerService(animate: false), store);
		Assert.Equal(2, again.Profiles.Profiles.Count);
		Assert.Equal(racing.Id, again.Profiles.ActiveId);
		Assert.Equal(90, again.GameHapticsLevel);
		Assert.Equal(racing.Id, again.Profiles.ProfileForGame(GameKey(fake)));
		Assert.False(again.ShortcutsEnabled);
	}

	[AvaloniaFact]
	public void KnownGame_ActivatesItsProfile_OtherwiseTheDefault()
	{
		var fake = new FakeControllerService(animate: false);
		string key = GameKey(fake);
		var state = new SettingsState(fake);
		ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues() with { GameHapticsLevel = 250 })!;
		state.Profiles.AssignGame(key, racing.Id);

		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		Assert.Equal(ProfileBook.DefaultId, state.Profiles.ActiveId);

		fake.Watcher.Exe = "Cyberpunk2077.exe";
		fake.Games.Tick();
		Assert.Equal(racing.Id, state.Profiles.ActiveId);
		Assert.Equal(250, fake.Settings.GameHapticsLevel);
		// The supported mode switch is left alone.
		Assert.False(fake.Games.IsSupportedModeOn);

		// Another known game without a profile: the default.
		fake.Watcher.Exe = "Returnal-Win64-Shipping.exe";
		fake.Games.Tick();
		Assert.Equal(ProfileBook.DefaultId, state.Profiles.ActiveId);
		Assert.Equal(150, fake.Settings.GameHapticsLevel);
	}

	[AvaloniaFact]
	public void SupportedGamesRow_PicksTheGamesProfile()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		ControllerProfile racing = vm.Settings.Profiles.Create("Racing", vm.Settings.CaptureValues())!;
		GameItemViewModel row = vm.SupportedGames.Games.First(g => g.Name == "Returnal");
		Assert.Equal(ProfileBook.DefaultId, row.Profile!.Selected!.Id);
		Assert.Equal(2, row.Profile.Options.Count);
		row.Profile.Selected = row.Profile.Options.First(o => o.Id == racing.Id);
		Assert.Equal(racing.Id, vm.Settings.Profiles.ProfileForGame(row.Key));
		Assert.True(row.Profile.HasProfile);
	}

	[AvaloniaFact]
	public void ProfileShortcuts_SwitchProfiles_LightTheLeds_AndToast()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var toasts = new List<AppNotification>();
		vm.Notifications.Raised += toasts.Add;
		ControllerProfile racing = vm.Settings.Profiles.Create("Racing", vm.Settings.CaptureValues() with { Brightness = 10 })!;

		fake.RaiseShortcut(ShortcutAction.NextProfile);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(racing.Id, vm.Settings.Profiles.ActiveId);
		Assert.Equal(10, vm.Settings.Brightness);
		Assert.Equal([2], fake.ProfileIndicators);
		AppNotification toast = Assert.Single(toasts);
		Assert.Equal(Text.ShortcutProfileTitle(), toast.Title);
		Assert.Contains("Racing", toast.Body);

		fake.RaiseShortcut(ShortcutAction.NextProfile);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(ProfileBook.DefaultId, vm.Settings.Profiles.ActiveId);
		fake.RaiseShortcut(ShortcutAction.PreviousProfile);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal([2, 1, 2], fake.ProfileIndicators);
		Assert.Equal(3, toasts.Count);
	}

	[AvaloniaFact]
	public void OtherShortcuts_Haptics_Mode_PowerOff()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		fake.RaiseShortcut(ShortcutAction.HapticsUp);
		Assert.Equal(160, vm.Settings.GameHapticsLevel);
		Assert.Equal(160, vm.Settings.Profiles.Active.Values.GameHapticsLevel);
		fake.RaiseShortcut(ShortcutAction.HapticsDown);
		fake.RaiseShortcut(ShortcutAction.HapticsDown);
		Assert.Equal(140, fake.Settings.GameHapticsLevel);
		vm.Settings.GameHapticsLevel = 295;
		fake.RaiseShortcut(ShortcutAction.HapticsUp);
		Assert.Equal(300, vm.Settings.GameHapticsLevel);

		fake.RaiseShortcut(ShortcutAction.ToggleSupportedMode);
		Assert.True(fake.Games.IsSupportedModeOn);
		fake.RaiseShortcut(ShortcutAction.ToggleSupportedMode);
		Assert.False(fake.Games.IsSupportedModeOn);

		fake.RaiseShortcut(ShortcutAction.PowerOff);
		Assert.Equal(1, fake.PowerOffs);
		Assert.Empty(fake.ProfileIndicators);
	}

	[AvaloniaFact]
	public void ShortcutsSwitch_IsPushedToTheService()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		Assert.True(fake.Settings.ShortcutsEnabled);
		state.ShortcutsEnabled = false;
		Assert.False(fake.Settings.ShortcutsEnabled);
	}

	[AvaloniaFact]
	public void ProfilesPanel_New_Duplicate_Rename_Delete()
	{
		var state = new SettingsState(new FakeControllerService(animate: false));
		ProfilesViewModel panel = state.ProfilesPanel;
		Assert.Single(panel.Items);
		Assert.False(panel.Items[0].CanEdit);

		panel.NewCommand.Execute(null);
		Assert.Equal(2, panel.Items.Count);
		Assert.True(panel.Items[1].IsActive);
		Assert.Equal(Text.ProfileNewName(1), panel.Items[1].Name);

		ProfileItemViewModel item = panel.Items[1];
		item.BeginRenameCommand.Execute(null);
		item.EditName = "Racing";
		item.CommitRenameCommand.Execute(null);
		Assert.Equal("Racing", panel.Items[1].Name);
		Assert.Equal("Racing", state.ActiveProfileName);

		panel.Items[1].DuplicateCommand.Execute(null);
		Assert.Equal(Text.ProfileCopyName("Racing"), panel.Items[2].Name);

		panel.Items[1].DeleteCommand.Execute(null);
		Assert.Equal(2, panel.Items.Count);
		Assert.True(panel.Items[0].IsActive);
		Assert.Equal(Text.ProfileDefault(), state.ActiveProfileName);
	}

	[AvaloniaFact]
	public void ProfilesPanel_ExportImport_RoundTrip()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-profiles-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			var state = new SettingsState(new FakeControllerService(animate: false));
			ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues() with { TriggerStrength = 35, RumbleLevel = 60 })!;
			string file = Path.Combine(dir, "racing.json");
			Assert.True(state.ProfilesPanel.ExportFile(racing.Id, file));
			Assert.Equal(Text.ProfileExported("Racing"), state.ProfilesPanel.StatusText);

			var other = new SettingsState(new FakeControllerService(animate: false));
			ControllerProfile imported = other.ProfilesPanel.ImportFile(file)!;
			Assert.Equal("Racing", imported.Name);
			Assert.Equal(racing.Values, imported.Values);
			Assert.Equal(2, other.ProfilesPanel.Items.Count);

			string bad = Path.Combine(dir, "bad.json");
			File.WriteAllText(bad, "{\"hello\":1}");
			Assert.Null(other.ProfilesPanel.ImportFile(bad));
			Assert.True(other.ProfilesPanel.StatusIsError);
			Assert.Equal(Text.ProfileImportFailed(), other.ProfilesPanel.StatusText);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	// Renders the settings page at the profiles group and at the shortcuts group; PNGs go to docs/screens only with
	// KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public void RenderProfilesAndShortcuts()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				var fake = new FakeControllerService(animate: false);
				var vm = new MainWindowViewModel(fake);
				vm.Sticks.StopPreview();
				ProfileBook book = vm.Settings.Profiles;
				ControllerProfile racing = book.Create(language == Language.Turkish ? "Yarış" : "Racing", vm.Settings.CaptureValues() with { GameHapticsLevel = 200 })!;
				book.Create(language == Language.Turkish ? "Sessiz Gece" : "Quiet Night", vm.Settings.CaptureValues() with { GameHapticsLevel = 60 });
				book.AssignGame("Returnal", racing.Id);
				book.AssignGame(GameKey(fake), racing.Id);
				vm.Settings.ActivateProfile(racing.Id);
				vm.Navigate(AppPage.Settings);
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
				window.Show();
				foreach ((string group, string name) in new[] { ("ProfilesGroup", "profiles"), ("ShortcutsGroup", "settings-shortcuts") })
				{
					Dispatcher.UIThread.RunJobs();
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					Border panel = window.GetVisualDescendantsOfType<Border>().First(b => b.Name == group);
					panel.BringIntoView();
					Thread.Sleep(320);
					Dispatcher.UIThread.RunJobs();
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (writeFiles)
					{
						frame!.Save(Path.Combine(outDir, $"{name}-{code}.png"));
					}
				}
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}
}
