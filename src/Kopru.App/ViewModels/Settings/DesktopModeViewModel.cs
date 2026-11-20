// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

public sealed class MouseActionOption(MouseAction action) : LabelledChoice(() => Text.DesktopActionName(action))
{
	public MouseAction Action { get; } = action;
}

// One remappable button of mouse mode and the action it does (written back into SettingsState.DesktopMode).
public sealed class DesktopButtonRow : ViewModelBase
{
	private readonly DesktopModeViewModel _panel;

	public DesktopButtonRow(DesktopModeViewModel panel, DsButton button)
	{
		_panel = panel;
		Button = button;
	}

	public DsButton Button { get; }
	public string Name => Text.ButtonName(Button);
	public IReadOnlyList<MouseActionOption> Actions => _panel.Actions;

	public MouseActionOption? Selected
	{
		get => Actions.FirstOrDefault(o => o.Action == _panel.Settings.DesktopMode.Buttons.ActionOf(Button));
		set
		{
			if (value != null)
			{
				_panel.SetAction(Button, value.Action);
			}
		}
	}

	public void Refresh() => OnPropertyChanged(string.Empty);
}

// The mouse mode tab (Settings): the switch, auto-off, the pointer stick, the speed sliders and the button mapping.
public sealed partial class DesktopModeViewModel : ViewModelBase
{
	private readonly IReadOnlyList<MouseActionOption> _actions = BuildActions();

	public DesktopModeViewModel(SettingsState settings)
	{
		Settings = settings;
		Rows = DesktopButtonMap.Buttons.Select(b => new DesktopButtonRow(this, b)).ToList();
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.DesktopMode))
			{
				OnPropertyChanged(string.Empty);
				foreach (DesktopButtonRow row in Rows)
				{
					row.Refresh();
				}
			}
			else if (e.PropertyName == nameof(SettingsState.DesktopModeOn))
			{
				OnPropertyChanged(nameof(DesktopModeOn));
			}
		};
	}

	public SettingsState Settings { get; }

	// The Mouse Mode switch on this tab (logged as "settings switch").
	public bool DesktopModeOn
	{
		get => Settings.DesktopModeOn;
		set => Settings.SetDesktopMode(value, "settings switch");
	}

	public string Title => Text.DesktopModeSection();
	public string SwitchLabel => Text.DesktopModeSwitch();
	public string Hint => Text.DesktopModeHint();
	public string Short => Text.DesktopModeShort();
	public string AutoOffLabel => Text.DesktopAutoOff();
	public string AutoOffHint => Text.DesktopAutoOffHint();
	public string AutoOffShort => Text.DesktopAutoOffShort();
	public string PointerStickLabel => Text.DesktopPointerStick();
	public string PointerStickHint => Text.DesktopPointerStickHint();
	public string PointerSpeedLabel => Text.DesktopPointerSpeed();
	public string PointerAccelerationLabel => Text.DesktopPointerAcceleration();
	public string TouchpadSpeedLabel => Text.DesktopTouchpadSpeed();
	public string ScrollSpeedLabel => Text.DesktopScrollSpeed();
	public string MappingTitle => Text.DesktopButtonsTitle();
	public string MappingNote => Text.DesktopButtonsNote();
	public string MappingHint => Text.DesktopTouchpadHint(Settings.DesktopMode.RightClick);
	public string RightClickLabel => Text.DesktopRightClick();
	public string RightClickHint => Text.DesktopRightClickHint();
	public string ResetText => Text.ResetToDefault();

	public bool AutoOffInGame { get => Settings.DesktopMode.AutoOffInGame; set => Set(d => d with { AutoOffInGame = value }); }
	public double PointerSpeed { get => Settings.DesktopMode.PointerSpeed; set => Set(d => d with { PointerSpeed = Math.Round(value, 1) }); }
	public double PointerAcceleration { get => Settings.DesktopMode.PointerAcceleration; set => Set(d => d with { PointerAcceleration = Math.Round(value) }); }
	public double TouchpadSpeed { get => Settings.DesktopMode.TouchpadSpeed; set => Set(d => d with { TouchpadSpeed = Math.Round(value, 1) }); }
	public double ScrollSpeed { get => Settings.DesktopMode.ScrollSpeed; set => Set(d => d with { ScrollSpeed = Math.Round(value, 1) }); }

	public string PointerSpeedText => Text.MultiplierValue(PointerSpeed);
	public string PointerAccelerationText => Text.PercentValue(PointerAcceleration);
	public string TouchpadSpeedText => Text.MultiplierValue(TouchpadSpeed);
	public string ScrollSpeedText => Text.MultiplierValue(ScrollSpeed);

	// 0 = left stick, 1 = right stick (the segmented choice's order).
	public IReadOnlyList<LabelledChoice> PointerSticks { get; } = [new(() => Text.DesktopStickLeft()), new(() => Text.DesktopStickRight())];

	public int PointerStickIndex
	{
		get => Settings.DesktopMode.PointerStick == PointerStick.Left ? 0 : 1;
		set
		{
			if (value is 0 or 1)
			{
				Set(d => d with { PointerStick = value == 0 ? PointerStick.Left : PointerStick.Right });
			}
		}
	}

	// Which touchpad press right clicks, in the segmented choice's order (index = TouchpadRightClick: Off, Right Side,
	// Two Fingers); one list for good, so a language switch keeps the choice.
	public IReadOnlyList<LabelledChoice> RightClicks { get; } = Enum.GetValues<TouchpadRightClick>()
		.Select(m => new LabelledChoice(() => Text.DesktopRightClickOption(m))).ToList();

	public int RightClickIndex
	{
		get => (int)Settings.DesktopMode.RightClick;
		set
		{
			if (Enum.IsDefined((TouchpadRightClick)value))
			{
				Set(d => d with { RightClick = (TouchpadRightClick)value });
			}
		}
	}

	// The remappable buttons, in display order, and the actions their pickers offer.
	public IReadOnlyList<DesktopButtonRow> Rows { get; }
	public IReadOnlyList<MouseActionOption> Actions => _actions;
	public bool IsMappingDefault => Settings.DesktopMode.Buttons.IsDefault;

	internal void SetAction(DsButton button, MouseAction action) => Set(d => d with { Buttons = d.Buttons.With(button, action) });

	[RelayCommand]
	private void ResetMapping() => Set(d => d with { Buttons = DesktopButtonMap.Default });

	private static IReadOnlyList<MouseActionOption> BuildActions() => DesktopButtonMap.Actions.Select(a => new MouseActionOption(a)).ToList();

	private void Set(Func<DesktopModeSettings, DesktopModeSettings> change)
	{
		DesktopModeSettings next = change(Settings.DesktopMode);
		if (next != Settings.DesktopMode)
		{
			Settings.DesktopMode = next;
		}
	}
}
