// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Mods;

namespace Kopru.Core.Localization;

// DSX mod support (Settings, General group). "DSX" only names the mods this is compatible with.
public static partial class Text
{
	public static string DsxGroup(Language? l = null) => Pick(l, $"Oyun Modları", $"Game Mods");

	public static string DsxModSupport(Language? l = null) => Pick(l, $"DSX Mod Desteği", $"DSX Mod Support");

	public static string DsxModSupportShort(Language? l = null) => Pick(l,
		$"DSX için yapılmış oyun modlarının tetik ve ışık komutlarını alır.",
		$"Takes trigger and light commands from game mods made for DSX.");

	public static string DsxModSupportHint(Language? l = null) => Pick(l,
		$"DSX için yapılmış oyun modları tetik efektlerini, ışık çubuğunu ve oyuncu ışıklarını bu bilgisayarın içinden gönderir; bu ayar açıkken kumandana ulaşır. Mod sıfırlayınca ya da kapanınca her şey eski haline döner. DSX'in kendisi açıksa aynı anda çalışmaz.",
		$"Game mods made for DSX send trigger effects, the light bar and the player lights from inside this PC; with this on they reach your controller. When the mod resets or closes, everything goes back. It does not work while DSX itself is running.");

	// The one-line state under the row while the setting is on (secondsAgo: since a mod's last message, null: none yet).
	public static string DsxStatus(DsxListenerState state, int port, int? secondsAgo, Language? l = null)
	{
		if (state == DsxListenerState.PortInUse)
		{
			return Pick(l, $"Port {port} kullanımda (DSX açık olabilir); dinlenmiyor.", $"Port {port} is in use (DSX may be running); not listening.");
		}
		if (state == DsxListenerState.Failed)
		{
			return Pick(l, $"Port {port} açılamadı; dinlenmiyor.", $"Could not open port {port}; not listening.");
		}
		if (state == DsxListenerState.Off)
		{
			return Pick(l, $"Kapalı.", $"Off.");
		}
		if (secondsAgo is { } ago)
		{
			int seconds = Math.Max(0, ago);
			return seconds < 60
				? Pick(l, $"Port {port} dinleniyor; son mod mesajı {seconds} sn önce.", $"Listening on port {port}; last message from a mod {seconds} s ago.")
				: Pick(l, $"Port {port} dinleniyor; son mod mesajı {seconds / 60} dk önce.", $"Listening on port {port}; last message from a mod {seconds / 60} min ago.");
		}
		return Pick(l, $"Port {port} dinleniyor.", $"Listening on port {port}.");
	}
}
