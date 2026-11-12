// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the controller test page. Button names that are the same everywhere (L1, R2, PS...) are not
// translated and live in the view model.
public static partial class Text
{
	public static string TesterSourceBridge(Language? l = null) => Pick(l, $"Canlı · Sanal Kumanda", $"Live · Virtual Controller");

	public static string TesterSourceDirect(Language? l = null) => Pick(l, $"Doğrudan Okuma", $"Direct Read");

	public static string TesterNoInput(Language? l = null) => Pick(l, $"Giriş Yok", $"No Input");

	public static string TesterNoInputHint(Language? l = null) => Pick(l,
		$"Kumandadan giriş gelmiyor. Bluetooth ile bağlı olduğundan emin ol.",
		$"No input from the controller. Make sure it is connected over Bluetooth.");

	public static string TesterDirectHint(Language? l = null) => Pick(l,
		$"Sanal kumanda kapalı olduğu için gerçek kumanda yalnızca okunuyor; ona hiçbir şey gönderilmiyor.",
		$"The virtual controller is off, so the controller is only read; nothing is sent to it.");

	public static string TesterLeftStick(Language? l = null) => Pick(l, $"Sol Analog", $"Left Stick");

	public static string TesterRightStick(Language? l = null) => Pick(l, $"Sağ Analog", $"Right Stick");

	public static string TesterStickHint(Language? l = null) => Pick(l,
		$"Gri nokta ham konum, mavi nokta ayarlarınla oyuna giden konum.",
		$"The grey dot is the raw position, the blue dot what games receive with your settings.");

	public static string TesterNoTouch(Language? l = null) => Pick(l, $"Dokunma Yok", $"No Touch");

	public static string TesterMotion(Language? l = null) => Pick(l, $"Hareket Sensörleri", $"Motion Sensors");

	public static string TesterGyro(Language? l = null) => Pick(l, $"Jiroskop", $"Gyroscope");

	public static string TesterAccel(Language? l = null) => Pick(l, $"İvmeölçer", $"Accelerometer");

	public static string TesterMotionNeedsFull(Language? l = null) => Pick(l,
		$"Hareket ve dokunma verisi kumanda tam rapora geçince görünür.",
		$"Motion and touch data appear once the controller sends full reports.");

	public static string TesterBattery(Language? l = null) => Pick(l, $"Pil", $"Battery");

	public static string TesterConnection(Language? l = null) => Pick(l, $"Bağlantı", $"Connection");

	public static string TesterReportRate(Language? l = null) => Pick(l, $"Rapor Hızı", $"Report Rate");

	public static string TesterSource(Language? l = null) => Pick(l, $"Kaynak", $"Source");

	public static string TesterOutputs(Language? l = null) => Pick(l, $"Çıkış Testleri", $"Output Tests");

	public static string TesterOutputsHint(Language? l = null) => Pick(l,
		$"Bir test çalışırken canlı okuma kısa bir süre durabilir.",
		$"Live reading may pause briefly while a test runs.");

	public static string TesterHaptics(Language? l = null) => Pick(l, $"Titreşim", $"Haptics");

	public static string TesterLeft(Language? l = null) => Pick(l, $"Sol", $"Left");

	public static string TesterRight(Language? l = null) => Pick(l, $"Sağ", $"Right");

	public static string TesterTriggerEffects(Language? l = null) => Pick(l, $"Tetik Efektleri", $"Trigger Effects");

	public static string TesterLightColour(Language? l = null) => Pick(l, $"Işık Rengi", $"Light Colour");

	// Two lines on purpose (the card is narrow; a free wrap leaves one word alone on the second line).
	public static string TesterCalibrationHint(Language? l = null) => Pick(l,
		$"Kumandayı bırak,\nçubuklara 3 saniye dokunma.",
		$"Put the controller down and\nleave the sticks for 3 seconds.");

	public static string TesterSquare(Language? l = null) => Pick(l, $"Kare", $"Square");

	public static string TesterCross(Language? l = null) => Pick(l, $"Çarpı", $"Cross");

	public static string TesterCircle(Language? l = null) => Pick(l, $"Daire", $"Circle");

	public static string TesterTriangle(Language? l = null) => Pick(l, $"Üçgen", $"Triangle");

	public static string TesterUp(Language? l = null) => Pick(l, $"Yukarı", $"Up");

	public static string TesterDown(Language? l = null) => Pick(l, $"Aşağı", $"Down");

	public static string TesterCreate(Language? l = null) => Pick(l, $"Oluştur", $"Create");

	public static string TesterOptions(Language? l = null) => Pick(l, $"Seçenekler", $"Options");

	public static string TesterTouchClick(Language? l = null) => Pick(l, $"Yüzey Tıklama", $"Pad Click");

	public static string TesterMute(Language? l = null) => Pick(l, $"Sessiz", $"Mute");

	public static string TesterLeftFn(Language? l = null) => Pick(l, $"Sol Fn", $"Left Fn");

	public static string TesterRightFn(Language? l = null) => Pick(l, $"Sağ Fn", $"Right Fn");

	public static string TesterLeftPaddle(Language? l = null) => Pick(l, $"Sol Arka", $"Left Back");

	public static string TesterRightPaddle(Language? l = null) => Pick(l, $"Sağ Arka", $"Right Back");

}
