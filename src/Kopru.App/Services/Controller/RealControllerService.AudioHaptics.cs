// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;

namespace Kopru.App.Services;

// "Turn sound into haptics": the runner follows the active game (each game tick) and its profile (each settings change);
// its frames go to the first controller's bridge.
public sealed partial class RealControllerService
{
	private readonly object audioHapticsGate = new();
	// Serialises the updates (a settings change on a pool thread and the game tick): each one reads the wanted settings
	// inside it, so an older reading never lands after a newer one (a switch turned off is never turned back on).
	private readonly object audioHapticsUpdateGate = new();
	private AudioHapticsRunner? audioHaptics;

	// What captures the game's sound: given to the constructor, else the platform's (WASAPI on Windows). Every way the app
	// makes the service gets the real capture; tests pass a fake. Fixed at construction, before the runner can be made.
	public IAudioCaptureFactory AudioCapture { get; }

	public AudioHapticsStatus AudioHapticsStatus => audioHaptics?.Status ?? AudioHapticsStatus.Off;

	public event Action? AudioHapticsStatusChanged;

	private static IAudioCaptureFactory DefaultAudioCapture() =>
		Kopru.Core.Platform.PlatformServices.Current.CreateAudioCapture();

	private AudioHapticsRunner AudioHaptics
	{
		get
		{
			lock (audioHapticsGate)
			{
				if (audioHaptics == null)
				{
					audioHaptics = new AudioHapticsRunner(AudioCapture, host);
					audioHaptics.StatusChanged += _ => AudioHapticsStatusChanged?.Invoke();
				}
				return audioHaptics;
			}
		}
	}

	// Starts or stops the capture for the active game and its profile, and hands a changed strength or band to the running
	// one. Never on the UI thread (the capture is set up on a pool thread; a settings change posts it there).
	private void UpdateAudioHaptics()
	{
		if (Volatile.Read(ref disposed) != 0)
		{
			return;
		}
		lock (audioHapticsUpdateGate)
		{
			try
			{
				AudioHapticsSettings wanted = Games.Profile.AudioHaptics;
				if (!wanted.On && audioHaptics == null)
				{
					return;
				}
				AudioHaptics.Update(Games.ActiveGame, wanted);
			}
			catch (Exception e)
			{
				Kopru.Core.Diagnostics.AppLog.Write($"audio haptics: update failed ({e.GetType().Name})");
			}
		}
	}

	private void QueueAudioHapticsUpdate() => ThreadPool.QueueUserWorkItem(_ => UpdateAudioHaptics());

	private void DisposeAudioHaptics()
	{
		lock (audioHapticsGate)
		{
			audioHaptics?.Dispose();
		}
	}
}
