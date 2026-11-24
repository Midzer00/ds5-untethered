// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;

namespace Kopru.App.Views;

// A game's settings in the Games page's pop-up (see GamePanelView.axaml): the header, the segmented switch and the three
// segments in one host. The host is as tall as the tallest segment (measured again when the width, the language or what
// the segments show changes), so switching segments never changes the pop-up's size. Ctrl+Tab and Ctrl+Shift+Tab switch
// segments anywhere in the pop-up; the keyboard then stays on the switch.
public partial class GamePanelView : UserControl
{
	// From this width Vibration shows its two cards side by side (and nothing scrolls); below it they stack and the
	// segments scroll.
	public const double TwoColumnWidth = 640;
	public const double ColumnGap = 12;
	// The one-column segments (Game, Sound And Light) and the default profile's offer: this wide at most, from the left.
	public const double SingleColumnWidth = 520;

	private GamePanelViewModel? _model;
	private bool _heightQueued;

	public GamePanelView()
	{
		InitializeComponent();
		SizeChanged += (_, e) =>
		{
			Arrange(e.NewSize.Width >= TwoColumnWidth, e.NewSize.Width);
			QueueHeight();
		};
		Arrange(false, 0);
		DataContextChanged += (_, _) => Follow(DataContext as GamePanelViewModel);
		AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
		// Longer or shorter words: the segments are measured again (only while the pop-up is in a window).
		AttachedToVisualTree += (_, _) => Localizer.LanguageChanged += OnLanguageChanged;
		DetachedFromVisualTree += (_, _) => Localizer.LanguageChanged -= OnLanguageChanged;
	}

	private void OnLanguageChanged(Language language) => Dispatcher.UIThread.Post(QueueHeight);

	public bool IsTwoColumns { get; private set; }

	// Puts the keyboard on Close, so Esc reaches the page and Tab starts in the panel.
	public void FocusClose() => ClosePanelButton.Focus();

	private void Follow(GamePanelViewModel? model)
	{
		if (_model != null)
		{
			_model.NamingStarted -= FocusName;
			_model.PropertyChanged -= OnModelChanged;
			_model.Feel.PropertyChanged -= OnModelChanged;
			_model.AudioHaptics.PropertyChanged -= OnModelChanged;
		}
		_model = model;
		if (_model != null)
		{
			_model.NamingStarted += FocusName;
			_model.PropertyChanged += OnModelChanged;
			_model.Feel.PropertyChanged += OnModelChanged;
			_model.AudioHaptics.PropertyChanged += OnModelChanged;
		}
		QueueHeight();
	}

	// What a segment shows may have changed (the game, its profile, a row shown or hidden, a wrapped status line).
	private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
	{
		ArrangeTriggers();
		QueueHeight();
	}

	// The profile name field opened: the keyboard goes to it with the name selected, so typing replaces it.
	private void FocusName() => Dispatcher.UIThread.Post(() =>
	{
		ProfileNameBox.Focus();
		ProfileNameBox.SelectAll();
	}, DispatcherPriority.Loaded);

