// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the light effects, player LEDs and the mute LED.
public static partial class Text
{
	public static string LightModeLabel(Language? l = null) => Pick(l, $"Işık Modu", $"Light Mode");
	public static string LightModeStatic(Language? l = null) => Pick(l, $"Sabit", $"Static");
	public static string LightModeBreathing(Language? l = null) => Pick(l, $"Nefes", $"Breathing");
	public static string LightModeRainbow(Language? l = null) => Pick(l, $"Gökkuşağı", $"Rainbow");
	public static string LightModeBattery(Language? l = null) => Pick(l, $"Pil Rengi", $"Battery Colour");
	public static string LightModeGame(Language? l = null) => Pick(l, $"Oyun Kontrolünde", $"Game Controlled");

	public static string LightModeStaticHint(Language? l = null) => Pick(l,
		$"Işık çubuğu seçtiğin renkte sabit yanar.",
		$"The light bar stays on in the colour you choose.");

	public static string LightModeBreathingHint(Language? l = null) => Pick(l,
		$"Seçtiğin renk yavaşça kısılır ve yeniden parlar.",
		$"Your colour slowly dims and brightens again.");

	public static string LightModeRainbowHint(Language? l = null) => Pick(l,
		$"Işık çubuğu renk çemberinde yavaşça döner.",
		$"The light bar slowly cycles through the colour wheel.");

	public static string LightModeBatteryHint(Language? l = null) => Pick(l,
		$"Yeşil, turuncu ve kırmızı pil seviyesini gösterir; %10'un altında kırmızı yanıp söner.",
		$"Green, amber and red show the battery level; under 10% red pulses.");

	public static string LightModeGameHint(Language? l = null) => Pick(l,
		$"Desteklenen bir oyun kendi rengini gönderdiğinde o renk kullanılır; o zamana kadar seçtiğin renk yanar.",
		$"When a supported game sends its own colour, that colour is used; until then your colour is shown.");

	public static string BreathingPeriodLabel(Language? l = null) => Pick(l, $"Nefes Süresi", $"Breathing Period");
	public static string BreathingMinLabel(Language? l = null) => Pick(l, $"En Düşük Parlaklık", $"Lowest Brightness");
	public static string BreathingMaxLabel(Language? l = null) => Pick(l, $"En Yüksek Parlaklık", $"Highest Brightness");
	public static string RainbowSpeedLabel(Language? l = null) => Pick(l, $"Hız", $"Speed");
	public static string SecondsValue(double seconds, Language? l = null) => Pick(l, $"{seconds:0.#} sn", $"{seconds:0.#} s");
	public static string SpeedValue(double speed, Language? l = null) => Pick(l, $"{speed:F0} / 10", $"{speed:F0} of 10");

	public static string PlayerLedsPatternHint(Language? l = null) => Pick(l,
		$"Dokunmatik yüzeyin altındaki LED'ler: oyuncu numarası ya da hepsi.",
		$"The LEDs below the touchpad: a player number or all of them.");

	public static string PlayerLedsOff(Language? l = null) => Pick(l, $"Kapalı", $"Off");
	public static string PlayerLedsAll(Language? l = null) => Pick(l, $"Tümü", $"All");
	public static string LedBrightnessLabel(Language? l = null) => Pick(l, $"LED Parlaklığı", $"LED Brightness");
	public static string LedBrightnessLow(Language? l = null) => Pick(l, $"Düşük", $"Low");
	public static string LedBrightnessMedium(Language? l = null) => Pick(l, $"Orta", $"Medium");
	public static string LedBrightnessHigh(Language? l = null) => Pick(l, $"Yüksek", $"High");
}
