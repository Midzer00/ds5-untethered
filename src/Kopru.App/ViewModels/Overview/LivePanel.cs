// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The Overview's live card while the bridge runs: the link's typical delay as the headline with its worst beside it, the
// last 30 seconds as a small graph (the worst delay of each second, scaled to the data with the "you can feel it" limit
// drawn only when it is in range), the input rate with its low, and the triggers and haptics as small live meters. Only
// measured values; Sample() is called once a second, Follow() at the input rate, both only while the page is shown.
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
	public string RateLabel => Text.StatInputRate();
	public string GraphCaption => Text.LiveLast30s();
	public string GraphTip => Text.LinkGraphTip();
	public double LimitMs => Kopru.Core.Diagnostics.LinkLimits.PoorDelayMs;
	public string Unit => "ms";
	public string TriggersLabel => Text.LiveTriggers();
	public string HapticsLabel => Text.CardHaptics();
	public string L2Label => "L2";
	public string R2Label => "R2";
	public string LeftLabel => Text.LeftShort();
	public string RightLabel => Text.RightShort();
	public string TriggersTip => Text.LiveTriggerTip();
	public string LeftHapticsTip => Text.LiveLeftHapticsTip();
	public string RightHapticsTip => Text.LiveRightHapticsTip();

	// The headline: the typical delay ("1,2 ms"), and the worst under it ("Gecikme · en kötü 4,5 ms"); a dash while there is
	// no reading yet.
	public string DelayValue => _service.Latency is { } r ? Text.DelayMs(r.LinkP50Ms) : "—";
	public string DelayDetail => _service.Latency is { } r ? Text.LiveDelayWorst(r.LinkP99Ms) : Text.StatLatency();

	// Input rate now ("718 rapor/sn") with the low of the last 30 seconds beside it ("en düşük 696").
	public string RateValue => Text.InputRate(Math.Round(_service.InputRateHz));
	public bool HasRateMin => _rates.Count >= 3;
	public string RateMin => _rates.Count > 0 ? Text.LiveRateMin(_rates.Min()) : string.Empty;
	public IReadOnlyList<double> TypicalHistory { get; private set; } = [];
	public IReadOnlyList<double> WorstHistory { get; private set; } = [];

	// The chart's scale: from 0 to a round value over the recent worst (ChartTop); the limit line only when it is inside
	// that range. A note at the top edge says what the line means, or that everything is under it.
	public double ChartTop { get; private set; } = ChartTopFor([]);
	public double ShownLimitMs => LimitMs <= ChartTop ? LimitMs : 0;
	public bool HasChartNote => WorstHistory.Count > 0;
	public string ChartNote => ShownLimitMs > 0 ? Text.LiveOverLimit(LimitMs) : Text.LiveUnderLimit(LimitMs);

	// Round ceilings for the chart, so the axis reads well ("10 ms", "20 ms", ...).
	private static readonly double[] NiceTops = [10, 15, 20, 30, 40, 60, 80, 100, 150, 200, 300, 500, 1000];

	// The top of the scale for these samples: about 1.5 times the highest, rounded up to a round value, never under 10 ms.
	public static double ChartTopFor(IReadOnlyList<double> values)
	{
		double want = Math.Max(10, 1.5 * values.DefaultIfEmpty(0).Max());
		return NiceTops.FirstOrDefault(n => n >= want, Math.Ceiling(want / 500) * 500);
	}

	// Meters (0-100) with their value as text ("%34"); a meter at rest reads 0 and is dimmed.
	[ObservableProperty] private double _l2;
	[ObservableProperty] private double _r2;
	[ObservableProperty] private double _hapticsLeft;
	[ObservableProperty] private double _hapticsRight;

	public string L2Text => Text.PercentValue(Math.Round(L2));
	public string R2Text => Text.PercentValue(Math.Round(R2));
	public string HapticsLeftText => Text.PercentValue(Math.Round(HapticsLeft));
	public string HapticsRightText => Text.PercentValue(Math.Round(HapticsRight));
	public bool IsL2Active => L2 >= 1;
	public bool IsR2Active => R2 >= 1;
	public bool IsHapticsLeftActive => HapticsLeft >= 1;
	public bool IsHapticsRightActive => HapticsRight >= 1;

	partial void OnL2Changed(double value) => Meter(nameof(L2Text), nameof(IsL2Active));
	partial void OnR2Changed(double value) => Meter(nameof(R2Text), nameof(IsR2Active));
	partial void OnHapticsLeftChanged(double value) => Meter(nameof(HapticsLeftText), nameof(IsHapticsLeftActive));
	partial void OnHapticsRightChanged(double value) => Meter(nameof(HapticsRightText), nameof(IsHapticsRightActive));

	private void Meter(string text, string active)
	{
		OnPropertyChanged(text);
		OnPropertyChanged(active);
	}

	// Called on every state change: the headline follows the reading; clears the history when the bridge stops.
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
		OnPropertyChanged(nameof(DelayValue));
		OnPropertyChanged(nameof(DelayDetail));
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
		ChartTop = ChartTopFor([.. _worst, .. _typical]);
		foreach (string p in new[] { nameof(TypicalHistory), nameof(WorstHistory), nameof(ChartTop), nameof(ShownLimitMs), nameof(HasChartNote), nameof(ChartNote),
			nameof(RateValue), nameof(RateMin), nameof(HasRateMin), nameof(DelayValue), nameof(DelayDetail) })
		{
			OnPropertyChanged(p);
		}
	}
}
