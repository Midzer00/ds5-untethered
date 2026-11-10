// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The controller's mic button as the microphone switch: Settings hints and the toast after a press.
public static partial class Text
{
	public static string MicButtonHint(Language? l = null) => Pick(l,
		$"Kumandanın mikrofonu oyunlarda kablolu kumandanınki gibi görünür. Kumandadaki mikrofon tuşu mikrofonu açıp kapatır; tuş turuncu yanarken mikrofon açıktır.",
		$"The controller's microphone shows up in games like the wired one. The mic button on the controller turns it on and off; while the button glows orange the microphone is on.");

	public static string MicButtonMuteWindowsHint(Language? l = null) => Pick(l,
		$"Mikrofon tuşuyla kapatınca Windows'un varsayılan mikrofonu da sessize alınır, açınca yeniden açılır",
		$"Turning the mic off with the controller's button also mutes the default Windows microphone; turning it on unmutes it");

	public static string MicButtonToastTitle(bool on, Language? l = null) => on
		? Pick(l, $"Mikrofon Açık", $"Microphone On")
		: Pick(l, $"Mikrofon Kapalı", $"Microphone Off");

	public static string MicButtonToastBody(bool on, Language? l = null) => on
		? Pick(l, $"Kumandanın mikrofonu oyunlara gidiyor.", $"The controller's microphone goes to games.")
		: Pick(l, $"Kumandanın mikrofonu kapatıldı.", $"The controller's microphone is off.");
}
