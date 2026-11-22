// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The Overview's live card while the bridge runs, in two views picked in its header:
// - Link: the typical delay as the headline beside a small quality gauge, and the last 60 seconds as an area chart (the
//   worst delay of each second; the bridge keeps that history whatever page is shown, Sample() only reads it once a second
//   while the page is shown), with small ticks under seconds that lost reports or paused, and the input rate in one muted line.
// - Signals: the last 10 seconds of the triggers, the haptic motors and (while it is live) the microphone as small scrolling
//   traces, sampled every SignalInterval into rings by a timer that runs only while this view is shown on a shown page.
public sealed partial class LivePanelViewModel : ViewModelBase
{
	public const int HistoryLength = LinkHistory.Seconds;
	public static readonly TimeSpan SignalInterval = TimeSpan.FromMilliseconds(33);
	// 10 s of samples at SignalInterval.
	public const int SignalSamples = 300;
	public const int LinkView = 0;
	public const int SignalsView = 1;

	private readonly IControllerService _service;
	private readonly Func<bool> _micLive;
	private DispatcherTimer? _signalTimer;
	private bool _shown;
	private int _flatSamples;

	public LivePanelViewModel(IControllerService service, Func<bool>? micLive = null)
	{
		_service = service;
		_micLive = micLive ?? (() => false);
		Views = [new TabOption(() => Text.LiveViewLink()), new TabOption(() => Text.LiveViewSignals())];
	}

