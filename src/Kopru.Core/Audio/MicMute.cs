// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Audio;

// The controller's mic (mute) button as the microphone switch: each press (the edge, not repeats while held, and not a
// second press within Debounce) raises Pressed; the button bit is always cleared, so games never see it. It is only
// given input while the bridge handles the button (see BridgeHostMic). Process runs on the input thread; Pressed is
// raised there (handlers must be short).
public sealed class MicButton(TimeProvider? time = null)
{
	// Body byte 9 (Buttons2) bit 2: the mute button (same in USB report 0x01 from byte 1 and BT 0x31 from byte 2).
	public const int ButtonByte = 9;
	public const byte ButtonBit = 0x04;

	public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

	private readonly TimeProvider time = time ?? TimeProvider.System;

	private bool wasDown;

	private long lastPress;

	private bool pressedBefore;

	public event Action? Pressed;

	public void Process(Span<byte> body)
	{
		if (body.Length <= ButtonByte)
		{
			return;
		}
		bool down = (body[ButtonByte] & ButtonBit) != 0;
		bool press = down && !wasDown;
		wasDown = down;
		body[ButtonByte] &= unchecked((byte)~ButtonBit);
		if (!press)
		{
			return;
		}
		long now = time.GetTimestamp();
		if (pressedBefore && time.GetElapsedTime(lastPress, now) < Debounce)
		{
			return;
		}
		pressedBefore = true;
		lastPress = now;
		Pressed?.Invoke();
	}
}
