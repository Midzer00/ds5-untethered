// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;

namespace Kopru.App.Services;

// "Turn sound into haptics": the runner follows the active game (each game tick) and its profile (each settings change);
// its frames go to the first controller's bridge.
public sealed partial class RealControllerService
{
	private readonly object audioHapticsGate = new();
	private AudioHapticsRunner? audioHaptics;

	// What captures the game's sound (CreateDefault: WASAPI); null: none (the option then reports it is not available).
	public IAudioCaptureFactory? AudioCapture { get; init; }

	public AudioHapticsStatus AudioHapticsStatus => audioHaptics?.Status ?? AudioHapticsStatus.Off;

	public event Action? AudioHapticsStatusChanged;

	private AudioHapticsRunner AudioHaptics
	{
		get
		{
			lock (audioHapticsGate)
			{
				if (audioHaptics == null)
				{
					audioHaptics = new AudioHapticsRunner(AudioCapture ?? NoAudioCapture.Instance, host);
					audioHaptics.StatusChanged += _ => AudioHapticsStatusChanged?.Invoke();
				}
				return audioHaptics;
			}
		}
	}

	// Starts or stops the capture for the active game and its profile. Never on the UI thread (the capture is set up on a
	// pool thread; a settings change posts it there).
	private void UpdateAudioHaptics()
	{
		if (Volatile.Read(ref disposed) != 0)
		{
			return;
		}
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

	private void QueueAudioHapticsUpdate() => ThreadPool.QueueUserWorkItem(_ => UpdateAudioHaptics());

	private void DisposeAudioHaptics()
	{
		lock (audioHapticsGate)
		{
			audioHaptics?.Dispose();
		}
	}

	private sealed class NoAudioCapture : IAudioCaptureFactory
	{
		public static readonly NoAudioCapture Instance = new();

		public IAudioCapture? Start(int processId, IAudioCaptureSink sink, Action<string> log)
		{
			log("audio haptics: no sound capture in this build");
			return null;
		}
	}
}
