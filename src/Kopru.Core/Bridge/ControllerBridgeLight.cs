// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// What the game does with the light (diagnostics) and its output session. A game output session starts with the first
// game report and ends after GameLightIdle without one, or when the active game ends (EndGameSession); its end also ends
// the game's hold on the light, so the user's light comes back (LightSender).
// Log lines (no addresses or paths):
//   "game light: flags1 0x04 flags2 0x02, setup 0x02, rgb 0,0,0, player leds 0x04, brightness 0x01; forwarded: rgb 0,0,0,
//    setup sent, release dropped (2 changes not logged)": the game's light fields when they change, at most once per
//    GameLightLogInterval (changes in between are counted; the latest is written when the interval is over).
//   "game light: session ended (no game output for 3 s): 812 reports, light bar in 790, light bar setup 2 times (sent 1),
//    release LEDs 0 times (sent 0), 5 light changes; the user's light is back": once per game output session.
public sealed partial class ControllerBridge
{
	public static readonly TimeSpan GameLightLogInterval = TimeSpan.FromMilliseconds(250);

	// Where the game light lines go. Tests replace it.
	public Action<string> LightLog { get; set; } = Diagnostics.AppLog.Write;

	// The light fields of a game report: flags1 (mute LED, light bar, release LEDs, player LEDs), flags2 (brightness, light
	// bar setup) and their bytes.
	private readonly record struct GameLight(byte Flags1, byte Flags2, byte Setup, byte R, byte G, byte B, byte PlayerLeds, byte Brightness)
	{
		public const byte Flags1Mask = OutputBlock.MuteLedControl | OutputBlock.LightbarControl | OutputBlock.ReleaseLeds | OutputBlock.PlayerLedControl;
		public const byte Flags2Mask = OutputBlock.LedBrightnessControl | OutputBlock.LightbarSetupControl;

		public static GameLight From(ReadOnlySpan<byte> b) => new((byte)(b[OutputBlock.Flag1] & Flags1Mask), (byte)(b[OutputBlock.Flag2] & Flags2Mask),
			b[OutputBlock.LightbarSetup], b[OutputBlock.Red], b[OutputBlock.Red + 1], b[OutputBlock.Red + 2], b[OutputBlock.PlayerLeds],
			b[OutputBlock.LedBrightness]);

		public override string ToString() => string.Create(CultureInfo.InvariantCulture,
			$"flags1 0x{Flags1:X2} flags2 0x{Flags2:X2}, setup 0x{Setup:X2}, rgb {R},{G},{B}, player leds 0x{PlayerLeds:X2}, brightness 0x{Brightness:X2}");
	}

	private readonly object lightLogGate = new();

	// Guarded by lightLogGate: the light fields of the last game report and the last line, the forwarded part of the
	// line, whether a change waits for the rate limit, changes replaced before they were written, when the last line went.
	private GameLight? lightSeen;
	private string forwardedLight = "nothing yet";
	private bool lightLogPending;
	private int lightChangesSkipped;
	private long lastLightLogAt = long.MinValue / 2;

	// The game output session (guarded by lightLogGate).
	private bool gameSession;
	private long sessionReports, sessionLight, sessionSetup, sessionSetupSent, sessionRelease, sessionReleaseSent, sessionLightChanges;

	private long gameLightWrites;

	// The game's last light (guarded by lightLogGate): an output block holding only the light bar colour, player LEDs and
	// their brightness the game set last, with their flags; null until it set one.
	private byte[]? rememberedLight;

	// Every game report: keeps the light fields it sets.
	private void RememberGameLight(ReadOnlySpan<byte> block)
	{
		byte flags1 = (byte)(block[OutputBlock.Flag1] & (OutputBlock.LightbarControl | OutputBlock.PlayerLedControl));
		byte flags2 = (byte)(block[OutputBlock.Flag2] & OutputBlock.LedBrightnessControl);
		if (flags1 == 0 && flags2 == 0)
		{
			return;
		}
		lock (lightLogGate)
		{
			byte[] light = rememberedLight ??= new byte[OutputBlock.Size];
			if ((flags1 & OutputBlock.LightbarControl) != 0)
			{
				OutputBlock.SetLightbar(light, block[OutputBlock.Red], block[OutputBlock.Red + 1], block[OutputBlock.Red + 2]);
			}
			if ((flags1 & OutputBlock.PlayerLedControl) != 0)
			{
				light[OutputBlock.Flag1] |= OutputBlock.PlayerLedControl;
				light[OutputBlock.PlayerLeds] = block[OutputBlock.PlayerLeds];
			}
			if (flags2 != 0)
			{
				light[OutputBlock.Flag2] |= OutputBlock.LedBrightnessControl;
				light[OutputBlock.LedBrightness] = block[OutputBlock.LedBrightness];
			}
		}
	}

