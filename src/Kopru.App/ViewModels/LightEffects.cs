// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One option of a segmented picker (the views bind Name on the base type).
public class LabelledChoice(Func<string> name)
{
	public string Name => name();
}

public sealed class LightChoice<T>(T value, Func<string> name) : LabelledChoice(name)
{
	public T Value { get; } = value;
}

// Light page: effect mode and its settings, player LEDs, mute LED and the live preview (the light bar animates on the
// controller drawing; the view ticks AdvancePreview about 30 times a second while it is shown and the mode moves).
public sealed partial class LightViewModel
{
	private readonly Stopwatch _previewClock = Stopwatch.StartNew();

	public IReadOnlyList<LightChoice<LightMode>> Modes { get; private set; } = [];
	public IReadOnlyList<LightChoice<PlayerLedPattern>> Patterns { get; private set; } = [];
	public IReadOnlyList<LightChoice<LedBrightness>> LedLevels { get; private set; } = [];
	public IReadOnlyList<LightChoice<MuteLedMode>> MuteChoices { get; private set; } = [];

	public string ModeLabel => Text.LightModeLabel();
	public string ModeHint => Settings.LightMode switch
	{
		LightMode.Breathing => Text.LightModeBreathingHint(),
		LightMode.Rainbow => Text.LightModeRainbowHint(),
		LightMode.Battery => Text.LightModeBatteryHint(),
		LightMode.Game => Text.LightModeGameHint(),
		_ => Text.LightModeStaticHint()
	};
	public string BreathingPeriodLabel => Text.BreathingPeriodLabel();
	public string BreathingMinLabel => Text.BreathingMinLabel();
	public string BreathingMaxLabel => Text.BreathingMaxLabel();
	public string RainbowSpeedLabel => Text.RainbowSpeedLabel();
	public string BreathingPeriodText => Text.SecondsValue(Settings.BreathingPeriod);
	public string BreathingMinText => Text.PercentValue(Math.Round(Settings.BreathingMin));
	public string BreathingMaxText => Text.PercentValue(Math.Round(Settings.BreathingMax));
	public string RainbowSpeedText => Text.SpeedValue(Settings.RainbowSpeed);
	public string PatternHint => Text.PlayerLedsPatternHint();
	public string LedBrightnessLabel => Text.LedBrightnessLabel();
	public string MuteLedLabel => Text.MuteLedLabel();
	public string MuteLedHint => Text.MuteLedHint();

	// Which settings show: the colour for static, breathing and game controlled; battery and rainbow pick their own.
	public bool ShowsColour => Settings.LightMode is LightMode.Static or LightMode.Breathing or LightMode.Game;
	public bool ShowsBreathing => Settings.LightMode == LightMode.Breathing;
	public bool ShowsRainbow => Settings.LightMode == LightMode.Rainbow;

	public LightChoice<LightMode>? SelectedMode
	{
		get => Modes.FirstOrDefault(m => m.Value == Settings.LightMode);
		set
		{
			if (value != null)
			{
				Settings.LightMode = value.Value;
			}
		}
	}

	public LightChoice<PlayerLedPattern>? SelectedPattern
	{
		get => Patterns.FirstOrDefault(m => m.Value == Settings.PlayerLedPattern);
		set
		{
			if (value != null)
			{
				Settings.PlayerLedPattern = value.Value;
			}
		}
	}

	public LightChoice<LedBrightness>? SelectedLedLevel
	{
		get => LedLevels.FirstOrDefault(m => m.Value == Settings.PlayerLedBrightness);
		set
		{
			if (value != null)
			{
				Settings.PlayerLedBrightness = value.Value;
			}
		}
	}

	public LightChoice<MuteLedMode>? SelectedMute
	{
		get => MuteChoices.FirstOrDefault(m => m.Value == Settings.MuteLed);
		set
		{
			if (value != null)
			{
				Settings.MuteLed = value.Value;
			}
		}
	}

	// Live preview.
	[ObservableProperty]
	private Color _previewColor;

	public int PreviewPlayerMask => LightAnimator.PatternMask(Settings.PlayerLedPattern);

	public bool IsPreviewAnimated => LightAnimator.IsAnimated(Settings.ToSettings().Light, PreviewBattery);

