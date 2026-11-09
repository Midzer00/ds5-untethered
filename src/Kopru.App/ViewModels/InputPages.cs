// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

public sealed class CurveOption(ResponseCurve curve)
{
	public ResponseCurve Curve { get; } = curve;
	public string Name => Text.CurveName(Curve);
}

// One stick's settings (written back into SettingsState.Tuning) and its live preview: raw and tuned position, -1..1.
public sealed partial class StickEditor : ViewModelBase
{
	private readonly SettingsState _settings;
	private readonly bool _left;

	public StickEditor(SettingsState settings, bool left)
	{
		_settings = settings;
		_left = left;
		Curves = BuildCurves();
	}

	private StickTuning Current => _left ? _settings.Tuning.LeftStick : _settings.Tuning.RightStick;

	public string Title => _left ? Text.LeftStick() : Text.RightStick();
	public string InnerLabel => Text.InnerDeadzoneLabel();
	public string OuterLabel => Text.OuterDeadzoneLabel();
	public string AntiLabel => Text.AntiDeadzoneLabel();
	public string CurveLabel => Text.ResponseCurveLabel();
	public IReadOnlyList<CurveOption> Curves { get; private set; }

	public double Inner { get => Current.InnerDeadzone; set => Set(t => t with { InnerDeadzone = value }); }
	public double Outer { get => Current.OuterDeadzone; set => Set(t => t with { OuterDeadzone = value }); }
	public double Anti { get => Current.AntiDeadzone; set => Set(t => t with { AntiDeadzone = value }); }
	public string InnerText => Text.PercentValue(Math.Round(Inner));
	public string OuterText => Text.PercentValue(Math.Round(Outer));
	public string AntiText => Text.PercentValue(Math.Round(Anti));

	public CurveOption? SelectedCurve
	{
		get => Curves.FirstOrDefault(c => c.Curve == Current.Curve);
		set
		{
			if (value != null && value.Curve != Current.Curve)
			{
				Set(t => t with { Curve = value.Curve });
			}
		}
	}

	// Raw input is arriving (the bridge runs with a controller).
	[ObservableProperty]
	private bool _isLive;

	[ObservableProperty]
	private double _rawX;

	[ObservableProperty]
	private double _rawY;

	[ObservableProperty]
	private double _tunedX;

	[ObservableProperty]
	private double _tunedY;

	// Called when SettingsState.Tuning changed.
	public void Refresh() => OnPropertyChanged(string.Empty);

	public void SetPosition(byte rawX, byte rawY, byte tunedX, byte tunedY)
	{
		RawX = Axis(rawX);
		RawY = Axis(rawY);
		TunedX = Axis(tunedX);
		TunedY = Axis(tunedY);
	}

	protected override void OnLanguageChanged()
	{
		Curves = BuildCurves();
		base.OnLanguageChanged();
	}

	private static double Axis(byte v) => Math.Clamp((v - 127.5) / 127.5, -1, 1);

	private static IReadOnlyList<CurveOption> BuildCurves() =>
		[new(ResponseCurve.Linear), new(ResponseCurve.Smooth), new(ResponseCurve.Aggressive)];

	private void Set(Func<StickTuning, StickTuning> change)
	{
		InputTuning tuning = _settings.Tuning;
		_settings.Tuning = _left ? tuning with { LeftStick = change(tuning.LeftStick) } : tuning with { RightStick = change(tuning.RightStick) };
	}
}

// One trigger's range (percent of travel) and its live level, 0..100.
public sealed partial class TriggerRangeEditor : ViewModelBase
{
	private readonly SettingsState _settings;
	private readonly bool _left;

	public TriggerRangeEditor(SettingsState settings, bool left)
	{
		_settings = settings;
		_left = left;
	}

	private TriggerTuning Current => _left ? _settings.Tuning.L2 : _settings.Tuning.R2;

	// Button names, the same in every language.
	public string Title => _left ? "L2" : "R2";
	public string StartLabel => Text.TriggerStartLabel();
	public string EndLabel => Text.TriggerEndLabel();

	// The end stays at least 5 % above the start.
	public double Start { get => Current.Start; set => Set(t => t with { Start = Math.Min(value, t.End - 5) }); }
	public double End { get => Current.End; set => Set(t => t with { End = Math.Max(value, t.Start + 5) }); }
	public string StartText => Text.PercentValue(Math.Round(Start));
	public string EndText => Text.PercentValue(Math.Round(End));

	[ObservableProperty]
	private bool _isLive;

	[ObservableProperty]
	private double _rawLevel;

	[ObservableProperty]
	private double _tunedLevel;

	public void Refresh() => OnPropertyChanged(string.Empty);

	public void SetLevel(byte raw, byte tuned)
	{
		RawLevel = raw / 255.0 * 100;
		TunedLevel = tuned / 255.0 * 100;
	}

	private void Set(Func<TriggerTuning, TriggerTuning> change)
	{
		InputTuning tuning = _settings.Tuning;
		_settings.Tuning = _left ? tuning with { L2 = change(tuning.L2) } : tuning with { R2 = change(tuning.R2) };
	}
}

