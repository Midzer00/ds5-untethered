// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

// The touchpad double press (ShortcutProcessor + TouchpadDoublePress): two touchpad clicks within 350 ms turn Mouse Mode on,
// only with the app's window in front and Mouse Mode off; the presses still reach the game.
public class TouchpadDoublePressTests
{
	private readonly StepClock clock = new();
	private readonly ShortcutProcessor shortcuts;
	private readonly List<ShortcutAction> fired = [];
	private bool appInFront = true, mouseModeOn;

	public TouchpadDoublePressTests()
	{
		shortcuts = new ShortcutProcessor(clock) { Log = _ => { }, AppInFront = () => appInFront, MouseModeOn = () => mouseModeOn };
		shortcuts.Triggered += fired.Add;
	}

	private static byte[] Body(bool touchpad = false, bool ps = false)
	{
		var b = new byte[InputState.BodySize];
		b[ShortcutProcessor.Buttons0] = 0x08;
		b[ShortcutProcessor.Buttons2] = (byte)((touchpad ? ShortcutProcessor.TouchpadBit : 0) | (ps ? ShortcutProcessor.PsBit : 0));
		return b;
	}

	private byte[] Step(bool touchpad, double ms = 4, bool ps = false)
	{
		clock.Advance(TimeSpan.FromMilliseconds(ms));
		byte[] body = Body(touchpad, ps);
		shortcuts.Apply(body);
		return body;
	}

	// A press: down for `held` ms, then up for `gap` ms.
	private void Press(double held = 80, double gap = 80)
	{
		Step(true);
		Step(true, held);
		Step(false);
		Step(false, gap);
	}

	private int Gestures => fired.Count(a => a == ShortcutAction.TouchpadDoublePress);

	[Fact]
	public void TwoQuickPresses_TurnMouseModeOn_AndStillReachTheGame()
	{
		Press();
		Assert.Equal(0, Gestures);
		byte[] second = Step(true);
		Assert.Equal(1, Gestures);
		Assert.NotEqual(0, second[ShortcutProcessor.Buttons2] & ShortcutProcessor.TouchpadBit);
		Assert.Equal(350, TouchpadDoublePress.Window.TotalMilliseconds);
	}

	[Fact]
	public void SinglePress_DoesNothing()
	{
		Press();
		Step(false, 2000);
		Assert.Equal(0, Gestures);
	}

	[Fact]
	public void TwoPressesTooSlow_DoNothing_ButTheSecondCanStartAPair()
	{
		Press(80, 300);
		Press();
		Assert.Equal(0, Gestures);
		Step(true);
		Assert.Equal(1, Gestures);
	}

	[Fact]
	public void ThreeQuickPresses_AreOneGesture()
	{
		Press(50, 50);
		Press(50, 50);
		Press(50, 50);
		Assert.Equal(1, Gestures);
	}

	[Fact]
	public void HeldPress_IsNotAQuickPress()
	{
		Press(600, 20);
		Press();
		Assert.Equal(0, Gestures);
	}

	[Fact]
	public void NothingWhenTheAppIsNotInFront_OrMouseModeIsOn_OrPsIsHeld()
	{
		appInFront = false;
		Press(50, 50);
		Press(50, 50);
		Assert.Equal(0, Gestures);

		appInFront = true;
		mouseModeOn = true;
		Press(50, 50);
		Press(50, 50);
		Assert.Equal(0, Gestures);

		mouseModeOn = false;
		Step(true, 4, ps: true);
		Step(false, 40, ps: true);
		Step(true, 40, ps: true);
		Step(false, 4, ps: true);
		Assert.Equal(0, Gestures);
	}

	[Fact]
	public void NothingWhileShortcutsAreOff()
	{
		shortcuts.Enabled = false;
		Press(50, 50);
		Press(50, 50);
		Assert.Equal(0, Gestures);

		// A press in progress when they are turned back on does not count as a first one.
		shortcuts.Enabled = true;
		Step(true);
		Assert.Equal(0, Gestures);
	}
}
