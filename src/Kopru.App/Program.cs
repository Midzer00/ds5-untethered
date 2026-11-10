// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Kopru.App.Services;
using Kopru.Core.Diagnostics;
using Kopru.Core.Instance;

namespace Kopru.App;

internal static class Program
{
	// The running copy's guard (null in tests, which never go through Main). App listens on it for later starts.
	public static SingleInstance? Instance { get; private set; }

	[STAThread]
	public static void Main(string[] args)
	{
		// One copy per user session, from any folder: two copies would fight over the controller.
		using SingleInstance instance = SingleInstance.Acquire();
		bool minimized = args.Contains(RunKeyStartupRegistration.MinimizedArgument, StringComparer.OrdinalIgnoreCase);
		switch (InstancePolicy.Decide(instance.IsFirst, minimized))
		{
			case InstanceDecision.ExitQuietly:
				AppLog.Write("start: already running; autostart copy exits");
				return;
			case InstanceDecision.ShowOtherAndExit:
				bool answered = instance.AskFirstToShow(SingleInstance.DefaultAnswerTimeout);
				AppLog.Write(answered ? "start: already running; showed its window and exits" : "start: already running but it did not answer; exits");
				return;
		}
		Instance = instance;
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
