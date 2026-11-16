// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Kopru.App.ViewModels;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Services;

// The controller type toasts ("Xbox 360 Modu" / "DualShock 4 Modu" / "DualSense Modu"): once per swap (SettingsState.PadSwapped,
// not once per controller), and when a type is held back for the running game. Only while the virtual controller runs for a
// connected controller: otherwise no game sees the change yet. They count as game notices (the Game Detection switch).
public sealed partial class NotificationCenter
{
	// The last controller type toast shown on its own (when, and for which running game), so a "game detected" toast of the
	// same moment takes its place; the pending type last announced.
	private long? _padToastAt;
	private string? _padToastGame;
	private VirtualPadType? _pendingShown;

	public static string PadTypeTitle(VirtualPadType type) => type switch
	{
		VirtualPadType.Xbox360 => Text.PadModeXboxTitle(),
		VirtualPadType.DualShock4 => Text.PadModeDualShock4Title(),
		_ => Text.PadModeDualSenseTitle()
	};

	// After a swap. A game running then may not see the new controller: "restart the game if it doesn't see it". With no game
	// running: the default type ("games you have not set separately see this"), or DualSense back when no default is set.
	public static AppNotification PadTypeNotice(VirtualPadType type, PadTypeSource source, bool gameRunning, bool isEdge = false)
	{
		string body = gameRunning ? Text.PadSwapRestartHint()
			: source == PadTypeSource.Default && type == VirtualPadType.DualSense ? Text.PadModeDualSenseBody()
			: Text.PadTypeDefaultHint();
		return new AppNotification(NotificationType.GameDetected, NotificationKind.Info, PadTypeTitle(type), body, AppPage.SupportedGames, IsEdge: isEdge);
	}

	// A type chosen while the game runs waits for the game to restart.
	public static AppNotification PadTypePending(VirtualPadType type, bool isEdge = false) =>
		new(NotificationType.GameDetected, NotificationKind.Info, PadTypeTitle(type), Text.PadToastPendingBody(), AppPage.SupportedGames, IsEdge: isEdge);

	// "Cyberpunk 2077 · Oyun Şunu Görüyor: DualSense".
	public static AppNotification GameDetected(string name, VirtualPadType type, double? battery = null, bool isEdge = false, bool charging = false) =>
		new(NotificationType.GameDetected, NotificationKind.Info, name, Text.GameSees(SettingsState.PadTypeName(type)), AppPage.SupportedGames, battery, isEdge,
			charging);

	private void InitPadToasts()
	{
		_settings.PadSwapped += OnPadSwapped;
		_settings.PropertyChanged += OnPadState;
	}

	private bool PadToastsMatter => _service.IsConnected && _service.IsBridgeRunning;

	// Caller holds _gate.
	private bool RecentPadToastFor(string game) =>
		_padToastAt is { } at && _clockMs() - at < MergeWindow.TotalMilliseconds && string.Equals(_padToastGame, game, StringComparison.Ordinal);

	private void OnPadSwapped(PadSwap swap)
	{
		lock (_gate)
		{
			_pendingShown = null;
		}
		string what = $"{swap.From} -> {swap.To}";
		if (!PadToastsMatter)
		{
			Log($"toast: controller type {what} not shown (virtual controller not running)");
			return;
		}
		bool gameRunning = swap.GameName != null;
		// A "game detected" toast of the same game just shown: it takes the new type instead of a second toast.
		AppNotification? recentGame;
		lock (_gate)
		{
			recentGame = gameRunning && _gameToast is { } g && g.Title == swap.GameName && _clockMs() - _gameToastAt < MergeWindow.TotalMilliseconds ? g : null;
		}
		AppNotification n = recentGame != null
			? recentGame with { Body = Text.GameSees(SettingsState.PadTypeName(swap.To)), Replaces = NotificationType.GameDetected }
			: PadTypeNotice(swap.To, _service.Games.PadStatus.Source, gameRunning, IsEdge);
		bool shown = Post(n, false, false, out string? why);
		if (shown)
		{
			lock (_gate)
			{
				if (recentGame != null)
				{
					_gameToast = n;
				}
				else
				{
					_padToastAt = _clockMs();
					_padToastGame = swap.GameName;
				}
			}
		}
		Log(shown
			? $"toast: controller type {what} shown{(recentGame != null ? " in the game detected toast" : string.Empty)}"
			: $"toast: controller type {what} not shown ({why})");
	}

	private void OnPadState(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(SettingsState.HasPendingPadType))
		{
			return;
		}
		VirtualPadType? pending = _settings.PendingPadType;
		lock (_gate)
		{
			if (pending == _pendingShown)
			{
				return;
			}
			_pendingShown = pending;
		}
		if (pending is not { } type)
		{
			return;
		}
		if (!PadToastsMatter)
		{
			Log($"toast: controller type {type} waiting for the game not shown (virtual controller not running)");
			return;
		}
		bool shown = Post(PadTypePending(type, IsEdge), false, false, out string? why);
		Log(shown ? $"toast: controller type {type} waiting for the game shown" : $"toast: controller type {type} waiting for the game not shown ({why})");
	}
}