	// The light bar's colour in the preview at time t (brightness shows through LightIntensity).
	public void UpdatePreview(TimeSpan t)
	{
		LightSettings light = Settings.ToSettings().Light with { Brightness = 1 };
		Rgb c = LightAnimator.ColourAt(light, t, PreviewBattery);
		PreviewColor = Color.FromRgb(c.R, c.G, c.B);
	}

	public void AdvancePreview()
	{
		if (IsPreviewAnimated)
		{
			UpdatePreview(_previewClock.Elapsed);
		}
	}

	private BatteryStatus? PreviewBattery => _service.HasBattery
		? new BatteryStatus((int)Math.Round(_service.BatteryPercent), _service.IsCharging ? ChargeState.Charging : ChargeState.Discharging)
		: null;

	private void InitEffects()
	{
		BuildChoices();
		UpdatePreview(TimeSpan.Zero);
		Settings.PropertyChanged += OnSettingsChanged;
	}

	private void BuildChoices()
	{
		Modes =
		[
			new(LightMode.Static, () => Text.LightModeStatic()),
			new(LightMode.Breathing, () => Text.LightModeBreathing()),
			new(LightMode.Rainbow, () => Text.LightModeRainbow()),
			new(LightMode.Battery, () => Text.LightModeBattery()),
			new(LightMode.Game, () => Text.LightModeGame()),
		];
		Patterns =
		[
			new(PlayerLedPattern.Off, () => Text.PlayerLedsOff()),
			new(PlayerLedPattern.One, () => "1"),
			new(PlayerLedPattern.Two, () => "2"),
			new(PlayerLedPattern.Three, () => "3"),
			new(PlayerLedPattern.Four, () => "4"),
			new(PlayerLedPattern.All, () => Text.PlayerLedsAll()),
		];
		LedLevels =
		[
			new(LedBrightness.Low, () => Text.LedBrightnessLow()),
			new(LedBrightness.Medium, () => Text.LedBrightnessMedium()),
			new(LedBrightness.High, () => Text.LedBrightnessHigh()),
		];
		MuteChoices =
		[
			new(MuteLedMode.Off, () => Text.MuteLedOff()),
			new(MuteLedMode.On, () => Text.MuteLedOn()),
			new(MuteLedMode.FollowMic, () => Text.MuteLedFollowMic()),
		];
	}

	private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(SettingsState.LightMode):
				OnPropertyChanged(nameof(SelectedMode));
				OnPropertyChanged(nameof(ModeHint));
				OnPropertyChanged(nameof(ShowsColour));
				OnPropertyChanged(nameof(ShowsBreathing));
				OnPropertyChanged(nameof(ShowsRainbow));
				break;
			case nameof(SettingsState.PlayerLedPattern):
				OnPropertyChanged(nameof(SelectedPattern));
				OnPropertyChanged(nameof(PreviewPlayerMask));
				break;
			case nameof(SettingsState.PlayerLedBrightness):
				OnPropertyChanged(nameof(SelectedLedLevel));
				break;
			case nameof(SettingsState.MuteLed):
				OnPropertyChanged(nameof(SelectedMute));
				break;
			case nameof(SettingsState.BreathingPeriod):
				OnPropertyChanged(nameof(BreathingPeriodText));
				break;
			case nameof(SettingsState.BreathingMin):
				OnPropertyChanged(nameof(BreathingMinText));
				break;
			case nameof(SettingsState.BreathingMax):
				OnPropertyChanged(nameof(BreathingMaxText));
				break;
			case nameof(SettingsState.RainbowSpeed):
				OnPropertyChanged(nameof(RainbowSpeedText));
				break;
		}
		OnPropertyChanged(nameof(IsPreviewAnimated));
		UpdatePreview(_previewClock.Elapsed);
	}

	// New lists so the pickers re-read the names in the new language.
	private void RefreshChoices()
	{
		BuildChoices();
		OnPropertyChanged(nameof(Modes));
		OnPropertyChanged(nameof(Patterns));
		OnPropertyChanged(nameof(LedLevels));
		OnPropertyChanged(nameof(MuteChoices));
	}
}
