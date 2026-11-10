// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The controller page: two tabs. Test (live input with every stick setting, the trigger ranges and drift calibration
// beside it, output tests with the trigger effects and strength, controller info) and Remap & Gyro. Every setting is on
// one tab only. The live readouts run only while the page is shown and the tab with them is selected: the Test tab holds the
// controller (TesterViewModel), the Remap tab polls the virtual controller's input for its gyro preview
// (SticksViewModel). The selected tab is kept while the app runs.
public sealed class ControllerPageViewModel : PageViewModel
{
	public const int TestTab = 0;
	public const int RemapTab = 1;

	private int _selectedTab = TestTab;

	public ControllerPageViewModel(IControllerService service, SettingsState settings, bool live = true)
	{
		Tuning = new SticksViewModel(service, settings, livePreview: live);
		Test = new TesterViewModel(service, settings, live, Tuning);
	}

	public TesterViewModel Test { get; }
	public SticksViewModel Tuning { get; }

	public override string Title => Text.NavController();
	public string Intro => Text.ControllerIntro();

	// Tabs for the segmented switch. The list stays the same on a language switch (only the names change), so the
	// switch never loses its selection. "Test" is the same word in both languages.
	public IReadOnlyList<TabOption> Tabs { get; } = [new(() => "Test"), new(() => Text.RemapTab())];

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
			OnPropertyChanged(nameof(IsRemapTab));
			UpdateLive();
		}
	}

	// Reset (stick and trigger tuning) shows with the Test tab, where that tuning is; the remap tab has its own.
	public bool IsTestTab => SelectedTab == TestTab;
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
