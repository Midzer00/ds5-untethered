// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Hiding;

// Hides the real Bluetooth controller from games while the bridge runs, so games see only the virtual pad (Windows:
// HidHide, HidHideHidingService). Faked in tests. Failures throw HidHideException.
public interface IControllerHiding
{
	// False when the platform has no way to hide (the app then warns that games may see two controllers).
	bool IsInstalled { get; }

	// Undoes what an earlier run left behind (crash). Safe on every start.
	Task RecoverAsync();

	// Lets our own process keep seeing the controller (before the controller is opened).
	Task BeginAsync();

	// Hides one controller, by the id the app knows it by (Windows: the Bluetooth HID instance, HID\{00001124-...}).
	Task HideAsync(string instanceId);

	// Undoes everything this run changed.
	Task RestoreAsync();

	// Lets our process through at app start, so a controller hidden by another tool is visible before the bridge starts
	// (after RecoverAsync).
	Task RegisterAppEarlyAsync();

	// Undoes RegisterAppEarlyAsync (app exit, after RestoreAsync).
	Task ReleaseAppEarlyAsync();
}

// No hiding on this platform: IsInstalled is false and every call does nothing.
public class NoControllerHiding : IControllerHiding
{
	public virtual bool IsInstalled => false;

	public virtual Task RecoverAsync() => Task.CompletedTask;

	public virtual Task BeginAsync() => Task.CompletedTask;

	public virtual Task HideAsync(string instanceId) => Task.CompletedTask;

	public virtual Task RestoreAsync() => Task.CompletedTask;

	public virtual Task RegisterAppEarlyAsync() => Task.CompletedTask;

	public virtual Task ReleaseAppEarlyAsync() => Task.CompletedTask;
}
