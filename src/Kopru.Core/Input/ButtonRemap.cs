// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Input;

// The remappable DualSense buttons (PS is never remapped). None as a target turns the button off.
public enum DsButton
{
	None,
	Cross,
	Circle,
	Square,
	Triangle,
	DpadUp,
	DpadDown,
	DpadLeft,
	DpadRight,
	L1,
	R1,
	L2,
	R2,
	L3,
	R3,
	Create,
	Options,
	Touchpad,
	Mute,
	// DualSense Edge back paddles (sources only; see EdgePaddleRemap).
	LeftPaddle,
	RightPaddle
}

// Button targets (physical button -> what the game sees), stick swap and per-axis inversion. Buttons missing from Map
// keep their own function. Defaults change nothing.
public sealed record RemapSettings
{
	public static RemapSettings Default { get; } = new();

	public Dictionary<DsButton, DsButton> Map { get; init; } = new();

	public bool SwapSticks { get; init; }

	public bool InvertLeftX { get; init; }

	public bool InvertLeftY { get; init; }

	public bool InvertRightX { get; init; }

	public bool InvertRightY { get; init; }

	// A DualSense Edge is presented to games as an Edge (its own identity, paddles passed through); off: as a plain
	// DualSense, where unmapped paddles do nothing.
	public bool PresentAsEdge { get; init; } = true;

	// What a button does with no entry in Map.
	public DsButton DefaultTargetOf(DsButton physical) => ButtonRemap.IsPaddle(physical) && !PresentAsEdge ? DsButton.None : physical;

	public DsButton TargetOf(DsButton physical) =>
		physical != DsButton.None && Map.TryGetValue(physical, out DsButton t) ? t : DefaultTargetOf(physical);

	public RemapSettings With(DsButton physical, DsButton target)
	{
		var map = new Dictionary<DsButton, DsButton>(Map);
		if (target == DefaultTargetOf(physical))
			map.Remove(physical);
		else
			map[physical] = target;
		return this with { Map = map };
	}

	public bool ButtonsAreIdentity => ButtonRemap.Physical.All(b => TargetOf(b) == b);

	public bool SticksAreIdentity => !SwapSticks && !InvertLeftX && !InvertLeftY && !InvertRightX && !InvertRightY;

	public bool PaddlesAreIdentity => ButtonRemap.Paddles.All(b => TargetOf(b) == b);

	public bool IsIdentity => ButtonsAreIdentity && PaddlesAreIdentity && SticksAreIdentity;

	public bool Equals(RemapSettings? other) =>
		other is not null && SwapSticks == other.SwapSticks && InvertLeftX == other.InvertLeftX && InvertLeftY == other.InvertLeftY
		&& InvertRightX == other.InvertRightX && InvertRightY == other.InvertRightY
		&& PresentAsEdge == other.PresentAsEdge
		&& ButtonRemap.Physical.Concat(ButtonRemap.Paddles).All(b => TargetOf(b) == other.TargetOf(b));

	public override int GetHashCode()
	{
		var h = new HashCode();
		h.Add(SwapSticks); h.Add(InvertLeftX); h.Add(InvertLeftY); h.Add(InvertRightX); h.Add(InvertRightY);
		h.Add(PresentAsEdge);
		foreach (DsButton b in ButtonRemap.Physical.Concat(ButtonRemap.Paddles))
			h.Add(TargetOf(b));
		return h.ToHashCode();
	}
}

// Rewrites buttons and sticks of the 63-byte input body in place. Immutable after construction; Apply allocates nothing.
//
// Body layout (public DualSense layout; Linux drivers/hid/hid-playstation.c, struct dualsense_input_report and the
// DS_BUTTONS* defines, minus the report ID byte):
//   0 LX, 1 LY, 2 RX, 3 RY (0..255, center ~128; Y grows downwards), 4 L2 analog, 5 R2 analog, 6 sequence,
//   7 buttons[0]: low nibble = D-pad hat (0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW, 8 released),
//                 0x10 Square, 0x20 Cross, 0x40 Circle, 0x80 Triangle
//   8 buttons[1]: 0x01 L1, 0x02 R1, 0x04 L2, 0x08 R2, 0x10 Create, 0x20 Options, 0x40 L3, 0x80 R3
//   9 buttons[2]: 0x01 PS (never touched), 0x02 touchpad click, 0x04 mute
// L2/R2 carry an analog value as well: a trigger moved to another button keeps its analog value only when the target
// is the other trigger; a digital button moved onto a trigger reads as a fully pressed trigger (255).
public sealed class ButtonRemap
{
	public const int Buttons0 = 7, Buttons1 = 8, Buttons2 = 9;
	public const byte HatMask = 0x0F, HatReleased = 8;

