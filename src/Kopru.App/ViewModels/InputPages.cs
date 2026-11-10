// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One tab of a segmented switch; the name follows the language.
public sealed class TabOption(Func<string> name) : ViewModelBase
{
	public string Name => name();
}

public sealed class CurveOption(ResponseCurve curve)
{
	public ResponseCurve Curve { get; } = curve;
	public string Name => Text.CurveName(Curve);
}

// One stick's settings (written back into SettingsState.Tuning); the Test tab shows them beside the live stick.
public sealed class StickEditor : ViewModelBase
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

	public string Title => _left ? Text.TesterLeftStick() : Text.TesterRightStick();
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

	// Called when SettingsState.Tuning changed.
	public void Refresh() => OnPropertyChanged(string.Empty);

	protected override void OnLanguageChanged()
	{
		Curves = BuildCurves();
		base.OnLanguageChanged();
	}

	private static IReadOnlyList<CurveOption> BuildCurves() =>
		[new(ResponseCurve.Linear), new(ResponseCurve.Smooth), new(ResponseCurve.Aggressive)];

	private void Set(Func<StickTuning, StickTuning> change)
	{
		InputTuning tuning = _settings.Tuning;
		_settings.Tuning = _left ? tuning with { LeftStick = change(tuning.LeftStick) } : tuning with { RightStick = change(tuning.RightStick) };
	}
}

// One trigger's range (percent of travel); the controller page's Test tab shows it beside the live trigger.
public sealed class TriggerRangeEditor : ViewModelBase
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

	public void Refresh() => OnPropertyChanged(string.Empty);

	private void Set(Func<TriggerTuning, TriggerTuning> change)
	{
		InputTuning tuning = _settings.Tuning;
		_settings.Tuning = _left ? tuning with { L2 = change(tuning.L2) } : tuning with { R2 = change(tuning.R2) };
	}
}

// The stick and trigger tuning of the controller page: the editors (the Test tab shows them beside the live input),
// drift calibration, reset, the trigger effects and strength (the Test tab's output tests) and remap & gyro. The remap tab's gyro
// preview polls the virtual controller's input only while that tab is shown (SetVisible, driven by
// ControllerPageViewModel).
public sealed partial class SticksViewModel : ViewModelBase
{
	public static readonly TimeSpan CalibrationLength = TimeSpan.FromSeconds(3);
	public static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(50);

	private readonly IControllerService _service;
	private readonly bool _livePreview;
	private Timer? _previewTimer;
	private bool _previewStopped;

	public SticksViewModel(IControllerService service, SettingsState settings, bool livePreview = true)
	{
		_service = service;
		_livePreview = livePreview;
		Settings = settings;
		LeftStick = new StickEditor(settings, left: true);
		RightStick = new StickEditor(settings, left: false);
		L2 = new TriggerRangeEditor(settings, left: true);
		R2 = new TriggerRangeEditor(settings, left: false);
		Triggers = new TriggersViewModel(service, settings);
		Remap = new RemapViewModel(service, settings);
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.Tuning))
			{
				LeftStick.Refresh();
				RightStick.Refresh();
				L2.Refresh();
				R2.Refresh();
			}
		};
	}

	public SettingsState Settings { get; }
	public StickEditor LeftStick { get; }
	public StickEditor RightStick { get; }
	public TriggerRangeEditor L2 { get; }
	public TriggerRangeEditor R2 { get; }

	// Trigger effects and strength (the Test tab's output tests).
	public TriggersViewModel Triggers { get; }

	// Button remapping and gyro aiming (Remap tab).
	public RemapViewModel Remap { get; }

	public string CalibrationLabel => Text.DriftCalibration();
	public string CalibrateText => Text.CalibrateAction();
	public string ResetText => Text.ResetToDefault();
	public bool HasCalibrationText => !string.IsNullOrEmpty(CalibrationText);

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CalibrateCommand))]
	private bool _isCalibrating;

	[ObservableProperty]
	private double _calibrationProgress;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasCalibrationText))]
	private string _calibrationText = string.Empty;

	public bool IsVisible { get; private set; }

	// The remap tab was shown or left: its live preview polls the raw input only while it is shown.
	public void SetVisible(bool visible)
	{
		if (visible == IsVisible)
		{
			return;
		}
		IsVisible = visible;
		_previewTimer?.Dispose();
		_previewTimer = null;
		if (visible && _livePreview && !_previewStopped)
		{
			_previewTimer = new Timer(_ => RunOnUi(RefreshInput), null, PreviewInterval, PreviewInterval);
		}
	}

	public bool IsPreviewRunning => _previewTimer != null;

	// Stops the live preview timer for good (tests).
	public void StopPreview()
	{
		_previewStopped = true;
		_previewTimer?.Dispose();
		_previewTimer = null;
	}

	// Reads the latest raw input and moves the gyro preview (timer; tests call it directly).
	public void RefreshInput() => Remap.UpdateLive(_service.RawInput);

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
