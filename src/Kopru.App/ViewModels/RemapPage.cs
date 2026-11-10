// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

public sealed class ButtonOption(DsButton button, Func<string>? name = null)
{
	public DsButton Button { get; } = button;
	public string Name => name?.Invoke() ?? Text.ButtonName(Button);
}

public sealed class GyroModeOption(GyroMode mode)
{
	public GyroMode Mode { get; } = mode;
	public string Name => Text.GyroModeName(Mode);
}

// One physical button and the button it sends (written back into SettingsState.Remap).
public sealed class RemapRow : ViewModelBase
{
	private readonly SettingsState _settings;
	private readonly Func<IReadOnlyList<ButtonOption>> _targets;

	public RemapRow(SettingsState settings, DsButton physical, Func<IReadOnlyList<ButtonOption>> targets)
	{
		_settings = settings;
		_targets = targets;
		Physical = physical;
	}

	public DsButton Physical { get; }
	public string Name => Text.ButtonName(Physical);
	public IReadOnlyList<ButtonOption> Targets => _targets();
	public bool IsChanged => _settings.Remap.TargetOf(Physical) != Physical;

	public ButtonOption? Selected
	{
		get => Targets.FirstOrDefault(o => o.Button == _settings.Remap.TargetOf(Physical));
		set
		{
			if (value != null && value.Button != _settings.Remap.TargetOf(Physical))
			{
				_settings.Remap = _settings.Remap.With(Physical, value.Button);
			}
		}
	}

	public void Refresh() => OnPropertyChanged(string.Empty);
}

// Remap & gyro tab of the sticks page: button targets, stick swap/invert, gyro aiming with a live offset and the gyro
// calibration.
public sealed partial class RemapViewModel : ViewModelBase
{
	public static readonly TimeSpan DefaultCalibrationLength = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan CalibrationPoll = TimeSpan.FromMilliseconds(10);

	private readonly IControllerService _service;
	private readonly TimeSpan _calibrationLength;
	private IReadOnlyList<ButtonOption> _targets = BuildTargets();
	private GyroAim _preview;

