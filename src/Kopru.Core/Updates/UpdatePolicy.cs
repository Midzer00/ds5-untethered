// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Updates;

public enum UpdateToastDecision
{
	Show,
	// Shown once for this version already (settings remember it across starts).
	AlreadyShown,
	// "Show Notifications" is off.
	NotificationsOff,
	// A game is in front, a full-screen app is, or the virtual controller is feeding a game: wait for later.
	Playing
}

// What may happen while the user plays. The toast waits (Playing); "Update" asks first (NeedsConfirmation).
public static class UpdatePolicy
{
	// game: a supported (or added) game is in front; fullScreen: a full-screen app is; feeding: a game is connected to the
	// virtual controller.
	public static bool IsPlaying(bool game, bool fullScreen, bool feeding) => game || fullScreen || feeding;

	public static UpdateToastDecision Toast(AppVersion available, string? shownFor, bool notificationsOn, bool playing)
	{
		if (AppVersion.Parse(shownFor) is { } shown && shown >= available)
		{
			return UpdateToastDecision.AlreadyShown;
		}
		if (!notificationsOn)
		{
			return UpdateToastDecision.NotificationsOff;
		}
		return playing ? UpdateToastDecision.Playing : UpdateToastDecision.Show;
	}

	// Updating stops the virtual controller for a moment: while playing, "Update" first asks.
	public static bool NeedsConfirmation(bool playing) => playing;
}
