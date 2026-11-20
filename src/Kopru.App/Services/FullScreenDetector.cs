// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.Services;

// Whether a full-screen game or video is in front ("Quiet While Playing", toasts). Linux: not known yet (Wayland does not
// tell other apps; X11 would need _NET_ACTIVE_WINDOW and _NET_WM_STATE_FULLSCREEN), so never.
// TODO(linux): an X11 check through the window manager's properties.
public static class FullScreenDetector
{
	public static bool IsForegroundFullScreen() => ForegroundFullScreenApp() != null;

	// The name of the full-screen program in front, or null when none (always null for now).
	public static string? ForegroundFullScreenApp() => null;
}
