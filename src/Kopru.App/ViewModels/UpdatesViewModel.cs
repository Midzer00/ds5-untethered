// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Kopru.Core.Updates;

namespace Kopru.App.ViewModels;

// What the app passes in for in-app updates (null in tests that do not need them: the block then only shows the
// version). Install: hands the verified installer over and exits the app (UpdateInstaller.InstallAsync), set by the app
// once its shell exists.
public sealed record UpdateServices(UpdateChecker Checker, UpdateDownloader Downloader, IInstallKind InstallKind, IUrlLauncher Browser, string CurrentVersion)
{
	public Func<string, UpdateAsset, string, Task<InstallOutcome>>? Install { get; set; }
}

public enum UpdatePhase
{
	Idle,
	Checking,
	Ready,
	// "Update" was clicked while playing: the next click updates (ConfirmWindow).
	Confirming,
	Downloading,
	Starting,
	Error
}

// The "Updates" block in Settings > General: the state line, one action button (Check Now / Update / Open Download Page)
// and the automatic check switch; also the automatic check (a short while after start, then weekly) and the one toast
// per new version.
public sealed partial class UpdatesViewModel : ViewModelBase
{
	public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(15);

	private readonly IControllerService _service;
	private readonly UpdateServices? _services;
	private readonly NotificationCenter _notifications;
	private readonly Func<DateTimeOffset> _now;
	private readonly Func<bool> _fullScreen;
	private readonly AppVersion _current;
	private Timer? _schedule;
	private Timer? _confirmTimer;
	private DateTimeOffset? _lastFailure;
	// A check in this run that found a newer version (it is not saved: the next start checks again).
	private DateTimeOffset? _availableAt;
	private bool _checkedThisRun;
	private string? _error;

	public UpdatesViewModel(IControllerService service, SettingsState settings, NotificationCenter notifications, UpdateServices? services,
		Func<DateTimeOffset>? now = null, Func<bool>? fullScreen = null)
	{
		_service = service;
		Settings = settings;
		_notifications = notifications;
		_services = services;
		_now = now ?? (() => DateTimeOffset.Now);
		_fullScreen = fullScreen ?? (() => false);
		_current = AppVersion.Parse(services?.CurrentVersion ?? AppInfo.Version) ?? AppVersion.Parse("0.0.0")!;
		// A toast held back while playing shows once the game is left.
		service.StateChanged += (_, _) => RunOnUi(TryToast);
	}

	public SettingsState Settings { get; }

