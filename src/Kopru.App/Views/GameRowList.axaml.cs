// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;

namespace Kopru.App.Views;

// The Games list (see GameRowList.axaml).
public partial class GameRowList : UserControl
{
	// From this width on the two pickers of a row sit side by side (a row is then one control high).
	public const double RoomyWidth = 600;

	public static readonly StyledProperty<IEnumerable?> ItemsProperty = AvaloniaProperty.Register<GameRowList, IEnumerable?>(nameof(Items));
	public static readonly StyledProperty<PadTypeChoiceViewModel?> PadChoiceProperty = AvaloniaProperty.Register<GameRowList, PadTypeChoiceViewModel?>(nameof(PadChoice));
	public static readonly StyledProperty<SettingsState?> PendingProperty = AvaloniaProperty.Register<GameRowList, SettingsState?>(nameof(Pending));
	public static readonly StyledProperty<string?> PendingLineProperty = AvaloniaProperty.Register<GameRowList, string?>(nameof(PendingLine));
	public static readonly StyledProperty<ICommand?> OpenCommandProperty = AvaloniaProperty.Register<GameRowList, ICommand?>(nameof(OpenCommand));
	public static readonly StyledProperty<object?> MessagesProperty = AvaloniaProperty.Register<GameRowList, object?>(nameof(Messages));
	public static readonly StyledProperty<object?> FooterProperty = AvaloniaProperty.Register<GameRowList, object?>(nameof(Footer));

	public GameRowList()
	{
		InitializeComponent();
		SizeChanged += (_, e) => Classes.Set("roomy", e.NewSize.Width >= RoomyWidth);
		Rows.AddHandler(TappedEvent, OnRowTapped);
	}

	// Runs with a row's game when the row itself is clicked (not one of its pickers or buttons).
	public ICommand? OpenCommand
	{
		get => GetValue(OpenCommandProperty);
		set => SetValue(OpenCommandProperty, value);
	}

	private void OnRowTapped(object? sender, TappedEventArgs e)
	{
		if (e.Source is not Visual source)
		{
			return;
		}
		foreach (Visual visual in source.GetSelfAndVisualAncestors())
		{
			if (visual is Button or ComboBox or ToggleButton)
			{
				return;
			}
			if (visual is Border { DataContext: GameItemViewModel game } row && row.Classes.Contains("gameRow"))
			{
				if (OpenCommand?.CanExecute(game) == true)
				{
					OpenCommand.Execute(game);
					e.Handled = true;
				}
				return;
			}
		}
	}

	// The rows (GameItemViewModel).
	public IEnumerable? Items
	{
		get => GetValue(ItemsProperty);
		set => SetValue(ItemsProperty, value);
	}

	// The default controller type, shown above the rows (hidden when null).
	public PadTypeChoiceViewModel? PadChoice
	{
		get => GetValue(PadChoiceProperty);
		set => SetValue(PadChoiceProperty, value);
	}

	// The controller type waiting for the running game (HasPendingPadType, its note and Apply Now; hidden when null).
	public SettingsState? Pending
	{
		get => GetValue(PendingProperty);
		set => SetValue(PendingProperty, value);
	}

	// The running game and the type waiting for it, under the note.
	public string? PendingLine
	{
		get => GetValue(PendingLineProperty);
		set => SetValue(PendingLineProperty, value);
	}

	// The page's own lines above the rows (no match, nothing found, searching).
	public object? Messages
	{
		get => GetValue(MessagesProperty);
		set => SetValue(MessagesProperty, value);
	}

	// The page's own line under the rows.
	public object? Footer
	{
		get => GetValue(FooterProperty);
		set => SetValue(FooterProperty, value);
	}
}
