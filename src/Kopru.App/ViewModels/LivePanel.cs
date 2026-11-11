// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The Overview's live card while the bridge runs: how the link feels with its last 30 seconds as a small graph (the worst
// delay of each second against the "you can feel it" limit, on a fixed scale), the input rate with its low and average,
// L2/R2 and the haptics output as level bars. Only measured values; Sample() is called once a second, Follow() at the
// input rate.
public sealed partial class LivePanelViewModel : ViewModelBase
{
	public const int HistoryLength = 30;

	private readonly IControllerService _service;
	private readonly Queue<double> _typical = new();
	private readonly Queue<double> _worst = new();
	private readonly Queue<double> _rates = new();
	private bool _wasRunning;

	public LivePanelViewModel(IControllerService service)
	{
		_service = service;
	}

	// Labels.
	public string LatencyLabel => Text.StatLatency();
	public string RateLabel => Text.StatInputRate();
	public string GraphCaption => Text.LiveLast30s();
	public string GraphTip => Text.LinkGraphTip();
	// The chart: a fixed 0 to twice the limit scale, so the same height always means the same delay.
	public double LimitMs => Kopru.Core.Diagnostics.LinkLimits.PoorDelayMs;
	public double ScaleMaxMs => 2 * LimitMs;
	public string LimitLabel => Text.LinkLimitLabel();
	public string Unit => "ms";
	public string L2Label => "L2";
	public string R2Label => "R2";
	public string LeftLabel => Text.LiveLeftHaptics();
	public string RightLabel => Text.LiveRightHaptics();
	public string TriggersTip => Text.LiveTriggerTip();
	public string LeftHapticsTip => Text.LiveLeftHapticsTip();
	public string RightHapticsTip => Text.LiveRightHapticsTip();

	// Link delay and input rate.
	public string RateValue => Text.InputRate(Math.Round(_service.InputRateHz));
	public bool HasRateRange => _rates.Count >= 3;
	public string RateRange => _rates.Count > 0 ? Text.LiveRateRange(_rates.Min(), _rates.Average()) : Text.HealthUnknown();
	public IReadOnlyList<double> TypicalHistory { get; private set; } = [];
	public IReadOnlyList<double> WorstHistory { get; private set; } = [];

	// Level bars (0-100).
	[ObservableProperty] private double _l2;
	[ObservableProperty] private double _r2;
	[ObservableProperty] private double _hapticsLeft;
	[ObservableProperty] private double _hapticsRight;

	// Called on every state change: clears the history when the bridge stops.
	public void OnStateChanged()
	{
		bool running = _service.IsBridgeRunning && _service.IsConnected;
		if (!running && _wasRunning)
		{
			_typical.Clear();
			_worst.Clear();
			_rates.Clear();
			Publish();
		}
		_wasRunning = running;
	}

	// Once a second while live: adds the current delay and input rate to the history.
	public void Sample()
	{
		if (!(_service.IsBridgeRunning && _service.IsConnected))
		{
			return;
		}
		if (_service.Latency is { } r)
		{
			Push(_typical, r.LinkP50Ms);
			Push(_worst, r.LinkP99Ms);
		}
		if (_service.InputRateHz > 0)
		{
			Push(_rates, _service.InputRateHz);
		}
		Publish();
	}

	// At the input rate while shown: the trigger, haptics and microphone levels.
	public void Follow()
	{
		byte[]? raw = _service.IsBridgeRunning ? _service.RawInput : null;
		L2 = raw is { Length: >= 6 } ? raw[4] / 2.55 : 0;
		R2 = raw is { Length: >= 6 } ? raw[5] / 2.55 : 0;
		HapticsLeft = Math.Clamp(_service.HapticsLeftLevel, 0, 1) * 100;
		HapticsRight = Math.Clamp(_service.HapticsRightLevel, 0, 1) * 100;
	}

	private static void Push(Queue<double> q, double v)
	{
		q.Enqueue(v);
		while (q.Count > HistoryLength)
		{
			q.Dequeue();
		}
	}

	private void Publish()
	{
		TypicalHistory = [.. _typical];
		WorstHistory = [.. _worst];
		OnPropertyChanged(nameof(TypicalHistory));
		OnPropertyChanged(nameof(WorstHistory));
		OnPropertyChanged(nameof(RateValue));
		OnPropertyChanged(nameof(RateRange));
		OnPropertyChanged(nameof(HasRateRange));
	}
}
