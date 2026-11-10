// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The desktop mode group (Settings): the switch, auto-off, the speed sliders and the controls legend.
public sealed class DesktopModeViewModel : ViewModelBase
{
	public DesktopModeViewModel(SettingsState settings)
	{
		Settings = settings;
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.DesktopMode))
			{
				OnPropertyChanged(string.Empty);
			}
		};
	}

	public SettingsState Settings { get; }

	public string Title => Text.DesktopModeSection();
	public string SwitchLabel => Text.DesktopModeSwitch();
	public string Hint => Text.DesktopModeHint();
	public string AutoOffLabel => Text.DesktopAutoOff();
	public string AutoOffHint => Text.DesktopAutoOffHint();
	public string PointerSpeedLabel => Text.DesktopPointerSpeed();
	public string PointerAccelerationLabel => Text.DesktopPointerAcceleration();
	public string TouchpadSpeedLabel => Text.DesktopTouchpadSpeed();
	public string ScrollSpeedLabel => Text.DesktopScrollSpeed();
	public string MappingTitle => Text.DesktopMappingTitle();

	public bool AutoOffInGame { get => Settings.DesktopMode.AutoOffInGame; set => Set(d => d with { AutoOffInGame = value }); }
	public double PointerSpeed { get => Settings.DesktopMode.PointerSpeed; set => Set(d => d with { PointerSpeed = Math.Round(value, 1) }); }
	public double PointerAcceleration { get => Settings.DesktopMode.PointerAcceleration; set => Set(d => d with { PointerAcceleration = Math.Round(value) }); }
	public double TouchpadSpeed { get => Settings.DesktopMode.TouchpadSpeed; set => Set(d => d with { TouchpadSpeed = Math.Round(value, 1) }); }
	public double ScrollSpeed { get => Settings.DesktopMode.ScrollSpeed; set => Set(d => d with { ScrollSpeed = Math.Round(value, 1) }); }

	public string PointerSpeedText => Text.MultiplierValue(PointerSpeed);
	public string PointerAccelerationText => Text.PercentValue(PointerAcceleration);
	public string TouchpadSpeedText => Text.MultiplierValue(TouchpadSpeed);
	public string ScrollSpeedText => Text.MultiplierValue(ScrollSpeed);

	public IReadOnlyList<ShortcutLegendItem> Mapping => Text.DesktopMapping().Select(m => new ShortcutLegendItem(m.Keys, m.Does)).ToList();

	private void Set(Func<DesktopModeSettings, DesktopModeSettings> change)
	{
		DesktopModeSettings next = change(Settings.DesktopMode);
		if (next != Settings.DesktopMode)
		{
			Settings.DesktopMode = next;
		}
	}
}
