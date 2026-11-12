// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Kopru.Core.Input;

namespace Kopru.Core.Tests;

// Mouse mode's pointer stick choice and button mapping.
public class DesktopRemapTests
{
	private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(4);

	// Button bits in the body: byte 7 (0x10 Square, 0x20 Cross, 0x40 Circle, 0x80 Triangle; low nibble the hat, 8 = none),
	// byte 8 (0x01 L1, 0x02 R1, 0x04 L2, 0x08 R2, 0x10 Create, 0x20 Options, 0x40 L3, 0x80 R3).
	private static byte[] Body(byte b0 = 0x08, byte b1 = 0)
	{
		var b = new byte[63];
		b[0] = b[1] = b[2] = b[3] = 128;
		b[7] = b0;
		b[8] = b1;
		b[32] = 0x80;
		b[36] = 0x80;
		return b;
	}

	private const byte Square = 0x08 | 0x10, Cross = 0x08 | 0x20, Circle = 0x08 | 0x40, Triangle = 0x08 | 0x80;

	private static (DesktopMode Mode, RecordingSink Sink) Active(DesktopButtonMap map, PointerStick stick = PointerStick.Right)
	{
		var sink = new RecordingSink();
		var mode = new DesktopMode(sink) { Active = true, Settings = DesktopModeSettings.Default with { Buttons = map, PointerStick = stick } };
		mode.Apply(Body(), Frame);
		return (mode, sink);
	}

	private static List<string> Tap(DesktopMode mode, RecordingSink sink, byte b0 = 0x08, byte b1 = 0)
	{
		sink.Clear();
		mode.Apply(Body(b0, b1), Frame);
		mode.Apply(Body(), Frame);
		return [.. sink.Events];
	}

	[Fact]
	public void DefaultMap_IsTheFixedMapping()
	{
		Assert.Equal(16, DesktopButtonMap.Buttons.Count);
		Assert.Equal(DesktopModeSettings.Default.Buttons, DesktopButtonMap.Default);
		Assert.Equal(PointerStick.Right, DesktopModeSettings.Default.PointerStick);
		var expected = new Dictionary<DsButton, MouseAction>
		{
			[DsButton.Cross] = MouseAction.LeftClick, [DsButton.Circle] = MouseAction.RightClick, [DsButton.Square] = MouseAction.Enter,
			[DsButton.Triangle] = MouseAction.OnScreenKeyboard, [DsButton.DpadUp] = MouseAction.ArrowUp, [DsButton.DpadDown] = MouseAction.ArrowDown,
			[DsButton.DpadLeft] = MouseAction.ArrowLeft, [DsButton.DpadRight] = MouseAction.ArrowRight, [DsButton.L1] = MouseAction.PreviousWindow,
			[DsButton.R1] = MouseAction.NextWindow, [DsButton.L2] = MouseAction.VolumeDown, [DsButton.R2] = MouseAction.VolumeUp,
			[DsButton.L3] = MouseAction.Escape, [DsButton.R3] = MouseAction.MiddleClick, [DsButton.Options] = MouseAction.WindowsKey,
			[DsButton.Create] = MouseAction.PlayPause
		};
		foreach (DsButton b in DesktopButtonMap.Buttons)
		{
			Assert.Equal(expected[b], DesktopButtonMap.Default.ActionOf(b));
			Assert.Equal(expected[b], DesktopButtonMap.DefaultActionOf(b));
		}
		Assert.Equal(MouseAction.None, DesktopButtonMap.Default.ActionOf(DsButton.Touchpad));
		Assert.Same(DesktopButtonMap.Default, DesktopButtonMap.Default.With(DsButton.Touchpad, MouseAction.Copy));
		Assert.True(DesktopButtonMap.Default.IsDefault);
	}

	[Fact]
	public void Map_HasValueEquality()
	{
		DesktopButtonMap a = DesktopButtonMap.Default.With(DsButton.Cross, MouseAction.Copy);
		DesktopButtonMap b = DesktopButtonMap.Default.With(DsButton.Cross, MouseAction.Copy);
		Assert.NotSame(a, b);
		Assert.Equal(a, b);
		Assert.True(a == b);
		Assert.Equal(a.GetHashCode(), b.GetHashCode());
		Assert.NotEqual(DesktopButtonMap.Default, a);
		Assert.False(a.IsDefault);
		Assert.Equal(DesktopButtonMap.Default, a.With(DsButton.Cross, MouseAction.LeftClick));
		Assert.Equal(DesktopModeSettings.Default with { Buttons = a }, DesktopModeSettings.Default with { Buttons = b });
		Assert.True(DesktopModeSettings.Default with { Buttons = a } != DesktopModeSettings.Default);
		Assert.Equal(DesktopButtonMap.Default, (DesktopModeSettings.Default with { Buttons = null! }).Buttons);
	}

