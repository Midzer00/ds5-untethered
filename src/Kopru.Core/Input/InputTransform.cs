// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Input;

// Applies remapping, an InputTuning and gyro aiming to the 63-byte input body in place, in this order:
//   1. ButtonRemap (buttons, stick swap/invert) - so the tuning below works on the sticks/triggers the game sees;
//   2. tuning (deadzones, curves, drift, trigger ranges);
//   3. gyro mix (added to the tuned right stick, clamped) - so stick deadzones never swallow small gyro motion.
// The gyro activation button is read from the body as it arrived (physical buttons, before the remap).
// Swapped as a whole when settings change; Apply allocates nothing. With gyro on, it keeps smoothing/toggle state, so
// only one thread (the bridge's input thread) may call Apply.
//
// Body offsets (same as InputState.Parse; Linux hid-playstation struct dualsense_input_report minus the report ID):
//   0 LX, 1 LY, 2 RX, 3 RY (0..255, center ~128), 4 L2 analog, 5 R2 analog,
//   8 buttons[1]: bit 0x04 = L2 digital, bit 0x08 = R2 digital (DS_BUTTONS1_L2 / DS_BUTTONS1_R2).
public sealed class InputTransform
{
	public const int LeftX = 0, LeftY = 1, RightX = 2, RightY = 3, L2Analog = 4, R2Analog = 5, Buttons1 = 8;
	public const byte L2Bit = 0x04, R2Bit = 0x08;

	private const double Center = 127.5;

	private readonly Stick left, right;
	private readonly byte[]? l2Map, r2Map;

	public InputTransform(InputTuning tuning, RemapSettings? remap = null, GyroSettings? gyro = null)
	{
		Tuning = tuning;
		Remap = new ButtonRemap(remap ?? RemapSettings.Default);
		Gyro = gyro is { IsOff: false } ? new GyroAim(gyro) : null;
		left = new Stick(tuning.LeftStick);
		right = new Stick(tuning.RightStick);
		l2Map = TriggerMap(tuning.L2);
		r2Map = TriggerMap(tuning.R2);
	}

	public static InputTransform Identity { get; } = new(InputTuning.Default);

	public InputTuning Tuning { get; }

	public ButtonRemap Remap { get; }

	public RemapSettings RemapSettings => Remap.Settings;

	// Null while gyro aiming is off.
	public GyroAim? Gyro { get; }

	public GyroSettings GyroSettings => Gyro?.Settings ?? GyroSettings.Default;

	public void Apply(Span<byte> body)
	{
		if (body.Length <= Buttons1)
			return;
		Gyro?.UpdateActivation(body);
		Remap.Apply(body);
		left.Apply(ref body[LeftX], ref body[LeftY]);
		right.Apply(ref body[RightX], ref body[RightY]);
		ApplyTrigger(body, l2Map, L2Analog, L2Bit);
		ApplyTrigger(body, r2Map, R2Analog, R2Bit);
		Gyro?.Apply(body);
	}

	private static void ApplyTrigger(Span<byte> body, byte[]? map, int offset, byte bit)
	{
		if (map is null)
			return;
		byte value = map[body[offset]];
		body[offset] = value;
		body[Buttons1] = (byte)(value > 0 ? body[Buttons1] | bit : body[Buttons1] & ~bit);
	}

	private static byte[]? TriggerMap(TriggerTuning t)
	{
		if (t.IsIdentity)
			return null;
		double start = Math.Clamp(t.Start, 0, 100) / 100 * 255;
		double end = Math.Max(Math.Clamp(t.End, 0, 100) / 100 * 255, start + 1);
		var map = new byte[256];
		for (int i = 0; i < 256; i++)
			map[i] = i <= start ? (byte)0 : (byte)Math.Round(Math.Clamp((i - start) / (end - start), 0, 1) * 255);
		return map;
	}

	private sealed class Stick
	{
		private readonly bool identity;
		private readonly double inner, edge, anti, exponent, driftX, driftY;

		public Stick(StickTuning t)
		{
			identity = t.IsIdentity;
			inner = Math.Clamp(t.InnerDeadzone, 0, 95) / 100;
			edge = Math.Max(1 - Math.Clamp(t.OuterDeadzone, 0, 95) / 100, inner + 0.01);
			anti = Math.Clamp(t.AntiDeadzone, 0, 95) / 100;
			exponent = t.EffectiveExponent;
			driftX = t.DriftX;
			driftY = t.DriftY;
		}

		public void Apply(ref byte rawX, ref byte rawY)
		{
			if (identity)
				return;
			double x = Math.Clamp((rawX - Center - driftX) / Center, -1, 1);
			double y = Math.Clamp((rawY - Center - driftY) / Center, -1, 1);
			double r = Math.Sqrt(x * x + y * y);
			if (r <= inner)
			{
				rawX = rawY = 128;
				return;
			}
			double n = Math.Min((r - inner) / (edge - inner), 1);
			if (exponent != 1.0)
				n = Math.Pow(n, exponent);
			n = anti + (1 - anti) * n;
			double scale = n / r;
			rawX = ToByte(x * scale);
			rawY = ToByte(y * scale);
		}

		private static byte ToByte(double v) =>
			(byte)Math.Clamp(Math.Round(Center + Math.Clamp(v, -1, 1) * Center, MidpointRounding.AwayFromZero), 0, 255);
	}
}
