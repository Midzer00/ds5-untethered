// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One button on the test page: lit while pressed.
public sealed partial class ButtonChip(PadButtons button, Func<string> name, bool edgeOnly = false) : ViewModelBase
{
	public PadButtons Button { get; } = button;
	public string Name => name();
	public bool IsEdgeOnly { get; } = edgeOnly;

	[ObservableProperty]
	private bool _isPressed;
}

// One stick on the test page: raw and tuned position (-1..1), the deadzones and the raw numbers.
public sealed partial class StickReadout(Func<string> title) : ViewModelBase
{
	public string Title => title();

	[ObservableProperty] private double _rawX;
	[ObservableProperty] private double _rawY;
	[ObservableProperty] private double _tunedX;
	[ObservableProperty] private double _tunedY;
	[ObservableProperty] private double _inner;
	[ObservableProperty] private double _outer;
	[ObservableProperty] private bool _isLive;
	[ObservableProperty] private string _valueText = "X 128 · Y 128";
}

// One trigger on the test page: the raw level (0..100), what games receive after the trigger range, the raw number.
public sealed partial class TriggerReadout(string name) : ViewModelBase
{
	// Button names, the same in every language.
	public string Name { get; } = name;

	[ObservableProperty] private double _level;
	[ObservableProperty] private double _gameLevel;
	[ObservableProperty] private string _valueText = "0";

	public void Set(byte raw, byte tuned)
	{
		Level = raw / 255.0 * 100;
		GameLevel = tuned / 255.0 * 100;
		ValueText = raw.ToString(CultureInfo.InvariantCulture);
	}
}

// A stick or trigger card on the Test tab: the live readout next to the page's editor for it.
public sealed class TesterStickPanel(StickReadout live, StickEditor tune)
{
	public StickReadout Live { get; } = live;
	public StickEditor Tune { get; } = tune;
}

public sealed class TesterTriggerPanel(TriggerReadout live, TriggerRangeEditor tune)
{
	public TriggerReadout Live { get; } = live;
	public TriggerRangeEditor Tune { get; } = tune;
}

// One motion axis: raw signed value and a bar from the centre (-100..100 % of full scale).
public sealed partial class AxisReadout(string name) : ViewModelBase
{
	public string Name { get; } = name;

	[ObservableProperty] private int _value;
	[ObservableProperty] private double _positive;
	[ObservableProperty] private double _negative;

	public void Set(short raw)
	{
		Value = raw;
		Positive = raw > 0 ? raw / 327.67 : 0;
		Negative = raw < 0 ? -raw / 327.68 : 0;
	}
}

// One light colour for the test.
public sealed class LightSwatch(uint rgb)
{
	public uint Rgb { get; } = rgb;
	public IBrush Brush { get; } = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}

// The controller page's Test tab: live input from the real controller (buttons, sticks, triggers, touchpad, motion,
// battery, report rate); also the Output Tests tab (vibration, trigger effects and strength, light). Input arrives on any thread (ILiveInputSource) and is applied at most
// UpdateInterval apart. The controller is only held while the tab is shown (SetVisible). Beside the live sticks and
// triggers the user fixes what they see: deadzones, curve, trigger range and drift calibration are the page's own
// editors (Tuning), the only place these settings are shown.
public sealed partial class TesterViewModel : ViewModelBase
{
	public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(16);

	private readonly IControllerService _service;
	private readonly bool _live;
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private readonly Queue<long> _arrivals = new();
	private DispatcherTimer? _timer;
	private IDisposable? _session;
	private InputSnapshot? _pending;
	private InputSnapshot? _shown;
	private long _lastArrivalMs = long.MinValue;
	private InputTransform _transform;

