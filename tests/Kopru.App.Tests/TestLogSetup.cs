// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using Kopru.Core.Diagnostics;

namespace Kopru.App.Tests;

// Keeps the bridge's log out of the real %LOCALAPPDATA%: the App tests run real BridgeHosts with fake controllers, and
// their "state ..." lines in the user's app.log once looked like a restart storm of the real app.
internal static class TestLogSetup
{
	[ModuleInitializer]
	internal static void RedirectAppLog() =>
		AppLog.Directory = Path.Combine(Path.GetTempPath(), "kopru-app-tests-log-" + Environment.ProcessId);
}
