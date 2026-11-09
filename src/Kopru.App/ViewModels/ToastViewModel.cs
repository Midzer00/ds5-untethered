// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One toast card: the controller drawing, title, one-line body and, when the controller's
// battery is known, a battery glyph with the level. Warnings and errors carry a small status mark before the title; the card
// itself stays neutral.
public sealed class ToastViewModel(AppNotification notification, bool compact = false) : ViewModelBase
{
	public AppNotification Notification { get; } = notification;

	// Smaller card without the header line (a full-screen app is in front).
	public bool IsCompact { get; } = compact;
	public bool ShowHeader => !IsCompact;
	public string AppName => AppInfo.Name;
	public string Title => Notification.Title;
	public string Body => Notification.Body;
	public string CloseText => Text.NotifyClose();
	public double CardWidth => IsCompact ? 344 : 368;

	// Visual on the left: the controller as the app draws it (the Edge drawing for an Edge).
	public bool IsEdge => Notification.IsEdge;
	public double ArtWidth => IsCompact ? 58 : 72;
	public double ArtHeight => IsCompact ? 38 : 48;
	// A controller that has gone away is drawn faded with its light off.
	public bool IsControllerGone => Notification.Type is NotificationType.Disconnected or NotificationType.PoweredOffIdle;
	public double ArtOpacity => IsControllerGone ? 0.45 : 1;
	public double LightIntensity => IsControllerGone ? 0 : 1;

	public bool HasBattery => Notification.Value != null;
	public double BatteryLevel => Math.Clamp(Notification.Value ?? 0, 0, 100);
	public bool IsCharging => Notification.IsCharging;
	// The glyph fills green while charging; the text says so too until the battery is full.
	public string BatteryText => IsCharging && BatteryLevel < 100 ? Text.BatteryCharging(Math.Round(BatteryLevel)) : Text.BatteryValue(Math.Round(BatteryLevel));

	// Status mark before the title: amber for warnings, red for errors, none otherwise.
	public bool HasStatusMark => Notification.Kind is NotificationKind.Warning or NotificationKind.Error;
	public Geometry? StatusMark => Notification.Kind switch
	{
		NotificationKind.Error => Geometry.Parse(ToastMarks.Error),
		NotificationKind.Warning => Geometry.Parse(ToastMarks.Warning),
		_ => null
	};
	public IBrush StatusBrush => new SolidColorBrush(Notification.Kind == NotificationKind.Error ? Color.Parse("#E5534B") : Color.Parse("#C9A24A"));
}

// 12x12 filled status marks (own drawings): a triangle and a circle, each with a cut-out exclamation mark.
public static class ToastMarks
{
	public const string Warning = "F0 M6,0.6 C6.4,0.6 6.7,0.8 6.9,1.1 L11.8,9.9 C12.2,10.6 11.7,11.4 10.9,11.4 H1.1 C0.3,11.4 -0.2,10.6 0.2,9.9 L5.1,1.1 C5.3,0.8 5.6,0.6 6,0.6 Z M5.3,4 V7.4 H6.7 V4 Z M5.3,8.4 V9.8 H6.7 V8.4 Z";
	public const string Error = "F0 M6,0 A6,6 0 1 1 5.99,0 Z M5.3,2.8 V6.8 H6.7 V2.8 Z M5.3,8 V9.4 H6.7 V8 Z";
}