	// Writes the game's last light to the controller while the game holds the light (after a profile change, a pad swap or
	// a reconnect, where the user's light may have been written meanwhile). True when written.
	public bool RestoreGameLight()
	{
		byte[] light;
		lock (lightLogGate)
		{
			if (rememberedLight == null || !GameControlsLight)
			{
				return false;
			}
			light = (byte[])rememberedLight.Clone();
		}
		session.UpdateOutput(b =>
		{
			if ((light[OutputBlock.Flag1] & OutputBlock.LightbarControl) != 0)
			{
				OutputBlock.SetLightbar(b, light[OutputBlock.Red], light[OutputBlock.Red + 1], light[OutputBlock.Red + 2]);
			}
			if ((light[OutputBlock.Flag1] & OutputBlock.PlayerLedControl) != 0)
			{
				b[OutputBlock.Flag1] |= OutputBlock.PlayerLedControl;
				b[OutputBlock.PlayerLeds] = light[OutputBlock.PlayerLeds];
			}
			if ((light[OutputBlock.Flag2] & OutputBlock.LedBrightnessControl) != 0)
			{
				b[OutputBlock.Flag2] |= OutputBlock.LedBrightnessControl;
				b[OutputBlock.LedBrightness] = light[OutputBlock.LedBrightness];
			}
			ClearOneShotLedCommands(b);
			ApplyAppMuteLed(b);
		});
		return true;
	}

	// The game's hold on the light, to carry into the next bridge of the same controller (pad swap, reconnect); null while
	// the game holds none.
	public GameLightCarry? CaptureGameLight()
	{
		lock (lightLogGate)
		{
			return gameSetLight && rememberedLight != null && SinceGameOutput < GameLightIdle
				? new GameLightCarry((byte[])rememberedLight.Clone(), Interlocked.Read(ref lastGameOutput))
				: null;
		}
	}

	// Takes over the previous bridge's hold: it runs out GameLightIdle after the game's last output there, as before.
	// Nothing is written here; RestoreGameLight shows it once the controller output is set up.
	public void SeedGameLight(GameLightCarry carry)
	{
		lock (lightLogGate)
		{
			rememberedLight = (byte[])carry.Light.Clone();
			Interlocked.Exchange(ref lastGameOutput, carry.LastOutput);
			gameSetLight = true;
			gameSession = true;
		}
	}

	// Game reports that reached the controller with a light bar colour or a light bar setup command: LightSender sends the
	// user's light again after a new one, even when its own frame did not change (a "light out" from a game that does not
	// hold the light, or a game hold shorter than the sender's tick).
	public long GameLightWrites => Interlocked.Read(ref gameLightWrites);

	// Every game report (repeats too): the session counters and the change of its light fields.
	private void CountGameLight(ReadOnlySpan<byte> block)
	{
		GameLight now = GameLight.From(block);
		lock (lightLogGate)
		{
			gameSession = true;
			sessionReports++;
			if ((now.Flags1 & OutputBlock.LightbarControl) != 0)
			{
				sessionLight++;
			}
			if ((now.Flags2 & OutputBlock.LightbarSetupControl) != 0)
			{
				sessionSetup++;
			}
			if ((now.Flags1 & OutputBlock.ReleaseLeds) != 0)
			{
				sessionRelease++;
			}
			if (lightSeen == now)
			{
				return;
			}
			lightSeen = now;
			sessionLightChanges++;
			if (lightLogPending)
			{
				lightChangesSkipped++;
			}
			lightLogPending = true;
		}
	}

