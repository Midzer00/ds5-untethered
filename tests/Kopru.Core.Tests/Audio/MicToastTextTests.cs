// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Kopru.Core.Localization;

namespace Kopru.Core.Tests;

// The mic button's toast for every outcome of a press, per button mode: "Kapat" (TurnOff) says on / off, "Sessize Al"
// (Mute) says on / muted; a PC microphone is named as such (never as the controller's); Low Power says why nothing changed.
public class MicToastTextTests
{
	[Theory]
	// Controller microphone, "Kapat".
	[InlineData(true, MicButtonMode.TurnOff, false, false, MicPress.ControllerOn, "Mikrofon Açık", "Kumanda Mikrofonu", "Microphone On", "Controller Microphone")]
	[InlineData(true, MicButtonMode.TurnOff, true, false, MicPress.ControllerOff, "Mikrofon Kapalı", "Kumanda Mikrofonu", "Microphone Off", "Controller Microphone")]
	// Controller microphone, "Sessize Al" (a first press while off turns it on).
	[InlineData(true, MicButtonMode.Mute, false, false, MicPress.ControllerOn, "Mikrofon Açık", "Kumanda Mikrofonu", "Microphone On", "Controller Microphone")]
	[InlineData(true, MicButtonMode.Mute, true, false, MicPress.ControllerMuted, "Mikrofon Sessizde", "Kumanda Mikrofonu", "Microphone Muted", "Controller Microphone")]
	[InlineData(true, MicButtonMode.Mute, true, true, MicPress.ControllerUnmuted, "Mikrofon Açık", "Kumanda Mikrofonu", "Microphone On", "Controller Microphone")]
	// PC microphone: muted / unmuted in both modes, worded by the mode.
	[InlineData(false, MicButtonMode.TurnOff, false, false, MicPress.PcMuted, "Mikrofon Kapalı", "Bilgisayar Mikrofonu", "Microphone Off", "PC Microphone")]
	[InlineData(false, MicButtonMode.Mute, false, false, MicPress.PcMuted, "Mikrofon Sessizde", "Bilgisayar Mikrofonu", "Microphone Muted", "PC Microphone")]
	public void EveryOutcome_HasItsModeSpecificText(bool controller, MicButtonMode mode, bool micEnabled, bool micMuted, MicPress press,
		string titleTr, string bodyTr, string titleEn, string bodyEn)
	{
		Assert.Equal(press, MicButtonRules.Press(controller, mode, micEnabled, micMuted, pcMuted: false, lowPower: false));
		AssertTexts(press, mode, titleTr, bodyTr, titleEn, bodyEn);
	}

	[Theory]
	[InlineData(MicButtonMode.TurnOff)]
	[InlineData(MicButtonMode.Mute)]
	public void PcUnmuted_AndLowPower(MicButtonMode mode)
	{
		Assert.Equal(MicPress.PcUnmuted, MicButtonRules.Press(false, mode, false, false, pcMuted: true, lowPower: false));
		AssertTexts(MicPress.PcUnmuted, mode, "Mikrofon Açık", "Bilgisayar Mikrofonu", "Microphone On", "PC Microphone");
		// Nothing to act on: Low Power holds the controller microphone off.
		Assert.Equal(MicPress.Ignored, MicButtonRules.Press(true, mode, false, false, null, lowPower: true));
		AssertTexts(MicPress.Ignored, mode, "Mikrofon Kapalı", "Düşük Güç Açık", "Microphone Off", "Low Power On");
	}

	[Fact]
	public void TheTwoModes_ShowDifferentTexts_ForTheSamePcPress() =>
		Assert.NotEqual(Text.MicToastTitle(MicPress.PcMuted, MicButtonMode.TurnOff, Language.Turkish),
			Text.MicToastTitle(MicPress.PcMuted, MicButtonMode.Mute, Language.Turkish));

	private static void AssertTexts(MicPress press, MicButtonMode mode, string titleTr, string bodyTr, string titleEn, string bodyEn)
	{
		Assert.Equal(titleTr, Text.MicToastTitle(press, mode, Language.Turkish));
		Assert.Equal(bodyTr, Text.MicToastBody(press, Language.Turkish));
		Assert.Equal(titleEn, Text.MicToastTitle(press, mode, Language.English));
		Assert.Equal(bodyEn, Text.MicToastBody(press, Language.English));
	}
}
