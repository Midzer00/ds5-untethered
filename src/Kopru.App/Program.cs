// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Kopru.App.Services;
using Kopru.Core.Diagnostics;
using Kopru.Core.Instance;

namespace Kopru.App;

internal static class Program
{
	// The running copy's guard (null in tests, which never go through Main). App listens on it for later starts.
	public static IAppInstance? Instance { get; private set; }

	public static void Main(string[] args)
	{
		// One copy per user session, from any folder: two copies would fight over the controller (a lock file and a Unix
		// domain socket, UnixSingleInstance).
		using IAppInstance instance = UnixSingleInstance.Acquire();
		bool minimized = args.Contains(XdgAutostartRegistration.MinimizedArgument, StringComparer.OrdinalIgnoreCase);
		switch (InstancePolicy.Decide(instance.IsFirst, minimized))
		{
			case InstanceDecision.ExitQuietly:
				AppLog.Write("start: already running; autostart copy exits");
				return;
			case InstanceDecision.ShowOtherAndExit:
				bool answered = instance.AskFirstToShow(UnixSingleInstance.DefaultAnswerTimeout);
				if (answered)
				{
					AppLog.Write("start: already running; showed its window and exits");
					return;
				}
				string hung = "DS5 Untethered is already running but did not answer. If it is stuck, end it (for example: pkill -f Kopru.App) " +
					"and start it again. Lock: " + UnixSingleInstance.DefaultDirectory();
				AppLog.Write("start: " + hung);
				Console.Error.WriteLine(hung);
				return;
		}
		Instance = instance;
		// The OS classes (controller, USB/IP client, hiding, input, sound) come from this platform from now on.
		Kopru.Core.Platform.IPlatform platform = PlatformSelection.Apply();
		AppLog.Write(platform.PrepareProcess());
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	// Popups (drop-downs, tooltips, menus) open inside the window, in the main window's scaled layer, so they grow with the
	// content (see UiScale); as separate windows they would keep the unscaled size.
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
		.With(new X11PlatformOptions { OverlayPopups = true });
}
