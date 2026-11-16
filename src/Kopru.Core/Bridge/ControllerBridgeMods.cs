// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Mods;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// DSX mod effects (docs/DSX-COMPAT.md): what a game mod asked of this controller (ModOutput), layered over the game.
// - Triggers: a trigger the mod set gets the mod's effect, over the game's own effect, over the fallback trigger and over
//   nothing; it is scaled by the trigger strength like a game's. The game's effects are still remembered (and count for
//   native support and the fallback rule): when the mod's effects end, the game's last effects come back. A game that
//   sends its own (not off) effects while the mod's are active is logged once per mod session ("dsx: the game sent its
//   own trigger effects while a mod's are active; the mod's stay"): the mod exists because the game has none, so it wins.
// - Light: the mod's light bar colour, player LEDs and mute LED are written over the game's and the user's light (the
//   user's light keeps animating underneath and comes back when the mod's light ends). The app's own mic mute LED (the
//   mic button as the microphone switch) still wins over the mod's.
// - Thresholds: below the mod's threshold the trigger reads as released (analog 0, its digital bit off).
public sealed partial class ControllerBridge
{
	private ModOutput? modOutput;

	// Guarded by lastBlock: the game's own trigger effects arrived while the mod's were active, logged once per mod output.
	private bool modConflictLogged;
	private long modConflicts;

	// The mod's light bar and player LEDs may show: false while the user's light is off (brightness 0, which Low Power
	// also sets); the mod's mute LED and triggers are not affected. The host sets it with the user's light.
	public bool ModLightAllowed
	{
		get => Volatile.Read(ref modLightAllowed);
		set => Volatile.Write(ref modLightAllowed, value);
	}

	private bool modLightAllowed = true;

	// What a mod asked of this controller now; null: no mod effects.
	public ModOutput? ModOutput => Volatile.Read(ref modOutput);

	// Game reports with their own trigger effects while a mod's were active (diagnostics, tests).
	public long ModConflicts => Interlocked.Read(ref modConflicts);

	// The mod's output carried over from the previous bridge (a pad swap, a reconnect); nothing is written here.
	public void SeedModOutput(ModOutput? value) => Volatile.Write(ref modOutput, value is { IsEmpty: true } ? null : value);

	// Sets (null ends) the mod's output and writes the triggers and the mod's light to the controller at once. True when
	// the mod's light ended, so the caller has the user's (or the game's) light written again.
	public bool SetModOutput(ModOutput? value)
	{
		ModOutput? next = value is { IsEmpty: true } ? null : value;
		ModOutput? before = Interlocked.Exchange(ref modOutput, next);
		if (next == null)
		{
			lock (lastBlock)
			{
				modConflictLogged = false;
			}
		}
		WriteModOutput(before);
		return before?.HasLight == true && next?.HasLight != true;
	}

	// Writes the triggers as they should be now (the mod's over the game's) and the mod's light. A mute LED the mod set
	// and no longer sets goes back to off (unless the app shows its own).
	public void WriteModOutput(ModOutput? before = null)
	{
		ModOutput? now = ModOutput;
		(byte[] right, byte[] left) = ControllerTriggers();
		bool releaseMic = before?.MicLed != null && now?.MicLed == null;
		session.UpdateOutput(b =>
		{
			OutputBlock.SetTriggers(b, right, left);
			if (releaseMic)
			{
				OutputBlock.SetMuteLed(b, OutputBlock.MuteLedOff);
			}
			now?.WriteLight(b, ModLightAllowed);
			ClearOneShotLedCommands(b);
			ApplyAppMuteLed(b);
		});
		// The game's next report goes out even when it repeats the last one (with the mod's output over it).
		ResendGameOutput();
	}

	// Writes the mod's light over an output block about to be sent (the user's light, a game report); a no-op without it.
	public void OverlayModLight(byte[] block) => ModOutput?.WriteLight(block, ModLightAllowed);

	// Puts the mod's trigger effects over the block's (caller: ApplyTriggers, after the game's scaling and the fallback).
	private void ApplyModTriggers(byte[] block, bool fromGame, bool rightSet, bool leftSet, double k)
	{
		ModOutput? mod = ModOutput;
		if (mod == null || !mod.HasTriggers)
		{
			return;
		}
		Span<byte> right = block.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size);
		Span<byte> left = block.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size);
		bool conflict = false;
		if (mod.RightTrigger is { } r)
		{
			conflict |= fromGame && rightSet && !TriggerEffects.IsOff(right);
			(k < 1 ? TriggerEffects.Scale(r, k) : r).CopyTo(right);
			block[OutputBlock.Flag0] |= OutputBlock.RightTriggerEffect;
		}
		if (mod.LeftTrigger is { } l)
		{
			conflict |= fromGame && leftSet && !TriggerEffects.IsOff(left);
			(k < 1 ? TriggerEffects.Scale(l, k) : l).CopyTo(left);
			block[OutputBlock.Flag0] |= OutputBlock.LeftTriggerEffect;
		}
		if (conflict)
		{
			Interlocked.Increment(ref modConflicts);
			bool first;
			lock (lastBlock)
			{
				first = !modConflictLogged;
				modConflictLogged = true;
			}
			if (first)
			{
				Diagnostics.AppLog.Write($"dsx{LogSlot}: the game sent its own trigger effects while a mod's are active; the mod's stay");
			}
		}
	}

	// Below the mod's threshold a trigger reads as released (analog 0 and its digital bit off).
	private void ApplyModThresholds(Span<byte> input)
	{
		ModOutput? mod = ModOutput;
		if (mod == null || !mod.HasThresholds)
		{
			return;
		}
		const int l2 = 4, r2 = 5, buttons1 = 8;
		if (mod.LeftThreshold != 0 && input[l2] < mod.LeftThreshold)
		{
			input[l2] = 0;
			input[buttons1] &= unchecked((byte)~Input.InputTransform.L2Bit);
		}
		if (mod.RightThreshold != 0 && input[r2] < mod.RightThreshold)
		{
			input[r2] = 0;
			input[buttons1] &= unchecked((byte)~Input.InputTransform.R2Bit);
		}
	}
}
