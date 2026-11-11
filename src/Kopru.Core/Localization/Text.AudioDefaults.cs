// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Keeping the Windows default speaker and microphone when the virtual controller starts (Settings, microphone group).
public static partial class Text
{
	public static string ProtectDefaultAudio(Language? l = null) => Pick(l, $"Varsayılan Ses Aygıtımı Koru", $"Keep My Default Audio Device");

	public static string ProtectDefaultAudioHint(Language? l = null) => Pick(l,
		$"Sanal kumanda başlayınca Windows bilgisayarın hoparlörünü ya da mikrofonunu kendiliğinden kumandaya çevirirse eskisine geri alınır. Kumandayı Windows ses ayarlarından bilerek varsayılan yaparsan buna dokunulmaz.",
		$"If Windows switches your PC's speaker or microphone to the controller by itself when the virtual controller starts, the previous one is put back. If you make the controller the default on purpose in the Windows sound settings, that is left alone.");
}
