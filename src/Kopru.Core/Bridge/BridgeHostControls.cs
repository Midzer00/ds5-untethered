// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// The user's light choice. Brightness 0..1 scales the colour; PlayerLeds false turns the player LEDs off, otherwise
// Options.PlayerPattern lights them. Options: the effect (LightAnimator), player LED brightness and the mute LED.
public sealed record LightSettings(byte Red, byte Green, byte Blue, double Brightness = 1, bool PlayerLeds = true)
{
	public const byte PlayerOneLed = 0x04;

	public Lighting.LightOptions Options { get; init; } = Lighting.LightOptions.Default;

	public byte PlayerMask => PlayerLeds ? Lighting.LightAnimator.PatternMask(Options.PlayerPattern) : (byte)0;

	public static LightSettings FromRgb(uint rgb, double brightness = 1, bool playerLeds = true) =>
		new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, brightness, playerLeds);

	// The light at the start of the effect.
	public void WriteTo(byte[] block) => Lighting.LightAnimator.FrameAt(this, TimeSpan.Zero).WriteTo(block);
}

// Light, quick tests and the adapter check: they use the running bridge's controller session, or, while the bridge is
// stopped, open the controller just for the test. While the bridge waits for the controller there is none to use.
public sealed partial class BridgeHost
{
	// Haptics test tones: the left actuator low, the right one higher (like the heavy and light rumble motors).
	public const double TestLeftHz = 80;
	public const double TestRightHz = 160;
	public const int TestAmplitude = 70;

	private readonly SemaphoreSlim sessionGate = new(1, 1);

	private LightSettings light = LightSettings.FromRgb(0x2F6BFF, 0.8);

	// Awaited after the controller was opened and before the virtual pad is plugged in (e.g. to hide the controller).
	// Exceptions are ignored; the hook reports its own failures.
	public Func<HidDevice, CancellationToken, Task>? ControllerOpened { get; set; }

	// The controller the bridge is running with, or null.
	public HidDevice? Device => current?.Session.Device;

	public LightSettings Light => light;

	// Loudest haptics sent per side (0..1) since the last call; zero while the bridge is not running.
	public (double Left, double Right) TakeHapticsLevels() => current?.Session.Haptics.TakePeaks() ?? (0, 0);

	// Remembers the light (applied on every connect) and sets it now when a session is open. A game's own light output
	// replaces it while the game sends it.
	public void ApplyLight(LightSettings settings)
	{
		light = settings;
		Connection? c = current;
		if (c != null)
		{
			c.Bridge.KeepMuteLed = settings.Options.MuteLed == Lighting.MuteLedMode.FollowMic;
		}
		if (c != null && !InvalidateLightSender())
		{
			ApplyLightTo(c.Session);
		}
	}

	// Plays the test tones on the chosen sides for the duration. False when no controller can be used.
	public Task<bool> TestHapticsAsync(bool left, bool right, TimeSpan duration, CancellationToken cancel = default) =>
		WithSessionAsync(async (session, _) =>
		{
			double phaseL = 0, phaseR = 0;
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while (clock.Elapsed < duration && !cancel.IsCancellationRequested)
			{
				while (session.Haptics.Buffered < 96)
				{
					sbyte l = left ? (sbyte)Math.Round(TestAmplitude * Math.Sin(phaseL)) : (sbyte)0;
					sbyte r = right ? (sbyte)Math.Round(TestAmplitude * Math.Sin(phaseR)) : (sbyte)0;
					session.Haptics.Push(l, r);
					phaseL = (phaseL + 2 * Math.PI * TestLeftHz / BluetoothReports.HapticsSampleRate) % (2 * Math.PI);
					phaseR = (phaseR + 2 * Math.PI * TestRightHz / BluetoothReports.HapticsSampleRate) % (2 * Math.PI);
				}
				session.WakeHaptics();
				await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
			}
		}, cancel);

	// Plays rumble (both motors at `strength` 0..1) as haptics with the given feel and level, like a game's rumble.
	public Task<bool> TestRumbleAsync(RumbleFeel feel, double level, TimeSpan duration, double strength = 0.8, CancellationToken cancel = default) =>
		WithSessionAsync(async (session, _) =>
		{
			RumbleSynth synth = new();
			byte motor = (byte)Math.Round(Math.Clamp(strength, 0, 1) * 255);
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while (clock.Elapsed < duration && !cancel.IsCancellationRequested)
			{
				while (session.Haptics.Buffered < 96)
				{
					(sbyte l, sbyte r) = synth.Next(motor, motor, level, feel);
					session.Haptics.Push(l, r);
				}
				session.WakeHaptics();
				await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
			}
		}, cancel);

	// Sets the effect on both triggers for the duration, then gives them back to the game (or turns them off).
	public Task<bool> TestTriggersAsync(byte[] effect, TimeSpan duration, CancellationToken cancel = default) =>
		WithSessionAsync(async (session, bridge) =>
		{
			session.UpdateOutput(b => OutputBlock.SetTriggers(b, effect, effect));
			try
			{
				await Task.Delay(duration, cancel).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Restore below.
			}
			// The game's effects at the current trigger strength (or the fallback), not the raw ones.
			(byte[] Right, byte[] Left)? game = bridge?.ControllerTriggers();
			session.UpdateOutput(b => OutputBlock.SetTriggers(b, game?.Right ?? TriggerEffects.Off(), game?.Left ?? TriggerEffects.Off()));
			bridge?.ResendGameOutput();
		}, cancel);

	// Measures the Bluetooth link (see AdapterCheck). Null when no controller can be used or too few reports arrived.
	public async Task<AdapterReport?> RunAdapterCheckAsync(TimeSpan idle, TimeSpan load, CancellationToken cancel = default)
	{
		AdapterReport? report = null;
		await WithSessionAsync(async (session, _) => report = await AdapterCheck.RunAsync(session, idle, load, cancel).ConfigureAwait(false), cancel)
			.ConfigureAwait(false);
		return report;
	}

	private void ApplyLightTo(BluetoothSession session)
	{
		LightSettings l = EffectiveLight();
		try
		{
			session.UpdateOutput(l.WriteTo);
		}
		catch (Exception)
		{
			// The controller is going away; the light is set again on the next connect.
		}
	}

	private async Task<bool> WithSessionAsync(Func<BluetoothSession, ControllerBridge?, Task> action, CancellationToken cancel)
	{
		Connection? c = current;
		if (c != null)
		{
			await action(c.Session, c.Bridge).ConfigureAwait(false);
			return true;
		}
		CancelReadOnlyUse();
		await sessionGate.WaitAsync(cancel).ConfigureAwait(false);
		try
		{
			c = current;
			if (c != null)
			{
				await action(c.Session, c.Bridge).ConfigureAwait(false);
				return true;
			}
			lock (gate)
			{
				if (supervisor != null)
				{
					// Waiting for or connecting to the controller: it is not ours to open.
					return false;
				}
			}
			IHidLink? link = controllers.TryOpen();
			if (link == null)
			{
				return false;
			}
			using BluetoothSession session = new(link);
			session.Start();
			ApplyLightTo(session);
			await action(session, null).ConfigureAwait(false);
			// Let the last haptics play out before closing.
			await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None).ConfigureAwait(false);
			return true;
		}
		finally
		{
			sessionGate.Release();
		}
	}
}
