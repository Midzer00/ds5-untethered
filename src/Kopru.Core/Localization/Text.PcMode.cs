// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of desktop mode (the controller drives Windows) and gyro to mouse.
public static partial class Text
{
	public static string DesktopModeSection(Language? l = null) => Pick(l, $"Fare Modu", $"Mouse Mode");

	public static string DesktopModeSwitch(Language? l = null) => Pick(l, $"Fare Modu", $"Mouse Mode");

	public static string DesktopModeHint(Language? l = null) => Pick(l,
		$"Açıkken kumanda Windows'ta fare ve klavye olur; bu sırada oyunlar kumandadan giriş almaz. PS + Seçenekler ile de açılıp kapanır.",
		$"While on, the controller is a mouse and keyboard in Windows; games get no input from it meanwhile. PS + Options turns it on and off too.");

	public static string DesktopAutoOff(Language? l = null) => Pick(l, $"Oyun Açılınca Kapat", $"Turn Off When a Game Starts");

	public static string DesktopAutoOffHint(Language? l = null) => Pick(l,
		$"Desteklenen listesindeki bir oyun öne gelince fare modu kendiliğinden kapanır.",
		$"Mouse mode turns itself off when a game from the supported list comes to the front.");

	public static string DesktopPointerSpeed(Language? l = null) => Pick(l, $"İmleç Hızı", $"Pointer Speed");

	public static string DesktopPointerAcceleration(Language? l = null) => Pick(l, $"İmleç İvmesi", $"Pointer Acceleration");

	public static string DesktopTouchpadSpeed(Language? l = null) => Pick(l, $"Dokunmatik Yüzey Hızı", $"Touchpad Speed");

	public static string DesktopScrollSpeed(Language? l = null) => Pick(l, $"Kaydırma Hızı", $"Scroll Speed");

	public static string DesktopMappingTitle(Language? l = null) => Pick(l, $"Tuş Düzeni", $"Controls");

	public static string DesktopOnTitle(Language? l = null) => Pick(l, $"Fare Modu Açık", $"Mouse Mode On");

	public static string DesktopOffTitle(Language? l = null) => Pick(l, $"Fare Modu Kapalı", $"Mouse Mode Off");

	public static string DesktopOnBody(Language? l = null) => Pick(l,
		$"Kumanda artık fare ve klavye; oyunlar giriş almıyor.",
		$"The controller is now a mouse and keyboard; games get no input.");

	public static string DesktopOffBody(Language? l = null) => Pick(l,
		$"Kumanda yeniden oyunlara gidiyor.",
		$"The controller goes to games again.");

	public static string DesktopOffGameBody(string game, Language? l = null) => Pick(l,
		$"{game} öne geldi; kumanda yeniden oyuna gidiyor.",
		$"{game} came to the front; the controller goes to the game again.");

	public static string TrayDesktopMode(Language? l = null) => Pick(l, $"Fare Modu", $"Mouse Mode");

	public static string ShortcutKeysDesktop(Language? l = null) => Pick(l, $"PS + Seçenekler", $"PS + Options");

	public static string ShortcutDoesDesktop(Language? l = null) => Pick(l,
		$"Fare modunu aç / kapat",
		$"Turn mouse mode on / off");

	// The mapping legend: controls, what they do.
	public static IReadOnlyList<(string Keys, string Does)> DesktopMapping(Language? l = null) =>
	[
		(Pick(l, $"Dokunmatik Yüzey", $"Touchpad"), Pick(l,
			$"Tek parmak imleci taşır; dokunma sol tık, iki parmakla dokunma sağ tık, iki parmak yukarı/aşağı kaydırır; basınca sol/sağ yarıya göre tıklar.",
			$"One finger moves the pointer; tap = left click, two-finger tap = right click, two fingers up/down scroll; pressing clicks by left/right half.")),
		(Pick(l, $"Sağ Çubuk / Sol Çubuk", $"Right Stick / Left Stick"), Pick(l, $"İmleç / kaydırma.", $"Pointer / scroll.")),
		(Pick(l, $"Çarpı / Daire", $"Cross / Circle"), Pick(l, $"Sol tık (basılı tutunca sürükler) / sağ tık.", $"Left click (hold to drag) / right click.")),
		(Pick(l, $"Kare / Üçgen", $"Square / Triangle"), Pick(l, $"Enter / ekran klavyesi.", $"Enter / on-screen keyboard.")),
		(Pick(l, $"Yön Tuşları", $"D-Pad"), Pick(l, $"Ok tuşları.", $"Arrow keys.")),
		(Pick(l, $"R1 / L1", $"R1 / L1"), Pick(l, $"Pencereler arasında ileri / geri (Alt+Tab).", $"Switch windows forward / back (Alt+Tab).")),
		(Pick(l, $"L2 / R2", $"L2 / R2"), Pick(l, $"Sesi azaltır / artırır.", $"Volume down / up.")),
		(Pick(l, $"Seçenekler / Oluştur", $"Options / Create"), Pick(l, $"Başlat menüsü / oynat-duraklat.", $"Start menu / play-pause.")),
		(Pick(l, $"L3 / R3", $"L3 / R3"), Pick(l, $"Esc / orta tık.", $"Esc / middle click.")),
	];

	public static string GyroTargetLabel(Language? l = null) => Pick(l, $"Gyro Çıkışı", $"Gyro Output");

	public static string GyroTargetHint(Language? l = null) => Pick(l,
		$"Fare seçilince gyro Windows imlecini taşır, sağ çubuğa dokunmaz; fare ve kumandayı birlikte kabul eden oyunlar için.",
		$"With Mouse, the gyro moves the Windows pointer and leaves the right stick alone; for games that take mouse and controller together.");

	public static string GyroTargetName(GyroTarget target, Language? l = null) => target switch
	{
		GyroTarget.Mouse => Pick(l, $"Fare", $"Mouse"),
		_ => Pick(l, $"Sağ Çubuk", $"Right Stick")
	};
}
