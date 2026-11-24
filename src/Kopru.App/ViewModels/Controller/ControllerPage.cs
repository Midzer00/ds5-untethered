// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

public sealed class CableModeOption(CableMode mode, Func<string> name) : LabelledChoice(name)
{
	public CableMode Mode { get; } = mode;
}

// The controller page: three tabs. Test (live input with every stick setting, the trigger ranges and drift calibration
// beside it, fitted to one window), Output Tests (vibration, trigger effects and strength, light; plus the cable mode
// setting) and Remap & Gyro. Every setting is on one tab only. The live readouts run only while the page is shown and
// the tab with them is selected: the Test tab holds the controller (TesterViewModel), the Remap tab polls the virtual
// controller's input for its gyro preview (SticksViewModel). The selected tab is kept while the app runs.
public sealed partial class ControllerPageViewModel : PageViewModel
{
	public const int TestTab = 0;
	public const int OutputsTab = 1;
	public const int RemapTab = 2;

	private readonly SettingsState _settings;
	private int _selectedTab = TestTab;

	public ControllerPageViewModel(IControllerService service, SettingsState settings, bool live = true)
	{
		_settings = settings;
		Tuning = new SticksViewModel(service, settings, livePreview: live);
		Test = new TesterViewModel(service, settings, live, Tuning);
		Look = new ControllerLookViewModel(service, settings);
		CableModes =
		[
			new CableModeOption(CableMode.Wired, () => Text.CableModeWired()),
			new CableModeOption(CableMode.ChargeOnly, () => Text.CableModeChargeOnly()),
		];
		_selectedCableMode = CableModes.First(o => o.Mode == _settings.CableMode);
		_settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.CableMode))
			{
				SelectedCableMode = CableModes.First(o => o.Mode == _settings.CableMode);
			}
		};
	}

	public TesterViewModel Test { get; }

	// The selected controller's look and its picker (the colour tile beside the header and the tabs).
	public ControllerLookViewModel Look { get; }
	public SticksViewModel Tuning { get; }
	public IReadOnlyList<CableModeOption> CableModes { get; }

	public override string Title => Text.NavController();
	public string Intro => Text.ControllerIntro();
	public string CableGroupLabel => Text.CardCable();
	public string CableModeLabel => Text.CableModeLabel();
	public string CableModeShort => Text.CableModeShort();
	public string CableModeHint => Text.CableModeHint();

	// The cable behaviour choice (Settings.CableMode both ways; a cleared selection while the language switch
	// rebuilds nothing is ignored, as with the language picker).
	[ObservableProperty]
	private CableModeOption _selectedCableMode;

	partial void OnSelectedCableModeChanged(CableModeOption value)
	{
		if (value != null && value.Mode != _settings.CableMode)
		{
			_settings.CableMode = value.Mode;
		}
	}

	// Tabs for the segmented switch. The list stays the same on a language switch (only the names change), so the
	// switch never loses its selection. "Test" is the same word in both languages.
	public IReadOnlyList<TabOption> Tabs { get; } = [new(() => "Test"), new(() => Text.ControllerTabDeviceTests()), new(() => Text.RemapTab())];

	// The shown tab; out of range values (-1: nothing selected) are ignored.
	public int SelectedTab
	{
		get => _selectedTab;
		set
		{
			if (value is < TestTab or > RemapTab || value == _selectedTab)
			{
				return;
			}
			_selectedTab = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsTestTab));
			OnPropertyChanged(nameof(IsOutputsTab));
			OnPropertyChanged(nameof(IsRemapTab));
			UpdateLive();
		}
	}

	// Reset (stick and trigger tuning) shows with the Test tab, where that tuning is; the remap tab has its own.
	public bool IsTestTab => SelectedTab == TestTab;
	public bool IsOutputsTab => SelectedTab == OutputsTab;
	public bool IsRemapTab => SelectedTab == RemapTab;

	// Opens a tab by number; one that does not exist (such as the former Triggers tab) opens Test.
	public void ShowTab(int tab) => SelectedTab = tab is >= TestTab and <= RemapTab ? tab : TestTab;

	public bool IsVisible { get; private set; }

	// The page was shown or left (the main window: selected in the sidebar and not covered by the setup page).
	public void SetVisible(bool visible)
	{
		IsVisible = visible;
		UpdateLive();
	}

	private void UpdateLive()
	{
		Test.SetVisible(IsVisible && IsTestTab);
		Tuning.SetVisible(IsVisible && IsRemapTab);
	}
}
