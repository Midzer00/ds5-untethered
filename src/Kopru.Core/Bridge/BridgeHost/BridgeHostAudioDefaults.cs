// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Bridge;

// Every attach and detach of the virtual pad (first connection, reconnects, resume, pad type swaps) is told to PadAudio,
// which keeps Windows from making the pad's speaker or microphone the default device (Audio.DefaultAudioGuard).
public sealed partial class BridgeHost
{
	// Set before starting; the same guard serves every slot.
	public Audio.IPadAudioEvents? PadAudio { get; set; }

	// The virtual hub port of the running pad (names the pad's audio endpoints when several pads run), or null.
	public int? VirtualPort => current?.AttachedPort;

	private sealed partial class Connection
	{
		public int? AttachedPort => port;
	}

	// The guard only queues its work; a failure never touches the bridge.
	private void NotifyPadAudio(Action<Audio.IPadAudioEvents> call)
	{
		if (PadAudio is not { } events)
		{
			return;
		}
		try
		{
			call(events);
		}
		catch (Exception e)
		{
			Diagnostics.AppLog.Write($"audio defaults{LogSlot}: {e.GetType().Name}");
		}
	}
}
