// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Mods;

public enum DsxApplyResult
{
	// Changed (or confirmed) the mod's output of the controllers it names.
	Applied,
	// GetDSXStatus: nothing to apply (the reply answers it).
	Status,
	// ResetToUserSettings: the mod's output of the controllers it names ends.
	Reset,
	// A known instruction with parameters we cannot use (missing, out of range, an unknown trigger mode or side).
	Invalid,
	// An instruction type we do not know.
	Unknown,
	// A controller index we have no slot for.
	NoController
}

// The mod's output per controller index (0 = the first controller, slot 1), built up instruction by instruction
// (docs/DSX-COMPAT.md). Not thread-safe: the listener's receive loop owns it.
public sealed class DsxModState
{
	public const int MaxControllers = 4;

	private readonly ModOutput?[] outputs = new ModOutput?[MaxControllers];

	// Null: the mod set nothing on that controller (or reset it).
	public ModOutput? this[int index] => index is >= 0 and < MaxControllers ? outputs[index] : null;

	public bool Any => outputs.Any(o => o != null);

	public void Clear() => Array.Clear(outputs);

	// Applies one instruction; `changed` gets the indices whose output it changed.
	public DsxApplyResult Apply(DsxInstruction instruction, ISet<int> changed)
	{
		if (instruction.Type is not { } type)
		{
			return DsxApplyResult.Unknown;
		}
		if (type == DsxInstructionType.GetDSXStatus)
		{
			return DsxApplyResult.Status;
		}
		int index = instruction.Index;
		if (index >= MaxControllers)
		{
			return DsxApplyResult.NoController;
		}
		IReadOnlyList<DsxParam> a = instruction.Args;
		if (type == DsxInstructionType.ResetToUserSettings)
		{
			foreach (int i in Targets(index))
			{
				if (outputs[i] != null)
				{
					outputs[i] = null;
					changed.Add(i);
				}
			}
			return DsxApplyResult.Reset;
		}
		Func<ModOutput, ModOutput>? update = type switch
		{
			DsxInstructionType.TriggerUpdate => TriggerUpdate(a),
			DsxInstructionType.RGBUpdate => RgbUpdate(a),
			DsxInstructionType.PlayerLED => PlayerLed(a),
			DsxInstructionType.PlayerLEDNewRevision => PlayerLedNewRevision(a),
			DsxInstructionType.MicLED => MicLed(a),
			DsxInstructionType.TriggerThreshold => Threshold(a),
			_ => null
		};
		if (update == null)
		{
			return DsxApplyResult.Invalid;
		}
		foreach (int i in Targets(index))
		{
			ModOutput before = outputs[i] ?? ModOutput.Empty;
			ModOutput after = update(before);
			ModOutput? stored = after.IsEmpty ? null : after;
			if (!Equals(outputs[i], stored))
			{
				outputs[i] = stored;
				changed.Add(i);
			}
		}
		return DsxApplyResult.Applied;
	}

	// A negative index means every controller.
	private static IEnumerable<int> Targets(int index) => index < 0 ? Enumerable.Range(0, MaxControllers) : [index];

	// Side: 1 = left, 2 = right (or the names).
	private static bool? IsLeft(DsxParam side) => side.Name is { } name
		? name.Trim().ToLowerInvariant() switch { "left" => true, "right" => false, _ => null }
		: side.Int switch { 1 => true, 2 => false, _ => null };

	private static Func<ModOutput, ModOutput>? TriggerUpdate(IReadOnlyList<DsxParam> a)
	{
		if (a.Count < 2 || IsLeft(a[0]) is not { } left)
		{
			return null;
		}
		DsxTriggerMode? mode = a[1].Name is { } name ? DsxProtocol.ParseName<DsxTriggerMode>(name)
			: Enum.IsDefined((DsxTriggerMode)a[1].Int) ? (DsxTriggerMode)a[1].Int : null;
		if (mode is not { } m || DsxTriggers.Effect(m, a.Skip(2).ToArray()) is not { } effect)
		{
			return null;
		}
		return left ? o => o with { LeftTrigger = effect } : o => o with { RightTrigger = effect };
	}

	// Red, green, blue 0-255 and (v3) brightness 0-255, absent = full.
	private static Func<ModOutput, ModOutput>? RgbUpdate(IReadOnlyList<DsxParam> a)
	{
		if (a.Count < 3 || a.Take(Math.Min(a.Count, 4)).Any(p => p.Int is < 0 or > 255))
		{
			return null;
		}
		int brightness = a.Count >= 4 ? a[3].Int : 255;
		byte Scale(int v) => (byte)Math.Round(v * brightness / 255.0, MidpointRounding.AwayFromZero);
		(byte, byte, byte) colour = (Scale(a[0].Int), Scale(a[1].Int), Scale(a[2].Int));
		return o => o with { Lightbar = colour };
	}

	// Five on/off values, left to right.
	private static Func<ModOutput, ModOutput>? PlayerLed(IReadOnlyList<DsxParam> a)
	{
		if (a.Count < 5)
		{
			return null;
		}
		int mask = 0;
		for (int i = 0; i < 5; i++)
		{
			mask |= a[i].Number != 0 ? 1 << i : 0;
		}
		return o => o with { PlayerLeds = (byte)mask };
	}

	// 0-4: the player 1-5 patterns; 5: all off.
	private static readonly byte[] PlayerPatterns = [0x04, 0x0A, 0x15, 0x1B, 0x1F, 0x00];

	private static Func<ModOutput, ModOutput>? PlayerLedNewRevision(IReadOnlyList<DsxParam> a)
	{
		if (a.Count < 1 || a[0].Int is < 0 or > 5)
		{
			return null;
		}
		byte mask = PlayerPatterns[a[0].Int];
		return o => o with { PlayerLeds = mask };
	}

	// 0 on, 1 pulse, 2 off (the output report's mute LED: 1 on, 2 pulse, 0 off).
	private static Func<ModOutput, ModOutput>? MicLed(IReadOnlyList<DsxParam> a)
	{
		if (a.Count < 1 || a[0].Int is < 0 or > 2)
		{
			return null;
		}
		byte value = a[0].Int switch { 0 => 1, 1 => 2, _ => 0 };
		return o => o with { MicLed = value };
	}

	// Side, point 0-255.
	private static Func<ModOutput, ModOutput>? Threshold(IReadOnlyList<DsxParam> a)
	{
		if (a.Count < 2 || IsLeft(a[0]) is not { } left || a[1].Int is < 0 or > 255)
		{
			return null;
		}
		byte point = (byte)a[1].Int;
		return left ? o => o with { LeftThreshold = point } : o => o with { RightThreshold = point };
	}
}
