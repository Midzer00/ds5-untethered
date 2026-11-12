// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Desktop mode: the switch (not saved; PS + Options, the tray and the Settings group change it), its tuning (saved), a toast
// on every change and the automatic switch-off when a known game comes to the front.
public sealed partial class SettingsState
{
	private DesktopModeViewModel? _desktopPanel;
	private string? _desktopGameKey;
	private string? _desktopOffGame;

	[ObservableProperty]
	private bool _desktopModeOn;

	[ObservableProperty]
	private DesktopModeSettings _desktopMode = DesktopModeSettings.Default;

	// The desktop mode group on the Settings page.
	public DesktopModeViewModel DesktopPanel => _desktopPanel ??= new DesktopModeViewModel(this);

	partial void OnDesktopModeOnChanged(bool value)
	{
		string? game = _desktopOffGame;
		_desktopOffGame = null;
		string body = value ? Text.DesktopOnBody() : game != null ? Text.DesktopOffGameBody(game) : Text.DesktopOffBody();
		ShortcutFeedback?.Invoke(value ? Text.DesktopOnTitle() : Text.DesktopOffTitle(), body);
	}

	private void InitDesktop()
	{
		_desktopGameKey = _service.Games.ActiveGame?.Key;
		_service.Games.StateChanged += _ => RunOnUi(CheckDesktopAutoOff);
	}

	// A known game that just came to the front turns desktop mode off (when the option is on). A game already in front
	// when the user turned desktop mode on doesn't.
	public void CheckDesktopAutoOff()
	{
		ForegroundGame? game = _service.Games.ActiveGame;
		string? key = game?.Key;
		if (string.Equals(key, _desktopGameKey, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_desktopGameKey = key;
		if (game != null && DesktopModeOn && DesktopMode.AutoOffInGame)
		{
			_desktopOffGame = ForegroundGame.DisplayName(game.Exe);
			Kopru.Core.Diagnostics.AppLog.Write($"mouse mode: switched off automatically, {game.Name} came to the front ({game.Exe})");
			DesktopModeOn = false;
		}
	}
}
