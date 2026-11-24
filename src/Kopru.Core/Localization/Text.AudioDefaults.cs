// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Keeping the Windows default speaker and microphone when the virtual controller starts (Settings, microphone group).
public static partial class Text
{
	public static string ProtectDefaultAudio(Language? l = null) => T(l, "Protect.DefaultAudio");

	public static string ProtectDefaultAudioHint(Language? l = null) => T(l, "Protect.DefaultAudioHint");
}