	// A game report went out: what of its light reached the controller (outFlag1: flags1 as sent, after the mute LED).
	private void NoteForwardedLight(ReadOnlySpan<byte> game, byte[] sent, byte outFlag1)
	{
		bool setupAsked = (game[OutputBlock.Flag2] & OutputBlock.LightbarSetupControl) != 0;
		bool setupSent = (sent[OutputBlock.Flag2] & OutputBlock.LightbarSetupControl) != 0;
		bool releaseAsked = (game[OutputBlock.Flag1] & OutputBlock.ReleaseLeds) != 0;
		bool releaseSent = (outFlag1 & OutputBlock.ReleaseLeds) != 0;
		if (setupSent || (outFlag1 & OutputBlock.LightbarControl) != 0)
		{
			Interlocked.Increment(ref gameLightWrites);
		}
		lock (lightLogGate)
		{
			sessionSetupSent += setupSent ? 1 : 0;
			sessionReleaseSent += releaseSent ? 1 : 0;
			if (lightLogPending)
			{
				string rgb = (outFlag1 & OutputBlock.LightbarControl) != 0
					? string.Create(CultureInfo.InvariantCulture, $"rgb {sent[OutputBlock.Red]},{sent[OutputBlock.Red + 1]},{sent[OutputBlock.Red + 2]}")
					: "no light bar";
				string setup = !setupAsked ? "no setup" : setupSent ? "setup sent" : "setup stripped";
				string release = !releaseAsked ? "no release" : releaseSent ? "release sent" : "release dropped";
				forwardedLight = $"{rgb}, {setup}, {release}";
			}
		}
		FlushGameLightLog();
	}

	// Writes the waiting light change once GameLightLogInterval has passed since the last line (force: at once).
	private void FlushGameLightLog(bool force = false)
	{
		string line;
		lock (lightLogGate)
		{
			long now = Time.GetTimestamp();
			if (!lightLogPending || lightSeen is not { } light
				|| (!force && Time.GetElapsedTime(lastLightLogAt, now) < GameLightLogInterval))
			{
				return;
			}
			lightLogPending = false;
			lastLightLogAt = now;
			string skipped = lightChangesSkipped > 0 ? $" ({lightChangesSkipped} changes not logged)" : string.Empty;
			lightChangesSkipped = 0;
			line = $"game light{LogSlot}: {light}; forwarded: {forwardedLight}{skipped}";
		}
		LightLog(line);
	}

	// The monitor's pass (BridgeHost, a few times per second): ends the game output session after GameLightIdle without
	// game output.
	public void CheckGameOutputIdle()
	{
		TimeSpan idle = GameLightIdle;
		if (Volatile.Read(ref gameSession) && SinceGameOutput >= idle)
		{
			EndGameSession(string.Create(CultureInfo.InvariantCulture, $"no game output for {idle.TotalSeconds:0.#} s"));
		}
	}

	// Ends the game output session (no game output for a while, or the active game ended): the game's hold on the light
	// ends, so the user's light comes back; one summary line. A game that writes its light again takes it back at once.
	public void EndGameSession(string why)
	{
		bool held = GameControlsLight || (LightFromGame && gameSetLight);
		gameSetLight = false;
		FlushGameLightLog(force: true);
		string line;
		lock (lightLogGate)
		{
			if (!gameSession)
			{
				return;
			}
			line = string.Create(CultureInfo.InvariantCulture,
				$"game light{LogSlot}: session ended ({why}): {sessionReports} reports, light bar in {sessionLight}, light bar setup {sessionSetup} times (sent {sessionSetupSent}), release LEDs {sessionRelease} times (sent {sessionReleaseSent}), {sessionLightChanges} light changes{(held ? "; the user's light is back" : string.Empty)}");
			gameSession = false;
			sessionReports = sessionLight = sessionSetup = sessionSetupSent = sessionRelease = sessionReleaseSent = sessionLightChanges = 0;
			lightSeen = null;
			rememberedLight = null;
			lightLogPending = false;
			lightChangesSkipped = 0;
			forwardedLight = "nothing yet";
		}
		LightLog(line);
	}
}

// The game's light and when its output was last seen (ControllerBridge.CaptureGameLight / SeedGameLight).
public sealed record GameLightCarry(byte[] Light, long LastOutput);