	[Fact]
	public void LeftPointerStick_SwapsPointerAndScroll()
	{
		(DesktopMode mode, RecordingSink sink) = Active(DesktopButtonMap.Default, PointerStick.Left);
		for (int i = 0; i < 250; i++)
		{
			byte[] body = Body();
			body[0] = 255; // left stick right: pointer
			body[3] = 0; // right stick up: scroll up
			mode.Apply(body, Frame);
		}
		Assert.InRange(sink.MoveX, DesktopMode.PointerPixelsPerSecond - 2, DesktopMode.PointerPixelsPerSecond + 2);
		Assert.Equal(0, sink.MoveY);
		Assert.InRange(sink.Wheel, DesktopMode.ScrollNotchesPerSecond * 120 - 2, DesktopMode.ScrollNotchesPerSecond * 120 + 2);
		Assert.Equal(0, sink.HWheel);

		// Switched back while running: the right stick moves the pointer again.
		mode.Settings = mode.Settings with { PointerStick = PointerStick.Right };
		sink.Clear();
		byte[] right = Body();
		right[2] = 255;
		mode.Apply(right, Frame);
		mode.Apply(right, Frame);
		Assert.True(sink.MoveX > 0);
		Assert.Equal(0, sink.HWheel);
	}

	[Fact]
	public void HeldKeys_StayDownWhileHeld()
	{
		DesktopButtonMap map = DesktopButtonMap.Default.With(DsButton.Cross, MouseAction.Space).With(DsButton.Square, MouseAction.Delete)
			.With(DsButton.Circle, MouseAction.Backspace).With(DsButton.Triangle, MouseAction.Tab);
		(DesktopMode mode, RecordingSink sink) = Active(map);
		mode.Apply(Body(Cross), Frame);
		mode.Apply(Body(Cross), Frame);
		Assert.Equal(["key 20 down"], sink.Events);
		mode.Apply(Body(), Frame);
		Assert.Equal(["key 20 down", "key 20 up"], sink.Events);
		Assert.Equal(["key 2E down", "key 2E up"], Tap(mode, sink, Square));
		Assert.Equal(["key 08 down", "key 08 up"], Tap(mode, sink, Circle));
		Assert.Equal(["key 09 down", "key 09 up"], Tap(mode, sink, Triangle));
	}

	[Fact]
	public void OnePressActions_TapKeysAndShortcuts()
	{
		var cases = new (MouseAction Action, string[] Events)[]
		{
			(MouseAction.PageUp, ["key 21 down", "key 21 up"]),
			(MouseAction.PageDown, ["key 22 down", "key 22 up"]),
			(MouseAction.WindowsKey, ["key 5B down", "key 5B up"]),
			(MouseAction.OnScreenKeyboard, ["key 5B down", "key 11 down", "key 4F down", "key 4F up", "key 11 up", "key 5B up"]),
			(MouseAction.Back, ["key 12 down", "key 25 down", "key 25 up", "key 12 up"]),
			(MouseAction.Forward, ["key 12 down", "key 27 down", "key 27 up", "key 12 up"]),
			(MouseAction.Copy, ["key 11 down", "key 43 down", "key 43 up", "key 11 up"]),
			(MouseAction.Paste, ["key 11 down", "key 56 down", "key 56 up", "key 11 up"]),
			(MouseAction.VolumeUp, ["key AF down", "key AF up"]),
			(MouseAction.VolumeDown, ["key AE down", "key AE up"]),
			(MouseAction.MuteSound, ["key AD down", "key AD up"]),
			(MouseAction.PlayPause, ["key B3 down", "key B3 up"]),
			(MouseAction.NextTrack, ["key B0 down", "key B0 up"]),
			(MouseAction.PreviousTrack, ["key B1 down", "key B1 up"])
		};
		foreach ((MouseAction action, string[] events) in cases)
		{
			(DesktopMode mode, RecordingSink sink) = Active(DesktopButtonMap.Default.With(DsButton.L3, action));
			// Sent once on the press; holding and releasing add nothing.
			mode.Apply(Body(b1: 0x40), Frame);
			mode.Apply(Body(b1: 0x40), Frame);
			mode.Apply(Body(), Frame);
			Assert.Equal(events, sink.Events);
		}
	}

