// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Diagnostics;

namespace Kopru.App.ViewModels;

// A tip shown in the main window (TipBar): its id (the saved key and the log name), its text, and when it counts as learned
// and stops appearing: the user did what it teaches UsesToLearn times, closed it ClosesToLearn times, or it was shown in
// RunsToLearn runs of the app (TipBar shows a tip at most once per run). UseName says in the log what was counted.
public sealed record TipRule(string Id, Func<string> Text, int UsesToLearn, int ClosesToLearn, string UseName, int RunsToLearn = int.MaxValue);

// The tips switch and each tip's progress (saved; a new settings store starts with nothing learned).
public sealed partial class SettingsState
{
	private readonly Dictionary<string, TipProgress> _tips = new(StringComparer.Ordinal);

	// "Show Tips" in Settings > General (on by default).
	[ObservableProperty]
	private bool _showTips = true;

	private void LoadTips(AppSettings saved)
	{
		ShowTips = saved.ShowTips;
		foreach ((string id, TipProgress p) in saved.Tips ?? [])
		{
			_tips[id] = Copy(p);
		}
	}

	private AppSettings WithTips(AppSettings settings)
	{
		settings.ShowTips = ShowTips;
		settings.Tips = _tips.Count == 0 ? null : _tips.ToDictionary(t => t.Key, t => Copy(t.Value));
		return settings;
	}

	private static TipProgress Copy(TipProgress p) => new() { Used = p.Used, Closed = p.Closed, Shown = p.Shown, Learned = p.Learned };

	public bool IsTipLearned(TipRule tip) => _tips.TryGetValue(tip.Id, out TipProgress? p) && p.Learned;

	// The user did what the tip teaches (UI thread).
	public void CountTipUse(TipRule tip) => Count(tip, p => p.Used++);

	// The user closed the tip (UI thread).
	public void CountTipClose(TipRule tip) => Count(tip, p => p.Closed++);

	// The tip was shown in this run (TipBar calls it once per run; UI thread).
	public void CountTipShown(TipRule tip) => Count(tip, p => p.Shown++);

	private void Count(TipRule tip, Action<TipProgress> change)
	{
		if (!_tips.TryGetValue(tip.Id, out TipProgress? p))
		{
			_tips[tip.Id] = p = new TipProgress();
		}
		if (p.Learned)
		{
			return;
		}
		change(p);
		string? why = p.Used >= tip.UsesToLearn ? $"{tip.UseName} {p.Used} times"
			: p.Closed >= tip.ClosesToLearn ? $"closed {p.Closed} times"
			: p.Shown >= tip.RunsToLearn ? $"shown in {p.Shown} runs"
			: null;
		if (why != null)
		{
			p.Learned = true;
			AppLog.Write($"tip learned: {tip.Id} ({why})");
		}
		ScheduleSave();
	}
}
