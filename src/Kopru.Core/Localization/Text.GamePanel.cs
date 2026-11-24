// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of a game's panel on the Games page.
public static partial class Text
{
	// Where a catalog game comes from: the supported list.
	public static string GameListed(Language? l = null) => T(l, "Game.Listed");

	public static string GamePanelClose(Language? l = null) => T(l, "Game.PanelClose");

	// The panel's own close (an icon button): this is its tooltip and accessible name.
	public static string GamePanelCloseTip(Language? l = null) => T(l, "Game.PanelCloseTip");

	// The state chip under the game's name.
	public static string GameStateRunning(Language? l = null) => T(l, "Game.StateRunning");

	public static string GameStateNotRunning(Language? l = null) => T(l, "Game.StateNotRunning");

	// The Game tab's chain card: the settings flow from the game to the controller.
	public static string GamePanelChainTitle(Language? l = null) => T(l, "Game.PanelChainTitle");

	// Right of the chain's title: whether the panel's settings apply now or once the game starts.
	public static string GameAppliedNow(Language? l = null) => T(l, "Game.AppliedNow");

	public static string GameAppliesOnStart(Language? l = null) => T(l, "Game.AppliesOnStart");

	// The chain's middle node: the controller type the game gets (the value beside it is the type's name).
	public static string GameSeesLabel(Language? l = null) => T(l, "Game.SeesLabel");

	// The controller node's light chip: the game sets the light, or the player's own light stays.
	public static string GameLightChip(Language? l = null) => T(l, "Game.LightChip");

	public static string OwnLightChip(Language? l = null) => T(l, "Own.LightChip");

	// The short form of "Settings are shared with other games using this profile" (ProfileSharedByGames, its tooltip).
	public static string ProfileSharedShort(Language? l = null) => T(l, "Profile.SharedShort");

	// A caption under Trigger Strength: the setting covers the game's own trigger effects.
	public static string GameEffectsShort(Language? l = null) => T(l, "Game.EffectsShort");

	// Try's caption in the Triggers tab's footer (the full sentence stays the button's tooltip, CustomTriggerTryHint).
	public static string CustomTriggerTryShort(Language? l = null) => T(l, "Custom.TriggerTryShort");

	// The sound-to-haptics plot: its words and the sentence under it.
	public static string GatePlotSound(Language? l = null) => T(l, "Gate.PlotSound");

	public static string GatePlotThreshold(Language? l = null) => T(l, "Gate.PlotThreshold");

	public static string GatePlotHaptics(Language? l = null) => T(l, "Gate.PlotHaptics");

	public static string GatePlotCaption(Language? l = null) => T(l, "Gate.PlotCaption");

	public static string GameUsesDefaultProfile(Language? l = null) => T(l, "Game.UsesDefaultProfile");

	public static string GameOwnSettings(Language? l = null) => T(l, "Game.OwnSettings");

	public static string GameOwnSettingsHint(Language? l = null) => T(l, "Game.OwnSettingsHint");

	public static string GameResetToDefault(Language? l = null) => T(l, "Game.ResetToDefault");

	public static string GameResetToDefaultHint(Language? l = null) => T(l, "Game.ResetToDefaultHint");

	// The file "Oyna" starts, chosen by the user.
	public static string StartFileChoose(Language? l = null) => T(l, "Start.FileChoose");

	public static string StartFileChange(Language? l = null) => T(l, "Start.FileChange");

	public static string StartFileRemove(Language? l = null) => T(l, "Start.FileRemove");

	// file: the file's name only.
	public static string StartFileLine(string file, Language? l = null) => T(l, "Start.FileLine", A("file", file));

	public static string StartFileHint(Language? l = null) => T(l, "Start.FileHint");

	public static string StartFileTypes(Language? l = null) => T(l, "Start.FileTypes");

	// Close Game: the header button, its waiting state and the question when the game stays open.
	public static string CloseGame(Language? l = null) => T(l, "Close.Game");

	public static string ClosingGame(Language? l = null) => T(l, "Closing.Game");

	public static string ForceCloseQuestion(Language? l = null) => T(l, "Force.CloseQuestion");

	public static string ForceClose(Language? l = null) => T(l, "Force.Close");

	// The short question in Close Game's place (ForceCloseQuestion is its tooltip).
	public static string ForceCloseAsk(Language? l = null) => T(l, "Force.CloseAsk");

	// The Vibration tab's card for the classic (Xbox and DS4) rumble.
	public static string GamePanelClassicRumble(Language? l = null) => T(l, "Game.PanelClassicRumble");
}
