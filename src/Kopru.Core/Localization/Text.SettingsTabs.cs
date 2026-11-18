// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The Settings page's tabs, their card titles and the one short line under each row (the longer explanation is the
// row's tooltip and the control's help text).
public static partial class Text
{
	public static string SettingsTabAudio(Language? l = null) => Pick(l, $"Ses", $"Audio");

	public static string SettingsTabShortcuts(Language? l = null) => Pick(l, $"Kısayollar", $"Shortcuts");

	public static string SettingsAppGroup(Language? l = null) => Pick(l, $"Uygulama", $"App");

	public static string NotifyControllerGroup(Language? l = null) => Pick(l, $"Kumanda Bildirimleri", $"Controller Alerts");

	public static string NotifyGamesGroup(Language? l = null) => Pick(l, $"Oyun ve Sanal Kumanda", $"Games And Virtual Controller");

	public static string ShortcutCombosGroup(Language? l = null) => Pick(l, $"Tuş Kombinasyonları", $"Button Combos");

	public static string StartWithWindowsShort(Language? l = null) => Pick(l,
		$"Oturum açınca arka planda başlar.",
		$"Starts in the background when you sign in.");

	public static string KeepInTrayShort(Language? l = null) => Pick(l,
		$"Simgesi saatin yanında kalır.",
		$"Its icon stays next to the clock.");

	public static string AutoStartBridgeShort(Language? l = null) => Pick(l,
		$"Açılışta ve kumanda bağlanınca başlar.",
		$"Starts with the app and on reconnect.");

	public static string BridgeRowShort(Language? l = null) => Pick(l,
		$"Başka programa bırakmak için durdur.",
		$"Stop it to free the controller.");

	public static string PowerOffWhenIdleShort(Language? l = null) => Pick(l,
		$"Süre dolunca kumandayı da kapatır.",
		$"Also turns the controller off when time is up.");

	public static string AutoLowPowerShort(Language? l = null) => Pick(l,
		$"Pil %20'nin altına inince bir kez açılır.",
		$"Turns on once below 20% battery.");

	public static string InfoShort(Language? l = null) => Pick(l,
		$"Kumandadan okunur; adres kısaltılır.",
		$"Read from the controller; address shortened.");

	public static string InfoReconnect(Language? l = null) => Pick(l, $"Yeniden Bağla", $"Reconnect");

	public static string ProfilesShort(Language? l = null) => Pick(l,
		$"Analog, tetik, tuş ve ışık ayarlarını birlikte saklar.",
		$"Keeps stick, trigger, remap and light settings.");

	public static string AudioOutputShort(Language? l = null) => Pick(l,
		$"Windows'un varsayılan ses çıkışı.",
		$"The Windows default sound output.");

	public static string AudioInputShort(Language? l = null) => Pick(l,
		$"Windows'un varsayılan mikrofonu.",
		$"The Windows default microphone.");

	public static string MicSendShort(Language? l = null) => Pick(l,
		$"Tuş turuncu yanarken mikrofon açıktır.",
		$"The mic is on while its button glows orange.");

	public static string MicButtonModeShort(Language? l = null) => Pick(l,
		$"İkisinde de oyun ses duymaz. Sessize Al anında geri açar; Tamamen Kapat kumanda mikrofonunu kapatır.",
		$"Either way games hear nothing. Mute comes back at once; Turn Off shuts the controller mic down.");

	public static string ProtectDefaultAudioShort(Language? l = null) => Pick(l,
		$"Windows kumandaya geçerse geri alır.",
		$"Switches back if Windows picks the controller.");

	public static string ShortcutsShort(Language? l = null) => Pick(l,
		$"PS'yi basılı tut ve listedeki tuşlardan birine bas.",
		$"Hold PS and press one of the listed buttons.");

	public static string PsOpenAppShort(Language? l = null) => Pick(l,
		$"Oyun açık değilken PS'ye kısa bas: uygulama öne gelir.",
		$"With no game open, a short PS press shows the app.");

	public static string DesktopModeShort(Language? l = null) => Pick(l,
		$"Kumanda fare ve klavye olur; oyunlar giriş almaz.",
		$"Acts as mouse and keyboard; games get no input.");

	public static string DesktopAutoOffShort(Language? l = null) => Pick(l,
		$"Oyun öne gelince ya da kumandayı kullanınca kapanır.",
		$"Turns off when a game comes to the front or uses the controller.");

	public static string NotifyLinkShort(Language? l = null) => Pick(l,
		$"Bağlantı uzun süre zayıf kalırsa.",
		$"When the link stays weak for a while.");

	public static string NotifyHidHideShort(Language? l = null) => Pick(l,
		$"HidHide eksik diye sanal kumanda başlamazsa.",
		$"When it can't start without HidHide.");
}
