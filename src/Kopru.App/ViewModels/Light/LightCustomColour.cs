// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The seventh swatch: the user's own colour, filled with it once there is one (an empty ring with a plus before that).
public sealed class CustomColourSwatch(Func<string> name) : ColourPreset(0, name)
{
	private uint? _rgb;
	private IBrush? _brush;

	public override uint Rgb => _rgb ?? 0;
	public override IBrush? Brush => _brush;
	public override bool IsCustom => true;
	public override bool IsEmpty => _rgb == null;

	public void Show(uint? rgb)
	{
		if (rgb == _rgb)
		{
			return;
		}
		_rgb = rgb;
		_brush = rgb is uint c ? BrushOf(c) : null;
		OnPropertyChanged(nameof(Rgb));
		OnPropertyChanged(nameof(Brush));
		OnPropertyChanged(nameof(IsEmpty));
		OnPropertyChanged(nameof(HasColour));
	}
}

// The Light page's custom colour: Custom after the six presets. While it is chosen its hex code stands where a preset's name
// does, and a click on the swatch opens the colour disc (hue around it, saturation from the centre: the colour at full
// value) with the hex code under it (#RRGGBB; anything else goes back to the colour when the box loses focus). The colour
// applied stays Settings.LightbarColour (per profile); the custom colour is remembered in Settings.LightbarCustomColour
// (one for the app), so choosing a preset and Custom again brings it back. A colour that is no preset (a saved one, another
// profile's) shows as Custom. While the disc is dragged, the light follows at most LiveInterval apart (the last value
// always arrives).
public sealed partial class LightViewModel
{
	public static readonly TimeSpan LiveInterval = TimeSpan.FromMilliseconds(33);

	private readonly Stopwatch _sinceApplied = Stopwatch.StartNew();
	private bool _appliedOnce;
	private uint? _pendingCustom;
	private IDisposable? _pendingTimer;
	private bool _syncing;
	private double _hue;
	private double _saturation = LightColour.MaxSaturation;
	private string _hexText = string.Empty;

	public CustomColourSwatch Custom { get; }

	public bool IsCustomSelected => SelectedPreset.IsCustom;
	public bool IsPresetSelected => !SelectedPreset.IsCustom;

	public string WheelLabel => Text.ColourWheelLabel();
	public string HexLabel => Text.CustomColourHex();

	// The custom colour's hex code (applied, not what the box holds while it is edited) or the preset's name.
	public string SelectedName => SelectedPreset.IsCustom ? LightColour.ToHex(CurrentCustom) : SelectedPreset.Name;

	// 0-359 degrees around the disc, whole degrees (0 at the top, clockwise).
	public double CustomHue
	{
		get => _hue;
		set
		{
			double v = double.IsFinite(value) ? (Math.Round(value) % 360 + 360) % 360 : 0;
			if (v == _hue)
			{
				return;
			}
			_hue = v;
			OnPropertyChanged();
			FromSliders();
		}
	}

	// 0-100 % (0: white, the disc's centre), whole percent.
	public double CustomSaturation
	{
		get => _saturation;
		set
		{
			double v = Math.Clamp(double.IsFinite(value) ? Math.Round(value) : 0, 0, LightColour.MaxSaturation);
			if (v == _saturation)
			{
				return;
			}
			_saturation = v;
			OnPropertyChanged();
			FromSliders();
		}
	}
	// What the hex box holds while it is edited; CommitHex applies it (or puts the colour back).
	public string CustomHex
	{
		get => _hexText;
		set => _hexText = value ?? string.Empty;
	}

	// The hex box lost focus or Enter was pressed: a valid code becomes the custom colour, anything else goes back.
	[RelayCommand]
	public void CommitHex()
	{
		// A colour the throttle still holds goes first, so the box compares with what is on.
		FlushCustomColour();
		if (LightColour.TryParseHex(_hexText, out uint rgb) && rgb != CurrentCustom)
		{
			SetSliders(rgb);
			Custom.Show(rgb);
			ApplyCustom(rgb, now: true);
		}
		ShowHex(CurrentCustom);
	}

	// Applies a held slider value now (the throttle's trailing edge; tests call it directly).
	public void FlushCustomColour()
	{
		_pendingTimer?.Dispose();
		_pendingTimer = null;
		if (_pendingCustom is uint rgb)
		{
			_pendingCustom = null;
			ApplyNow(rgb);
		}
	}

	// A colour waiting for the throttle (tests).
	public bool HasPendingCustomColour => _pendingCustom != null;

	private uint CurrentCustom => Settings.LightbarCustomColour ?? Settings.LightbarColour;

