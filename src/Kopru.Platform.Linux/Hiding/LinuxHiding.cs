// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hiding;

namespace Kopru.Platform.Linux.Hiding;

// Hiding the real controller on Linux: NOT IMPLEMENTED. Every call only logs; IsInstalled is false, so the app treats
// hiding as unavailable (games may see the real pad next to the virtual one).
//
// TODO(linux): pick a way once hardware results are in (question (c) in docs/linux/README.md). The options known so far:
//   1. Unbind the kernel's DualSense driver (hid-playstation) from the Bluetooth device through sysfs, so its evdev
//      gamepad, touchpad and motion nodes go away. Needs root (a tiny helper). Open questions: does the hidraw node stay
//      usable for us, does BlueZ keep the link, and what rebinds it afterwards?
//   2. Leave the driver bound and take its evdev nodes away from games with EVIOCGRAB, opened by our process. No root if
//      the user can open them (uaccess). Games that read hidraw directly (SDL's HIDAPI driver, Steam) still see the pad,
//      so this needs (3) as well.
//   3. A udev rule that restricts the Bluetooth DualSense's hidraw node to a group only our app (or a helper that passes
//      the open descriptor on) is in. Games run as the same user, so plain file modes alone are not enough.
//   4. Per-game: SDL hints or Steam Input settings that ignore the Bluetooth pad. No system change, but per game and per
//      launcher.
// Whatever is chosen: root-only code stays tiny and separate from the app, every change is undone on exit and after a
// crash (as HidingSession does with its ledger on Windows), and BlueZ input must keep working.
public sealed class LinuxHiding(Action<string>? log = null) : NoControllerHiding
{
	public const string NotImplemented = "hiding: not implemented on Linux yet; games may see the real controller too (docs/linux/README.md)";

	private int logged;

	public override Task BeginAsync() => Log();

	public override Task HideAsync(string instanceId) => Log();

	private Task Log()
	{
		if (Interlocked.Exchange(ref logged, 1) == 0)
		{
			log?.Invoke(NotImplemented);
		}
		return Task.CompletedTask;
	}
}
