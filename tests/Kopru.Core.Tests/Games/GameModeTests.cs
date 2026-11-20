// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;

namespace Kopru.Core.Tests;

// The fake process source the game mode tests share (the Windows-path game mode tests themselves are not on this branch).
public class GameModeTests
{
	// Processes by exe name: Show starts one (or brings it to the front), Exit ends it.
	internal sealed class FakeProcesses : IProcessSource
	{
		private readonly Dictionary<string, ProcessInfo> running = new(StringComparer.OrdinalIgnoreCase);
		private int nextId = 100;
		private ProcessInfo? current;

		public void Show(string exe, string? path = null)
		{
			if (!running.TryGetValue(exe, out ProcessInfo? process) || process.Path != path)
			{
				process = new ProcessInfo(nextId++, exe, path);
				running[exe] = process;
			}
			current = process;
		}

		public void Exit(string exe)
		{
			running.Remove(exe);
			if (current?.Exe == exe)
			{
				current = null;
			}
		}

		public ProcessInfo? Foreground() => current;

		public bool IsRunning(ProcessInfo process) => running.Values.Any(p => p.Id == process.Id);

		public IReadOnlyList<ProcessInfo> WindowedApps() => running.Values.ToList();

		// A process that runs without coming to the front (a game starting behind its launcher).
		public void Start(string exe, string? path = null)
		{
			if (!running.ContainsKey(exe))
			{
				running[exe] = new ProcessInfo(nextId++, exe, path);
			}
		}

		public int AllProcessesCalls { get; private set; }

		public IReadOnlyList<ProcessInfo> AllProcesses()
		{
			AllProcessesCalls++;
			return running.Values.ToList();
		}
	}
}
