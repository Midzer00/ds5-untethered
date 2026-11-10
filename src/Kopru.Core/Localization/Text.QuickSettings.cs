// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the Overview's quick settings card and Low Power mode.
public static partial class Text
{
	public static string QuickSettingsTitle(Language? l = null) => Pick(l, $"Hızlı Ayarlar", $"Quick Settings");

	public static string LowPower(Language? l = null) => Pick(l, $"Düşük Güç", $"Low Power");

	public static string LowPowerNote(Language? l = null) => Pick(l,
		$"Titreşim, ışık ve ses kısıldı; pil daha uzun gider.",
		$"Haptics, light and sound are reduced; the battery lasts longer.");

	public static string HapticsStrength(Language? l = null) => Pick(l, $"Haptik Gücü", $"Haptics Strength");

	public static string QuickLight(Language? l = null) => Pick(l, $"Işık", $"Light");

	public static string QuickMicrophone(Language? l = null) => Pick(l, $"Mikrofon", $"Microphone");

	public static string StepLight(Language? l = null) => Pick(l, $"Hafif", $"Light");

	public static string StepNormal(Language? l = null) => Pick(l, $"Normal", $"Medium");

	public static string StepStrong(Language? l = null) => Pick(l, $"Güçlü", $"Strong");

	public static string StepOff(Language? l = null) => Pick(l, $"Kapalı", $"Off");

	public static string StepLow(Language? l = null) => Pick(l, $"Düşük", $"Low");

	public static string StepHigh(Language? l = null) => Pick(l, $"Yüksek", $"High");

	// A value between the steps.
	public static string QuickCustom(double percent, Language? l = null) => Pick(l, $"Özel · %{percent:F0}", $"Custom · {percent:F0}%");

	public static string AdvancedSettings(Language? l = null) => Pick(l, $"Gelişmiş Ayarlar ›", $"Advanced Settings ›");

	public static string AppliedWhenConnected(Language? l = null) => Pick(l,
		$"Kumanda bağlanınca uygulanır.",
		$"Applied when the controller connects.");

	public static string AutoLowPower(Language? l = null) => Pick(l,
		$"Pil %20'nin Altına Düşünce Düşük Güce Geç",
		$"Switch To Low Power Below 20% Battery");

	public static string AutoLowPowerHint(Language? l = null) => Pick(l,
		$"Şarj olmazken pil %20'nin altına inince Düşük Güç bir kez açılır. Sen kapatırsan pil yeniden %20'nin üstüne çıkana kadar açılmaz.",
		$"When the battery drops below 20% while not charging, Low Power turns on once. If you turn it off, it stays off until the battery is above 20% again.");

	// Toggle tiles: desktop mode under the name gamers look for, and the state word under each tile's label.
	public static string QuickMouseMode(Language? l = null) => Pick(l, $"Fare Modu", $"Mouse Mode");

	public static string QuickStateOn(Language? l = null) => Pick(l, $"Açık", $"On");

	public static string QuickStateOff(Language? l = null) => Pick(l, $"Kapalı", $"Off");

	public static string QuickLowPowerShort(Language? l = null) => Pick(l,
		$"Açık · titreşim, ışık ve ses kısıldı",
		$"On · haptics, light and sound reduced");

	// One plain sentence per quick setting (the tooltips of the card).
	public static string QuickLowPowerTip(Language? l = null) => Pick(l,
		$"Titreşimi ve tetikleri hafifletir, ışığı ve sesi kapatır, kumandayı boşta daha erken kapatır; pil daha uzun gider.",
		$"Softens haptics and triggers, turns the light and sound off and powers the controller off sooner when idle, so the battery lasts longer.");

	public static string QuickSupportedModeTip(Language? l = null) => Pick(l,
		$"Açıkken oyun kumandayı kablolu DualSense gibi kullanır: kendi haptiği, tetik efektleri, ışığı ve kumanda hoparlörüne gönderdiği sesler gelir. Kapalıyken klasik titreşim kullanılır.",
		$"When on, the game uses the controller like a wired DualSense: its own haptics, trigger effects, light and the sounds it sends to the controller's speaker. When off, classic rumble is used.");

	public static string QuickMouseModeTip(Language? l = null) => Pick(l,
		$"Kumandayla Windows'ta fare imlecini oynatır ve tıklarsın; bu sırada oyunlar kumandadan giriş almaz.",
		$"Move and click the Windows mouse pointer with the controller; games get no input from it meanwhile.");

	public static string QuickSpeakerTip(Language? l = null) => Pick(l,
		$"Oyunun kumanda hoparlörüne gönderdiği sesleri çalar. DualSense Modu açıkken kendiliğinden açılır.",
		$"Plays the sounds games send to the controller's speaker. Turns on by itself in DualSense Mode.");

	public static string QuickMicTip(Language? l = null) => Pick(l,
		$"Kumandanın mikrofonunu oyunlara kablolu kumandanınki gibi açar.",
		$"Makes the controller's microphone available to games, like on a wired controller.");

	public static string QuickHapticsTip(Language? l = null) => Pick(l,
		$"Oyunun gönderdiği haptik titreşimin gücünü seçer.",
		$"Sets how strong the haptics sent by the game feel.");

	public static string QuickTriggerTip(Language? l = null) => Pick(l,
		$"Uyarlanabilir tetiklerin ne kadar sert direneceğini seçer.",
		$"Sets how hard the adaptive triggers push back.");

	public static string QuickLightTip(Language? l = null) => Pick(l,
		$"Kumandanın ışık çubuğunun parlaklığını seçer.",
		$"Sets the brightness of the controller's light bar.");

	public static string QuickProfileTip(Language? l = null) => Pick(l,
		$"Kayıtlı ayar profilleri arasında geçer; seçtiğin profil hemen uygulanır.",
		$"Switches between saved settings profiles; the chosen one applies right away.");

	// Added to the tooltip of a setting Low Power overrides while it is on.
	public static string QuickOverriddenByLowPower(Language? l = null) => Pick(l,
		$"Düşük Güç açıkken bunu Düşük Güç belirler.",
		$"While Low Power is on, Low Power sets this.");

	// The Overview's live level bars: the two haptic motors and the triggers.
	public static string LiveLeftHaptics(Language? l = null) => Pick(l, $"Sol Haptik", $"Left Haptics");

	public static string LiveRightHaptics(Language? l = null) => Pick(l, $"Sağ Haptik", $"Right Haptics");

	public static string LiveLeftHapticsTip(Language? l = null) => Pick(l,
		$"Kumandanın sol haptik motoruna şu an giden titreşim.",
		$"The vibration going to the controller's left haptic motor right now.");

	public static string LiveRightHapticsTip(Language? l = null) => Pick(l,
		$"Kumandanın sağ haptik motoruna şu an giden titreşim.",
		$"The vibration going to the controller's right haptic motor right now.");

	public static string LiveTriggerTip(Language? l = null) => Pick(l,
		$"Tetiğe ne kadar bastığın.",
		$"How far you are pressing the trigger.");

	// Tooltip of the battery reading in the Overview's hero.
	public static string HeroBatteryTip(Language? l = null) => Pick(l,
		$"Kumandanın pil seviyesi.",
		$"The controller's battery level.");

	public static string HeroBatteryChargingTip(Language? l = null) => Pick(l,
		$"Kumandanın pil seviyesi; şu an şarj oluyor.",
		$"The controller's battery level; it is charging now.");

	// The health strip when nothing needs attention: one quiet line with what was checked.
	public static string HealthAllGood(string names, Language? l = null) => Pick(l, $"Sorun yok · {names}", $"All good · {names}");
}
