// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;

namespace Kopru.Core.Bridge;

// Audio haptics frames go to the current connection's bridge (ControllerBridge.PushAudioHaptics); none while no
// controller is connected.
public sealed partial class BridgeHost : IAudioHapticsOutput
{
	public AudioHapticsPush PushAudioHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
	{
		Connection? c = current;
		if (c == null)
		{
			return AudioHapticsPush.NoController;
		}
		try
		{
			return c.Bridge.PushAudioHaptics(left, right);
		}
		catch (ObjectDisposedException)
		{
			// The controller is going away.
			return AudioHapticsPush.NoController;
		}
	}
}
