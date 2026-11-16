// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Kopru.Core.Mods;

// The UDP messages game mods made for DSX send (docs/DSX-COMPAT.md): one JSON object per datagram,
// {"instructions":[{"type":1,"parameters":[0,2,13,0,8]}, ...]}. The first parameter is the controller index.
public enum DsxInstructionType
{
	GetDSXStatus = 0,
	TriggerUpdate = 1,
	RGBUpdate = 2,
	PlayerLED = 3,
	TriggerThreshold = 4,
	MicLED = 5,
	PlayerLEDNewRevision = 6,
	ResetToUserSettings = 7
}

// TriggerUpdate's mode (its third parameter).
public enum DsxTriggerMode
{
	Normal = 0,
	GameCube = 1,
	VerySoft = 2,
	Soft = 3,
	Hard = 4,
	VeryHard = 5,
	Hardest = 6,
	Rigid = 7,
	VibrateTrigger = 8,
	Choppy = 9,
	Medium = 10,
	VibrateTriggerPulse = 11,
	CustomTriggerValue = 12,
	Resistance = 13,
	Bow = 14,
	Galloping = 15,
	SemiAutomaticGun = 16,
	AutomaticGun = 17,
	Machine = 18,
	VIBRATE_TRIGGER_10Hz = 19,
	OFF = 20,
	FEEDBACK = 21,
	WEAPON = 22,
	VIBRATION = 23,
	SLOPE_FEEDBACK = 24,
	MULTIPLE_POSITION_FEEDBACK = 25,
	MULTIPLE_POSITION_VIBRATION = 26
}

// One parameter: a number (numbers, numeric strings, booleans as 1/0, null as 0) and, for a non-numeric string, its
// text (a mode or side given by name).
public readonly record struct DsxParam(double Number, string? Name = null)
{
	public int Int => double.IsFinite(Number) ? (int)Math.Clamp(Math.Truncate(Number), int.MinValue, int.MaxValue) : 0;
}

// One instruction: its type when known (null: a type number or name we do not know, kept in TypeText), its parameters
// and a short sample of its JSON (for the "unknown instruction" log line).
public sealed record DsxInstruction(DsxInstructionType? Type, string TypeText, IReadOnlyList<DsxParam> Parameters, string Sample)
{
	public int Index => Parameters.Count > 0 ? Parameters[0].Int : 0;

	// The parameters after the controller index.
	public IReadOnlyList<DsxParam> Args => Parameters.Count > 1 ? Parameters.Skip(1).ToArray() : [];
}

public enum DsxParseError
{
	None,
	Oversized,
	Malformed
}

public sealed record DsxPacket(IReadOnlyList<DsxInstruction> Instructions, int Skipped);

public static class DsxProtocol
{
	// Larger datagrams are ignored (the biggest real message is a few hundred bytes).
	public const int MaxPacketBytes = 8192;

	// An unknown instruction's JSON is logged up to this many characters.
	public const int SampleLength = 120;

	private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

	// Parses one datagram. Lenient like the mods are varied: keys in any case, type as a number or a name, parameters null
	// or missing (empty), parameter values as numbers, numeric strings, booleans or null. An instruction without a readable
	// type is skipped (counted); the rest of the packet still counts. Never throws.
	public static (DsxPacket? Packet, DsxParseError Error) Parse(ReadOnlySpan<byte> data)
	{
		if (data.Length > MaxPacketBytes)
		{
			return (null, DsxParseError.Oversized);
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(data.ToArray(), Options);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || Property(doc.RootElement, "instructions") is not { ValueKind: JsonValueKind.Array } list)
			{
				return (null, DsxParseError.Malformed);
			}
			List<DsxInstruction> instructions = [];
			int skipped = 0;
			foreach (JsonElement item in list.EnumerateArray())
			{
				if (ReadInstruction(item) is { } instruction)
				{
					instructions.Add(instruction);
				}
				else
				{
					skipped++;
				}
			}
			return (new DsxPacket(instructions, skipped), DsxParseError.None);
		}
		catch (JsonException)
		{
			return (null, DsxParseError.Malformed);
		}
		catch (ArgumentException)
		{
			return (null, DsxParseError.Malformed);
		}
		catch (InvalidOperationException)
		{
			// Invalid UTF-8 inside a string (GetString) and other content the reader refuses.
			return (null, DsxParseError.Malformed);
		}
	}

	// Type texts and names are kept (and logged) at most this long.
	public const int NameLength = 32;

	// Text that came from a mod, safe to key and log: control characters (line breaks that could forge log lines) become
	// spaces, and it is cut to `max` characters.
	public static string Clean(string? text, int max = NameLength)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		string cut = text.Length > max ? text[..max] + "..." : text;
		return string.Create(cut.Length, cut, (span, s) =>
		{
			for (int i = 0; i < s.Length; i++)
			{
				span[i] = char.IsControl(s[i]) || char.IsSurrogate(s[i]) ? ' ' : s[i];
			}
		});
	}

	private static DsxInstruction? ReadInstruction(JsonElement item)
	{
		if (item.ValueKind != JsonValueKind.Object || Property(item, "type") is not { } type)
		{
			return null;
		}
		string typeText;
		DsxInstructionType? known;
		switch (type.ValueKind)
		{
			case JsonValueKind.Number when type.TryGetInt32(out int number):
				typeText = number.ToString(CultureInfo.InvariantCulture);
				known = Enum.IsDefined((DsxInstructionType)number) ? (DsxInstructionType)number : null;
				break;
			case JsonValueKind.String:
				typeText = Clean(type.GetString());
				known = ParseName<DsxInstructionType>(typeText);
				break;
			default:
				return null;
		}
		List<DsxParam> parameters = [];
		if (Property(item, "parameters") is { ValueKind: JsonValueKind.Array } values)
		{
			foreach (JsonElement value in values.EnumerateArray())
			{
				parameters.Add(ReadParam(value));
			}
		}
		return new DsxInstruction(known, typeText, parameters, Clean(item.GetRawText(), SampleLength));
	}

	private static DsxParam ReadParam(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Number => new DsxParam(value.GetDouble()),
		JsonValueKind.True => new DsxParam(1),
		JsonValueKind.False => new DsxParam(0),
		JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) => new DsxParam(n),
		JsonValueKind.String => new DsxParam(0, Clean(value.GetString())),
		_ => new DsxParam(0)
	};

	// A name (any case) or a numeric text of an enum value; null when it is neither.
	public static T? ParseName<T>(string? text) where T : struct, Enum
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
		{
			T byNumber = (T)(object)number;
			return Enum.IsDefined(byNumber) ? byNumber : null;
		}
		return Enum.TryParse(text.Trim(), ignoreCase: true, out T byName) && Enum.IsDefined(byName) ? byName : null;
	}

	private static JsonElement? Property(JsonElement obj, string name)
	{
		foreach (JsonProperty p in obj.EnumerateObject())
		{
			if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				return p.Value;
			}
		}
		return null;
	}
}
