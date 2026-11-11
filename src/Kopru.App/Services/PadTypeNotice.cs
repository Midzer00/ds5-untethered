// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Services;

// The brief toast when the virtual controller type changes with the game ("Xbox 360 Mode" / "DualShock 4 Mode" /
// "DualSense Mode").
public sealed partial class NotificationCenter
{
	// Forced: the type was chosen by hand for every game ("Kumanda Türü" not on Automatic), so the body does not say "for
	// this game".
	public static AppNotification PadTypeNotice(string? detail, bool isEdge = false, bool forced = false)
	{
		VirtualPadType type = Enum.TryParse(detail, out VirtualPadType parsed) ? parsed : VirtualPadType.DualSense;
		(string title, string body) = type switch
		{
			VirtualPadType.Xbox360 => (Text.PadModeXboxTitle(), forced ? Text.PadForcedXboxBody() : Text.PadModeXboxBody()),
			VirtualPadType.DualShock4 => (Text.PadModeDualShock4Title(), forced ? Text.PadForcedDualShock4Body() : Text.PadModeDualShock4Body()),
			_ => (Text.PadModeDualSenseTitle(), forced ? Text.PadForcedDualSenseBody() : Text.PadModeDualSenseBody())
		};
		return new AppNotification(NotificationType.GameDetected, NotificationKind.Info, title, body, AppPage.SupportedGames, IsEdge: isEdge);
	}
}
