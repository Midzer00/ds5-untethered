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
		// A mod's light (DSX mod support) goes over the user's light in every write.
		LightSender sender = new(write => c.Session.UpdateOutput(b =>
		{
			write(b);
			c.Bridge.OverlayModLight(b);
		}), () => light, () => battery, () => c.Bridge.GameControlsLight, time)
		{
			GameLightWrites = () => c.Bridge.GameLightWrites
		};
		Interlocked.Exchange(ref lightSender, sender)?.Dispose();
		// A hold carried over from the last connection: the game's light again over the user's light set on connect.
		c.Bridge.RestoreGameLight();
		SyncModOutput(c.Bridge);
		sender.Start();
	}

	// No game output for this long ends the game's hold on the light (ControllerBridge.GameLightIdle); tests shorten it.
	public TimeSpan GameLightIdle { get; set; } = ControllerBridge.DefaultGameLightIdle;

	// The active game ended (GameModeController): its hold on the light ends, the user's light comes back.
	public void GameEnded()
	{
		try
		{
			current?.Bridge.EndGameSession("the active game ended");
		}
		catch (Exception)
		{
			// The controller is going away.
		}
	}

	// The active game exited with no other game taking over (GameModeController): its last rumble stops, trigger rumble too.
	public void GameExited()
	{
		try
		{
			current?.Bridge.StopAllRumble("the active game exited");
		}
		catch (Exception)
		{
			// The controller is going away.
		}
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
