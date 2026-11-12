// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Bluetooth;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// Joins a Bluetooth DualSense to the virtual wired one: the controller's input goes to games unchanged, the games'
// output (triggers, lights) goes to the controller, their haptics audio becomes the controller's haptics stream, and
// their classic rumble is played as haptics too (rumble bits over Bluetooth would turn audio haptics off).
public sealed class ControllerBridge : IDisposable
{
	public const double DefaultRumbleLevel = 1.0;

	// Rumble tones (soft feel): the heavy left motor low, the light right motor higher.
	public const double LeftRumbleHz = RumbleSynth.SoftLeftHz;
	public const double RightRumbleHz = RumbleSynth.SoftRightHz;

	// Haptics audio quieter than this (16-bit sample) counts as silence for native support detection.
	public const int SilenceThreshold = 256;

	// While the game streams audio, rumble is mixed into it; otherwise a pump keeps this many frames buffered.
	public const int RumbleAheadFrames = 64;

	// The game's speaker channels (1-2) silent for longer than this: speaker audio stops, so the controller stream goes
	// back from 0x36 to plain haptics (0x32) once the buffered speaker audio has played.
	public const int SpeakerTailMs = 200;
	// Speaker samples quieter than this (16-bit) count as silence: games stream digital zeros or near-zero dither.
	public const int SpeakerSilenceThreshold = 32;
	public const int SpeakerTailFrames = Audio.SpeakerAudioSender.InputRate * SpeakerTailMs / 1000;

	private static readonly long AudioIdleTicks = Stopwatch.Frequency / 20;

	private readonly BluetoothSession session;

	private readonly IVirtualPad pad;

	private readonly HapticsConverter converter = new();

	private readonly object audioGate = new();

	private readonly object rumbleGate = new();

	private readonly byte[] lastBlock = new byte[OutputBlock.Size];

	private readonly Thread rumblePump;

	private volatile bool disposed;

	// Stick/trigger tuning applied to input before games see it; swap live (atomic reference write).
	public volatile Input.InputTransform InputTransform = Input.InputTransform.Identity;

	// Controller shortcuts (PS held as a modifier), applied before the tuning; null = none.
	public volatile Input.ShortcutProcessor? Shortcuts;

	// Desktop mode (replaces the game's input with a neutral one while on) and gyro to mouse; null = none.
	public volatile Input.DesktopMode? Desktop;

	public volatile Input.GyroMouse? GyroMouse;

	// The mic button as the microphone switch (BridgeHostMic); null = the button goes to the game as usual.
	public volatile Audio.MicButton? MicButton;

	// The mute LED value the app shows (OutputBlock.MuteLedOn / MuteLedOff: the microphone is live or not), put into every
	// relayed game output in place of the game's; NoAppMuteLed lets the game's mute LED through (the game owns the button).
	public const int NoAppMuteLed = -1;

	// Game reports let through after a mute LED change even when they repeat the last one, so the new value goes out
	// with the next game report too (not only in the app's own write).
	public const int MuteLedResendReports = 3;

	// A game output within this time counts as the game streaming output (MuteLedDelivery).
	public static readonly TimeSpan GameStreamingWindow = TimeSpan.FromSeconds(1);

	// Appended to the bridge's own log lines (the host's " (slot N)"; empty for the first controller).
	public string LogSlot { get; init; } = string.Empty;

	private int appMuteLed = NoAppMuteLed;

	public int AppMuteLed
	{
		get => Volatile.Read(ref appMuteLed);
		set
		{
			if (Interlocked.Exchange(ref appMuteLed, value) == value)
			{
				return;
			}
			lock (lastBlock)
			{
				muteLedResends = MuteLedResendReports;
				muteLedChangedAt = Stopwatch.GetTimestamp();
				muteLedConfirmPending = value != NoAppMuteLed;
			}
		}
	}

	// Mute LED bookkeeping (guarded by lastBlock): game reports still to let through after a change, when it changed,
	// whether the first game report carrying it is still to be logged, and game reports that asked for another mute LED
	// value (the last one asked for) or for the LEDs to be released since the change.
	private int muteLedResends;
	private long muteLedChangedAt;
	private bool muteLedConfirmPending;
	private int gameMuteLedAsked, gameMuteLedValue, gameReleaseLedsAsked;