	// Ctrl+Tab / Ctrl+Shift+Tab: the next or previous segment; the keyboard goes to the switch.
	private void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Tab || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || _model == null)
		{
			return;
		}
		_model.StepSegment(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
		FocusTabs();
		e.Handled = true;
	}

	// The keyboard on the switch's selected segment.
	public void FocusTabs()
	{
		if (PanelTabs.ContainerFromIndex(PanelTabs.SelectedIndex) is { } item)
		{
			item.Focus(NavigationMethod.Directional);
		}
		else
		{
			PanelTabs.Focus();
		}
	}

	// Vibration and Triggers in two columns side by side or stacked; the Game segment and the default profile's offer up to
	// SingleColumnWidth wide; the picture's size; scrolling (and room for its bar) only where the columns stack.
	private void Arrange(bool wide, double width)
	{
		if (IsTwoColumns != wide || VibrationColumns.ColumnDefinitions.Count + VibrationColumns.RowDefinitions.Count == 0)
		{
			IsTwoColumns = wide;
			Columns(VibrationColumns, VibrationRight, wide);
			ArrangeTriggers();
			PanelScroll.VerticalScrollBarVisibility = wide ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
			SegmentHost.Margin = wide ? default : new Thickness(0, 0, NarrowScrollRoom, 0);
		}
		double single = Math.Max(0, Math.Min(SingleColumnWidth, width - SegmentHost.Margin.Left - SegmentHost.Margin.Right));
		IEnumerable<Layoutable> singles = Array.Empty<Layoutable>()
			.Concat(VibrationSegment.Children.Concat(TriggersSegment.Children).OfType<ContentControl>().Where(c => c.Classes.Contains("single")));
		foreach (Layoutable part in singles)
		{
			part.Width = width > 0 ? single : double.NaN;
		}
	}

	// Triggers' footer: with nothing for its left card (an unsupported game), Try takes the whole width instead of leaving
	// a hole; otherwise it is the right card.
	private void ArrangeTriggers()
	{
		bool left = _model?.IsSupported ?? true;
		Grid.SetColumn(TryCard, left ? 1 : 0);
		Grid.SetColumnSpan(TryCard, left ? 1 : 2);
	}

	// Room right of the segments for the scroll bar, where they stack and scroll.
	public const double NarrowScrollRoom = 12;

	// Two equal columns side by side, or the second under the first.
	private static void Columns(Grid grid, Control second, bool wide)
	{
		grid.ColumnDefinitions.Clear();
		grid.RowDefinitions.Clear();
		if (wide)
		{
			grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
			grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
			grid.ColumnSpacing = ColumnGap;
			grid.RowSpacing = 0;
		}
		else
		{
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			grid.ColumnSpacing = 0;
			grid.RowSpacing = 16;
		}
		Grid.SetColumn(second, wide ? 1 : 0);
		Grid.SetRow(second, wide ? 0 : 1);
	}
	private void QueueHeight()
	{
		if (_heightQueued)
		{
			return;
		}
		_heightQueued = true;
		Dispatcher.UIThread.Post(() =>
		{
			_heightQueued = false;
			UpdateHostHeight();
		}, DispatcherPriority.Loaded);
	}

	// The host's MinHeight: the tallest segment at the host's width (each measured shown, the hidden ones only for the
	// measure), so every segment gets the same height.
	public void UpdateHostHeight()
	{
		double width = SegmentHost.Bounds.Width;
		if (width <= 0 || !IsEffectivelyVisible && _model?.IsOpen != true)
		{
			return;
		}
		double tallest = 0;
		foreach (Control segment in new Control[] { GameSegment, VibrationSegment, TriggersSegment })
		{
			bool shown = segment.IsVisible;
			if (!shown)
			{
				segment.SetCurrentValue(IsVisibleProperty, true);
			}
			if (segment == TriggersSegment)
			{
				UpdateCustomTriggerRoom(width);
			}
			// Twice: the first measure of a segment never shown builds its parts (templates, list items), the second one
			// measures them as they will be.
			for (int pass = 0; pass < 2; pass++)
			{
				foreach (Layoutable part in segment.GetVisualDescendants().OfType<Layoutable>())
				{
					part.InvalidateMeasure();
				}
				segment.InvalidateMeasure();
				segment.Measure(new Size(width, double.PositiveInfinity));
			}
			tallest = Math.Max(tallest, segment.DesiredSize.Height);
			if (!shown)
			{
				segment.SetCurrentValue(IsVisibleProperty, false);
			}
		}
		tallest = Math.Ceiling(tallest);
		if (Math.Abs(SegmentHost.MinHeight - tallest) >= 1)
		{
			SegmentHost.MinHeight = tallest;
		}
	}

	// The trigger cards keep the room of their tallest state: four rows on both triggers (Weapon or Vibration), measured
	// with those rows shown for the measure only. The cards then show just their rows; the rest stays at the bottom, so
	// the footer under them never moves.
	private void UpdateCustomTriggerRoom(double hostWidth)
	{
		double width = hostWidth;
		List<Grid> rows = PanelCustomTrigger.GetLogicalDescendants().OfType<Grid>()
			.Where(g => g.Classes.Contains("resistance") || g.Classes.Contains("weapon") || g.Classes.Contains("vibration") || g.Classes.Contains("force")).ToList();
		Dictionary<Grid, bool> shown = rows.ToDictionary(r => r, r => r.IsVisible);
		double tallest = 0;
		PanelCustomTrigger.MinHeight = 0;
		foreach (string kind in new[] { "weapon", "vibration" })
		{
			foreach (Grid row in rows)
			{
				row.SetCurrentValue(IsVisibleProperty, row.Classes.Contains(kind) || row.Classes.Contains("force"));
			}
			for (int pass = 0; pass < 2; pass++)
			{
				foreach (Layoutable part in PanelCustomTrigger.GetVisualDescendants().OfType<Layoutable>())
				{
					part.InvalidateMeasure();
				}
				PanelCustomTrigger.InvalidateMeasure();
				PanelCustomTrigger.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
			}
			tallest = Math.Max(tallest, PanelCustomTrigger.DesiredSize.Height);
		}
		foreach (Grid row in rows)
		{
			row.SetCurrentValue(IsVisibleProperty, shown[row]);
		}
		PanelCustomTrigger.MinHeight = Math.Ceiling(tallest);
	}
}
