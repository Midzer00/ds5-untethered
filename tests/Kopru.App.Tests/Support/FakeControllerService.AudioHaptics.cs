// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;

namespace Kopru.App.Tests;

// "Turn sound into haptics": a status the tests set (the real one comes from AudioHapticsRunner).
public sealed partial class FakeControllerService
{
	private AudioHapticsStatus audioHapticsStatus = AudioHapticsStatus.Off;

	public AudioHapticsStatus AudioHapticsStatus
	{
		get => audioHapticsStatus;
		set
		{
			audioHapticsStatus = value;
			AudioHapticsStatusChanged?.Invoke();
		}
	}

	public event Action? AudioHapticsStatusChanged;
}