	// The physical buttons, in display order.
	public static IReadOnlyList<DsButton> Physical { get; } =
	[
		DsButton.Cross, DsButton.Circle, DsButton.Square, DsButton.Triangle,
		DsButton.DpadUp, DsButton.DpadDown, DsButton.DpadLeft, DsButton.DpadRight,
		DsButton.L1, DsButton.R1, DsButton.L2, DsButton.R2, DsButton.L3, DsButton.R3,
		DsButton.Create, DsButton.Options, DsButton.Touchpad, DsButton.Mute
	];

	// The DualSense Edge back paddles (remappable sources, never targets).
	public static IReadOnlyList<DsButton> Paddles { get; } = [DsButton.LeftPaddle, DsButton.RightPaddle];

	public static bool IsPaddle(DsButton b) => b is DsButton.LeftPaddle or DsButton.RightPaddle;

	// The standard buttons (the paddles are handled by EdgePaddleRemap).
	private const int Count = (int)DsButton.Mute + 1;

	// Hat value -> up/right/down/left bits (bit 0 up, 1 right, 2 down, 3 left).
	private static readonly byte[] HatToDirs = [0b0001, 0b0011, 0b0010, 0b0110, 0b0100, 0b1100, 0b1000, 0b1001];

	// Direction bits -> hat value (opposite directions cancel).
	private static readonly byte[] DirsToHat = BuildDirsToHat();

	private readonly DsButton[] targets = new DsButton[Count];
	private readonly bool buttonsIdentity;
	private readonly bool paddlesIdentity;
	private readonly DsButton leftPaddleTarget, rightPaddleTarget;
	private readonly RemapSettings settings;

	public ButtonRemap(RemapSettings settings)
	{
		this.settings = settings;
		for (int i = 0; i < Count; i++)
			targets[i] = settings.TargetOf((DsButton)i);
		buttonsIdentity = settings.ButtonsAreIdentity;
		paddlesIdentity = settings.PaddlesAreIdentity;
		leftPaddleTarget = settings.TargetOf(DsButton.LeftPaddle);
		rightPaddleTarget = settings.TargetOf(DsButton.RightPaddle);
		IsIdentity = settings.IsIdentity;
	}

	public RemapSettings Settings => settings;

	public bool IsIdentity { get; }

	// Bit position and byte of each button (D-pad directions are decoded from the hat).
	public static (int Offset, byte Bit) Location(DsButton b) => b switch
	{
		DsButton.Square => (Buttons0, 0x10),
		DsButton.Cross => (Buttons0, 0x20),
		DsButton.Circle => (Buttons0, 0x40),
		DsButton.Triangle => (Buttons0, 0x80),
		DsButton.L1 => (Buttons1, 0x01),
		DsButton.R1 => (Buttons1, 0x02),
		DsButton.L2 => (Buttons1, 0x04),
		DsButton.R2 => (Buttons1, 0x08),
		DsButton.Create => (Buttons1, 0x10),
		DsButton.Options => (Buttons1, 0x20),
		DsButton.L3 => (Buttons1, 0x40),
		DsButton.R3 => (Buttons1, 0x80),
		DsButton.Touchpad => (Buttons2, 0x02),
		DsButton.Mute => (Buttons2, 0x04),
		DsButton.LeftPaddle => (Buttons2, EdgePaddleRemap.LeftBit),
		DsButton.RightPaddle => (Buttons2, EdgePaddleRemap.RightBit),
		_ => (-1, 0)
	};

	public static bool IsPressed(ReadOnlySpan<byte> body, DsButton b)
	{
		if (body.Length <= Buttons2)
			return false;
		int hat = body[Buttons0] & HatMask;
		byte dirs = hat < 8 ? HatToDirs[hat] : (byte)0;
		return b switch
		{
			DsButton.None => false,
			DsButton.DpadUp => (dirs & 1) != 0,
			DsButton.DpadRight => (dirs & 2) != 0,
			DsButton.DpadDown => (dirs & 4) != 0,
			DsButton.DpadLeft => (dirs & 8) != 0,
			_ => (body[Location(b).Offset] & Location(b).Bit) != 0
		};
	}

