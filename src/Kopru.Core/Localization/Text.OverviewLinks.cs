// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The Overview's connection line under the hero's drawing, the live card's link caption and notices, and the live card's
// panel while there are no live numbers (the chain controller -> virtual controller -> game, a title and one paragraph).
public static partial class Text
{
	// The hero's connection line (Title Case); while charging the shared "Bluetooth · Kablo Takılı" is used.
	public static string HeroLinkBluetooth(Language? l = null) => T(l, "Hero.LinkBluetooth");
	public static string HeroLinkWired(Language? l = null) => T(l, "Hero.LinkWired");

	// The live card's caption under the delay: the link the delay is measured on.
	public static string LiveTypicalDelayBluetooth(Language? l = null) => T(l, "Live.TypicalDelayBluetooth");

	// The notice's lead-in while the link is fair (the weak one uses NotifyLinkTitle).
	public static string LinkNoticeFair(Language? l = null) => T(l, "Link.NoticeFair");

	// The state panel's titles (Title Case).
	public static string StateTurnOnHeading(Language? l = null) => T(l, "State.TurnOnHeading");
	public static string StatePausedHeading(Language? l = null) => T(l, "State.PausedHeading");

	// The state panel's paragraphs: why, and what happens next.
	public static string StateNotConnectedText(Language? l = null) => T(l, "State.NotConnectedText");
	public static string StateWaitingText(Language? l = null) => T(l, "State.WaitingText");
	public static string StateVirtualOffText(Language? l = null) => T(l, "State.VirtualOffText");
	public static string StateCableOnlyText(Language? l = null) => T(l, "State.CableOnlyText");

	// The chain's state words and step names (Title Case).
	public static string ChainWaiting(Language? l = null) => T(l, "Chain.Waiting");
	public static string ChainConnected(Language? l = null) => T(l, "Chain.Connected");
	public static string ChainPaused(Language? l = null) => T(l, "Chain.Paused");
	public static string ChainSeesController(Language? l = null) => T(l, "Chain.SeesController");
	public static string ChainPressPs(Language? l = null) => T(l, "Chain.PressPs");
	public static string ChainConnects(Language? l = null) => T(l, "Chain.Connects");
	public static string ChainStarts(Language? l = null) => T(l, "Chain.Starts");
	public static string ChainUsbCable(Language? l = null) => T(l, "Chain.UsbCable");
}
