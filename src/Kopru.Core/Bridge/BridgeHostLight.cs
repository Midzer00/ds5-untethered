// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Lighting;

namespace Kopru.Core.Bridge;

// The light effect while the bridge runs: a LightSender per connection (started when the connection runs, stopped
// before it closes). The game's light wins while the profile lets it through and the game has set one.
public sealed partial class BridgeHost
{
	private LightSender? lightSender;

	// Writes the light reports of the running connection (diagnostics and tests); zero while none runs.
	public long LightReportsSent => lightSender?.Sent ?? 0;

	private void StartLightSender(Connection c)
	{
		LightSender sender = new(c.Session.UpdateOutput, () => light, () => battery, () => c.Bridge.GameControlsLight, time);
		Interlocked.Exchange(ref lightSender, sender)?.Dispose();
		sender.Start();
	}

	private void StopLightSender() => Interlocked.Exchange(ref lightSender, null)?.Dispose();

	// True when the settings change went to a running sender (it sends the change itself).
	private bool InvalidateLightSender()
	{
		LightSender? sender = lightSender;
		sender?.Invalidate();
		return sender != null;
	}
}