	// The game's light bar setup command (flag2 bit 1, byte 41) while the app shows the mute LED: the last value let
	// through (-1: none yet), and how often the game sent it / it was let through since the last mute LED change (guarded
	// by lastBlock). It is a one-shot command; streamed in every report (and, before, kept in the output state and so
	// repeated in the app's own mute LED write) it sets the controller's LEDs up again. Suspected (2026-11-12, not yet
	// confirmed on hardware) of the mute LED not following while a game drives the light (DualSense Mode on).
	private int lastGameLightbarSetup = -1;
	private int gameSetupSent, gameSetupPassed;

	private long lastGameOutput = long.MinValue / 2;

	// The game sent output (relayed or a repeat) within GameStreamingWindow.
	public bool GameOutputStreaming =>
		Stopwatch.GetElapsedTime(Interlocked.Read(ref lastGameOutput)) < GameStreamingWindow;

	// How the app's mute LED reaches the controller right now (for the "mute led:" log line).
	public string MuteLedDelivery => GameOutputStreaming ? "sent directly and merged into every game report (game streaming output)"
		: "sent directly (no game output)";

	// What games asked of the mute LED since the last change (one log line per kind, empty when nothing), and starts
	// counting again.
	public IReadOnlyList<string> TakeMuteLedOverrides()
	{
		int asked, value, release, setup, setupPassed, kept;
		lock (lastBlock)
		{
			(asked, value, release, setup, setupPassed) = (gameMuteLedAsked, gameMuteLedValue, gameReleaseLedsAsked, gameSetupSent, gameSetupPassed);
			gameMuteLedAsked = gameReleaseLedsAsked = gameSetupSent = gameSetupPassed = 0;
			kept = AppMuteLed;
		}
		if (kept == NoAppMuteLed)
		{
			return [];
		}
		List<string> lines = [];
		if (asked > 0)
		{
			lines.Add($"game asked for {MuteLedName(value)} {asked} times, kept {MuteLedName(kept)}");
		}
		if (release > 0)
		{
			lines.Add($"game asked to release the LEDs {release} times, kept {MuteLedName(kept)}");
		}
		if (setup > 0)
		{
			lines.Add($"game sent the light bar setup {setup} times, passed {setupPassed} (mute led {MuteLedName(kept)} sent again after each)");
		}
		return lines;
	}

	// Takes the one-shot LED commands (release LEDs, light bar setup) out of an output state, so a later report built from
	// it (the app's own writes) does not repeat them.
	public static void ClearOneShotLedCommands(Span<byte> block)
	{
		block[OutputBlock.Flag1] &= unchecked((byte)~OutputBlock.ReleaseLeds);
		block[OutputBlock.Flag2] &= unchecked((byte)~OutputBlock.LightbarSetupControl);
		block[OutputBlock.LightbarSetup] = 0;
	}

	public static string MuteLedName(int value) => value switch
	{
		OutputBlock.MuteLedOff => "off",
		OutputBlock.MuteLedOn => "on",
		2 => "pulse",
		_ => $"0x{value:X2}"
	};

	private bool hasBlock;

	private bool resendNext;

	private byte leftMotor, rightMotor;

	private readonly RumbleSynth synth = new();

	private int sawHaptics, sawTriggers, sawRumble;

	private double triggerIntensity = 1;

	private byte[]? fallbackTrigger;

	// The game's last effect per trigger (as sent, unscaled; null until it sets one). Guarded by lastBlock.
	private byte[]? gameRight, gameLeft;

	private long unknownTriggerEffects;

	private long lastAudio = long.MinValue / 2;

	private long inputs, outputs, audioBytes, speakerBytes;

	// 48 kHz frames of speaker silence since the last sound (starts beyond the tail: nothing to send yet).
	private long speakerSilentFrames = SpeakerTailFrames + 1;

	private volatile bool speakerEnabled;

	public ControllerBridge(BluetoothSession session, IVirtualPad pad)
	{
		this.session = session;
		this.pad = pad;
		session.InputBodyReceived += OnInput;
		pad.OutputReceived += OnOutput;
		pad.AudioReceived += OnAudio;
		rumblePump = new Thread(PumpRumble) { Name = "Rumble as haptics", IsBackground = true };
		rumblePump.Start();
	}

