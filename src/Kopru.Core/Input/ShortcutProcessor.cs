// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Input;

// What a controller shortcut asks for.
public enum ShortcutAction
{
	PreviousProfile,
	NextProfile,
	HapticsUp,
	HapticsDown,
	PowerOff,
	ToggleSupportedMode,
	ToggleDesktopMode,
	// The controller's mic button (BridgeHostMic, not a PS combo); the app decides what it does (Audio.MicButtonRules).
	MicButton,
	// A single short PS tap with no game in front (TapRoute): bring the app's window to the front.
	OpenApp,
	// A single short PS tap while the app's window is the foreground window: hide it (like closing it).
	HideApp
}

// Where a single short PS tap (no combo) goes.
public enum PsTapRoute
{
	// Passed on to the game as a short tap (today's behaviour).
	Game,
	// Not passed on; OpenApp is raised.
	OpenApp,
	// Not passed on; HideApp is raised (the app's window is the foreground window).
	HideApp
}

// Where a tap goes and why (the reason is logged: "setting off", "game in front: <exe>", "app in front", ...).
public readonly record struct PsTapDecision(PsTapRoute Route, string Reason);

public static class PsTapRule
{
	// "PS: Open The App" off: always the game. On, with "Also In Games" (inGames) off: a game (or anything full-screen) in
	// front gets the tap as before, and the app is neither shown nor hidden; the app's own window in front is hidden again;
	// otherwise the tap opens the app. With "Also In Games" on: the app's window in front is hidden (checked first: the game
	// in front is only refreshed about once a second), and a game in front no longer keeps the app from opening.
	// gameInFront: the game's (or full-screen program's) exe, null when none.
	public static PsTapDecision Decide(bool opensApp, bool appInFront, string? gameInFront, bool inGames = false) =>
		!opensApp ? new(PsTapRoute.Game, "setting off")
		: inGames && appInFront ? new(PsTapRoute.HideApp, "app in front")
		: gameInFront != null ? (inGames
			? new(PsTapRoute.OpenApp, "game in front, opens anyway: " + gameInFront)
			: new(PsTapRoute.Game, "game in front: " + gameInFront))
		: appInFront ? new(PsTapRoute.HideApp, "app in front")
		: new(PsTapRoute.OpenApp, "no game in front");
}

// Controller shortcuts with the PS button held as a modifier: PS + D-pad left/right = previous/next profile, PS + D-pad
// up/down = game haptics level up/down, PS + Square = supported mode on/off, PS + Options = desktop mode on/off, PS + Triangle held PowerOffHold = turn the
// controller off. Works on the 63-byte input body in place, before games see it:
// - while PS is held it is hidden from the game; released without a combo, it is passed on as a short tap (TapLength),
//   so a plain PS press still reaches the game; a short one (OpenAppMaxHold) asks TapRoute first and may open the app instead;
// - a button used in a combo is hidden until it is released (even after PS is let go).
// Apply is called on the input thread only; Enabled and the Triggered handlers may be touched from any thread.
// Triggered is raised on the input thread: handlers must be short (post the work elsewhere).
//
// Body offsets (Linux hid-playstation dualsense_input_report minus the report ID): 7 = buttons[0] (low nibble the D-pad
// hat 0 up .. 7 up-left, 8 none; 0x10 square, 0x80 triangle), 9 = buttons[2] (0x01 PS).
public sealed class ShortcutProcessor
{
	public const int Buttons0 = 7, Buttons1 = 8, Buttons2 = 9;
	public const byte OptionsBit = 0x20;
	public const byte HatMask = 0x0F, HatNone = 0x08, SquareBit = 0x10, TriangleBit = 0x80, PsBit = 0x01;

	public static readonly TimeSpan PowerOffHold = TimeSpan.FromSeconds(1);
	public static readonly TimeSpan TapLength = TimeSpan.FromMilliseconds(100);
	// A PS press held longer than this is not a "single tap" for TapRoute: it goes to the game as before.
	public static readonly TimeSpan OpenAppMaxHold = TimeSpan.FromMilliseconds(600);

	private readonly TimeProvider time;
	private volatile bool enabled = true;

	private bool psHeld, comboUsed;
	private long tapUntil;
	private bool tapPending;
	private ShortcutAction? lastDirection;
	private bool lastSquare, lastTriangle;
	private bool hatMasked, squareMasked, triangleMasked;
	private bool lastOptions, optionsMasked;
	private long triangleSince, psSince;
	private bool triangleTiming, powerFired;

	public ShortcutProcessor(TimeProvider? time = null)
	{
		this.time = time ?? TimeProvider.System;
	}

	// Off: the input passes unchanged (a combo in progress is dropped).
	public bool Enabled
	{
		get => enabled;
		set => enabled = value;
	}

	public event Action<ShortcutAction>? Triggered;

	// Asked on the input thread when a single short PS tap ends (keep it cheap); null or a throw: the game gets the tap.
	public Func<PsTapDecision>? TapRoute { get; set; }

	// One line per PS press: its length, where it went and why (input thread; tests replace it).
	public Action<string> Log { get; set; } = Diagnostics.AppLog.Write;

	private bool psDownWhileOff;
	private long psDownWhileOffSince;

	// Forgets a combo in progress (a new connection).
	public void Reset()
	{
		psHeld = comboUsed = tapPending = false;
		lastDirection = null;
		lastSquare = lastTriangle = false;
		hatMasked = squareMasked = triangleMasked = false;
		lastOptions = optionsMasked = false;
		triangleTiming = powerFired = false;
	}

