// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// "Restart The Computer" on the setup page's drivers step (shown while Windows reports a pending restart).
public static partial class Text
{
	public static string SetupRestartButton(Language? l = null) => T(l, "Setup.RestartButton");

	public static string SetupRestartQuestion(Language? l = null) => T(l, "Setup.RestartQuestion");

	public static string SetupRestartConfirm(Language? l = null) => T(l, "Setup.RestartConfirm");

	public static string SetupRestartCancel(Language? l = null) => T(l, "Setup.RestartCancel");

	public static string SetupRestartFailed(Language? l = null) => T(l, "Setup.RestartFailed");
}
