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

	private static readonly long AudioIdleTicks = Stopwatch.Frequency / 20;

	private readonly BluetoothSession session;

	private readonly VirtualDualSense pad;

	private readonly HapticsConverter converter = new();

	private readonly object audioGate = new();

	private readonly object rumbleGate = new();

	private readonly byte[] lastBlock = new byte[OutputBlock.Size];

	private readonly Thread rumblePump;

	private volatile bool disposed;

	// Stick/trigger tuning applied to input before games see it; swap live (atomic reference write).
	public volatile Input.InputTransform InputTransform = Input.InputTransform.Identity;

	private bool hasBlock;

	private bool resendNext;

	private byte leftMotor, rightMotor;

	private readonly RumbleSynth synth = new();

	private int sawHaptics, sawTriggers, sawRumble;

	private double triggerIntensity = 1;

	private byte[]? fallbackTrigger;

	private long lastAudio = long.MinValue / 2;

	private long inputs, outputs, audioBytes;

	public ControllerBridge(BluetoothSession session, VirtualDualSense pad)
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
		TriggerIntensity = profile.TriggerIntensity;
		FallbackTrigger = profile.FallbackTrigger;
		(byte[] Right, byte[] Left)? game = GameTriggers;
		byte[] right, left;
		if (game is { } g)
		{
			right = TriggerEffects.Scale(g.Right, TriggerIntensity);
			left = TriggerEffects.Scale(g.Left, TriggerIntensity);
		}
		else
		{
			right = left = profile.FallbackTrigger ?? TriggerEffects.Off();
		}
		session.UpdateOutput(b => OutputBlock.SetTriggers(b, right, left));
		ResendGameOutput();
	}

	// What the game sent since the last call (native support detection).
	public GameSignals TakeGameSignals() => new(Interlocked.Exchange(ref sawHaptics, 0) != 0,
		Interlocked.Exchange(ref sawTriggers, 0) != 0, Interlocked.Exchange(ref sawRumble, 0) != 0);

	public long Inputs => Interlocked.Read(ref inputs);

	public long Outputs => Interlocked.Read(ref outputs);

	public long AudioBytes => Interlocked.Read(ref audioBytes);

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
		InputTransform.Apply(report.Slice(1, InputState.BodySize));
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
		byte[] sent;
		lock (lastBlock)
		{
			// Games resend the same state every frame.
			if (hasBlock && !resendNext && block.SequenceEqual(lastBlock))
			{
				return;
			}
			resendNext = false;
			block.CopyTo(lastBlock);
			hasBlock = true;
			sent = block.ToArray();
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
		ApplyTriggers(sent);
		if (!LightFromGame)
		{
			sent[OutputBlock.Flag1] &= unchecked((byte)~(OutputBlock.LightbarControl | OutputBlock.PlayerLedControl));
			sent[OutputBlock.Flag2] &= unchecked((byte)~OutputBlock.LightbarSetupControl);
		}
		sent[OutputBlock.Flag0] &= unchecked((byte)~(OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect));
		sent[OutputBlock.MotorLeft] = sent[OutputBlock.MotorRight] = 0;
		sent[OutputBlock.Flag2] &= unchecked((byte)~OutputBlock.ImprovedRumble);
		session.UpdateOutput(b => sent.CopyTo(b, 0));
		Interlocked.Increment(ref outputs);
	}

	// Scales the game's trigger effects, or puts the fallback effect on when the game sets none.
	private void ApplyTriggers(byte[] block)
	{
		Span<byte> right = block.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size);
		Span<byte> left = block.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size);
		bool rightSet = (block[OutputBlock.Flag0] & OutputBlock.RightTriggerEffect) != 0;
		bool leftSet = (block[OutputBlock.Flag0] & OutputBlock.LeftTriggerEffect) != 0;
		if ((rightSet && !TriggerEffects.IsOff(right)) || (leftSet && !TriggerEffects.IsOff(left)))
		{
			Interlocked.Exchange(ref sawTriggers, 1);
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