	// The newer release, once a check found one.
	public UpdateManifest? Manifest { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StateText), nameof(ActionText), nameof(IsPrimaryAction), nameof(HasNotes), nameof(NotesLine), nameof(NotesTip), nameof(HasError))]
	[NotifyCanExecuteChangedFor(nameof(ActCommand))]
	private UpdatePhase _phase;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StateText))]
	private double _progress;

	public string GroupLabel => Text.UpdatesGroup();
	public string AutoCheckLabel => Text.UpdateAutoCheck();
	public string AutoCheckHint => Text.UpdateAutoCheckHint();
	public string AppName => AppInfo.Name;
	public string VersionText => Text.AboutVersion(_current.ToString());
	public string Tagline => Text.AboutTagline();

	// This copy was unzipped (not installed), or is too old to update in place: the page instead of "Update".
	public bool UsesDownloadPage => _services != null && (!_services.InstallKind.IsInstalled || Manifest?.CanUpdateInPlace(_current) == false);

	public bool HasError => Phase == UpdatePhase.Error;

	public string StateText => Phase switch
	{
		UpdatePhase.Checking => Text.UpdateChecking(),
		UpdatePhase.Ready when Manifest != null => Text.UpdateReady(Manifest.Version.ToString()),
		UpdatePhase.Confirming => Text.UpdateConfirm(),
		UpdatePhase.Downloading => Text.UpdateDownloading(Math.Round(Progress)),
		UpdatePhase.Starting => Text.UpdateStarting(),
		UpdatePhase.Error => _error ?? string.Empty,
		_ => _checkedThisRun || Settings.UpdateCheckedAt != null ? Text.UpdateUpToDate() : Text.UpdateNotChecked()
	};

	// The button: Check Now until a newer version is known, then Update (Update Anyway to confirm) or Open Download Page.
	public string ActionText => Manifest == null ? Text.UpdateCheckNow()
		: UsesDownloadPage ? Text.UpdateOpenPage()
		: Phase == UpdatePhase.Confirming ? Text.UpdateInstallAnyway()
		: Text.UpdateInstall();

	public bool IsPrimaryAction => Manifest != null;

	// The release notes: the first line under the state, all of them as the tooltip (zip copies also learn why the
	// button opens a page).
	public bool HasNotes => Manifest != null && Phase is UpdatePhase.Ready && NotesLine.Length > 0;
	public string NotesLine => Manifest?.Notes().Split('\n')[0] ?? string.Empty;
	public string? NotesTip => Manifest == null ? null
		: string.Join("\n", new[] { Manifest.Notes(), UsesDownloadPage ? Text.UpdateZipCopy() : string.Empty }.Where(s => s.Length > 0)) is { Length: > 0 } tip ? tip : null;

	// A game, a full-screen app or a game on the virtual controller: the toast waits and "Update" asks first.
	public bool IsPlaying
	{
		get
		{
			bool full;
			try
			{
				full = _fullScreen();
			}
			catch (Exception)
			{
				full = false;
			}
			return UpdatePolicy.IsPlaying(_service.Games.State.Exe != null, full, _service.IsGameConnected);
		}
	}

	private bool CanAct() => _services != null && Phase is UpdatePhase.Idle or UpdatePhase.Ready or UpdatePhase.Confirming or UpdatePhase.Error;

	[RelayCommand(CanExecute = nameof(CanAct))]
	private async Task Act()
	{
		if (_services == null)
		{
			return;
		}
		if (Manifest == null)
		{
			await CheckAsync(automatic: false);
			return;
		}
		if (UsesDownloadPage)
		{
			OpenPage(Manifest.Page);
			return;
		}
		if (Phase != UpdatePhase.Confirming && UpdatePolicy.NeedsConfirmation(IsPlaying))
		{
			Phase = UpdatePhase.Confirming;
			_confirmTimer?.Dispose();
			_confirmTimer = new Timer(_ => RunOnUi(() =>
			{
				if (Phase == UpdatePhase.Confirming)
				{
					Phase = UpdatePhase.Ready;
				}
			}), null, ConfirmWindow, Timeout.InfiniteTimeSpan);
			return;
		}
		await UpdateAsync(Manifest);
	}

	// Starts the automatic check: StartDelay after start, then every Tick it looks whether one is due.
	public void StartSchedule()
	{
		_schedule ??= new Timer(_ => RunOnUi(() => _ = RunScheduledCheckAsync()), null, UpdateSchedule.StartDelay, UpdateSchedule.Tick);
	}

	// The automatic check, when the switch is on and one is due (tests call it directly).
	public Task RunScheduledCheckAsync()
	{
		DateTimeOffset? last = Settings.UpdateCheckedAt;
		if (_availableAt is { } found && (last == null || found > last))
		{
			last = found;
		}
		return _services != null && Phase is UpdatePhase.Idle or UpdatePhase.Ready or UpdatePhase.Error
			&& UpdateSchedule.IsDue(Settings.AutoCheckUpdates, last, _lastFailure, _now())
			? CheckAsync(automatic: true)
			: Task.CompletedTask;
	}

	private async Task CheckAsync(bool automatic)
	{
		if (_services == null || Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Starting)
		{
			return;
		}
		UpdatePhase before = Phase;
		// The automatic check works quietly: the line only changes when it finds something.
		if (!automatic)
		{
			Phase = UpdatePhase.Checking;
		}
		UpdateCheckResult result = await _services.Checker.CheckAsync(_current);
		RunOnUi(() => Apply(result, automatic, before));
	}

	private void Apply(UpdateCheckResult result, bool automatic, UpdatePhase before)
	{
		switch (result.Status)
		{
			case UpdateCheckStatus.UpToDate:
				_checkedThisRun = true;
				_lastFailure = null;
				Settings.UpdateCheckedAt = _now();
				Manifest = null;
				_error = null;
				Phase = UpdatePhase.Idle;
				break;
			case UpdateCheckStatus.Available:
				_checkedThisRun = true;
				_lastFailure = null;
				_availableAt = _now();
				Manifest = result.Manifest;
				_error = null;
				Phase = UpdatePhase.Ready;
				TryToast();
				break;
			default:
				_lastFailure = _now();
				if (automatic)
				{
					// Tried again later; what was shown stays.
					Phase = before;
				}
				else
				{
					_error = result.Error switch
					{
						UpdateCheckError.Timeout => Text.UpdateCheckTimeout(),
						UpdateCheckError.Offline => Text.UpdateCheckOffline(),
						_ => Text.UpdateCheckInvalid()
					};
					Phase = Manifest != null ? UpdatePhase.Ready : UpdatePhase.Error;
				}
				break;
		}
		OnPropertyChanged(string.Empty);
	}

	private async Task UpdateAsync(UpdateManifest manifest)
	{
		_confirmTimer?.Dispose();
		Progress = 0;
		Phase = UpdatePhase.Downloading;
		var progress = new UiProgress(p => Progress = p);
		DownloadResult download = await _services!.Downloader.DownloadAsync(manifest.Setup, manifest.Version, progress);
		if (download.Status != DownloadStatus.Verified || download.Path == null)
		{
			Fail(download.Status switch
			{
				DownloadStatus.NotYetAvailable => Text.UpdateNotYetAvailable(),
				DownloadStatus.Offline => Text.UpdateDownloadOffline(),
				DownloadStatus.SaveFailed => Text.UpdateSaveFailed(),
				DownloadStatus.Cancelled => null,
				_ => Text.UpdateDownloadRejected()
			});
			return;
		}
		RunOnUi(() => Phase = UpdatePhase.Starting);
		if (_services.Install is not { } install)
		{
			Fail(Text.UpdateLaunchFailed());
			return;
		}
		InstallOutcome outcome = await install(download.Path, manifest.Setup, Localizer.Code(Localizer.Current));
		if (outcome != InstallOutcome.Started)
		{
			Fail(outcome == InstallOutcome.Rejected ? Text.UpdateDownloadRejected() : Text.UpdateLaunchFailed());
		}
	}

	private void Fail(string? message) => RunOnUi(() =>
	{
		_error = message;
		Phase = message == null ? UpdatePhase.Ready : UpdatePhase.Error;
		OnPropertyChanged(string.Empty);
	});

	private void OpenPage(string url)
	{
		try
		{
			_services!.Browser.Open(url);
		}
		catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			AppLog.Write($"update: could not open the download page ({e.GetType().Name})");
		}
	}

	// One toast per version: not while playing (shown when the game is left), not with notifications off.
	public void TryToast()
	{
		if (Manifest is not { } m)
		{
			return;
		}
		string version = m.Version.ToString();
		UpdateToastDecision decision = UpdatePolicy.Toast(m.Version, Settings.UpdateToastedVersion, Settings.NotificationsEnabled, IsPlaying);
		if (decision == UpdateToastDecision.Show && _notifications.Post(NotificationCenter.UpdateAvailable(version)))
		{
			Settings.UpdateToastedVersion = version;
			AppLog.Write($"toast: new version {version} shown");
		}
	}

	protected override void OnLanguageChanged()
	{
		// The error line was written in the old language: show the plain state again.
		if (Phase == UpdatePhase.Error && Manifest == null)
		{
			Phase = UpdatePhase.Idle;
		}
		base.OnLanguageChanged();
	}

	// Reports on the UI thread (the download runs on the thread pool).
	private sealed class UiProgress(Action<double> report) : IProgress<double>
	{
		public void Report(double value) => RunOnUiStatic(() => report(value));
	}

	private static void RunOnUiStatic(Action action) => RunOnUi(action);
}
