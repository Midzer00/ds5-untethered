// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;
using Kopru.Core.Mods;

namespace Kopru.App.ViewModels;

// The DSX Mod Support row (Controllers tab, its own Game Mods card): the switch, its one-line hint while off, and the
// listener's one-line state while on (listening on the port, the port in use, the last message from a mod N s ago).
public sealed partial class SettingsViewModel
{
	public string DsxGroupLabel => Text.DsxGroup();
	public string DsxModSupportLabel => Text.DsxModSupport();
	public string DsxModSupportShort => Text.DsxModSupportShort();
	public string DsxModSupportHint => Text.DsxModSupportHint();

	// The state line shows instead of the hint while the setting is on.
	public bool ShowsDsxStatus => Settings.DsxModSupport;

	public bool ShowsDsxHint => !Settings.DsxModSupport;

	public string DsxStatusText
	{
		get
		{
			DsxStatus status = _service.DsxStatus;
			// Just switched on: the listener's first state is on its way.
			DsxListenerState state = status.State == DsxListenerState.Off && Settings.DsxModSupport ? DsxListenerState.Listening : status.State;
			int? ago = status.LastMessageAt is { } at ? (int)Math.Round((DateTimeOffset.UtcNow - at).TotalSeconds) : null;
			return Text.DsxStatus(state, status.Port, ago);
		}
	}

	// The port is taken or could not be opened: the line shows as a problem.
	public bool DsxStatusIsProblem => Settings.DsxModSupport && _service.DsxStatus.State is DsxListenerState.PortInUse or DsxListenerState.Failed;

	public bool DsxStatusIsFine => Settings.DsxModSupport && !DsxStatusIsProblem;

	private void InitDsx()
	{
		_service.DsxStatusChanged += (_, _) => RunOnUi(NotifyDsx);
		Settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.DsxModSupport))
			{
				NotifyDsx();
			}
		};
	}

	private void NotifyDsx()
	{
		OnPropertyChanged(nameof(ShowsDsxStatus));
		OnPropertyChanged(nameof(ShowsDsxHint));
		OnPropertyChanged(nameof(DsxStatusText));
		OnPropertyChanged(nameof(DsxStatusIsProblem));
		OnPropertyChanged(nameof(DsxStatusIsFine));
	}
}
