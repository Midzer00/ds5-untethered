// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;
using Kopru.Core.Updates;

namespace Kopru.App.Tests;

// The Updates block in Settings > General: the check (automatic and on demand), the one toast per version, the
// confirmation while playing, download and hand-over, zip copies and the errors.
public sealed class UpdatesAppTests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

	private sealed class Harness
	{
		public Harness(UpdateFakes? fakes = null, AppSettings? saved = null, bool withServices = true)
		{
			Fakes = fakes ?? new UpdateFakes();
			// No game in front (the fake starts with one).
			Fake.Watcher.Exit("Cyberpunk2077.exe");
			Fake.Games.Tick();
			Assert.Null(Fake.Games.State.Exe);
			Store = new MemorySettingsStore(saved ?? new AppSettings { Language = "en" });
			Vm = new MainWindowViewModel(Fake, new AppEnvironment(Store, Now: () => Clock, FullScreen: () => Playing,
				Updates: withServices ? Fakes.Services() : null));
			Vm.Notifications.Raised += Raised.Add;
		}

		public FakeControllerService Fake { get; } = new(animate: false);
		public UpdateFakes Fakes { get; }
		public MemorySettingsStore Store { get; }
		public MainWindowViewModel Vm { get; }
		public UpdatesViewModel Updates => Vm.Updates;
		public List<AppNotification> Raised { get; } = [];
		public bool Playing { get; set; }
		public DateTimeOffset Clock { get; set; } = Now;

		public List<AppNotification> UpdateToasts => Raised.Where(n => n.Type == NotificationType.UpdateAvailable).ToList();

		public async Task Click()
		{
			await Updates.ActCommand.ExecuteAsync(null);
			Dispatcher.UIThread.RunJobs();
		}

		public async Task Scheduled()
		{
			await Updates.RunScheduledCheckAsync();
			Dispatcher.UIThread.RunJobs();
		}
	}

	private static T WithEnglish<T>(Func<T> run)
	{
		Language? before = Localizer.Chosen;
		Localizer.Chosen = Language.English;
		try
		{
			return run();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public Task Schedule_FirstStart_ThenWeekly_AndNotWithTheSwitchOff() => WithEnglish(async () =>
	{
		var h = new Harness(new UpdateFakes { ManifestVersion = "0.1.5" });
		Assert.Equal(Text.UpdateNotChecked(), h.Updates.StateText);
		await h.Scheduled();
		Assert.Equal(1, h.Fakes.ManifestRequests);
		Assert.Equal(Text.UpdateUpToDate(), h.Updates.StateText);
		Assert.Equal(Now, h.Vm.Settings.UpdateCheckedAt);

		h.Clock = Now.AddDays(6);
		await h.Scheduled();
		Assert.Equal(1, h.Fakes.ManifestRequests);
		h.Clock = Now.AddDays(7);
		h.Vm.Settings.AutoCheckUpdates = false;
		await h.Scheduled();
		Assert.Equal(1, h.Fakes.ManifestRequests);
		h.Vm.Settings.AutoCheckUpdates = true;
		await h.Scheduled();
		Assert.Equal(2, h.Fakes.ManifestRequests);

		// On demand, whatever the schedule says.
		await h.Click();
		Assert.Equal(3, h.Fakes.ManifestRequests);
		Assert.Equal(Text.UpdateCheckNow(), h.Updates.ActionText);
	});

	[AvaloniaFact]
	public Task AutomaticFailure_IsQuiet_AndRetriedLater() => WithEnglish(async () =>
	{
		var h = new Harness(new UpdateFakes { ManifestStatus = HttpStatusCode.NotFound });
		await h.Scheduled();
		Assert.Equal(UpdatePhase.Idle, h.Updates.Phase);
		Assert.Null(h.Vm.Settings.UpdateCheckedAt);
		await h.Scheduled();
		Assert.Equal(1, h.Fakes.ManifestRequests);
		h.Clock = Now + UpdateSchedule.RetryAfterFailure;
		h.Fakes.ManifestStatus = HttpStatusCode.OK;
		await h.Scheduled();
		Assert.Equal(UpdatePhase.Ready, h.Updates.Phase);
	});

	[AvaloniaFact]
	public Task NewVersion_ShowsReady_WithNotes_AndOneToastPerVersion() => WithEnglish(async () =>
	{
		var h = new Harness();
		await h.Scheduled();
		Assert.Equal(UpdatePhase.Ready, h.Updates.Phase);
		Assert.Equal(Text.UpdateReady("0.1.6"), h.Updates.StateText);
		Assert.Equal(Text.UpdateInstall(), h.Updates.ActionText);
		Assert.True(h.Updates.IsPrimaryAction);
		Assert.True(h.Updates.HasNotes);
		Assert.Equal(h.Fakes.NotesEn, h.Updates.NotesLine);
		AppNotification toast = Assert.Single(h.UpdateToasts);
		Assert.Equal(Text.NotifyUpdateTitle(), toast.Title);
		Assert.Equal(Text.NotifyUpdateBody("0.1.6"), toast.Body);
		Assert.Equal(AppPage.SettingsGeneral, toast.Page);
		Assert.Equal("0.1.6", h.Vm.Settings.UpdateToastedVersion);

		// Checked again a week later (and in a later run, with the saved settings): no second toast for the same version.
		h.Clock = Now.AddDays(7);
		await h.Scheduled();
		Assert.Equal(2, h.Fakes.ManifestRequests);
		h.Vm.Settings.SaveNow();
		var later = new Harness(saved: h.Store.Saved);
		await later.Scheduled();
		Assert.Equal(UpdatePhase.Ready, later.Updates.Phase);
		Assert.Empty(later.UpdateToasts);
		Assert.Single(h.UpdateToasts);
	});

	[AvaloniaFact]
	public Task Toast_WaitsWhilePlaying_AndRespectsTheNotificationSwitch() => WithEnglish(async () =>
	{
		var h = new Harness { Playing = true };
		await h.Scheduled();
		Assert.Empty(h.UpdateToasts);
		h.Playing = false;
		h.Fake.RaiseStateChanged();
		Dispatcher.UIThread.RunJobs();
		Assert.Single(h.UpdateToasts);

		var off = new Harness();
		off.Vm.Settings.NotificationsEnabled = false;
		await off.Scheduled();
		Assert.Empty(off.UpdateToasts);
		Assert.Null(off.Vm.Settings.UpdateToastedVersion);
	});

	[AvaloniaFact]
	public Task Update_DownloadsVerifies_AndHandsOver() => WithEnglish(async () =>
	{
		var h = new Harness();
		await h.Scheduled();
		await h.Click();
		(string path, UpdateAsset asset, string language) = Assert.Single(h.Fakes.Installs);
		Assert.Equal(@"X:\data\updates\DS5-Untethered-0.1.6-Setup.exe", path);
		Assert.Equal(UpdateFakes.Installer, h.Fakes.Files.Data[path]);
		Assert.Equal(UpdateFakes.Installer.Length, asset.Size);
		Assert.Equal("en", language);
		Assert.Equal(UpdatePhase.Starting, h.Updates.Phase);
		Assert.Equal(Text.UpdateStarting(), h.Updates.StateText);
		Assert.False(h.Updates.ActCommand.CanExecute(null));
	});

	[AvaloniaFact]
	public Task Update_WhilePlaying_AsksFirst() => WithEnglish(async () =>
	{
		var h = new Harness();
		await h.Scheduled();
		h.Playing = true;
		await h.Click();
		Assert.Empty(h.Fakes.Installs);
		Assert.Equal(UpdatePhase.Confirming, h.Updates.Phase);
		Assert.Equal(Text.UpdateConfirm(), h.Updates.StateText);
		Assert.Equal(Text.UpdateInstallAnyway(), h.Updates.ActionText);
		await h.Click();
		Assert.Single(h.Fakes.Installs);
	});

	[AvaloniaFact]
	public Task Update_WhileTheVirtualControllerFeedsAGame_AsksFirst() => WithEnglish(async () =>
	{
		var h = new Harness();
		await h.Scheduled();
		h.Vm.Overview.ToggleBridgeCommand.Execute(null);
		h.Fake.Tick();
		Assert.True(h.Fake.IsGameConnected);
		await h.Click();
		Assert.Equal(UpdatePhase.Confirming, h.Updates.Phase);
		Assert.Empty(h.Fakes.Installs);
	});

	[AvaloniaFact]
	public Task Downloading_ShowsProgress() => WithEnglish(async () =>
	{
		var fakes = new UpdateFakes();
		TaskCompletionSource release = fakes.Stall(42);
		var h = new Harness(fakes);
		await h.Scheduled();
		Task click = h.Updates.ActCommand.ExecuteAsync(null);
		for (int i = 0; i < 200 && h.Updates.Progress < 42; i++)
		{
			await Task.Delay(10);
			Dispatcher.UIThread.RunJobs();
		}
		Assert.Equal(UpdatePhase.Downloading, h.Updates.Phase);
		Assert.Equal(Text.UpdateDownloading(42), h.Updates.StateText);
		release.SetResult();
		await click;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(UpdatePhase.Error, h.Updates.Phase);
		Assert.Equal(Text.UpdateDownloadOffline(), h.Updates.StateText);
		Assert.Empty(h.Fakes.Files.Data);
		Assert.Empty(h.Fakes.Installs);
	});

	[AvaloniaFact]
	public Task Errors_AreSaidInPlainWords() => WithEnglish(async () =>
	{
		// The manifest is live before the release's file: "not ready yet", and Update stays to try again.
		var early = new UpdateFakes { Download = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)) };
		var h = new Harness(early);
		await h.Scheduled();
		await h.Click();
		Assert.True(h.Updates.HasError);
		Assert.Equal(Text.UpdateNotYetAvailable(), h.Updates.StateText);
		Assert.Equal(Text.UpdateInstall(), h.Updates.ActionText);
		Assert.True(h.Updates.ActCommand.CanExecute(null));

		// A damaged file.
		var bad = new UpdateFakes();
		bad.Download = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[UpdateFakes.Installer.Length]) });
		var b = new Harness(bad);
		await b.Scheduled();
		await b.Click();
		Assert.Equal(Text.UpdateDownloadRejected(), b.Updates.StateText);
		Assert.Empty(bad.Files.Data);

		// Check Now while offline.
		var offline = new Harness(new UpdateFakes { ManifestStatus = HttpStatusCode.ServiceUnavailable });
		await offline.Click();
		Assert.True(offline.Updates.HasError);
		Assert.Equal(Text.UpdateCheckOffline(), offline.Updates.StateText);
		Assert.Equal(Text.UpdateCheckNow(), offline.Updates.ActionText);

		// The installer would not start.
		var refused = new Harness(new UpdateFakes { Outcome = InstallOutcome.LaunchFailed });
		await refused.Scheduled();
		await refused.Click();
		Assert.Equal(Text.UpdateLaunchFailed(), refused.Updates.StateText);
	});

	[AvaloniaFact]
	public Task ZipCopy_OpensTheDownloadPage() => WithEnglish(async () =>
	{
		var h = new Harness(new UpdateFakes { Installed = false });
		await h.Scheduled();
		Assert.Equal(Text.UpdateOpenPage(), h.Updates.ActionText);
		Assert.Contains(Text.UpdateZipCopy(), h.Updates.NotesTip);
		await h.Click();
		Assert.Equal(["https://github.com/Midzer00/ds5-untethered/releases/tag/v0.1.6"], h.Fakes.Opened);
		Assert.Empty(h.Fakes.Installs);
		Assert.Empty(h.Fakes.Files.Data);
	});

	[AvaloniaFact]
	public void WithoutServices_OnlyTheVersionShows() => WithEnglish(() =>
	{
		var h = new Harness(withServices: false);
		Assert.Equal(Text.AboutVersion(AppInfo.Version), h.Updates.VersionText);
		Assert.False(h.Updates.ActCommand.CanExecute(null));
		return 0;
	});

	[AvaloniaFact]
	public void RunVersion_IsSavedOnlyWhenItChanges()
	{
		var store = new MemorySettingsStore(new AppSettings { LastRunVersion = "0.1.5" });
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(store));
		int saves = store.Saves;
		vm.Settings.MarkRunVersion("0.1.5");
		vm.Settings.SaveNow();
		Assert.Equal(saves, store.Saves);
		vm.Settings.MarkRunVersion("0.1.6");
		vm.Settings.SaveNow();
		Assert.Equal("0.1.6", store.Saved!.LastRunVersion);
	}
}
