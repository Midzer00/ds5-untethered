// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// "PS: Open The App" (Settings, Controller Shortcuts group).
public static partial class Text
{
	public static string PsOpenApp(Language? l = null) => Pick(l, $"PS: Uygulamayı Aç", $"PS: Open The App");

	public static string PsOpenAppHint(Language? l = null) => Pick(l,
		$"Oyun açık değilken PS tuşuna kısa basınca DS5 Untethered öne gelir. Oyun açıkken PS oyuna gider. Sanal kumanda çalışırken geçerli.",
		$"With no game open, a short PS press brings DS5 Untethered to the front. In a game, PS still goes to the game. Works while the virtual controller is running.");
}