// Sticks & triggers: deadzones, curve, drift calibration and trigger ranges, with a live preview of the raw input.
public sealed partial class SticksViewModel : PageViewModel
{
	public static readonly TimeSpan CalibrationLength = TimeSpan.FromSeconds(3);
	public static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(50);

	private readonly IControllerService _service;
	private readonly Timer? _previewTimer;
	private byte[]? _lastRaw;
	private InputTransform _transform;

	public SticksViewModel(IControllerService service, SettingsState settings, bool livePreview = true)
	{
		_service = service;
		Settings = settings;
		_transform = new InputTransform(settings.Tuning);
		LeftStick = new StickEditor(settings, left: true);
		RightStick = new StickEditor(settings, left: false);
		L2 = new TriggerRangeEditor(settings, left: true);
		R2 = new TriggerRangeEditor(settings, left: false);
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.Tuning))
			{
				_transform = new InputTransform(Settings.Tuning);
				LeftStick.Refresh();
				RightStick.Refresh();
				L2.Refresh();
				R2.Refresh();
				UpdatePreview(_lastRaw, force: true);
			}
		};
		if (livePreview)
		{
			_previewTimer = new Timer(_ => RunOnUi(RefreshInput), null, PreviewInterval, PreviewInterval);
		}
	}

	public SettingsState Settings { get; }
	public StickEditor LeftStick { get; }
	public StickEditor RightStick { get; }
	public TriggerRangeEditor L2 { get; }
	public TriggerRangeEditor R2 { get; }

	public override string Title => Text.NavSticks();
	public string Intro => Text.SticksIntro();
	public string PreviewHint => HasLiveInput ? Text.StickPreviewHint() : Text.LivePreviewNeedsBridge();
	public string CalibrationLabel => Text.DriftCalibration();
	public string CalibrationHint => Text.DriftCalibrationHint();
	public string CalibrateText => Text.CalibrateAction();
	public string TriggerRangeLabel => Text.TriggerRange();
	public string TriggerRangeHint => Text.TriggerRangeHint();
	public string ResetText => Text.ResetToDefault();
	public bool HasLiveInput => _lastRaw != null;
	public bool HasCalibrationText => !string.IsNullOrEmpty(CalibrationText);

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CalibrateCommand))]
	private bool _isCalibrating;

	[ObservableProperty]
	private double _calibrationProgress;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasCalibrationText))]
	private string _calibrationText = string.Empty;

	// Stops the live preview timer.
	public void StopPreview() => _previewTimer?.Dispose();

	// Reads the latest raw input and moves the preview (timer; tests call it directly).
	public void RefreshInput() => UpdatePreview(_service.RawInput, force: false);

	private void UpdatePreview(byte[]? raw, bool force)
	{
		if (raw == null || raw.Length <= InputTransform.Buttons1)
		{
			if (_lastRaw != null)
			{
				_lastRaw = null;
				SetLive(false);
				OnPropertyChanged(nameof(HasLiveInput));
				OnPropertyChanged(nameof(PreviewHint));
			}
			return;
		}
		if (!force && _lastRaw != null && raw.AsSpan().SequenceEqual(_lastRaw))
		{
			return;
		}
		bool appeared = _lastRaw == null;
		_lastRaw = raw;
		byte[] tuned = (byte[])raw.Clone();
		_transform.Apply(tuned);
		LeftStick.SetPosition(raw[InputTransform.LeftX], raw[InputTransform.LeftY], tuned[InputTransform.LeftX], tuned[InputTransform.LeftY]);
		RightStick.SetPosition(raw[InputTransform.RightX], raw[InputTransform.RightY], tuned[InputTransform.RightX], tuned[InputTransform.RightY]);
		L2.SetLevel(raw[InputTransform.L2Analog], tuned[InputTransform.L2Analog]);
		R2.SetLevel(raw[InputTransform.R2Analog], tuned[InputTransform.R2Analog]);
		if (appeared)
		{
			SetLive(true);
			OnPropertyChanged(nameof(HasLiveInput));
			OnPropertyChanged(nameof(PreviewHint));
		}
	}

	private void SetLive(bool live) => LeftStick.IsLive = RightStick.IsLive = L2.IsLive = R2.IsLive = live;

	private bool CanCalibrate() => !IsCalibrating;

	[RelayCommand(CanExecute = nameof(CanCalibrate))]
	private async Task Calibrate()
	{
		IsCalibrating = true;
		CalibrationProgress = 0;
		CalibrationText = Text.CalibrationKeepStill();
		try
		{
			var progress = new Progress<double>(p => RunOnUi(() => CalibrationProgress = Math.Round(p * 100)));
			DriftCalibrationResult result = await _service.CalibrateDriftAsync(CalibrationLength, progress);
			if (result.Success)
			{
				Settings.Tuning = Settings.Tuning.WithCalibration(result);
			}
			CalibrationProgress = 100;
			CalibrationText = result.Message();
		}
		finally
		{
			IsCalibrating = false;
		}
	}

	[RelayCommand]
	private void Reset()
	{
		Settings.Tuning = InputTuning.Default;
		CalibrationText = string.Empty;
		CalibrationProgress = 0;
	}
}
