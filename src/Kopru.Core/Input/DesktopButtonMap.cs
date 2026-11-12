// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kopru.Core.Input;

// Which analog stick moves the pointer in mouse mode; the other one scrolls.
public enum PointerStick
{
	Right,
	Left
}

// What a button does in mouse mode. Mouse buttons and the "held" keys stay down while the controller button is held (so
// dragging works); the others are one press (a key tap or a shortcut) when the button goes down.
public enum MouseAction
{
	None,
	LeftClick,
	RightClick,
	MiddleClick,
	Enter,
	Escape,
	Tab,
	Space,
	Backspace,
	Delete,
	ArrowUp,
	ArrowDown,
	ArrowLeft,
	ArrowRight,
	PageUp,
	PageDown,
	WindowsKey,
	OnScreenKeyboard,
	NextWindow,
	PreviousWindow,
	Back,
	Forward,
	Copy,
	Paste,
	VolumeUp,
	VolumeDown,
	MuteSound,
	PlayPause,
	NextTrack,
	PreviousTrack
}

// The action of every remappable button in mouse mode. Immutable, with value equality (it sits in the DesktopModeSettings
// record, which is compared by value and swapped as a whole for the input thread).
[JsonConverter(typeof(DesktopButtonMapJsonConverter))]
public sealed class DesktopButtonMap : IEquatable<DesktopButtonMap>
{
	// The remappable buttons, in display order (two per line: Cross | Circle, Square | Triangle, ...).
	public static IReadOnlyList<DsButton> Buttons { get; } =
	[
		DsButton.Cross, DsButton.Circle, DsButton.Square, DsButton.Triangle, DsButton.DpadUp, DsButton.DpadDown, DsButton.DpadLeft,
		DsButton.DpadRight, DsButton.L1, DsButton.R1, DsButton.L2, DsButton.R2, DsButton.L3, DsButton.R3, DsButton.Options, DsButton.Create
	];

	// Every action, in the order the pickers list them.
	public static IReadOnlyList<MouseAction> Actions { get; } = Enum.GetValues<MouseAction>();

	// The input bits of Buttons (same order).
	internal static readonly PadButtons[] Bits =
	[
		PadButtons.Cross, PadButtons.Circle, PadButtons.Square, PadButtons.Triangle, PadButtons.DpadUp, PadButtons.DpadDown, PadButtons.DpadLeft,
		PadButtons.DpadRight, PadButtons.L1, PadButtons.R1, PadButtons.L2, PadButtons.R2, PadButtons.L3, PadButtons.R3, PadButtons.Options, PadButtons.Create
	];

	private static readonly MouseAction[] Defaults =
	[
		MouseAction.LeftClick, MouseAction.RightClick, MouseAction.Enter, MouseAction.OnScreenKeyboard, MouseAction.ArrowUp, MouseAction.ArrowDown,
		MouseAction.ArrowLeft, MouseAction.ArrowRight, MouseAction.PreviousWindow, MouseAction.NextWindow, MouseAction.VolumeDown, MouseAction.VolumeUp,
		MouseAction.Escape, MouseAction.MiddleClick, MouseAction.WindowsKey, MouseAction.PlayPause
	];

	public static DesktopButtonMap Default { get; } = new((MouseAction[])Defaults.Clone());

	private readonly MouseAction[] _actions;

	private DesktopButtonMap(MouseAction[] actions) => _actions = actions;

	public static int IndexOf(DsButton button)
	{
		for (int i = 0; i < Bits.Length; i++)
		{
			if (Buttons[i] == button)
			{
				return i;
			}
		}
		return -1;
	}

	public static MouseAction DefaultActionOf(DsButton button) => IndexOf(button) is int i and >= 0 ? Defaults[i] : MouseAction.None;

	// None for a button that is not remappable.
	public MouseAction ActionOf(DsButton button) => IndexOf(button) is int i and >= 0 ? _actions[i] : MouseAction.None;

	internal MouseAction At(int index) => _actions[index];

	public DesktopButtonMap With(DsButton button, MouseAction action)
	{
		int i = IndexOf(button);
		if (i < 0 || !Enum.IsDefined(action) || _actions[i] == action)
		{
			return this;
		}
		var next = (MouseAction[])_actions.Clone();
		next[i] = action;
		return new DesktopButtonMap(next);
	}

	public bool IsDefault => Equals(Default);

	public bool Equals(DesktopButtonMap? other) => other is not null && _actions.AsSpan().SequenceEqual(other._actions);

	public override bool Equals(object? obj) => obj is DesktopButtonMap other && Equals(other);

	public override int GetHashCode()
	{
		var h = new HashCode();
		foreach (MouseAction a in _actions)
		{
			h.Add(a);
		}
		return h.ToHashCode();
	}

	public static bool operator ==(DesktopButtonMap? a, DesktopButtonMap? b) => a is null ? b is null : a.Equals(b);

	public static bool operator !=(DesktopButtonMap? a, DesktopButtonMap? b) => !(a == b);

	// Reads { "Cross": "LeftClick", ... }; a button or an action it does not know keeps that button's default (a file from a
	// newer or older version never resets the other settings).
	internal static DesktopButtonMap Read(ref Utf8JsonReader reader)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
		{
			reader.Skip();
			return Default;
		}
		var actions = (MouseAction[])Defaults.Clone();
		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			string? name = reader.GetString();
			reader.Read();
			if (reader.TokenType == JsonTokenType.String && Enum.TryParse(name, ignoreCase: true, out DsButton button) && IndexOf(button) is int i and >= 0
				&& LenientEnum.TryParse(reader.GetString(), out MouseAction action))
			{
				actions[i] = action;
			}
			else
			{
				reader.Skip();
			}
		}
		return new DesktopButtonMap(actions);
	}

	internal void Write(Utf8JsonWriter writer)
	{
		writer.WriteStartObject();
		for (int i = 0; i < _actions.Length; i++)
		{
			writer.WriteString(Buttons[i].ToString(), _actions[i].ToString());
		}
		writer.WriteEndObject();
	}
}

public sealed class DesktopButtonMapJsonConverter : JsonConverter<DesktopButtonMap>
{
	public override bool HandleNull => true;

	public override DesktopButtonMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
		reader.TokenType == JsonTokenType.Null ? DesktopButtonMap.Default : DesktopButtonMap.Read(ref reader);

	public override void Write(Utf8JsonWriter writer, DesktopButtonMap value, JsonSerializerOptions options) =>
		(value ?? DesktopButtonMap.Default).Write(writer);
}

// An enum saved by name; a name (or number) it does not know reads as the enum's default instead of failing the file.
public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
	public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.String && LenientEnum.TryParse(reader.GetString(), out T value))
		{
			return value;
		}
		if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n) && Enum.IsDefined(typeof(T), n))
		{
			return (T)Enum.ToObject(typeof(T), n);
		}
		reader.Skip();
		return default;
	}

	public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

internal static class LenientEnum
{
	// A defined name only (not a number in a string, not a combination).
	public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
	{
		value = default;
		if (string.IsNullOrWhiteSpace(text) || char.IsDigit(text.Trim()[0]) || text.Trim()[0] == '-' || text.Contains(','))
		{
			return false;
		}
		return Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
	}
}
