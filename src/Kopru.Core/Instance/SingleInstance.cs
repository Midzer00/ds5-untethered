// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Instance;

public enum InstanceDecision
{
	// No other copy runs: this one is the app.
	Run,
	// Another copy runs: ask it to show its window, then exit.
	ShowOtherAndExit,
	// Another copy runs and this start is the autostart (--minimized): exit without disturbing it.
	ExitQuietly
}

public static class InstancePolicy
{
	public static InstanceDecision Decide(bool firstCopy, bool minimized) =>
		firstCopy ? InstanceDecision.Run : minimized ? InstanceDecision.ExitQuietly : InstanceDecision.ShowOtherAndExit;
}
