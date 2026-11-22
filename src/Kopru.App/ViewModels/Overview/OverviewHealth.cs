// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Health;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One chip in the Overview health strip, shown from a Core HealthChip: name, a short state, the reason in the tooltip and,
// when the model has a fix, a "Fix" button that runs it.
public sealed partial class HealthChipViewModel(HealthChipKind kind, Func<FixAction, Task> runFix) : ViewModelBase
{
	public HealthChipKind Kind { get; } = kind;

	[ObservableProperty]
	private string _name = " ";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsGood), nameof(IsAttention), nameof(IsProblem))]
	private HealthLevel _level = HealthLevel.Unknown;

	[ObservableProperty]
	private string _statusText = " ";

	[ObservableProperty]
	private string _reason = " ";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasAction), nameof(ActionText), nameof(ActionTip))]
	private FixAction _fix;

	public bool IsGood => Level == HealthLevel.Ok;
	public bool IsAttention => Level == HealthLevel.Warning;
	public bool IsProblem => Level == HealthLevel.Error;
	public bool HasAction => Fix != FixAction.None;
	public string ActionText => Text.HealthFix();
	// The button's tooltip: the reason, then what the fix does.
	public string ActionTip => HasAction ? Reason + Environment.NewLine + HealthFixes.Label(Fix) : Reason;

	public void Set(HealthChip chip)
	{
		Name = chip.Label;
		Level = chip.Level;
		StatusText = HealthFixes.LevelText(chip.Level);
		Reason = chip.Reason;
		Fix = chip.Fix;
		OnPropertyChanged(nameof(ActionTip));
	}

	[RelayCommand]
	private Task Run() => Fix == FixAction.None ? Task.CompletedTask : runFix(Fix);
}

public static class HealthFixes
{
	// Title Case button text for a fix.
	public static string Label(FixAction fix) => fix switch
	{
		FixAction.InstallDriver => Text.FixInstallDriver(),
		FixAction.InstallHidHide => Text.FixInstallHidHide(),
		FixAction.OpenSteamInputHelp => Text.FixSteamInputHelp(),
		FixAction.RunAdapterTest => Text.FixRunAdapterTest(),
		FixAction.OpenBluetoothSettings => Text.FixOpenBluetooth(),
		FixAction.PairController => Text.PairController(),
		FixAction.RestartBridge => Text.FixRestartBridge(),
		FixAction.ReconnectController => Text.ReconnectController(),
		_ => string.Empty
	};

	public static string LevelText(HealthLevel level) => level switch
	{
		HealthLevel.Ok => Text.HealthLevelOk(),
		HealthLevel.Warning => Text.HealthLevelWarning(),
		HealthLevel.Error => Text.HealthLevelError(),
		_ => Text.HealthLevelUnknown()
	};
}