	public TesterViewModel(IControllerService service, SettingsState settings, bool live = true, SticksViewModel? tuning = null)
	{
		_service = service;
		_live = live;
		Settings = settings;
		Tuning = tuning ?? new SticksViewModel(service, settings, livePreview: false);
		_transform = new InputTransform(settings.Tuning);
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.Tuning))
			{
				_transform = new InputTransform(Settings.Tuning);
				RefreshDeadzones();
				if (_shown is { } s)
				{
					Show(s);
				}
			}
		};
		_service.StateChanged += (_, _) => RunOnUi(() =>
		{
			OnPropertyChanged(nameof(IsEdge));
			OnPropertyChanged(nameof(ConnectionText));
			OnPropertyChanged(nameof(BatteryText));
			OnPropertyChanged(nameof(SourceText));
			OnPropertyChanged(nameof(SourceHint));
		});
		Buttons =
		[
			new(PadButtons.Cross, () => Text.TesterCross()), new(PadButtons.Circle, () => Text.TesterCircle()),
			new(PadButtons.Square, () => Text.TesterSquare()), new(PadButtons.Triangle, () => Text.TesterTriangle()),
			new(PadButtons.DpadUp, () => Text.TesterUp()), new(PadButtons.DpadDown, () => Text.TesterDown()),
			new(PadButtons.DpadLeft, () => Text.TesterLeft()), new(PadButtons.DpadRight, () => Text.TesterRight()),
			new(PadButtons.L1, () => "L1"), new(PadButtons.R1, () => "R1"),
			new(PadButtons.L2, () => "L2"), new(PadButtons.R2, () => "R2"),
			new(PadButtons.L3, () => "L3"), new(PadButtons.R3, () => "R3"),
			new(PadButtons.Create, () => Text.TesterCreate()), new(PadButtons.Options, () => Text.TesterOptions()),
			new(PadButtons.PS, () => "PS"), new(PadButtons.Touchpad, () => Text.TesterTouchClick()),
			new(PadButtons.Mute, () => Text.TesterMute()),
			new(PadButtons.LeftFunction, () => Text.TesterLeftFn(), true), new(PadButtons.RightFunction, () => Text.TesterRightFn(), true),
			new(PadButtons.LeftPaddle, () => Text.TesterLeftPaddle(), true), new(PadButtons.RightPaddle, () => Text.TesterRightPaddle(), true),
		];
		LeftStick = new StickReadout(() => Text.TesterLeftStick());
		RightStick = new StickReadout(() => Text.TesterRightStick());
		L2 = new TriggerReadout("L2");
		R2 = new TriggerReadout("R2");
		LeftPanel = new TesterStickPanel(LeftStick, Tuning.LeftStick);
		RightPanel = new TesterStickPanel(RightStick, Tuning.RightStick);
		L2Panel = new TesterTriggerPanel(L2, Tuning.L2);
		R2Panel = new TesterTriggerPanel(R2, Tuning.R2);
		Gyro = [new("X"), new("Y"), new("Z")];
		Accel = [new("X"), new("Y"), new("Z")];
		Swatches = [new(0xFF3B30), new(0x34C759), new(0x2F6BFF), new(0xFFCC00), new(0xAF52DE), new(0xFFFFFF)];
		RefreshDeadzones();
	}

	public SettingsState Settings { get; }
	// The page's stick and trigger tuning: its editors and calibration command are used here as they are.
	public SticksViewModel Tuning { get; }
	// The trigger effects to feel and the trigger strength, in the output tests.
	public TriggersViewModel Triggers => Tuning.Triggers;
	public bool IsEdge => _service.Kind == ControllerKind.DualSenseEdge;

	public IReadOnlyList<ButtonChip> Buttons { get; }
	public StickReadout LeftStick { get; }
	public StickReadout RightStick { get; }
	public TriggerReadout L2 { get; }
	public TriggerReadout R2 { get; }
	public TesterStickPanel LeftPanel { get; }
	public TesterStickPanel RightPanel { get; }
	public TesterTriggerPanel L2Panel { get; }
	public TesterTriggerPanel R2Panel { get; }
	public IReadOnlyList<AxisReadout> Gyro { get; }
	public IReadOnlyList<AxisReadout> Accel { get; }
	public IReadOnlyList<LightSwatch> Swatches { get; }

	// Labels.
	public string StickHint => Text.TesterStickHint();
	public string CalibrationHint => Text.TesterCalibrationHint();
	public string MotionLabel => Text.TesterMotion();
	public string GyroLabel => Text.TesterGyro();
	public string AccelLabel => Text.TesterAccel();
	public string BatteryLabel => Text.TesterBattery();
	public string ConnectionLabel => Text.TesterConnection();
	public string RateLabel => Text.TesterReportRate();
	public string SourceLabel => Text.TesterSource();
	public string OutputsLabel => Text.TesterOutputs();
	public string OutputsHint => Text.TesterOutputsHint();
	public string HapticsLabel => Text.TesterHaptics();
	public string LeftText => Text.TesterLeft();
	public string RightText => Text.TesterRight();
	public string LightLabel => Text.TesterLightColour();
	public string NoInputHint => Text.TesterNoInputHint();
	public string MotionNeedsFull => Text.TesterMotionNeedsFull();

	// Live state.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNoInput), nameof(SourceText), nameof(SourceHint), nameof(BatteryText))]
	private bool _hasInput;

	public bool HasNoInput => !HasInput;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsMotionNote))]
	private bool _isFull;

	public bool ShowsMotionNote => HasInput && !IsFull;

	[ObservableProperty] private PadButtons _pressed;
	[ObservableProperty] private TouchPoint _touch1;
	[ObservableProperty] private TouchPoint _touch2;
	[ObservableProperty] private string _touch1Text = "—";
	[ObservableProperty] private string _touch2Text = "—";
	[ObservableProperty] private double _rateHz;
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(BatteryText))]
	private BatteryStatus? _battery;

	public string RateText => HasInput ? string.Create(Localizer.Culture(Localizer.Current), $"{RateHz:F0} Hz") : "—";

	partial void OnRateHzChanged(double value) => OnPropertyChanged(nameof(RateText));

	public string BatteryText => Battery is { } b
		? b.State == ChargeState.Charging ? Text.BatteryValue(b.Percent) + " · " + Text.Charging() : Text.BatteryValue(b.Percent)
		: _service.HasBattery
			? _service.IsCharging ? Text.BatteryValue(Math.Round(_service.BatteryPercent)) + " · " + Text.Charging()
				: Text.BatteryValue(Math.Round(_service.BatteryPercent))
			: Text.BatteryUnknown();

	public string ConnectionText => _service.IsConnected || HasInput ? "Bluetooth" : Text.DeviceNotConnected();
	public string SourceText => !HasInput ? Text.TesterNoInput() : _service.IsBridgeRunning ? Text.TesterSourceBridge() : Text.TesterSourceDirect();
	public string SourceHint => !HasInput ? Text.TesterNoInputHint() : _service.IsBridgeRunning ? StickHint : Text.TesterDirectHint();

	public bool IsVisible { get; private set; }

	// The page was shown or left: opens or releases the controller.
	public void SetVisible(bool visible)
	{
		if (visible == IsVisible)
		{
			return;
		}
		IsVisible = visible;
		if (visible)
		{
			_session = (_service as ILiveInputSource)?.OpenLiveInput(Receive);
			if (_live)
			{
				_timer = new DispatcherTimer(UpdateInterval, DispatcherPriority.Background, (_, _) => Tick());
				_timer.Start();
			}
		}
		else
		{
			_session?.Dispose();
			_session = null;
			_timer?.Stop();
			_timer = null;
			Volatile.Write(ref _pending, null);
			lock (_arrivals)
			{
				_arrivals.Clear();
			}
			_shown = null;
			HasInput = false;
			RateHz = 0;
		}
	}

	// Any thread: keeps the latest report for the next Tick.
	public void Receive(InputSnapshot snapshot)
	{
		long now = _clock.ElapsedMilliseconds;
		lock (_arrivals)
		{
			_arrivals.Enqueue(now);
			_lastArrivalMs = now;
		}
		Volatile.Write(ref _pending, snapshot);
	}

	// UI thread (timer; tests call it directly): shows the latest report and the report rate.
	public void Tick()
	{
		long now = _clock.ElapsedMilliseconds;
		bool stale;
		lock (_arrivals)
		{
			while (_arrivals.Count > 0 && now - _arrivals.Peek() > 1000)
			{
				_arrivals.Dequeue();
			}
			RateHz = _arrivals.Count;
			stale = _lastArrivalMs == long.MinValue || now - _lastArrivalMs > 1000;
		}
		InputSnapshot? next = Interlocked.Exchange(ref _pending, null);
		if (next != null)
		{
			Show(next);
		}
		else if (stale && HasInput)
		{
			_shown = null;
			HasInput = false;
			SetLive(false);
		}
	}

	private void Show(InputSnapshot s)
	{
		_shown = s;
		RefreshDeadzones();
		byte[] tuned = s.ToTransformBody();
		_transform.Apply(tuned);
		Set(LeftStick, s.LeftX, s.LeftY, tuned[InputTransform.LeftX], tuned[InputTransform.LeftY]);
		Set(RightStick, s.RightX, s.RightY, tuned[InputTransform.RightX], tuned[InputTransform.RightY]);
		Pressed = s.Buttons;
		foreach (ButtonChip chip in Buttons)
		{
			chip.IsPressed = s.IsPressed(chip.Button);
		}
		L2.Set(s.L2, tuned[InputTransform.L2Analog]);
		R2.Set(s.R2, tuned[InputTransform.R2Analog]);
		Touch1 = s.Touch1;
		Touch2 = s.Touch2;
		Touch1Text = TouchText(s.Touch1);
		Touch2Text = TouchText(s.Touch2);
		Gyro[0].Set(s.GyroX);
		Gyro[1].Set(s.GyroY);
		Gyro[2].Set(s.GyroZ);
		Accel[0].Set(s.AccelX);
		Accel[1].Set(s.AccelY);
		Accel[2].Set(s.AccelZ);
		if (s.Battery is { } b)
		{
			Battery = b;
		}
		IsFull = s.IsFull;
		if (!HasInput)
		{
			HasInput = true;
			SetLive(true);
			OnPropertyChanged(nameof(ConnectionText));
			OnPropertyChanged(nameof(RateText));
			OnPropertyChanged(nameof(ShowsMotionNote));
		}
	}

	private static string TouchText(TouchPoint t) => t.Active
		? string.Create(CultureInfo.InvariantCulture, $"X {t.X} · Y {t.Y}")
		: Text.TesterNoTouch();

	private static void Set(StickReadout stick, byte rawX, byte rawY, byte tunedX, byte tunedY)
	{
		stick.RawX = Axis(rawX);
		stick.RawY = Axis(rawY);
		stick.TunedX = Axis(tunedX);
		stick.TunedY = Axis(tunedY);
		stick.ValueText = string.Create(CultureInfo.InvariantCulture, $"X {rawX} · Y {rawY}");
	}

	private void SetLive(bool live) => LeftStick.IsLive = RightStick.IsLive = live;

	private void RefreshDeadzones()
	{
		LeftStick.Inner = Settings.Tuning.LeftStick.InnerDeadzone;
		LeftStick.Outer = Settings.Tuning.LeftStick.OuterDeadzone;
		RightStick.Inner = Settings.Tuning.RightStick.InnerDeadzone;
		RightStick.Outer = Settings.Tuning.RightStick.OuterDeadzone;
	}

	private static double Axis(byte v) => Math.Clamp((v - 127.5) / 127.5, -1, 1);

	protected override void OnLanguageChanged()
	{
		if (_shown is { } s)
		{
			Touch1Text = TouchText(s.Touch1);
			Touch2Text = TouchText(s.Touch2);
		}
		base.OnLanguageChanged();
	}

	[RelayCommand]
	private void TestHaptics(HapticSide side) => _service.TestHaptics(side);

	[RelayCommand]
	private void TestLight(LightSwatch swatch) => _service.TestLight(swatch.Rgb);
}