	public double GameHapticsLevel
	{
		get => converter.Level;
		set => converter.Level = value;
	}

	public double RumbleLevel { get; set; } = DefaultRumbleLevel;

	public RumbleFeel RumbleFeel { get; set; } = RumbleFeel.Soft;

	// Lets the game's light colour through; false keeps the user's light.
	public bool LightFromGame { get; set; } = true;

	private volatile bool gameSetLight;

	// The game set the light bar since the last profile change and may: the user's light effect stays out of its way.
	public bool GameControlsLight => LightFromGame && gameSetLight;

	// 0..1: scales the game's trigger effects (TriggerEffects.Scale).
	public double TriggerIntensity
	{
		get => Volatile.Read(ref triggerIntensity);
		set => Volatile.Write(ref triggerIntensity, Math.Clamp(value, 0, 1));
	}

	// Put on both triggers while the game sets no trigger effect; null leaves them alone.
	public byte[]? FallbackTrigger
	{
		get => Volatile.Read(ref fallbackTrigger);
		set => Volatile.Write(ref fallbackTrigger, value?.ToArray());
	}

	// Applies a profile; the triggers and light change at once (the game's last output is sent again with it).
	public void ApplyProfile(BridgeProfile profile)
	{
		GameHapticsLevel = profile.GameHapticsLevel;
		RumbleLevel = profile.RumbleLevel;
		RumbleFeel = profile.Feel;
		LightFromGame = profile.LightFromGame;
		gameSetLight = false;
		TriggerIntensity = profile.TriggerIntensity;
		FallbackTrigger = profile.FallbackTrigger;
		(byte[] right, byte[] left) = ControllerTriggers();
		session.UpdateOutput(b => OutputBlock.SetTriggers(b, right, left));
		ResendGameOutput();
	}

