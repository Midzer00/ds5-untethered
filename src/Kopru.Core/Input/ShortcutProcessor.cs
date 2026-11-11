// SPDX-License-Identifier: GPL-3.0-or-later
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
	OpenApp
}

// Where a single short PS tap (no combo) goes.
public enum PsTapRoute
{
	// Passed on to the game as a short tap (today's behaviour).
	Game,
	// Not passed on; OpenApp is raised.
	OpenApp,
	// Not passed on and nothing happens (the app's window is already in front).
	Nothing
}

public static class PsTapRule
{
	// "PS: Open The App" on: the app's window already in front swallows the tap; a game (or anything full-screen) in front
	// gets it as before; otherwise the tap opens the app. Off: always the game.
	public static PsTapRoute Decide(bool opensApp, bool appInFront, bool gameInFront) =>
		!opensApp ? PsTapRoute.Game
		: appInFront ? PsTapRoute.Nothing
		: gameInFront ? PsTapRoute.Game
		: PsTapRoute.OpenApp;
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
	public Func<PsTapRoute>? TapRoute { get; set; }

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
		if (!enabled)
		{
			if (psHeld || tapPending || hatMasked || squareMasked || triangleMasked || optionsMasked)
			{
				Reset();
			}
			return;
		}
		long now = time.GetTimestamp();
		bool ps = (body[Buttons2] & PsBit) != 0;
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
				if (!comboUsed)
				{
					PsTapRoute route = time.GetElapsedTime(psSince, now) <= OpenAppMaxHold ? RouteTap() : PsTapRoute.Game;
					if (route == PsTapRoute.Game)
					{
						tapPending = true;
						tapUntil = now + (long)(TapLength.TotalSeconds * time.TimestampFrequency);
					}
					else if (route == PsTapRoute.OpenApp)
					{
						Triggered?.Invoke(ShortcutAction.OpenApp);
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

	private PsTapRoute RouteTap()
	{
		try
		{
			return TapRoute?.Invoke() ?? PsTapRoute.Game;
		}
		catch (Exception)
		{
			return PsTapRoute.Game;
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
