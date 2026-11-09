// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Bridge;

// Tells real use of the controller from the noise in its input body: the counter, timestamps, gyro and accelerometer
// change in every report, and resting sticks wobble by a few steps. Compared: sticks (body 0-3, with a tolerance),
// triggers (4-5) and buttons (7-9).
public static class InputActivity
{
	public const int Size = 10;

	public const int StickTolerance = 12;

	// Compares the body with the last remembered one and remembers it when it changed.
	public static bool Changed(byte[] remembered, ReadOnlySpan<byte> body)
	{
		if (body.Length < Size)
		{
			return false;
		}
		bool changed = false;
		for (int i = 0; i < 4; i++)
		{
			changed |= Math.Abs(body[i] - remembered[i]) > StickTolerance;
		}
		changed |= body[4] != remembered[4] || body[5] != remembered[5];
		changed |= body[7] != remembered[7] || body[8] != remembered[8] || body[9] != remembered[9];
		if (changed)
		{
			body[..Size].CopyTo(remembered);
		}
		return changed;
	}
}
