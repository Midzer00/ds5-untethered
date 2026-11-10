// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of button remapping and gyro aiming.
public static partial class Text
{
	public static string RemapTab(Language? l = null) => Pick(l, $"Tuş Atama ve Gyro", $"Remap & Gyro");

	public static string RemapIntro(Language? l = null) => Pick(l,
		$"Tuşlar, çubuklar ve gyro DualSense raporunun içinde değişir; oyunun titreşimi ve tetik efektleri çalışmaya devam eder.",
		$"Buttons, sticks and gyro change inside the DualSense report, so the game's haptics and trigger effects keep working.");

	public static string ButtonRemapping(Language? l = null) => Pick(l, $"Tuş Atama", $"Button Remapping");

	public static string ButtonRemappingHint(Language? l = null) => Pick(l,
		$"Her fiziksel tuşun oyuna hangi tuş olarak gideceğini seçin. PS tuşu değiştirilemez.",
		$"Choose which button each physical button sends to the game. The PS button can't be remapped.");

	public static string PhysicalButton(Language? l = null) => Pick(l, $"Fiziksel Tuş", $"Physical Button");

	public static string TargetButton(Language? l = null) => Pick(l, $"Oyuna Giden", $"Sends");

	public static string ButtonName(DsButton button, Language? l = null) => button switch
	{
		DsButton.Cross => Pick(l, $"Çarpı", $"Cross"),
		DsButton.Circle => Pick(l, $"Daire", $"Circle"),
		DsButton.Square => Pick(l, $"Kare", $"Square"),
		DsButton.Triangle => Pick(l, $"Üçgen", $"Triangle"),
		DsButton.DpadUp => Pick(l, $"Yön Yukarı", $"D-Pad Up"),
		DsButton.DpadDown => Pick(l, $"Yön Aşağı", $"D-Pad Down"),
		DsButton.DpadLeft => Pick(l, $"Yön Sol", $"D-Pad Left"),
		DsButton.DpadRight => Pick(l, $"Yön Sağ", $"D-Pad Right"),
		DsButton.L1 => "L1",
		DsButton.R1 => "R1",
		DsButton.L2 => "L2",
		DsButton.R2 => "R2",
		DsButton.L3 => "L3",
		DsButton.R3 => "R3",
		DsButton.Create => Pick(l, $"Oluştur", $"Create"),
		DsButton.Options => Pick(l, $"Seçenekler", $"Options"),
		DsButton.Touchpad => Pick(l, $"Dokunmatik Yüzey", $"Touchpad Click"),
		DsButton.Mute => Pick(l, $"Mikrofon Sessiz", $"Mute"),
		DsButton.LeftPaddle => EdgeLeftPaddle(l),
		DsButton.RightPaddle => EdgeRightPaddle(l),
		_ => Pick(l, $"Yok", $"None")
	};

	public static string StickSwapInvert(Language? l = null) => Pick(l, $"Çubuk Değişimi ve Ters Çevirme", $"Stick Swap & Invert");

	public static string SwapSticks(Language? l = null) => Pick(l, $"Sol ve Sağ Çubuğu Değiştir", $"Swap Left and Right Sticks");

	public static string InvertLeftX(Language? l = null) => Pick(l, $"Sol Çubuk Yatay Ters", $"Invert Left Stick X");

	public static string InvertLeftY(Language? l = null) => Pick(l, $"Sol Çubuk Dikey Ters", $"Invert Left Stick Y");

	public static string InvertRightX(Language? l = null) => Pick(l, $"Sağ Çubuk Yatay Ters", $"Invert Right Stick X");

	public static string InvertRightY(Language? l = null) => Pick(l, $"Sağ Çubuk Dikey Ters", $"Invert Right Stick Y");

	public static string GyroAiming(Language? l = null) => Pick(l, $"Gyro ile Nişan", $"Gyro Aiming");