	[Fact]
	public void MouseButtons_OnOtherButtons_HoldForDragging()
	{
		DesktopButtonMap map = DesktopButtonMap.Default.With(DsButton.R2, MouseAction.LeftClick).With(DsButton.L2, MouseAction.MiddleClick)
			.With(DsButton.DpadUp, MouseAction.RightClick);
		(DesktopMode mode, RecordingSink sink) = Active(map);
		mode.Apply(Body(b1: 0x08), Frame);
		mode.Apply(Body(b1: 0x08), Frame);
		Assert.Equal(["Left down"], sink.Events);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], sink.Events);
		Assert.Equal(["Middle down", "Middle up"], Tap(mode, sink, b1: 0x04));
		Assert.Equal(["Right down", "Right up"], Tap(mode, sink, 0x00));
	}

	[Fact]
	public void None_DoesNothing()
	{
		DesktopButtonMap map = DesktopButtonMap.Default;
		foreach (DsButton b in DesktopButtonMap.Buttons)
		{
			map = map.With(b, MouseAction.None);
		}
		(DesktopMode mode, RecordingSink sink) = Active(map);
		Assert.Empty(Tap(mode, sink, 0x00 | 0x10 | 0x20 | 0x40 | 0x80, 0xFF));
	}

	[Fact]
	public void TwoButtonsOnOneAction_ReleaseWhenBothAreUp()
	{
		(DesktopMode mode, RecordingSink sink) = Active(DesktopButtonMap.Default.With(DsButton.Square, MouseAction.LeftClick)
			.With(DsButton.DpadUp, MouseAction.Enter).With(DsButton.DpadDown, MouseAction.Enter));
		mode.Apply(Body(Cross), Frame);
		mode.Apply(Body(0x08 | 0x20 | 0x10), Frame);
		mode.Apply(Body(Square), Frame);
		Assert.Equal(["Left down"], sink.Events);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], sink.Events);
	}

	[Fact]
	public void Switcher_OnRemappedButtons_AndTheLeftClickButtonPicks()
	{
		DesktopButtonMap map = DesktopButtonMap.Default.With(DsButton.Triangle, MouseAction.NextWindow).With(DsButton.Circle, MouseAction.PreviousWindow)
			.With(DsButton.Square, MouseAction.LeftClick).With(DsButton.Cross, MouseAction.None).With(DsButton.R1, MouseAction.Copy);
		(DesktopMode mode, RecordingSink sink) = Active(map);
		mode.Apply(Body(Triangle), Frame);
		Assert.Equal(["key 12 down", "key 09 down", "key 09 up"], sink.Events);
		// Held: Alt stays down however long.
		for (int i = 0; i < 300; i++)
			mode.Apply(Body(Triangle), Frame);
		mode.Apply(Body(), Frame);
		mode.Apply(Body(Circle), Frame);
		Assert.Equal(["key 12 down", "key 09 down", "key 09 up", "key 10 down", "key 09 down", "key 09 up", "key 10 up"], sink.Events);
		sink.Clear();
		mode.Apply(Body(), Frame);
		mode.Apply(Body(Cross), Frame); // None: nothing, the switcher stays
		mode.Apply(Body(), Frame);
		Assert.Empty(sink.Events);
		mode.Apply(Body(Square), Frame); // Left Click picks: Alt up, no click
		mode.Apply(Body(Square), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["key 12 up"], sink.Events);
		// Switcher closed: Square clicks again; R1 is Copy now.
		Assert.Equal(["Left down", "Left up"], Tap(mode, sink, Square));
		Assert.Equal(["key 11 down", "key 43 down", "key 43 up", "key 11 up"], Tap(mode, sink, b1: 0x02));
		// Closes by itself after SwitcherHold.
		sink.Clear();
		mode.Apply(Body(Triangle), Frame);
		for (int i = 0; i < 200; i++)
			mode.Apply(Body(), Frame);
		Assert.Single(sink.Events, "key 12 up");
		Assert.Equal("key 12 up", sink.Events[^1]);
	}

	[Fact]
	public void BackWhileTheSwitcherIsOpen_ClosesItFirst()
	{
		(DesktopMode mode, RecordingSink sink) = Active(DesktopButtonMap.Default.With(DsButton.Square, MouseAction.Back));
		mode.Apply(Body(b1: 0x02), Frame);
		mode.Apply(Body(), Frame);
		sink.Clear();
		mode.Apply(Body(Square), Frame);
		Assert.Equal(["key 12 up", "key 12 down", "key 25 down", "key 25 up", "key 12 up"], sink.Events);
		for (int i = 0; i < 200; i++)
			mode.Apply(Body(), Frame);
		Assert.Equal(2, sink.Events.Count(e => e == "key 12 up"));
	}

	[Fact]
	public void RemapWhileHeld_LetsGoAtOnce_AndWaitsForTheRelease()
	{
		(DesktopMode mode, RecordingSink sink) = Active(DesktopButtonMap.Default);
		mode.Apply(Body(Cross), Frame);
		mode.Apply(Body(0x00 | 0x20), Frame); // Cross + D-pad up
		Assert.Equal(["Left down", "key 26 down"], sink.Events);
		sink.Clear();
		mode.Settings = mode.Settings with { Buttons = mode.Settings.Buttons.With(DsButton.Cross, MouseAction.Enter).With(DsButton.DpadUp, MouseAction.Copy) };
		mode.Apply(Body(0x00 | 0x20), Frame);
		Assert.Equal(["Left up", "key 26 up"], sink.Events);
		// Still held: the new actions wait for a new press.
		mode.Apply(Body(0x00 | 0x20), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left up", "key 26 up"], sink.Events);
		Assert.Equal(["key 0D down", "key 0D up"], Tap(mode, sink, Cross));

		// A change that leaves the held button's action alone keeps it down.
		sink.Clear();
		mode.Apply(Body(Circle), Frame);
		mode.Settings = mode.Settings with { PointerSpeed = 2, Buttons = mode.Settings.Buttons.With(DsButton.Square, MouseAction.Copy) };
		mode.Apply(Body(Circle), Frame);
		Assert.Equal(["Right down"], sink.Events);

		// Turning off releases what the remapped buttons hold.
		mode.Apply(Body(0x08 | 0x40 | 0x20), Frame);
		mode.Active = false;
		mode.Apply(Body(0x08 | 0x40 | 0x20), Frame);
		Assert.Equal(["Right down", "key 0D down", "key 0D up", "Right up"], sink.Events);
		mode.Active = true;
		sink.Clear();
		mode.Apply(Body(0x08 | 0x40 | 0x20), Frame);
		mode.Apply(Body(), Frame);
		Assert.Empty(sink.Events);
	}

	[Fact]
	public void NewKeys_AreExtendedWhereWindowsNeedsIt()
	{
		foreach (ushort vk in new[] { VirtualKeys.PageUp, VirtualKeys.PageDown, VirtualKeys.Delete, VirtualKeys.VolumeMute, VirtualKeys.MediaNextTrack, VirtualKeys.MediaPreviousTrack })
			Assert.True(VirtualKeys.IsExtended(vk), $"{vk:X2}");
		foreach (ushort vk in new[] { VirtualKeys.Space, VirtualKeys.Backspace, VirtualKeys.C, VirtualKeys.V, VirtualKeys.Tab })
			Assert.False(VirtualKeys.IsExtended(vk), $"{vk:X2}");
	}

	private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

	[Fact]
	public void Settings_RoundTrip_WithStickAndMapping()
	{
		var desktop = new DesktopModeSettings
		{
			PointerSpeed = 1.3, PointerStick = PointerStick.Left,
			Buttons = DesktopButtonMap.Default.With(DsButton.Cross, MouseAction.Paste).With(DsButton.R1, MouseAction.None)
		};
		string json = JsonSerializer.Serialize(desktop, Options);
		Assert.Contains("\"PointerStick\":\"Left\"", json);
		Assert.Contains("\"Cross\":\"Paste\"", json);
		Assert.Equal(desktop, JsonSerializer.Deserialize<DesktopModeSettings>(json, Options));
		// Older files: no stick, no mapping.
		DesktopModeSettings old = JsonSerializer.Deserialize<DesktopModeSettings>("{\"PointerSpeed\":2.0,\"AutoOffInGame\":false}", Options)!;
		Assert.Equal(DesktopModeSettings.Default with { PointerSpeed = 2, AutoOffInGame = false }, old);
		Assert.Equal(DesktopButtonMap.Default, JsonSerializer.Deserialize<DesktopModeSettings>("{\"Buttons\":null}", Options)!.Buttons);
	}

	[Fact]
	public void Settings_UnknownValues_FallBackPerButton()
	{
		const string json = """
			{"PointerSpeed":1.5,"PointerStick":"Up","Buttons":{"Cross":"Fly","Circle":"copy","PS":"Enter","Square":5,"Triangle":"7",
			 "L1":"LeftClick, RightClick","Touchpad":"Copy","R2":{"x":1},"Options":null,"Create":"NextTrack"}}
			""";
		DesktopModeSettings s = JsonSerializer.Deserialize<DesktopModeSettings>(json, Options)!;
		Assert.Equal(1.5, s.PointerSpeed);
		Assert.Equal(PointerStick.Right, s.PointerStick);
		DesktopButtonMap expected = DesktopButtonMap.Default.With(DsButton.Circle, MouseAction.Copy).With(DsButton.Create, MouseAction.NextTrack);
		Assert.Equal(expected, s.Buttons);
		Assert.Equal(PointerStick.Left, JsonSerializer.Deserialize<DesktopModeSettings>("{\"PointerStick\":1}", Options)!.PointerStick);
		Assert.Equal(PointerStick.Right, JsonSerializer.Deserialize<DesktopModeSettings>("{\"PointerStick\":7}", Options)!.PointerStick);
		Assert.Equal(DesktopButtonMap.Default, JsonSerializer.Deserialize<DesktopModeSettings>("{\"Buttons\":[1,2]}", Options)!.Buttons);
	}
}
