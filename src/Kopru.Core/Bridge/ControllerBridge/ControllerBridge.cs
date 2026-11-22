// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Bluetooth;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// Joins a Bluetooth DualSense to the virtual wired one: the controller's input goes to games unchanged, the games'
// output (triggers, lights) goes to the controller, their haptics audio becomes the controller's haptics stream, and
// their classic rumble is played as haptics too (rumble bits over Bluetooth would turn audio haptics off).
public sealed partial class ControllerBridge : IDisposable
{
	public const double DefaultRumbleLevel = 1.0;

	// Rumble tones (soft feel): the heavy left motor low, the light right motor higher.
	public const double LeftRumbleHz = RumbleSynth.SoftLeftHz;
	public const double RightRumbleHz = RumbleSynth.SoftRightHz;

	// Haptics audio quieter than this (16-bit sample) counts as silence for native support detection.
	public const int SilenceThreshold = 256;

	// While the game streams audio, rumble is mixed into it; otherwise the haptics stream tops it up to this many frames
	// before each report (BluetoothSession.HapticsRefill).
	public const int RumbleAheadFrames = 64;

	// The game's speaker channels (1-2) silent for longer than this: speaker audio stops, so the controller stream goes
	// back from 0x36 to plain haptics (0x32) once the buffered speaker audio has played.
	public const int SpeakerTailMs = 200;
	// Speaker samples quieter than this (16-bit) count as silence: games stream digital zeros or near-zero dither.
	public const int SpeakerSilenceThreshold = 32;
	public const int SpeakerTailFrames = Audio.SpeakerAudioSender.InputRate * SpeakerTailMs / 1000;

	// The game's audio or audio haptics arrived within this time (50 ms, in Time's timestamp units): the rumble refill
	// leaves the timing to them.
	private long AudioIdleTicks => Time.TimestampFrequency / 20;

	private readonly BluetoothSession session;

	private readonly IVirtualPad pad;

	private readonly HapticsConverter converter = new();

	private readonly object audioGate = new();

	private readonly object rumbleGate = new();

	private readonly byte[] lastBlock = new byte[OutputBlock.Size];

	// Converted frames of one audio packet (guarded by audioGate; grown when a packet is larger than any before).
	private sbyte[] frameLeft = new sbyte[Math.Max(RumbleAheadFrames, HapticsConverter.MaxFrames(8192))];
	private sbyte[] frameRight = new sbyte[Math.Max(RumbleAheadFrames, HapticsConverter.MaxFrames(8192))];

	private readonly Action refill;

	private long rumbleRefills;

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

	// The clock of the game output timing (GameOutputStreaming, the game's hold on the light) and of the audio timing the
	// rumble refill follows (AudioIdleTicks). Tests replace it.
	public TimeProvider Time { get; init; } = TimeProvider.System;

	private TimeSpan SinceGameOutput => Time.GetElapsedTime(Interlocked.Read(ref lastGameOutput));

