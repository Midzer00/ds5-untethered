// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of desktop mode (the controller drives Windows) and gyro to mouse.
public static partial class Text
{
	public static string DesktopModeSection(Language? l = null) => Pick(l, $"Fare Modu", $"Mouse Mode");

	public static string DesktopModeSwitch(Language? l = null) => Pick(l, $"Fare Modu", $"Mouse Mode");

	public static string DesktopModeHint(Language? l = null) => Pick(l,
		$"Açıkken kumanda Windows'ta fare ve klavye olur; bu sırada oyunlar kumandadan giriş almaz. PS + Seçenekler ile de açılıp kapanır; PS ile uygulamayı açınca ya da uygulama öndeyken touchpad'e iki kez basınca da açılır.",
		$"While on, the controller is a mouse and keyboard in Windows; games get no input from it meanwhile. PS + Options turns it on and off too; opening the app with PS or pressing the touchpad twice while the app is in front turns it on.");

	public static string DesktopAutoOff(Language? l = null) => Pick(l, $"Oyun Açılınca Kapat", $"Turn Off When a Game Starts");

	public static string DesktopAutoOffHint(Language? l = null) => Pick(l,
		$"Listedeki bir oyun öne gelince ya da öndeki oyun kumandayı kullanınca (titreşim, tetik efekti) fare modu kendiliğinden kapanır.",
		$"Mouse mode turns itself off when a game from the list comes to the front or the game in front uses the controller (vibration, trigger effects).");

	public static string DesktopPointerSpeed(Language? l = null) => Pick(l, $"İmleç Hızı", $"Pointer Speed");

	public static string DesktopPointerAcceleration(Language? l = null) => Pick(l, $"İmleç İvmesi", $"Pointer Acceleration");

	public static string DesktopTouchpadSpeed(Language? l = null) => Pick(l, $"Dokunmatik Yüzey Hızı", $"Touchpad Speed");

	public static string DesktopScrollSpeed(Language? l = null) => Pick(l, $"Kaydırma Hızı", $"Scroll Speed");

	public static string DesktopOnTitle(Language? l = null) => Pick(l, $"Fare Modu Açık", $"Mouse Mode On");

	public static string DesktopOffTitle(Language? l = null) => Pick(l, $"Fare Modu Kapalı", $"Mouse Mode Off");

	public static string DesktopOnBody(Language? l = null) => Pick(l,
		$"Kumanda fare oldu.",
		$"The controller is a mouse now.");

	public static string DesktopOffBody(Language? l = null) => Pick(l,
		$"Kumanda oyunlara döndü.",
		$"Controller is back in games.");

	// Mouse mode switched itself off (the toast's title when the game's name is not known).
	public static string DesktopAutoOffTitle(Language? l = null) => Pick(l, $"Fare Modu Kapandı", $"Mouse Mode Off");

	// The toast under the game's name: Mouse Mode switched itself off for that game.
	public static string DesktopOffGameBody(Language? l = null) => Pick(l,
		$"Fare Modu kapandı, kumanda oyunda.",
		$"Mouse Mode off, controller in game.");

	// Mouse mode switched itself off for a game that started using the controller (its name not known).
	public static string DesktopOffGameStartedBody(Language? l = null) => Pick(l,
		$"Kumanda oyuna geçti.",
		$"Controller is back in the game.");

	public static string TrayDesktopMode(Language? l = null) => Pick(l, $"Fare Modu", $"Mouse Mode");

	public static string ShortcutKeysDesktop(Language? l = null) => Pick(l, $"PS + Seçenekler", $"PS + Options");

	public static string ShortcutDoesDesktop(Language? l = null) => Pick(l,
		$"Fare modunu aç / kapat",
		$"Turn mouse mode on / off");

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