	public static string GyroAimingHint(Language? l = null) => Pick(l,
		$"Kolu çevirdiğinizde sağ çubuk hareket eder; gerçek sağ çubuğa eklenir.",
		$"Turning the controller moves the right stick; it adds to the real right stick.");

	public static string GyroModeLabel(Language? l = null) => Pick(l, $"Gyro Modu", $"Gyro Mode");

	public static string GyroModeName(GyroMode mode, Language? l = null) => mode switch
	{
		GyroMode.Always => Pick(l, $"Her Zaman", $"Always"),
		GyroMode.WhileL2 => Pick(l, $"L2 Basılıyken", $"While L2 Held"),
		GyroMode.WhileR1 => Pick(l, $"R1 Basılıyken", $"While R1 Held"),
		_ => Pick(l, $"Kapalı", $"Off")
	};

	public static string GyroToggle(Language? l = null) => Pick(l, $"Basınca Aç/Kapat", $"Press to Toggle");

	public static string GyroToggleHint(Language? l = null) => Pick(l,
		$"Açıkken tuşa her basış gyroyu açar ya da kapatır.",
		$"When on, each press turns the gyro on or off.");

	public static string GyroSensitivityX(Language? l = null) => Pick(l, $"Yatay Hassasiyet", $"Horizontal Sensitivity");

	public static string GyroSensitivityY(Language? l = null) => Pick(l, $"Dikey Hassasiyet", $"Vertical Sensitivity");

	public static string GyroInvertX(Language? l = null) => Pick(l, $"Yatayı Ters Çevir", $"Invert Horizontal");

	public static string GyroInvertY(Language? l = null) => Pick(l, $"Dikeyi Ters Çevir", $"Invert Vertical");

	public static string GyroSmoothing(Language? l = null) => Pick(l, $"Yumuşatma", $"Smoothing");

	public static string GyroDeadzone(Language? l = null) => Pick(l, $"Gyro Ölü Bölgesi", $"Gyro Deadzone");

	public static string MultiplierValue(double value, Language? l = null) => Pick(l, $"{value:F1}×", $"{value:F1}×");

	public static string DegreesPerSecond(double value, Language? l = null) => Pick(l, $"{value:F1} °/sn", $"{value:F1} °/s");

	public static string GyroOffset(Language? l = null) => Pick(l, $"Gyro Katkısı", $"Gyro Offset");

	public static string GyroOffsetHint(Language? l = null) => Pick(l,
		$"Mavi nokta gyronun sağ çubuğa eklediği konum.",
		$"The blue dot is the offset the gyro adds to the right stick.");

	public static string GyroCalibration(Language? l = null) => Pick(l, $"Gyro Kalibrasyonu", $"Gyro Calibration");

	public static string GyroCalibrationHint(Language? l = null) => Pick(l,
		$"Kolu düz bir yere bırakın ve 2 saniye kıpırdatmayın; gyronun kayması ölçülüp düzeltilir.",
		$"Put the controller on a flat surface and keep it still for 2 seconds; the gyro's drift is measured and removed.");

	public static string GyroCalibrationKeepStill(Language? l = null) => Pick(l,
		$"Kolu kıpırdatmayın, kalibrasyon sürüyor…",
		$"Keep the controller still, calibrating…");

	public static string GyroCalibrationResultText(GyroCalibrationStatus status, Language? l = null) => status switch
	{
		GyroCalibrationStatus.Success => Pick(l, $"Gyro kalibrasyonu tamamlandı.", $"Gyro calibration complete."),
		GyroCalibrationStatus.Moved => Pick(l, $"Kol hareket etti. Kıpırdatmadan tekrar deneyin.",
			$"The controller moved. Try again without moving it."),
		_ => Pick(l, $"Gyro verisi gelmedi. Sanal kumandanın çalıştığından emin olup tekrar deneyin.",
			$"No gyro data arrived. Make sure the virtual controller is running and try again.")
	};
}