	public RemapViewModel(IControllerService service, SettingsState settings, TimeSpan? calibrationLength = null)
	{
		_service = service;
		_calibrationLength = calibrationLength ?? DefaultCalibrationLength;
		Settings = settings;
		_preview = new GyroAim(settings.Gyro);
		Rows = ButtonRemap.Physical.Select(b => new RemapRow(settings, b, () => _targets)).ToList();
		Edge = new EdgeRemapPanel(service, settings, () => _targets);
		Modes = BuildModes();
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.Remap))
			{
				Edge.Refresh();
				foreach (RemapRow row in Rows)
				{
					row.Refresh();
				}
				OnPropertyChanged(string.Empty);
			}
			else if (e.PropertyName == nameof(SettingsState.Gyro))
			{
				_preview = new GyroAim(Settings.Gyro);
				OnPropertyChanged(string.Empty);
			}
		};
	}

	public SettingsState Settings { get; }
	public IReadOnlyList<RemapRow> Rows { get; }

	// DualSense Edge back paddles and presentation (shown only while an Edge is connected).
	public EdgeRemapPanel Edge { get; }
	public IReadOnlyList<GyroModeOption> Modes { get; private set; }

	public string Intro => Text.RemapIntro();
	public string RemapLabel => Text.ButtonRemapping();
	public string RemapHint => Text.ButtonRemappingHint();
	public string PhysicalLabel => Text.PhysicalButton();
	public string TargetLabel => Text.TargetButton();
	public string SticksLabel => Text.StickSwapInvert();
	public string SwapLabel => Text.SwapSticks();
	public string InvertLeftXLabel => Text.InvertLeftX();
	public string InvertLeftYLabel => Text.InvertLeftY();
	public string InvertRightXLabel => Text.InvertRightX();
	public string InvertRightYLabel => Text.InvertRightY();
	public string GyroLabel => Text.GyroAiming();
	public string GyroHint => Text.GyroAimingHint();
	public string ModeLabel => Text.GyroModeLabel();
	public string ToggleLabel => Text.GyroToggle();
	public string ToggleHint => Text.GyroToggleHint();
	public string SensitivityXLabel => Text.GyroSensitivityX();
	public string SensitivityYLabel => Text.GyroSensitivityY();
	public string InvertXLabel => Text.GyroInvertX();
	public string InvertYLabel => Text.GyroInvertY();
	public string SmoothingLabel => Text.GyroSmoothing();
	public string DeadzoneLabel => Text.GyroDeadzone();
	public string OffsetLabel => Text.GyroOffset();
	public string OffsetHint => IsLive ? Text.GyroOffsetHint() : Text.LivePreviewNeedsBridge();
	public string CalibrationLabel => Text.GyroCalibration();
	public string CalibrationHint => Text.GyroCalibrationHint();
	public string CalibrateText => Text.CalibrateAction();
	public string ResetText => Text.ResetToDefault();

	// Buttons
	public bool SwapSticks { get => Settings.Remap.SwapSticks; set => SetRemap(r => r with { SwapSticks = value }); }
	public bool InvertLeftX { get => Settings.Remap.InvertLeftX; set => SetRemap(r => r with { InvertLeftX = value }); }
	public bool InvertLeftY { get => Settings.Remap.InvertLeftY; set => SetRemap(r => r with { InvertLeftY = value }); }
	public bool InvertRightX { get => Settings.Remap.InvertRightX; set => SetRemap(r => r with { InvertRightX = value }); }
	public bool InvertRightY { get => Settings.Remap.InvertRightY; set => SetRemap(r => r with { InvertRightY = value }); }

	// Gyro
	public GyroModeOption? SelectedMode
	{
		get => Modes.FirstOrDefault(m => m.Mode == Settings.Gyro.Mode);
		set
		{
			if (value != null && value.Mode != Settings.Gyro.Mode)
			{
				SetGyro(g => g with { Mode = value.Mode });
			}
		}
	}

	public bool IsGyroOn => Settings.Gyro.Mode != GyroMode.Off;
	public bool HasActivationButton => Settings.Gyro.Mode is GyroMode.WhileL2 or GyroMode.WhileR1;
	public bool Toggle { get => Settings.Gyro.Toggle; set => SetGyro(g => g with { Toggle = value }); }
	public double SensitivityX { get => Settings.Gyro.SensitivityX; set => SetGyro(g => g with { SensitivityX = Math.Round(value, 1) }); }
	public double SensitivityY { get => Settings.Gyro.SensitivityY; set => SetGyro(g => g with { SensitivityY = Math.Round(value, 1) }); }
	public bool InvertX { get => Settings.Gyro.InvertX; set => SetGyro(g => g with { InvertX = value }); }
	public bool InvertY { get => Settings.Gyro.InvertY; set => SetGyro(g => g with { InvertY = value }); }
	public double Smoothing { get => Settings.Gyro.Smoothing; set => SetGyro(g => g with { Smoothing = value }); }
	public double Deadzone { get => Settings.Gyro.Deadzone; set => SetGyro(g => g with { Deadzone = Math.Round(value, 1) }); }
	public string SensitivityXText => Text.MultiplierValue(SensitivityX);
	public string SensitivityYText => Text.MultiplierValue(SensitivityY);
	public string SmoothingText => Text.PercentValue(Math.Round(Smoothing));
	public string DeadzoneText => Text.DegreesPerSecond(Deadzone);

	// Live gyro offset (-1..1) the gyro adds to the right stick.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(OffsetHint))]
	private bool _isLive;

	[ObservableProperty]
	private double _offsetX;

	[ObservableProperty]
	private double _offsetY;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CalibrateCommand))]
	private bool _isCalibrating;

	[ObservableProperty]
	private double _calibrationProgress;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasCalibrationText))]
	private string _calibrationText = string.Empty;

	public bool HasCalibrationText => !string.IsNullOrEmpty(CalibrationText);

	// Moves the live indicator from a raw input body (null: no input); the sticks page's preview timer calls it.
	public void UpdateLive(byte[]? raw)
	{
		bool live = raw != null && raw.Length >= GyroAim.MinBodySize;
		IsLive = live;
		if (!live)
		{
			OffsetX = OffsetY = 0;
			return;
		}
		byte[] body = (byte[])raw!.Clone();
		_preview.UpdateActivation(body);
		_preview.Apply(body);
		OffsetX = _preview.OffsetX;
		OffsetY = _preview.OffsetY;
	}

	protected override void OnLanguageChanged()
	{
		_targets = BuildTargets();
		Modes = BuildModes();
		Edge.Refresh();
		GyroTargets = BuildGyroTargets();
		foreach (RemapRow row in Rows)
		{
			row.Refresh();
		}
		base.OnLanguageChanged();
	}

	private static IReadOnlyList<ButtonOption> BuildTargets() =>
		[.. ButtonRemap.Physical.Select(b => new ButtonOption(b)), new(DsButton.None)];

	private static IReadOnlyList<GyroModeOption> BuildModes() =>
		[new(GyroMode.Off), new(GyroMode.Always), new(GyroMode.WhileL2), new(GyroMode.WhileR1)];

	private void SetRemap(Func<RemapSettings, RemapSettings> change) => Settings.Remap = change(Settings.Remap);

	private void SetGyro(Func<GyroSettings, GyroSettings> change) => Settings.Gyro = change(Settings.Gyro);

	private bool CanCalibrate() => !IsCalibrating;

	[RelayCommand(CanExecute = nameof(CanCalibrate))]
	private async Task Calibrate()
	{
		IsCalibrating = true;
		CalibrationProgress = 0;
		CalibrationText = Text.GyroCalibrationKeepStill();
		try
		{
			var calibrator = new GyroCalibrator(_calibrationLength);
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while (!calibrator.IsComplete && clock.Elapsed < _calibrationLength + TimeSpan.FromSeconds(1))
			{
				if (_service.RawInput is { } raw)
				{
					calibrator.Add(raw, clock.Elapsed);
				}
				CalibrationProgress = Math.Round(Math.Min(1, clock.Elapsed / _calibrationLength) * 100);
				await Task.Delay(CalibrationPoll);
			}
			GyroCalibrationResult result = calibrator.Finish();
			if (result.Success)
			{
				SetGyro(g => g with { BiasX = result.BiasX, BiasY = result.BiasY, BiasZ = result.BiasZ });
			}
			CalibrationProgress = 100;
			CalibrationText = Text.GyroCalibrationResultText(result.Status);
		}
		finally
		{
			IsCalibrating = false;
		}
	}

	[RelayCommand]
	private void ResetRemap() => Settings.Remap = RemapSettings.Default;

	// Keeps the calibration (it belongs to the controller, not to the preference).
	[RelayCommand]
	private void ResetGyro() => Settings.Gyro = GyroSettings.Default with
	{
		BiasX = Settings.Gyro.BiasX, BiasY = Settings.Gyro.BiasY, BiasZ = Settings.Gyro.BiasZ
	};
}
