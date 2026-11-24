// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Kopru.App.Controls;

// What one link (Bluetooth or the USB cable) is doing for the controller a card shows.
public enum LinkUse
{
	// Not there (no cable, no Bluetooth link): no icon.
	Absent,
	// The virtual controller's data runs over it now: the accent.
	InUse,
	// Connected, carrying nothing (Bluetooth while wired, or while the virtual controller is off).
	Standby,
	// The cable is in but only charges.
	ChargeOnly,
	// The cable while Bluetooth is lost: games use the wired controller directly, the app carries nothing.
	Direct
}

// The connection line of the Overview's hero and the sidebar card: the icons of the links (the one in use first, in the
// accent; a connected idle link in the secondary text colour; an absent link has no icon), the line's text and, while the
// cable is the link, a quiet "Deneysel" tag. Hero: icons 16, text 13 semibold; sidebar (Hero false): icons 14, text 12.
public sealed class LinkLine : StackPanel
{
	public static readonly StyledProperty<LinkUse> BluetoothProperty = AvaloniaProperty.Register<LinkLine, LinkUse>(nameof(Bluetooth));
	public static readonly StyledProperty<LinkUse> UsbProperty = AvaloniaProperty.Register<LinkLine, LinkUse>(nameof(Usb));
	public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<LinkLine, string?>(nameof(Text));
	public static readonly StyledProperty<bool> HeroProperty = AvaloniaProperty.Register<LinkLine, bool>(nameof(Hero));
	// The "Deneysel" tag: its text, and whether it shows.
	public static readonly StyledProperty<string?> TagTextProperty = AvaloniaProperty.Register<LinkLine, string?>(nameof(TagText));
	public static readonly StyledProperty<bool> ShowsTagProperty = AvaloniaProperty.Register<LinkLine, bool>(nameof(ShowsTag));

	public LinkLine()
	{
		Orientation = Orientation.Horizontal;
		Spacing = 6;
		Rebuild();
	}

	public LinkUse Bluetooth { get => GetValue(BluetoothProperty); set => SetValue(BluetoothProperty, value); }
	public LinkUse Usb { get => GetValue(UsbProperty); set => SetValue(UsbProperty, value); }
	public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
	public bool Hero { get => GetValue(HeroProperty); set => SetValue(HeroProperty, value); }
	public string? TagText { get => GetValue(TagTextProperty); set => SetValue(TagTextProperty, value); }
	public bool ShowsTag { get => GetValue(ShowsTagProperty); set => SetValue(ShowsTagProperty, value); }

	// The text block of the line (tests read it).
	public TextBlock? Label { get; private set; }

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == BluetoothProperty || change.Property == UsbProperty || change.Property == TextProperty || change.Property == HeroProperty
			|| change.Property == TagTextProperty || change.Property == ShowsTagProperty)
		{
			Rebuild();
		}
	}

	// The stroke of a link icon by what the link does: the accent only for the link in use now.
	public static IBrush StrokeFor(LinkUse use) => use switch
	{
		LinkUse.InUse => Res("AccentBrush"),
		LinkUse.Absent => Res("DisabledTextBrush"),
		_ => Res("TextSecondaryBrush")
	};

	internal static IBrush Res(string key) => Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

	private void Rebuild()
	{
		Children.Clear();
		bool hero = Hero;
		double size = hero ? 16 : 14;
		var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = hero ? 3 : 2, VerticalAlignment = VerticalAlignment.Center };
		(LinkUse Use, Geometry Data)[] links = [(Usb, IconPaths.Cable), (Bluetooth, IconPaths.Bluetooth)];
		foreach ((LinkUse use, Geometry data) in links.OrderBy(l => l.Use == LinkUse.InUse ? 0 : 1))
		{
			if (use != LinkUse.Absent)
			{
				icons.Children.Add(new IconSlot { Data = data, Size = size, Stroke = StrokeFor(use), Weight = 1.9, VerticalAlignment = VerticalAlignment.Center });
			}
		}
		if (icons.Children.Count > 0)
		{
			Children.Add(icons);
		}
		Label = new TextBlock
		{
			Text = Text, FontSize = hero ? 13 : 12, FontWeight = hero ? FontWeight.SemiBold : FontWeight.Normal,
			Foreground = Res(hero ? "TextBrush" : "TextSecondaryBrush"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		};
		Children.Add(Label);
		if (ShowsTag && !string.IsNullOrEmpty(TagText))
		{
			Children.Add(new ExperimentalTag(TagText));
		}
	}
}

// "Deneysel": a small quiet word tag (no icon, no edge), smaller than a chip so it never reads as a third link.
public sealed class ExperimentalTag : Border
{
	public ExperimentalTag(string text)
	{
		Height = 18;
		CornerRadius = new CornerRadius(9);
		Padding = new Thickness(8, 0);
		Background = LinkLine.Res("QuietFillBrush");
		VerticalAlignment = VerticalAlignment.Center;
		Child = new TextBlock
		{
			Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = LinkLine.Res("TextSecondaryBrush"),
			TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center
		};
	}
}

// The hero's drawing and the texts beside it: the texts take the width they need (between TextMin and TextMax; a longer
// name is trimmed), the drawing gets all the rest up to its own caps, and the texts start right after it. So the drawing
// is as large as the card allows: about 157 px beside "DualSense", narrower beside "DualSense Edge", wider again in a
// wider window. First child: the drawing; the others: text blocks sharing one place (one shown at a time).
public sealed class ArtRow : Panel
{
	public double Gap { get; set; } = 12;
	public double TextMin { get; set; } = 144;
	public double TextMax { get; set; } = 186;

	protected override Size MeasureOverride(Size availableSize)
	{
		if (Children.Count == 0)
		{
			return default;
		}
		double textWidth = 0, textHeight = 0;
		double textLimit = double.IsInfinity(availableSize.Width) ? TextMax : Math.Min(TextMax, Math.Max(0, availableSize.Width - Gap));
		foreach (Control text in Children.Skip(1))
		{
			text.Measure(new Size(textLimit, availableSize.Height));
			textWidth = Math.Max(textWidth, text.DesiredSize.Width);
			textHeight = Math.Max(textHeight, text.DesiredSize.Height);
		}
		textWidth = Math.Min(Math.Max(textWidth, TextMin), textLimit);
		Control art = Children[0];
		art.Measure(new Size(double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - Gap - textWidth), availableSize.Height));
		return new Size(art.DesiredSize.Width + Gap + textWidth, Math.Max(art.DesiredSize.Height, textHeight));
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		if (Children.Count == 0)
		{
			return finalSize;
		}
		Control art = Children[0];
		art.Arrange(new Rect(0, Math.Max(0, (finalSize.Height - art.DesiredSize.Height) / 2), art.DesiredSize.Width, Math.Min(finalSize.Height, art.DesiredSize.Height)));
		double x = art.DesiredSize.Width + Gap;
		foreach (Control text in Children.Skip(1))
		{
			text.Arrange(new Rect(x, 0, Math.Max(0, finalSize.Width - x), finalSize.Height));
		}
		return finalSize;
	}
}