	// The header's switch.
	public IReadOnlyList<TabOption> Views { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsLinkView), nameof(IsSignalsView))]
	private int _selectedView = LinkView;

	public bool IsLinkView => SelectedView != SignalsView;
	public bool IsSignalsView => SelectedView == SignalsView;

	partial void OnSelectedViewChanged(int value) => UpdateSignalTimer();

	private bool IsLive => _service.IsBridgeRunning && _service.IsConnected;

	// Labels.
	public string RateLabel => Text.StatInputRate();
	public string GraphCaption => Text.LiveLast60s();
	public string SignalsCaption => Text.LiveLast10s();
	public string GraphTip => Text.LinkGraphTip();
	public string DelayCaption => Text.LiveTypicalDelay();
	public double LimitMs => LinkLimits.PoorDelayMs;
	public string Unit => "ms";
	public string L2Label => "L2";
	public string R2Label => "R2";
	public string HapticsLabel => Text.CardHaptics();
	public string MicLabel => Text.LiveMicShort();
	public string SignalsTip => Text.LiveSignalsTip();
	public string TriggersTip => Text.LiveTriggerTip();
	public string LeftHapticsTip => Text.LiveLeftHapticsTip();
	public string RightHapticsTip => Text.LiveRightHapticsTip();
	public string HapticsTip => LeftHapticsTip + Environment.NewLine + RightHapticsTip;

	// The headline: the typical delay ("1,2 ms"); a dash while there is no reading yet.
	public string DelayValue => _service.Latency is { } r ? Text.DelayMs(r.LinkP50Ms) : "—";

	// Input rate now, one muted line under the chart ("718 rapor/sn").
	public string RateValue => Text.InputRate(Math.Round(_service.InputRateHz));
	public bool HasRate => _service.InputRateHz > 0;

	// The last minute, oldest first: the worst and typical delay of each second (NaN: no reading that second) and the
	// seconds that lost reports or paused.
	public IReadOnlyList<double> WorstHistory { get; private set; } = [];
	public IReadOnlyList<double> TypicalHistory { get; private set; } = [];
	public IReadOnlyList<bool> TroubleMarks { get; private set; } = [];

	// The chart's scale: from 0 to a round value over the recent worst (ChartTop); the limit line only when it is inside
	// that range.
	public double ChartTop { get; private set; } = ChartTopFor([]);
	public double ShownLimitMs => LimitMs <= ChartTop ? LimitMs : 0;

	// Round ceilings for the chart, so the axis reads well ("10 ms", "20 ms", ...).
	private static readonly double[] NiceTops = [10, 15, 20, 25, 30, 40, 50, 60, 80, 100, 150, 200, 300, 500, 1000];

	// The top of the scale for these samples: about 1.2 times the highest, rounded up to a round value, never under 10 ms.
	public static double ChartTopFor(IReadOnlyList<double> values)
	{
		double want = Math.Max(10, 1.2 * values.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max());
		return NiceTops.FirstOrDefault(n => n >= want, Math.Ceiling(want / 500) * 500);
	}

	// Signals: one ring per series (0..1) and a revision that changes when what they show changed.
	public SignalRing L2Trace { get; } = new(SignalSamples);
	public SignalRing R2Trace { get; } = new(SignalSamples);
	public SignalRing HapticsLeftTrace { get; } = new(SignalSamples);
	public SignalRing HapticsRightTrace { get; } = new(SignalSamples);
	public SignalRing MicTrace { get; } = new(SignalSamples);

	[ObservableProperty]
	private long _signalRevision;

	// The current values as small numbers (0-100).
	public string L2Text => Percent(L2Trace.Latest);
	public string R2Text => Percent(R2Trace.Latest);
	public string HapticsLeftText => Percent(HapticsLeftTrace.Latest);
	public string HapticsRightText => Percent(HapticsRightTrace.Latest);
	public string MicText => Percent(MicTrace.Latest);

	private static string Percent(float v) => Math.Round(v * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

	// The microphone lane only while the controller's microphone is live.
	[ObservableProperty]
	private bool _hasMic;

	// Whether the Signals timer runs (the view is selected, the page shown and the bridge live).
	public bool IsSignalsRunning => _signalTimer != null;

	// The page was shown or left (set by the Overview).
	public void SetShown(bool shown)
	{
		_shown = shown;
		if (shown)
		{
			Sample();
		}
		UpdateSignalTimer();
	}

	// Called on every state change: the headline follows the reading; the history is cleared when the bridge stops.
	public void OnStateChanged()
	{
		if (!IsLive && WorstHistory.Count > 0)
		{
			Publish([]);
		}
		OnPropertyChanged(nameof(DelayValue));
		OnPropertyChanged(nameof(RateValue));
		OnPropertyChanged(nameof(HasRate));
		HasMic = IsLive && _micLive() && _service is IMicLevelSource;
		UpdateSignalTimer();
	}

	// Once a second while the page is shown: the last minute as the bridge kept it.
	public void Sample() => Publish(IsLive ? _service.LinkSeconds ?? [] : []);

	// Every SignalInterval while the Signals view is shown: the triggers, haptics and microphone levels.
	public void Follow()
	{
		byte[]? raw = _service.IsBridgeRunning ? _service.RawInput : null;
		double l2 = raw is { Length: >= 6 } ? raw[4] / 255.0 : 0;
		double r2 = raw is { Length: >= 6 } ? raw[5] / 255.0 : 0;
		double left = _service.IsBridgeRunning ? Math.Clamp(_service.HapticsLeftLevel, 0, 1) : 0;
		double right = _service.IsBridgeRunning ? Math.Clamp(_service.HapticsRightLevel, 0, 1) : 0;
		double mic = HasMic && _service is IMicLevelSource source ? Math.Clamp(source.MicLevel, 0, 1) : 0;
		bool same = L2Trace.Count > 0 && Same(L2Trace, l2) && Same(R2Trace, r2) && Same(HapticsLeftTrace, left) && Same(HapticsRightTrace, right)
			&& Same(MicTrace, mic);
		L2Trace.Push(l2);
		R2Trace.Push(r2);
		HapticsLeftTrace.Push(left);
		HapticsRightTrace.Push(right);
		MicTrace.Push(mic);
		// Once the whole window shows the same values, another equal sample changes nothing on screen.
		_flatSamples = same ? _flatSamples + 1 : 0;
		if (_flatSamples < SignalSamples)
		{
			SignalRevision++;
			foreach (string p in new[] { nameof(L2Text), nameof(R2Text), nameof(HapticsLeftText), nameof(HapticsRightText), nameof(MicText) })
			{
				OnPropertyChanged(p);
			}
		}
	}

	private static bool Same(SignalRing ring, double value) => ring.Latest == (float)Math.Clamp(value, 0, 1);

	private void UpdateSignalTimer()
	{
		bool run = _shown && IsSignalsView && IsLive;
		if (run && _signalTimer == null)
		{
			// A fresh window: what was drawn before the view was left is not the last 10 seconds any more.
			foreach (SignalRing ring in new[] { L2Trace, R2Trace, HapticsLeftTrace, HapticsRightTrace, MicTrace })
			{
				ring.Clear();
			}
			_flatSamples = 0;
			_signalTimer = new DispatcherTimer(SignalInterval, DispatcherPriority.Background, (_, _) => Follow());
			_signalTimer.Start();
			Follow();
		}
		else if (!run && _signalTimer != null)
		{
			_signalTimer.Stop();
			_signalTimer = null;
		}
	}

	private void Publish(IReadOnlyList<LinkSecondPoint> seconds)
	{
		WorstHistory = [.. seconds.Select(s => s.WorstMs)];
		TypicalHistory = [.. seconds.Select(s => s.TypicalMs)];
		TroubleMarks = [.. seconds.Select(s => s.HasTrouble)];
		ChartTop = ChartTopFor([.. WorstHistory, .. TypicalHistory]);
		foreach (string p in new[] { nameof(WorstHistory), nameof(TypicalHistory), nameof(TroubleMarks), nameof(ChartTop), nameof(ShownLimitMs),
			nameof(RateValue), nameof(HasRate), nameof(DelayValue) })
		{
			OnPropertyChanged(p);
		}
	}
}