	public void Apply(Span<byte> body)
	{
		if (body.Length <= Buttons2)
		{
			return;
		}
		long now = time.GetTimestamp();
		bool ps = (body[Buttons2] & PsBit) != 0;
		if (!enabled)
		{
			if (psHeld || tapPending || hatMasked || squareMasked || triangleMasked || optionsMasked)
			{
				Reset();
			}
			if (ps && !psDownWhileOff)
			{
				psDownWhileOff = true;
				psDownWhileOffSince = now;
			}
			else if (!ps && psDownWhileOff)
			{
				psDownWhileOff = false;
				LogTap(now - psDownWhileOffSince, "Game", "shortcuts off");
			}
			return;
		}
		psDownWhileOff = false;
		int hat = body[Buttons0] & HatMask;
		bool square = (body[Buttons0] & SquareBit) != 0;
		bool triangle = (body[Buttons0] & TriangleBit) != 0;
		bool options = (body[Buttons1] & OptionsBit) != 0;
		ShortcutAction? direction = Direction(hat);

		if (ps && !psHeld)
		{
			// Buttons already down when PS goes down do not count as a combo.
			psHeld = true;
			psSince = now;
			comboUsed = false;
			tapPending = false;
			triangleTiming = false;
		}
		else if (psHeld)
		{
			if (direction != null && direction != lastDirection)
			{
				comboUsed = hatMasked = true;
				Triggered?.Invoke(direction.Value);
			}
			if (square && !lastSquare)
			{
				comboUsed = squareMasked = true;
				Triggered?.Invoke(ShortcutAction.ToggleSupportedMode);
			}
			if (options && !lastOptions)
			{
				comboUsed = optionsMasked = true;
				Triggered?.Invoke(ShortcutAction.ToggleDesktopMode);
			}
			if (triangle && !lastTriangle)
			{
				comboUsed = triangleMasked = true;
				triangleTiming = true;
				powerFired = false;
				triangleSince = now;
			}
			if (triangle && triangleTiming && !powerFired && time.GetElapsedTime(triangleSince, now) >= PowerOffHold)
			{
				powerFired = true;
				Triggered?.Invoke(ShortcutAction.PowerOff);
			}
			if (!ps)
			{
				psHeld = false;
				triangleTiming = false;
				if (comboUsed)
				{
					LogTap(now - psSince, "combo", "modifier used");
				}
				else
				{
					PsTapDecision decision = time.GetElapsedTime(psSince, now) <= OpenAppMaxHold
						? RouteTap()
						: new(PsTapRoute.Game, $"held too long (over {OpenAppMaxHold.TotalMilliseconds:F0} ms)");
					LogTap(now - psSince, decision.Route.ToString(), decision.Reason);
					if (decision.Route == PsTapRoute.Game)
					{
						tapPending = true;
						tapUntil = now + (long)(TapLength.TotalSeconds * time.TimestampFrequency);
					}
					else if (decision.Route == PsTapRoute.OpenApp)
					{
						Triggered?.Invoke(ShortcutAction.OpenApp);
					}
					else if (decision.Route == PsTapRoute.HideApp)
					{
						Triggered?.Invoke(ShortcutAction.HideApp);
					}
				}
			}
		}
		if (!triangle)
		{
			triangleTiming = false;
		}
		lastDirection = direction;
		lastSquare = square;
		lastTriangle = triangle;
		lastOptions = options;

		if (hatMasked && hat == HatNone)
		{
			hatMasked = false;
		}
		if (squareMasked && !square)
		{
			squareMasked = false;
		}
		if (triangleMasked && !triangle)
		{
			triangleMasked = false;
		}
		if (optionsMasked && !options)
		{
			optionsMasked = false;
		}

		if (psHeld)
		{
			body[Buttons2] &= unchecked((byte)~PsBit);
		}
		else if (tapPending)
		{
			if (now < tapUntil)
			{
				body[Buttons2] |= PsBit;
			}
			else
			{
				tapPending = false;
			}
		}
		if (hatMasked)
		{
			body[Buttons0] = (byte)((body[Buttons0] & ~HatMask) | HatNone);
		}
		if (squareMasked)
		{
			body[Buttons0] &= unchecked((byte)~SquareBit);
		}
		if (triangleMasked)
		{
			body[Buttons0] &= unchecked((byte)~TriangleBit);
		}
		if (optionsMasked)
		{
			body[Buttons1] &= unchecked((byte)~OptionsBit);
		}
	}

	private PsTapDecision RouteTap()
	{
		Func<PsTapDecision>? route = TapRoute;
		if (route == null)
		{
			return new(PsTapRoute.Game, "no route");
		}
		try
		{
			return route();
		}
		catch (Exception e)
		{
			return new(PsTapRoute.Game, "route failed: " + e.GetType().Name);
		}
	}

	private void LogTap(long ticks, string route, string reason)
	{
		double ms = ticks * 1000.0 / time.TimestampFrequency;
		try
		{
			Log(string.Create(CultureInfo.InvariantCulture, $"ps tap: {ms:F0} ms -> {route} ({reason})"));
		}
		catch (Exception)
		{
			// Logging never breaks input.
		}
	}

	// Straight D-pad directions only (diagonals do nothing).
	private static ShortcutAction? Direction(int hat) => hat switch
	{
		0 => ShortcutAction.HapticsUp,
		2 => ShortcutAction.NextProfile,
		4 => ShortcutAction.HapticsDown,
		6 => ShortcutAction.PreviousProfile,
		_ => null
	};
}
