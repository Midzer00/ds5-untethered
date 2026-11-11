// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The controller's mic button as the microphone switch: Settings hints and the toast after a press.
public static partial class Text
{
	public static string MicButtonHint(Language? l = null) => Pick(l,
		$"Kumandanın mikrofonu oyunlarda kablolu kumandanınki gibi görünür. Kumandadaki mikrofon tuşu mikrofonu açıp kapatır; tuş turuncu yanarken mikrofon açıktır.",
		$"The controller's microphone shows up in games like the wired one. The mic button on the controller turns it on and off; while the button glows orange the microphone is on.");
}
