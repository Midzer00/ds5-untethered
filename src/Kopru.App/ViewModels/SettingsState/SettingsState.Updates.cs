// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;

namespace Kopru.App.ViewModels;

// In-app updates: the automatic check switch and when it last succeeded, the version whose "new version" toast was shown,
// and the version that ran last (for the settings backup on the first start of a new version).
public sealed partial class SettingsState
{
	[ObservableProperty]
	private bool _autoCheckUpdates = true;

	[ObservableProperty]
	private DateTimeOffset? _updateCheckedAt;

	[ObservableProperty]
	private string? _updateToastedVersion;

	// The version written as LastRunVersion: the one that last ran until MarkRunVersion says otherwise.
	public string? LastRunVersion { get; private set; }

	private void LoadUpdates(AppSettings saved)
	{
		AutoCheckUpdates = saved.AutoCheckUpdates;
		UpdateCheckedAt = saved.UpdateCheckedAt;
		UpdateToastedVersion = saved.UpdateToastedVersion;
		LastRunVersion = saved.LastRunVersion;
	}

	private AppSettings WithUpdates(AppSettings settings)
	{
		settings.AutoCheckUpdates = AutoCheckUpdates;
		settings.UpdateCheckedAt = UpdateCheckedAt;
		settings.UpdateToastedVersion = UpdateToastedVersion;
		settings.LastRunVersion = LastRunVersion;
		return settings;
	}

	// The app started as `version` (after the settings backup): saved soon when it differs from the last one.
	public void MarkRunVersion(string version)
	{
		if (LastRunVersion == version)
		{
			return;
		}
		LastRunVersion = version;
		ScheduleSave();
	}
}