	public void Apply(Span<byte> body)
	{
		if (IsIdentity || body.Length <= Buttons2)
			return;
		ApplySticks(body);
		if (!buttonsIdentity)
			ApplyButtons(body);
		if (!paddlesIdentity)
			EdgePaddleRemap.Apply(body, leftPaddleTarget, rightPaddleTarget);
	}

	// D-pad hat value <-> direction bits (bit 0 up, 1 right, 2 down, 3 left), for EdgePaddleRemap.
	internal static byte DirectionsOf(int hat) => hat is >= 0 and < 8 ? HatToDirs[hat] : (byte)0;

	internal static byte HatOf(int directions) => DirsToHat[directions & 0x0F];

	private void ApplySticks(Span<byte> body)
	{
		if (settings.SwapSticks)
		{
			(body[0], body[2]) = (body[2], body[0]);
			(body[1], body[3]) = (body[3], body[1]);
		}
		if (settings.InvertLeftX) body[0] = (byte)(255 - body[0]);
		if (settings.InvertLeftY) body[1] = (byte)(255 - body[1]);
		if (settings.InvertRightX) body[2] = (byte)(255 - body[2]);
		if (settings.InvertRightY) body[3] = (byte)(255 - body[3]);
	}

	private void ApplyButtons(Span<byte> body)
	{
		byte l2In = body[4], r2In = body[5];
		int hat = body[Buttons0] & HatMask;
		byte dirsIn = hat < 8 ? HatToDirs[hat] : (byte)0;
		byte b0 = (byte)(body[Buttons0] & 0xF0), b1 = body[Buttons1], b2 = body[Buttons2];

		byte o0 = 0, o1 = 0, o2 = (byte)(b2 & ~0x06), dirsOut = 0;
		int l2Out = 0, r2Out = 0;
		for (int i = 1; i < Count; i++)
		{
			var src = (DsButton)i;
			bool pressed = src switch
			{
				DsButton.DpadUp => (dirsIn & 1) != 0,
				DsButton.DpadRight => (dirsIn & 2) != 0,
				DsButton.DpadDown => (dirsIn & 4) != 0,
				DsButton.DpadLeft => (dirsIn & 8) != 0,
				_ => ((Location(src).Offset switch { Buttons0 => b0, Buttons1 => b1, _ => b2 }) & Location(src).Bit) != 0
			};
			// Triggers carry their analog value; any other button counts as 0 or 255.
			byte analog = src == DsButton.L2 ? l2In : src == DsButton.R2 ? r2In : pressed ? (byte)255 : (byte)0;
			if (!pressed && analog == 0)
				continue;
			DsButton dst = targets[i];
			if (dst == DsButton.L2)
				l2Out = Math.Max(l2Out, analog);
			else if (dst == DsButton.R2)
				r2Out = Math.Max(r2Out, analog);
			if (!pressed)
				continue;
			switch (dst)
			{
				case DsButton.None: break;
				case DsButton.DpadUp: dirsOut |= 1; break;
				case DsButton.DpadRight: dirsOut |= 2; break;
				case DsButton.DpadDown: dirsOut |= 4; break;
				case DsButton.DpadLeft: dirsOut |= 8; break;
				default:
					(int off, byte bit) = Location(dst);
					if (off == Buttons0) o0 |= bit;
					else if (off == Buttons1) o1 |= bit;
					else o2 |= bit;
					break;
			}
		}
		body[Buttons0] = (byte)(o0 | DirsToHat[dirsOut]);
		body[Buttons1] = o1;
		body[Buttons2] = o2;
		body[4] = (byte)l2Out;
		body[5] = (byte)r2Out;
	}

	private static byte[] BuildDirsToHat()
	{
		var map = new byte[16];
		for (int d = 0; d < 16; d++)
		{
			int dirs = d;
			if ((dirs & 0b0101) == 0b0101) dirs &= ~0b0101;
			if ((dirs & 0b1010) == 0b1010) dirs &= ~0b1010;
			int hat = Array.IndexOf(HatToDirs, (byte)dirs);
			map[d] = hat < 0 ? HatReleased : (byte)hat;
		}
		return map;
	}
}
