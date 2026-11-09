// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Steam;

namespace Kopru.Core.Localization;

// User-facing texts of compatibility checks (Steam Input).
public static partial class Text
{
	public static string SteamInputAdvice(SteamInputStatus status, Language? l = null) => status switch
	{
		SteamInputStatus.NotInstalled => Pick(l,
			$"Steam kurulu değil; Steam Input çakışması yok.",
			$"Steam is not installed; no Steam Input conflict."),
		SteamInputStatus.Off => Pick(l,
			$"Steam Input kapalı; DS5 Untethered'ın kolu oyuna doğrudan ulaşır.",
			$"Steam Input is off; DS5 Untethered's controller reaches the game directly."),
		SteamInputStatus.OnGlobally => Pick(l,
			$"Steam'de PlayStation kumanda desteği açık. Steam Input kolu kendi sanal kumandasıyla değiştirebilir ve haptik ile tetik efektleri kaybolur. Oyun için kapatmak: Steam kütüphanesinde oyuna sağ tıklayın > Özellikler > Kumanda > \"Steam Input'u devre dışı bırak\".",
			$"PlayStation controller support is on in Steam. Steam Input may replace the controller with its own virtual pad, and haptics and trigger effects are lost. To turn it off for the game: in the Steam library, right-click the game > Properties > Controller > \"Disable Steam Input\"."),
		SteamInputStatus.OnForApp => Pick(l,
			$"Steam Input bu oyun için açık. Haptik ve tetik efektleri kaybolabilir. Kapatmak: Steam kütüphanesinde oyuna sağ tıklayın > Özellikler > Kumanda > \"Steam Input'u devre dışı bırak\".",
			$"Steam Input is on for this game. Haptics and trigger effects may be lost. To turn it off: in the Steam library, right-click the game > Properties > Controller > \"Disable Steam Input\"."),
		_ => Pick(l,
			$"Steam Input durumu okunamadı. Steam'den açılan oyunlarda haptik çalışmazsa oyuna sağ tıklayın > Özellikler > Kumanda > \"Steam Input'u devre dışı bırak\".",
			$"Could not read the Steam Input setting. If haptics don't work in a game launched from Steam, right-click the game > Properties > Controller > \"Disable Steam Input\".")
	};
}