	// The trigger effects (right, left) the controller should have now: the game's last effect per trigger at the
	// current strength, or the fallback (else off) for a trigger the game has not set or has turned off.
	public (byte[] Right, byte[] Left) ControllerTriggers()
	{
		byte[] block = new byte[OutputBlock.Size];
		lock (lastBlock)
		{
			if (gameRight != null)
			{
				gameRight.CopyTo(block, OutputBlock.RightTrigger);
				block[OutputBlock.Flag0] |= OutputBlock.RightTriggerEffect;
			}
			if (gameLeft != null)
			{
				gameLeft.CopyTo(block, OutputBlock.LeftTrigger);
				block[OutputBlock.Flag0] |= OutputBlock.LeftTriggerEffect;
			}
		}
		ApplyTriggers(block, false);
		byte[] right = (block[OutputBlock.Flag0] & OutputBlock.RightTriggerEffect) != 0
			? block.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size).ToArray() : TriggerEffects.Off();
		byte[] left = (block[OutputBlock.Flag0] & OutputBlock.LeftTriggerEffect) != 0
			? block.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray() : TriggerEffects.Off();
		return (right, left);
	}

	// Game trigger effects in a mode TriggerEffects.Scale does not know (passed through unscaled), for diagnostics.
	public long UnknownTriggerEffects => Interlocked.Read(ref unknownTriggerEffects);

	// What the game sent since the last call (native support detection).
	public GameSignals TakeGameSignals() => new(Interlocked.Exchange(ref sawHaptics, 0) != 0,
		Interlocked.Exchange(ref sawTriggers, 0) != 0, Interlocked.Exchange(ref sawRumble, 0) != 0);

	public long Inputs => Interlocked.Read(ref inputs);

	public long Outputs => Interlocked.Read(ref outputs);

	public long AudioBytes => Interlocked.Read(ref audioBytes);

	// Game audio bytes handed to the controller speaker (all 4 channels; the session uses channels 1-2).
	public long SpeakerBytes => Interlocked.Read(ref speakerBytes);

	// EXPERIMENTAL, off by default: sends the game's speaker channels (1-2) to session.PushSpeakerAudio (the session needs
	// a Speaker for them to play). Haptics (channels 3-4) are not affected.
	public bool SpeakerEnabled
	{
		get => speakerEnabled;
		set
		{
			speakerEnabled = value;
			Interlocked.Exchange(ref speakerSilentFrames, SpeakerTailFrames + 1);
		}
	}

	public (byte Left, byte Right) Motors
	{
		get
		{
			lock (rumbleGate)
			{
				return (leftMotor, rightMotor);
			}
		}
	}

	// The trigger effects (right, left) of the game's last output, or null while the game has not set any.
	public (byte[] Right, byte[] Left)? GameTriggers
	{
		get
		{
			lock (lastBlock)
			{
				if (!hasBlock || (lastBlock[OutputBlock.Flag0] & (OutputBlock.RightTriggerEffect | OutputBlock.LeftTriggerEffect)) == 0)
				{
					return null;
				}
				return (lastBlock.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size).ToArray(),
					lastBlock.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray());
			}
		}
	}

	// Lets the game's next output through even if it repeats the last one (after the app changed the output itself).
	public void ResendGameOutput()
	{
		lock (lastBlock)
		{
			resendNext = true;
		}
	}

	public void Dispose()
	{
		disposed = true;
		session.InputBodyReceived -= OnInput;
		pad.OutputReceived -= OnOutput;
		pad.AudioReceived -= OnAudio;
		rumblePump.Join(500);
	}

	private void OnInput(ReadOnlySpan<byte> body)
	{
		Span<byte> report = stackalloc byte[UsbReports.InputSize];
		report[0] = UsbReports.InputId;
		body[..Math.Min(body.Length, InputState.BodySize)].CopyTo(report[1..]);
		Span<byte> input = report.Slice(1, InputState.BodySize);
		MicButton?.Process(input);
		Shortcuts?.Apply(input);
		if (Desktop?.Apply(input) != true)
		{
			Input.InputTransform transform = InputTransform;
			transform.Apply(input);
			GyroMouse?.Apply(transform.Gyro, input);
		}
		pad.PushInput(report);
		Interlocked.Increment(ref inputs);
	}

	private void OnOutput(byte[] report)
	{
		if (report.Length < 1 + OutputBlock.Size || report[0] != UsbReports.OutputId)
		{
			return;
		}
		ReadOnlySpan<byte> block = report.AsSpan(1, OutputBlock.Size);
		if ((block[OutputBlock.Flag1] & OutputBlock.LightbarControl) != 0)
		{
			gameSetLight = true;
		}
		Interlocked.Exchange(ref lastGameOutput, Stopwatch.GetTimestamp());
		byte[] sent;
		lock (lastBlock)
		{
			CountGameMuteLed(block);
			// Games resend the same state every frame (repeats still go out for a few reports after a mute LED change).
			if (hasBlock && !resendNext && muteLedResends == 0 && block.SequenceEqual(lastBlock))
			{
				return;
			}
			resendNext = false;
			if (muteLedResends > 0)
			{
				muteLedResends--;
			}
			block.CopyTo(lastBlock);
			hasBlock = true;
			sent = block.ToArray();
			if ((sent[OutputBlock.Flag0] & OutputBlock.RightTriggerEffect) != 0)
			{
				gameRight = sent.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size).ToArray();
			}
			if ((sent[OutputBlock.Flag0] & OutputBlock.LeftTriggerEffect) != 0)
			{
				gameLeft = sent.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray();
			}
		}
		if ((sent[OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect)) != 0)
		{
			lock (rumbleGate)
			{
				leftMotor = sent[OutputBlock.MotorLeft];
				rightMotor = sent[OutputBlock.MotorRight];
			}
			if (sent[OutputBlock.MotorLeft] != 0 || sent[OutputBlock.MotorRight] != 0)
			{
				Interlocked.Exchange(ref sawRumble, 1);
			}
		}
		ApplyTriggers(sent, true);
		if (!LightFromGame)
		{
			sent[OutputBlock.Flag1] &= unchecked((byte)~(OutputBlock.LightbarControl | OutputBlock.PlayerLedControl | OutputBlock.MuteLedControl
				| OutputBlock.ReleaseLeds));
			sent[OutputBlock.Flag2] &= unchecked((byte)~(OutputBlock.LightbarSetupControl | OutputBlock.LedBrightnessControl));
		}
		sent[OutputBlock.Flag0] &= unchecked((byte)~(OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect));
		sent[OutputBlock.MotorLeft] = sent[OutputBlock.MotorRight] = 0;
		sent[OutputBlock.Flag2] &= unchecked((byte)~OutputBlock.ImprovedRumble);
		bool setupPassed = FilterLightbarSetup(sent);
		int carried = NoAppMuteLed;
		session.UpdateOutput(b =>
		{
			sent.CopyTo(b, 0);
			// The game's audio fields are for a wired pad; keep the speaker route while the speaker is on.
			session.WriteSpeakerRoute(b);
			// Read under the session's output lock: a mute LED change (BridgeHost.SyncMuteLed sets AppMuteLed, then writes
			// through the same lock) can never be overtaken by a game report built with the old value.
			carried = ApplyAppMuteLed(b);
		});
		if (setupPassed)
		{
			// The game's (new) light bar setup went out: the mute LED again right after it, without the command.
			session.UpdateOutput(b =>
			{
				ClearOneShotLedCommands(b);
				ApplyAppMuteLed(b);
			});
		}
		Interlocked.Increment(ref outputs);
		if (carried != NoAppMuteLed)
		{
			LogFirstCarry(carried);
		}
	}

	// The whole block replaces the controller's output state: carry the app's mute LED (and its valid bit) in it, so
	// neither the game's value nor a cleared bit ever reaches the controller, and never let the game's "release LEDs"
	// through (it would hand the mute LED back to the controller's default). The value written, or NoAppMuteLed.
	private int ApplyAppMuteLed(byte[] b)
	{
		int muteLed = AppMuteLed;
		if (muteLed != NoAppMuteLed)
		{
			OutputBlock.SetMuteLed(b, (byte)muteLed);
			b[OutputBlock.Flag1] &= unchecked((byte)~OutputBlock.ReleaseLeds);
		}
		return muteLed;
	}

	// While the app shows the mute LED, the game's light bar setup command goes out only when its value changes (true: it
	// is in this report); repeats are taken out of the report.
	private bool FilterLightbarSetup(byte[] sent)
	{
		if (AppMuteLed == NoAppMuteLed || (sent[OutputBlock.Flag2] & OutputBlock.LightbarSetupControl) == 0)
		{
			return false;
		}
		lock (lastBlock)
		{
			gameSetupSent++;
			if (sent[OutputBlock.LightbarSetup] != lastGameLightbarSetup)
			{
				lastGameLightbarSetup = sent[OutputBlock.LightbarSetup];
				gameSetupPassed++;
				return true;
			}
		}
		sent[OutputBlock.Flag2] &= unchecked((byte)~OutputBlock.LightbarSetupControl);
		sent[OutputBlock.LightbarSetup] = 0;
		return false;
	}

	// Counts a game report asking for another mute LED value or for the LEDs to be released (caller holds lastBlock).
	private void CountGameMuteLed(ReadOnlySpan<byte> block)
	{
		int kept = AppMuteLed;
		if (kept == NoAppMuteLed)
		{
			return;
		}
		if ((block[OutputBlock.Flag1] & OutputBlock.MuteLedControl) != 0 && block[OutputBlock.MuteLed] != kept)
		{
			gameMuteLedAsked++;
			gameMuteLedValue = block[OutputBlock.MuteLed];
		}
		if ((block[OutputBlock.Flag1] & OutputBlock.ReleaseLeds) != 0)
		{
			gameReleaseLedsAsked++;
		}
	}

	// One line per mute LED change: the first game report that carried the new value to the controller.
	private void LogFirstCarry(int carried)
	{
		long changedAt;
		lock (lastBlock)
		{
			if (!muteLedConfirmPending || carried != AppMuteLed)
			{
				return;
			}
			muteLedConfirmPending = false;
			changedAt = muteLedChangedAt;
		}
		Diagnostics.AppLog.Write($"mute led{LogSlot}: {MuteLedName(carried)} carried by the first game report after the change (+{Stopwatch.GetElapsedTime(changedAt).TotalMilliseconds:F0} ms)");
	}

	// Scales the game's trigger effects, or puts the fallback effect on when the game sets none. fromGame: a new game
	// report (counts the native signal and unknown modes), not a re-apply of the last one.
	private void ApplyTriggers(byte[] block, bool fromGame)
	{
		Span<byte> right = block.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size);
		Span<byte> left = block.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size);
		bool rightSet = (block[OutputBlock.Flag0] & OutputBlock.RightTriggerEffect) != 0;
		bool leftSet = (block[OutputBlock.Flag0] & OutputBlock.LeftTriggerEffect) != 0;
		if (fromGame && ((rightSet && !TriggerEffects.IsOff(right)) || (leftSet && !TriggerEffects.IsOff(left))))
		{
			Interlocked.Exchange(ref sawTriggers, 1);
		}
		if (fromGame)
		{
			int unknown = (rightSet && !TriggerEffects.IsKnown(right) ? 1 : 0) + (leftSet && !TriggerEffects.IsKnown(left) ? 1 : 0);
			if (unknown != 0)
			{
				Interlocked.Add(ref unknownTriggerEffects, unknown);
			}
		}
		double k = TriggerIntensity;
		if (rightSet && k < 1)
		{
			TriggerEffects.Scale(right, k).CopyTo(right);
		}
		if (leftSet && k < 1)
		{
			TriggerEffects.Scale(left, k).CopyTo(left);
		}
		byte[]? fallback = FallbackTrigger;
		if (fallback != null)
		{
			if (!rightSet || TriggerEffects.IsOff(right))
			{
				fallback.CopyTo(right);
			}
			if (!leftSet || TriggerEffects.IsOff(left))
			{
				fallback.CopyTo(left);
			}
			block[OutputBlock.Flag0] |= OutputBlock.RightTriggerEffect | OutputBlock.LeftTriggerEffect;
		}
	}

	private static bool HasSound(ReadOnlySpan<byte> audio)
	{
		for (int i = 4; i + 3 < audio.Length; i += HapticsConverter.Channels * 2)
		{
			if (Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audio[i..])) > SilenceThreshold
				|| Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audio[(i + 2)..])) > SilenceThreshold)
			{
				return true;
			}
		}
		return false;
	}

	private void OnAudio(ReadOnlyMemory<byte> audio)
	{
		Interlocked.Add(ref audioBytes, audio.Length);
		if (Volatile.Read(ref sawHaptics) == 0 && HasSound(audio.Span))
		{
			Interlocked.Exchange(ref sawHaptics, 1);
		}
		lock (audioGate)
		{
			Interlocked.Exchange(ref lastAudio, Stopwatch.GetTimestamp());
			converter.Convert(audio.Span, (l, r) =>
			{
				(sbyte rl, sbyte rr) = NextRumble();
				session.Haptics.Push(Add(l, rl), Add(r, rr));
			});
		}
		session.WakeHaptics();
		bool speakerSound = HasSpeakerSound(audio.Span);
		TrackSpeakerChannels(speakerSound, audio.Length / (HapticsConverter.Channels * 2));
		if (speakerEnabled)
		{
			FeedSpeaker(audio.Span, speakerSound);
		}
	}

	// Speaker-channel sounds logged per bridge at most (a game that talks all the time must not flood the log).
	public const int SpeakerActivityLogLimit = 100;

	// Whether channels 1-2 carry sound (after SpeakerTailMs of silence they count as quiet again), frames since the last
	// sound, frames since the sound started, lines written. Only touched on the audio thread (OnAudio).
	private bool speakerChannelsActive;
	private long speakerChannelsQuietFrames, speakerChannelsSoundFrames;
	private int speakerActivityLines;

	// Logs when the game's speaker channels start and stop carrying sound, whether or not the speaker is on, so a test can
	// tell whether a sound reached the virtual controller's speaker channels at all (and how the controller speaker coped).
	private void TrackSpeakerChannels(bool sound, int frames)
	{
		if (sound)
		{
			speakerChannelsQuietFrames = 0;
			if (!speakerChannelsActive)
			{
				speakerChannelsActive = true;
				speakerChannelsSoundFrames = 0;
				LogSpeakerActivity($"bridge: game sound on the speaker channels (1-2), controller speaker {(speakerEnabled ? "on" : "off")}");
			}
		}
		else if (speakerChannelsActive && (speakerChannelsQuietFrames += frames) > SpeakerTailFrames)
		{
			speakerChannelsActive = false;
			Audio.SpeakerAudioSender? s = session.Speaker;
			long ms = (speakerChannelsSoundFrames - speakerChannelsQuietFrames + frames) * 1000 / Audio.SpeakerAudioSender.InputRate;
			LogSpeakerActivity(s != null
				? $"bridge: speaker channels quiet again after {Math.Max(0, ms)} ms ({s.Underruns} underruns, {s.DroppedSamples} samples dropped so far, {SpeakerBufferText(s)})"
				: $"bridge: speaker channels quiet again after {Math.Max(0, ms)} ms (controller speaker off, not played)");
		}
		if (speakerChannelsActive)
		{
			speakerChannelsSoundFrames += frames;
		}
	}

	// "buffer 38/43/51 ms min/target/max, rate trim +0.12 %": how full the speaker buffer stayed while the sound was sent
	// (before each frame) and the clock correction at the end.
	internal static string SpeakerBufferText(Audio.SpeakerAudioSender s)
	{
		(int Min, int Max)? levels = s.ReadLevels();
		string fill = levels is { } l
			? $"{Audio.SpeakerAudioSender.Milliseconds(l.Min)}/{Audio.SpeakerAudioSender.Milliseconds(Audio.SpeakerAudioSender.TargetSamples)}/{Audio.SpeakerAudioSender.Milliseconds(l.Max)} ms min/target/max"
			: $"-/{Audio.SpeakerAudioSender.Milliseconds(Audio.SpeakerAudioSender.TargetSamples)}/- ms min/target/max";
		return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"buffer {fill}, rate trim {s.Trim * 100:+0.00;-0.00;0.00} %");
	}

	private void LogSpeakerActivity(string line)
	{
		if (speakerActivityLines < SpeakerActivityLogLimit)
		{
			Diagnostics.AppLog.Write(line);
		}
		else if (speakerActivityLines == SpeakerActivityLogLimit)
		{
			Diagnostics.AppLog.Write($"bridge: speaker channel lines stop here ({SpeakerActivityLogLimit} written)");
		}
		speakerActivityLines++;
	}

	// Sound on channels 1-2 goes to the speaker, then up to SpeakerTailMs of silence (so the end isn't cut off); longer
	// silence is not sent, so the session's speaker buffer runs dry and the stream falls back to haptics-only reports.
	private void FeedSpeaker(ReadOnlySpan<byte> audio, bool sound)
	{
		int frames = audio.Length / (HapticsConverter.Channels * 2);
		long silent = sound ? 0 : Interlocked.Add(ref speakerSilentFrames, frames);
		if (silent == 0)
		{
			Interlocked.Exchange(ref speakerSilentFrames, 0);
		}
		if (silent - frames > SpeakerTailFrames)
		{
			return;
		}
		session.PushSpeakerAudio(audio);
		Interlocked.Add(ref speakerBytes, audio.Length);
	}

	private static bool HasSpeakerSound(ReadOnlySpan<byte> audio)
	{
		for (int i = 0; i + 3 < audio.Length; i += HapticsConverter.Channels * 2)
		{
			if (Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audio[i..])) > SpeakerSilenceThreshold
				|| Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audio[(i + 2)..])) > SpeakerSilenceThreshold)
			{
				return true;
			}
		}
		return false;
	}

	private void PumpRumble()
	{
		while (!disposed)
		{
			bool audioIdle = Stopwatch.GetTimestamp() - Interlocked.Read(ref lastAudio) > AudioIdleTicks;
			if (audioIdle && Motors != (0, 0))
			{
				lock (audioGate)
				{
					while (session.Haptics.Buffered < RumbleAheadFrames)
					{
						(sbyte l, sbyte r) = NextRumble();
						session.Haptics.Push(l, r);
					}
				}
				session.WakeHaptics();
			}
			Thread.Sleep(5);
		}
	}

	// One 3 kHz frame of the rumble tones; silence (and the tones restart) while the motors are off.
	private (sbyte Left, sbyte Right) NextRumble()
	{
		lock (rumbleGate)
		{
			return synth.Next(leftMotor, rightMotor, RumbleLevel, RumbleFeel);
		}
	}

	private static sbyte Add(sbyte a, sbyte b) => (sbyte)Math.Clamp(a + b, -127, 127);
}