	private void InitCustomColour()
	{
		Custom.Show(Settings.LightbarCustomColour);
		SyncSelection();
		Localizer.LanguageChanged += _ => Dispatcher.UIThread.Post(() =>
		{
			OnPropertyChanged(nameof(WheelLabel));
			OnPropertyChanged(nameof(HexLabel));
			OnPropertyChanged(nameof(SelectedName));
		});
		Settings.PropertyChanged += OnLightSettingChanged;
	}

	private void OnLightSettingChanged(object? sender, PropertyChangedEventArgs e)
	{
		// A colour set elsewhere (a profile switch): the swatch that shows it.
		if (e.PropertyName == nameof(SettingsState.LightbarColour) && !_syncing && _pendingCustom == null)
		{
			SyncSelection();
		}
	}

	// The swatch for the applied colour: its preset, else Custom holding it.
	private void SyncSelection()
	{
		uint colour = Settings.LightbarColour;
		ColourPreset? preset = Presets.FirstOrDefault(p => p.Rgb == colour);
		_syncing = true;
		try
		{
			if (preset == null && Settings.LightbarCustomColour != colour)
			{
				Settings.LightbarCustomColour = colour;
			}
			Custom.Show(Settings.LightbarCustomColour);
			if (preset == null || (SelectedPreset.IsCustom && Settings.LightbarCustomColour == colour))
			{
				// A custom colour equal to a preset stays Custom while Custom is chosen.
				SelectedPreset = Custom;
			}
			else
			{
				SelectedPreset = preset;
			}
		}
		finally
		{
			_syncing = false;
		}
		SetSliders(CurrentCustom);
		ShowHex(CurrentCustom);
	}

	private void OnSwatchChosen(ColourPreset value)
	{
		if (_syncing)
		{
			return;
		}
		_pendingCustom = null;
		_pendingTimer?.Dispose();
		_pendingTimer = null;
		if (value.IsCustom)
		{
			// The first time: the colour on now, to start from.
			uint rgb = CurrentCustom;
			Custom.Show(rgb);
			SetSliders(rgb);
			ShowHex(rgb);
			ApplyNow(rgb);
			return;
		}
		ApplyColour(value.Rgb);
	}

	// The sliders moved: the custom colour at full value, shown at once, applied through the throttle.
	private void FromSliders()
	{
		if (_syncing || !IsCustomSelected)
		{
			return;
		}
		uint rgb = LightColour.FromHueSaturation(_hue, _saturation);
		Custom.Show(rgb);
		ShowHex(rgb);
		ApplyCustom(rgb, now: false);
	}

	private void ApplyCustom(uint rgb, bool now)
	{
		if (now || !_appliedOnce || _sinceApplied.Elapsed >= LiveInterval)
		{
			_pendingCustom = null;
			_pendingTimer?.Dispose();
			_pendingTimer = null;
			ApplyNow(rgb);
			return;
		}
		_pendingCustom = rgb;
		_pendingTimer ??= DispatcherTimer.RunOnce(FlushCustomColour, LiveInterval - _sinceApplied.Elapsed);
	}

	private void ApplyNow(uint rgb)
	{
		_syncing = true;
		try
		{
			Settings.LightbarCustomColour = rgb;
		}
		finally
		{
			_syncing = false;
		}
		OnPropertyChanged(nameof(SelectedName));
		ApplyColour(rgb);
	}

	private void ApplyColour(uint rgb)
	{
		_appliedOnce = true;
		_sinceApplied.Restart();
		_syncing = true;
		try
		{
			Settings.LightbarColour = rgb;
		}
		finally
		{
			_syncing = false;
		}
		_service.TestLight(rgb);
	}

	// The sliders for a colour (its hue and saturation; the value is left out), without applying anything.
	private void SetSliders(uint rgb)
	{
		(double hue, double saturation) = LightColour.ToHueSaturation(rgb);
		double h = Math.Round(hue) % 360;
		bool grey = saturation <= 0;
		bool wasSyncing = _syncing;
		_syncing = true;
		try
		{
			if (!grey && h != _hue)
			{
				// A grey keeps the hue it had, so turning saturation up again goes back to it.
				_hue = h;
				OnPropertyChanged(nameof(CustomHue));
			}
			double s = Math.Round(saturation);
			if (s != _saturation)
			{
				_saturation = s;
				OnPropertyChanged(nameof(CustomSaturation));
				}
		}
		finally
		{
			_syncing = wasSyncing;
		}
	}

	private void ShowHex(uint rgb)
	{
		_hexText = LightColour.ToHex(rgb);
		OnPropertyChanged(nameof(CustomHex));
		OnPropertyChanged(nameof(SelectedName));
	}
}
