// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// "PS: Open The App" and "Also In Games" (Settings, Controller Shortcuts group).
public static partial class Text
{
	public static string PsOpenApp(Language? l = null) => Pick(l, $"PS: Uygulamayı Aç", $"PS: Open The App");

	public static string PsOpenAppHint(Language? l = null) => Pick(l,
		$"PS tuşuna kısa basınca DS5 Untethered öne gelir, yeniden basınca saat yanındaki simgeye çekilir. Oyun açıkken ne olacağını \"Oyunda Da Aç\" belirler. Uzun basış her zaman oyuna gider. Sanal kumanda çalışırken geçerli.",
		$"A short PS press brings DS5 Untethered to the front; another press sends it back to the icon next to the clock. \"Also In Games\" decides what happens while a game is open. A long press always goes to the game. Works while the virtual controller is running.");

	public static string PsOpenAppInGames(Language? l = null) => Pick(l, $"Oyunda Da Aç", $"Also In Games");

	public static string PsOpenAppInGamesShort(Language? l = null) => Pick(l,
		$"Oyun açıkken de kısa PS uygulamayı açar.",
		$"A short PS press opens the app in games too.");

	public static string PsOpenAppInGamesHint(Language? l = null) => Pick(l,
		$"Açıkken oyun sırasında PS'ye kısa basınca uygulama öne gelir ve bu basış oyuna gitmez; yeniden basınca uygulama gizlenir ve oyun öne döner. Kapalıyken oyun açıkken PS oyuna gider.",
		$"On: in a game, a short PS press brings the app up and the press does not reach the game; another press hides the app and brings the game back. Off: in a game, PS goes to the game.");
}
