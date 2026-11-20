// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Audio;

// No sound capture on this platform: "turn sound into haptics" stays off; each start attempt logs why.
public sealed class NoAudioCapture(string reason) : IAudioCaptureFactory
{
	public IAudioCapture? Start(int processId, IAudioCaptureSink sink, Action<string> log)
	{
		log(reason);
		return null;
	}
}