	// The game sent output (relayed or a repeat) within GameStreamingWindow.
	public bool GameOutputStreaming => SinceGameOutput < GameStreamingWindow;

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
		if (pad is ITriggerRumbleSource triggerRumble)
		{
			triggerRumble.TriggerRumbleChanged += PlayTriggerRumble;
		}
		pad.HostReleased += OnHostReleased;
		refill = RefillRumble;
		session.HapticsRefill = refill;
	}

	// Times rumble was topped up while the game sent no audio (diagnostics, tests).
	public long RumbleRefills => Interlocked.Read(ref rumbleRefills);

	public double GameHapticsLevel
	{
		get => converter.Level;
		set => converter.Level = value;
	}

	// Changing the level or feel while a motor runs starts the stream again: it may have gone idle while the rumble was
	// silent (level 0), and identical game reports (motors unchanged) are dropped, so nothing else would restart it.
	public double RumbleLevel
	{
		get => Volatile.Read(ref rumbleLevel);
		set
		{
			Volatile.Write(ref rumbleLevel, value);
			KickRumble();
		}
	}

	private double rumbleLevel = DefaultRumbleLevel;

	public RumbleFeel RumbleFeel
	{
		get => rumbleFeel;
		set
		{
			rumbleFeel = value;
			KickRumble();
		}
	}

	private volatile RumbleFeel rumbleFeel = RumbleFeel.Soft;

	// Lets the game's light colour through; false keeps the user's light.
	public bool LightFromGame { get; set; } = true;

	// No game output for this long: the game's hold on the light ends and the user's light comes back (a game that keeps
	// streaming its output keeps the light; one that writes again with the light bar takes it back at once).
	public static readonly TimeSpan DefaultGameLightIdle = TimeSpan.FromSeconds(3);

	public TimeSpan GameLightIdle { get; set; } = DefaultGameLightIdle;

	// Set by a game report with the light bar flag (or carried over from the previous bridge, SeedGameLight); cleared by
	// the end of the game's output session (EndGameSession: no game output for GameLightIdle, or the active game ended).
	private volatile bool gameSetLight;

	private bool GameLightHeld => gameSetLight && SinceGameOutput < GameLightIdle;

	// The game set the light bar, may, and still sends output: the user's light effect stays out of its way.
	public bool GameControlsLight => LightFromGame && GameLightHeld;

	// 0..1: scales the game's trigger effects (TriggerEffects.Scale).
	public double TriggerIntensity
	{
		get => Volatile.Read(ref triggerIntensity);
		set => Volatile.Write(ref triggerIntensity, Math.Clamp(value, 0, 1));
	}

	// Put on a trigger the game has not set (or turned off), until the game sends its first real trigger effect in the game
	// session (TriggersSeen): a native game that turns its triggers off later keeps them off. Null leaves them alone.
	public byte[]? FallbackTrigger
	{
		get => Volatile.Read(ref fallbackTrigger);
		set => Volatile.Write(ref fallbackTrigger, value?.ToArray());
	}

	// Custom Trigger (null: the game's effect): put on that trigger over the game's effect, the fallback, a trigger rumble
	// and a mod's effect, whatever each report sets; not scaled by TriggerIntensity.
	public byte[]? CustomRightTrigger
	{
		get => Volatile.Read(ref customRight);
		set => Volatile.Write(ref customRight, value?.ToArray());
	}

	public byte[]? CustomLeftTrigger
	{
		get => Volatile.Read(ref customLeft);
		set => Volatile.Write(ref customLeft, value?.ToArray());
	}

	private byte[]? customRight;
	private byte[]? customLeft;

	// The game sent a trigger effect other than off in this game session (the fallback effect stays off from then on).
	public bool TriggersSeen => triggersSeen;

	// The game session the bridge runs in (BridgeProfile.GameSession); a new one forgets TriggersSeen.
	public long GameSession
	{
		get => Interlocked.Read(ref triggerSession);
		set
		{
			if (Interlocked.Exchange(ref triggerSession, value) != value)
			{
				triggersSeen = false;
			}
		}
	}

	private volatile bool triggersSeen;
	private long triggerSession;

	// Applies a profile; the triggers and light change at once (the game's last output is sent again with it).
	public void ApplyProfile(BridgeProfile profile)
	{
		GameSession = profile.GameSession;
		GameHapticsLevel = profile.GameHapticsLevel;
		RumbleLevel = profile.RumbleLevel;
		RumbleFeel = profile.Feel;
		LightFromGame = profile.LightFromGame;
		// The game's hold on the light survives a profile change (a strength slider, the mode switch): while the new
		// profile lets the game's light through and the hold has not run out, its last light is shown again. A profile
		// that keeps the user's light makes GameControlsLight false (the host then has the user's light sent).
		RestoreGameLight();
		TriggerIntensity = profile.TriggerIntensity;
		FallbackTrigger = profile.FallbackTrigger;
		CustomRightTrigger = profile.CustomRightTrigger;
		CustomLeftTrigger = profile.CustomLeftTrigger;
		TriggerRumbleFeel = profile.PlayedTriggerRumble;
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

	// Haptics audio silent for longer than this before a sound: the sound counts as the game starting to use haptics again.
	public static readonly TimeSpan GameHapticsQuietGap = TimeSpan.FromSeconds(2);

	// The game started using the controller (GameUse): raised once when a strong sign appears after its absence (a trigger
	// effect after none or off, the motors starting after they stood still, haptics sound after GameHapticsQuietGap of
	// silence), not for every report while it lasts. Only the game's side of the virtual pad counts (its output reports and
	// audio); the app's own tests write to the controller session directly and never pass here. Raised on the thread that
	// got the game's report or audio: handlers must be short.
	public event Action<GameUse>? GameUseStarted;

	// No GameUseStarted for this long after the virtual pad was plugged in (BridgeHost: a new connection or a pad swap): a
	// game still running sends its held state again to the new pad, which is not a game starting to use the controller.
	public static readonly TimeSpan GameUseSettle = TimeSpan.FromSeconds(5);

	// The game's trigger effects were not all off at its last output (written under lastBlock), its motors were running at
	// its last rumble output (written under rumbleGate), its last haptics sound, and until when GameUseStarted stays quiet.
	private volatile bool gameTriggersOn, gameRumbleOn;
	private long lastGameHapticsSound = long.MinValue / 2;
	private long gameUseQuietUntil = long.MinValue;

	// What the game is doing with the controller right now (the level behind GameUseStarted): a trigger effect not off,
	// the motors running, or haptics sound within GameHapticsQuietGap. Null: none of them. Lock-free.
	public GameUse? GameUseNow =>
		gameTriggersOn ? GameUse.TriggerEffect
		: gameRumbleOn ? GameUse.Rumble
		: Stopwatch.GetElapsedTime(Interlocked.Read(ref lastGameHapticsSound)) <= GameHapticsQuietGap ? GameUse.Haptics
		: null;

	// The edge state (what counts as already on), to carry over to the next bridge of the same controller or slot.
	public GameUseState CaptureGameUse() => new(gameTriggersOn, gameRumbleOn, Interlocked.Read(ref lastGameHapticsSound), triggersSeen,
		Interlocked.Read(ref triggerSession));

	// Takes over the edge state of the previous bridge, so a game still holding a trigger effect, its motors or its haptics
	// sound does not count as starting again.
	public void SeedGameUse(GameUseState state)
	{
		lock (lastBlock)
		{
			gameTriggersOn = state.Triggers;
			// The same game session goes on (a pad swap or a reconnect): a game that sent its trigger effects keeps them.
			if (state.TriggersSeen && state.TriggerSession == Interlocked.Read(ref triggerSession))
			{
				triggersSeen = true;
			}
		}
		lock (rumbleGate)
		{
			gameRumbleOn = state.Rumble;
		}
		Interlocked.Exchange(ref lastGameHapticsSound, state.HapticsAt);
	}

	// GameUseStarted stays quiet for `length` from now (the edge state still follows the game).
	public void QuietGameUse(TimeSpan length) =>
		Interlocked.Exchange(ref gameUseQuietUntil, Stopwatch.GetTimestamp() + (long)(length.TotalSeconds * Stopwatch.Frequency));

	private void RaiseGameUse(GameUse use)
	{
		if (Stopwatch.GetTimestamp() < Interlocked.Read(ref gameUseQuietUntil))
		{
			return;
		}
		try
		{
			GameUseStarted?.Invoke(use);
		}
		catch (Exception)
		{
			// A handler failing never breaks the bridge.
		}
	}

	// What the game sent since the last call (native support detection).
	public GameSignals TakeGameSignals() => new(Interlocked.Exchange(ref sawHaptics, 0) != 0,
		Interlocked.Exchange(ref sawTriggers, 0) != 0, Interlocked.Exchange(ref sawRumble, 0) != 0, TakeTriggerRumbleSignal());

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
		// The last rumble must not outlive the bridge (app exit, bridge stop, pad swap): the refill stops topping it up.
		StopRumble("bridge stopped");
		disposed = true;
		session.InputBodyReceived -= OnInput;
		pad.OutputReceived -= OnOutput;
		pad.AudioReceived -= OnAudio;
		if (pad is ITriggerRumbleSource triggerRumble)
		{
			triggerRumble.TriggerRumbleChanged -= PlayTriggerRumble;
		}
		EndTriggerRumble();
		pad.HostReleased -= OnHostReleased;
		if (session.HapticsRefill == refill)
		{
			session.HapticsRefill = null;
		}
	}

	// The host let go of the virtual pad (USB/IP detach, the host driver unplugged it): nobody is left to stop the rumble.
	private void OnHostReleased() => StopAllRumble("the host released the virtual pad");

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
		ApplyModThresholds(input);
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
		// A game taking the light (back): its report goes out even when it repeats the last one, since the user's light
		// may have been written meanwhile (the hold ran out or the game ended).
		bool takesLight = false;
		if ((block[OutputBlock.Flag1] & OutputBlock.LightbarControl) != 0)
		{
			takesLight = !GameLightHeld;
			gameSetLight = true;
		}
		RememberGameLight(block);
		Interlocked.Exchange(ref lastGameOutput, Time.GetTimestamp());
		NoteRumbleReport(block);
		CountGameLight(block);
		byte[] sent;
		bool triggersStarted = false;
		bool firstEffect = false;
		lock (lastBlock)
		{
			CountGameMuteLed(block);
			// Games resend the same state every frame (repeats still go out for a few reports after a mute LED change).
			if (hasBlock && !resendNext && !takesLight && muteLedResends == 0 && block.SequenceEqual(lastBlock))
			{
				sent = [];
			}
			else
			{
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
				bool triggersOn = (gameRight != null && !TriggerEffects.IsOff(gameRight)) || (gameLeft != null && !TriggerEffects.IsOff(gameLeft));
				triggersStarted = triggersOn && !gameTriggersOn;
				gameTriggersOn = triggersOn;
				if (triggersOn && !triggersSeen)
				{
					triggersSeen = true;
					firstEffect = true;
				}
			}
		}
		if (sent.Length == 0)
		{
			// Nothing new goes out; a light change held back by the log rate limit is written now.
			FlushGameLightLog();
			return;
		}
		if (triggersStarted)
		{
			RaiseGameUse(GameUse.TriggerEffect);
		}
		if (MotorsAsked(sent) is { } asked)
		{
			bool running = asked.Left != 0 || asked.Right != 0;
			bool motorsStarted;
			lock (rumbleGate)
			{
				motorsStarted = running && !gameRumbleOn;
				gameRumbleOn = running;
				leftMotor = asked.Left;
				rightMotor = asked.Right;
			}
			if (motorsStarted)
			{
				RaiseGameUse(GameUse.Rumble);
			}
			if (running)
			{
				Interlocked.Exchange(ref sawRumble, 1);
				KickRumble();
			}
		}
		ApplyTriggers(sent, true);
		if (firstEffect && FallbackTrigger != null)
		{
			// The fallback effect may still sit on the trigger this report leaves alone: off with it.
			ClearUnsetTriggers(sent);
		}
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
		byte outFlag1 = 0;
		session.UpdateOutput(b =>
		{
			sent.CopyTo(b, 0);
			// The game's audio fields are for a wired pad; keep the speaker route while the speaker is on.
			session.WriteSpeakerRoute(b);
			// Read under the session's output lock: a mute LED change (BridgeHost.SyncMuteLed sets AppMuteLed, then writes
			// through the same lock) can never be overtaken by a game report built with the old value.
			carried = ApplyAppMuteLed(b);
			// Read before the mod's light: only the game's own light counts as a game light write.
			outFlag1 = b[OutputBlock.Flag1];
			// A mod's light (DSX mod support) over the game's; the app's mute LED still wins over the mod's.
			if (ModOutput != null)
			{
				OverlayModLight(b);
				ApplyAppMuteLed(b);
			}
		});
		NoteForwardedLight(block, sent, outFlag1);
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

	// Scales the game's trigger effects, or puts the fallback effect on when the game sets none (only until the game sent
	// its first real trigger effect in the game session: TriggersSeen); a trigger rumble (ControllerBridgeImpulse) and a
	// mod's effects go over them, a Custom Trigger over everything. fromGame: a new game
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
		if (fallback != null && !triggersSeen)
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
		ApplyImpulseTriggers(block, k);
		ApplyModTriggers(block, fromGame, rightSet, leftSet, k);
		ApplyCustomTriggers(block);
	}

	// Custom Trigger last: the chosen effect on its trigger whatever else is there (every report carries it).
	private void ApplyCustomTriggers(byte[] block)
	{
		if (CustomRightTrigger is { } right)
		{
			right.CopyTo(block, OutputBlock.RightTrigger);
			block[OutputBlock.Flag0] |= OutputBlock.RightTriggerEffect;
		}
		if (CustomLeftTrigger is { } left)
		{
			left.CopyTo(block, OutputBlock.LeftTrigger);
			block[OutputBlock.Flag0] |= OutputBlock.LeftTriggerEffect;
		}
	}

	// Turns off each trigger the block does not set (with its valid bit), so nothing left from the fallback effect stays on.
	private static void ClearUnsetTriggers(byte[] block)
	{
		if ((block[OutputBlock.Flag0] & OutputBlock.RightTriggerEffect) == 0)
		{
			TriggerEffects.Off().CopyTo(block, OutputBlock.RightTrigger);
			block[OutputBlock.Flag0] |= OutputBlock.RightTriggerEffect;
		}
		if ((block[OutputBlock.Flag0] & OutputBlock.LeftTriggerEffect) == 0)
		{
			TriggerEffects.Off().CopyTo(block, OutputBlock.LeftTrigger);
			block[OutputBlock.Flag0] |= OutputBlock.LeftTriggerEffect;
		}
	}

	// One pass over a packet: sound on the haptics channels (3-4, above SilenceThreshold; only looked for when wanted)
	// and on the speaker channels (1-2, above SpeakerSilenceThreshold). Stops as soon as both answers are known.
	internal static (bool Haptics, bool Speaker) ScanSound(ReadOnlySpan<byte> audio, bool wantHaptics)
	{
		bool haptics = false, speaker = false;
		for (int i = 0; i + HapticsConverter.Channels * 2 <= audio.Length; i += HapticsConverter.Channels * 2)
		{
			if (!speaker && (Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audio[i..])) > SpeakerSilenceThreshold
				|| Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audio[(i + 2)..])) > SpeakerSilenceThreshold))
			{
				speaker = true;
			}
			if (wantHaptics && !haptics && (Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audio[(i + 4)..])) > SilenceThreshold
				|| Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audio[(i + 6)..])) > SilenceThreshold))
			{
				haptics = true;
			}
			if (speaker && (haptics || !wantHaptics))
			{
				break;
			}
		}
		return (haptics, speaker);
	}

	private void OnAudio(ReadOnlyMemory<byte> audio)
	{
		ReadOnlySpan<byte> span = audio.Span;
		Interlocked.Add(ref audioBytes, span.Length);
		(bool hapticsSound, bool speakerSound) = ScanSound(span, true);
		if (hapticsSound)
		{
			Interlocked.Exchange(ref sawHaptics, 1);
			// Measured on the game's own channels 3-4 (above SilenceThreshold), not on the session's haptics mixer, which
			// also carries the app's test tones and the rumble played as haptics.
			long now = Stopwatch.GetTimestamp();
			long before = Interlocked.Exchange(ref lastGameHapticsSound, now);
			if (Stopwatch.GetElapsedTime(before, now) > GameHapticsQuietGap)
			{
				RaiseGameUse(GameUse.Haptics);
			}
		}
		lock (audioGate)
		{
			Interlocked.Exchange(ref lastAudio, Time.GetTimestamp());
			int most = HapticsConverter.MaxFrames(span.Length);
			if (frameLeft.Length < most)
			{
				frameLeft = new sbyte[most];
				frameRight = new sbyte[most];
			}
			int frames = converter.Convert(span, frameLeft, frameRight);
			MixOverlay(frameLeft.AsSpan(0, frames), frameRight.AsSpan(0, frames));
			MixRumble(frameLeft.AsSpan(0, frames), frameRight.AsSpan(0, frames));
			session.Haptics.Push(frameLeft.AsSpan(0, frames), frameRight.AsSpan(0, frames));
		}
		// Every packet, silent or not, keeps the stream running (it stops only once the game's audio stops altogether).
		session.FeedHaptics();
		TrackSpeakerChannels(speakerSound, span.Length / (HapticsConverter.Channels * 2));
		if (speakerEnabled)
		{
			FeedSpeaker(span, speakerSound);
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

	// Starts the stream for a running motor (a no-op while it already runs): it tops the rumble up itself while the game
	// sends no audio (and mixes it into the game's audio while it does), and stops after its silent tail once the motors
	// are off or the rumble is silent.
	private void KickRumble()
	{
		if (disposed || Motors == (0, 0) || RumbleLevel <= 0)
		{
			return;
		}
		RefillRumble();
		session.StartHaptics("rumble");
	}

	// On the haptics thread before each report: while the game sends no audio and no audio haptics arrive (for AudioIdleTicks)
	// and a motor runs, keeps
	// RumbleAheadFrames of rumble buffered. Nothing to do (and nothing allocated) otherwise.
	private void RefillRumble()
	{
		if (disposed)
		{
			return;
		}
		CheckRumbleStale();
		if (Time.GetTimestamp() - Interlocked.Read(ref lastAudio) <= AudioIdleTicks || AudioHapticsFlowing || Motors == (0, 0))
		{
			return;
		}
		lock (audioGate)
		{
			// Only the idle cushion of silence buffered: drop it, so the rumble starts at once instead of behind ~21 ms.
			if (session.Haptics.Audible == 0)
			{
				session.Haptics.TrimSilence(0);
			}
			int missing = RumbleAheadFrames - session.Haptics.Buffered;
			if (missing <= 0)
			{
				return;
			}
			Span<sbyte> left = frameLeft.AsSpan(0, Math.Min(missing, frameLeft.Length));
			Span<sbyte> right = frameRight.AsSpan(0, left.Length);
			left.Clear();
			right.Clear();
			MixRumble(left, right);
			session.Haptics.Push(left, right);
		}
		Interlocked.Increment(ref rumbleRefills);
	}

	// Adds the rumble tones to a block of frames (one lock for the block).
	private void MixRumble(Span<sbyte> left, Span<sbyte> right)
	{
		// Fades out a rumble no game report holds any more (CheckRumbleStale then zeroes the motors).
		double level = RumbleLevel * RumbleGain;
		RumbleFeel feel = RumbleFeel;
		lock (rumbleGate)
		{
			for (int i = 0; i < left.Length; i++)
			{
				(sbyte l, sbyte r) = synth.Next(leftMotor, rightMotor, level, feel);
				left[i] = Add(left[i], l);
				right[i] = Add(right[i], r);
			}
		}
	}

	private static sbyte Add(sbyte a, sbyte b) => (sbyte)Math.Clamp(a + b, -127, 127);
}
