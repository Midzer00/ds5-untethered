// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// In-app tips: one short sentence at a time in the quiet bar at the bottom of the content column (in the pages' bottom
// padding, where nothing else is), with a close button. A tip shows when its moment comes (TryShow) unless tips are off or it is
// learned (SettingsState.IsTipLearned: see TipRule), and hides after ShowFor, when closed, or when what it teaches is no
// longer needed. One log line when a tip is shown (and when one is learned, SettingsState.Count). UI thread only.
public sealed partial class TipBar : ViewModelBase
{
	public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(8);

	// "Press the touchpad twice for Mouse Mode": when the main window becomes active while the gesture can work (the Virtual
	// Controller running with the controller, controller shortcuts on) and Mouse Mode is off. Learned after turning Mouse
	// Mode on with the gesture 3 times, closing the tip twice, or showing it in 5 runs.
	public static readonly TipRule MouseModeGesture = new("mouse-mode-gesture", () => Kopru.Core.Localization.Text.TipMouseModeGesture(), 3, 2,
		"turned mouse mode on with the touchpad", RunsToLearn: 5);

	// Tips shown in this run of the app: each one at most once per run.
	private readonly HashSet<string> _shownThisRun = new(StringComparer.Ordinal);

	private readonly IControllerService _service;
	private readonly SettingsState _settings;
	private DispatcherTimer? _timer;

	public TipBar(IControllerService service, SettingsState settings)
	{
		_service = service;
		_settings = settings;
		settings.DesktopGestureUsed += () => settings.CountTipUse(MouseModeGesture);
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.DesktopModeOn) && settings.DesktopModeOn && Current == MouseModeGesture)
			{
				Hide();
			}
			else if (e.PropertyName == nameof(SettingsState.ShowTips) && !settings.ShowTips)
			{
				Hide();
			}
		};
	}

	// The tip shown now, or null.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsVisible))]
	[NotifyPropertyChangedFor(nameof(Text))]
	private TipRule? _current;

	public bool IsVisible => Current != null;

	public string Text => Current?.Text() ?? string.Empty;

	public string CloseLabel => Kopru.Core.Localization.Text.CloseTip();

	// The main window became active (also when PS brought it up).
	public void OnWindowActivated()
	{
		// One tip at a time: a tip still showing keeps its place.
		if (Current != null)
		{
			return;
		}
		// The gesture needs the Virtual Controller running (the bridge reads the controller) and controller shortcuts on. Not
		// while a PS tap brings the window up: Mouse Mode comes on with it.
		if (_service.IsGameConnected && _service.IsConnected && _settings.ShortcutsEnabled && !_settings.DesktopModeOn && !_settings.PsOpeningApp)
		{
			TryShow(MouseModeGesture);
		}
	}

	// Shows the tip unless tips are off, it is learned, or it was already shown in this run. True when it shows now.
	public bool TryShow(TipRule tip)
	{
		if (!_settings.ShowTips || _settings.IsTipLearned(tip) || !_shownThisRun.Add(tip.Id))
		{
			return false;
		}
		Current = tip;
		AppLog.Write($"tip shown: {tip.Id}");
		_settings.CountTipShown(tip);
		_timer?.Stop();
		_timer = new DispatcherTimer { Interval = ShowFor };
		_timer.Tick += (_, _) => Expire();
		_timer.Start();
		return true;
	}

	// ShowFor has passed (the timer; tests call it directly).
	public void Expire() => Hide();

	[RelayCommand]
	private void Close()
	{
		if (Current is { } tip)
		{
			Hide();
			_settings.CountTipClose(tip);
		}
	}

	private void Hide()
	{
		_timer?.Stop();
		_timer = null;
		Current = null;
	}
}
