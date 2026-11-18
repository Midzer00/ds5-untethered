// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The stuck-rumble rules on the Xbox Series pad: its main rumble is held for the long limit like the Xbox 360's, and the
// game exiting, the host releasing the pad and the bridge stopping zero it and take any trigger rumble off the triggers
// (the fallback comes back). The pad forgets the stopped levels, so the game's next command plays again.
public partial class BridgeTests
{
	private static void SendGip(VirtualXboxOne pad, uint seq, byte[] command) =>
		pad.Submit(Command(seq, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, command), _ => { });

	// Main motors 60 % / 20 % (DualSense bytes 153 / 51) and the right trigger motor at 80 %, held (Windows' timing).
	private static byte[] HeldRumble() => GipMotor(0x0F, 0, 80, 60, 20, 255, 0, 235);

	[Fact]
	public void XboxSeries_MainRumble_IsHeldForTheLongLimit_ThenFades_AndTheSameCommandBringsItBack()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(nowMs: () => 0, autoTick: false);
		ConcurrentQueue<string> log = new();
		using ControllerBridge bridge = new(session, pad) { Time = clock, RumbleLog = log.Enqueue };
		Assert.Equal(ControllerBridge.LongRumbleHoldLimit, bridge.HoldLimit);

		SendGip(pad, 9, GipMotor(0x03, 0, 0, 60, 20, 255, 0, 235));
		Assert.Equal(((byte)153, (byte)51), bridge.Motors);
		clock.Advance(30_000);
		bridge.CheckRumbleStale();
		Assert.Equal(((byte)153, (byte)51), bridge.Motors);
		Assert.Equal(1, bridge.RumbleGain);

		clock.Advance(30_000 + (long)ControllerBridge.RumbleFadeTime.TotalMilliseconds);
		bridge.CheckRumbleStale();
		Assert.Equal(((byte)0, (byte)0), bridge.Motors);
		Assert.Single(log, l => l.Contains("no rumble report from the game for 60 s"));
		Assert.Equal(default, pad.Motors);

		// The game resends the same command: a change again for the pad, so the rumble is back.
		SendGip(pad, 10, GipMotor(0x03, 0, 0, 60, 20, 255, 0, 235));
		Assert.Equal(((byte)153, (byte)51), bridge.Motors);
		Assert.Equal(1, bridge.RumbleGain);
	}

	[Fact]
	public void XboxSeries_HostRelease_RaisesHostReleased_StopsTheRumble_AndTheTriggerRumble()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(nowMs: () => 0, autoTick: false);
		ConcurrentQueue<string> log = new();
		using ControllerBridge bridge = new(session, pad) { Time = clock, TriggerRumbleTuning = InstantImpulse, RumbleLog = log.Enqueue };
		byte[] fallback = TriggerEffects.Vibration(2, 3, 30);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback));
		int released = 0;
		pad.HostReleased += () => released++;

		SendGip(pad, 9, HeldRumble());
		Assert.Equal(((byte)153, (byte)51), bridge.Motors);
		AssertSent(link, Vibration(80), fallback);

		pad.Released();

		Assert.Equal(1, released);
		Assert.Equal(((byte)0, (byte)0), bridge.Motors);
		Assert.False(bridge.TriggerRumbleOn);
		AssertSent(link, fallback, fallback);
		Assert.Single(log, l => l == "trigger rumble: stopped (the host released the virtual pad)");
		Assert.Equal(0, clock.ActiveTimers);
		Assert.False(bridge.StopAllRumble("the host released the virtual pad"));
	}

	[Fact]
	public void XboxSeries_GameExit_StopsTheRumble_AndTheTriggerRumble_ThenTheGamesNextCommandPlaysAgain()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(nowMs: () => 0, autoTick: false);
		ConcurrentQueue<string> log = new();
		using ControllerBridge bridge = new(session, pad) { Time = clock, TriggerRumbleTuning = InstantImpulse, RumbleLog = log.Enqueue };
		byte[] fallback = TriggerEffects.Feedback(2, 5);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback));

		SendGip(pad, 9, HeldRumble());
		AssertSent(link, Vibration(80), fallback);
		Assert.True(bridge.TriggerRumbleOn);

		Assert.True(bridge.StopAllRumble("the active game exited"));
		Assert.False(bridge.StopAllRumble("the active game exited"));

		Assert.Equal(((byte)0, (byte)0), bridge.Motors);
		Assert.False(bridge.TriggerRumbleOn);
		AssertSent(link, fallback, fallback);
		Assert.Equal(default, pad.Motors);
		Assert.Equal(
			["rumble: stopped (the active game exited); motors were 153/51", "trigger rumble: stopped (the active game exited)"],
			log.ToArray());

		// A game that holds the same rumble (or the next game sending the same levels) gets both back.
		clock.Advance(10);
		SendGip(pad, 10, HeldRumble());
		Assert.Equal(((byte)153, (byte)51), bridge.Motors);
		AssertSent(link, Vibration(80), fallback);
	}

	[Fact]
	public void XboxSeries_TheBridgeStopping_ZeroesTheRumble_ClearsTheTriggerRumble_AndThePadForgetsBoth()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(nowMs: () => 0, autoTick: false);
		ConcurrentQueue<string> log = new();
		ControllerBridge bridge = new(session, pad) { Time = clock, TriggerRumbleTuning = InstantImpulse, RumbleLog = log.Enqueue };
		byte[] fallback = TriggerEffects.Vibration(2, 3, 30);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback));

		SendGip(pad, 9, HeldRumble());
		AssertSent(link, Vibration(80), fallback);

		bridge.Dispose();

		Assert.Equal(((byte)0, (byte)0), bridge.Motors);
		Assert.False(bridge.TriggerRumbleOn);
		AssertSent(link, fallback, fallback);
		Assert.Single(log, l => l == "rumble: stopped (bridge stopped); motors were 153/51");
		Assert.Equal(default, pad.Motors);
		Assert.Equal(0, clock.ActiveTimers);

		// A new bridge on the same pad (the controller reconnected) plays the game's held rumble when it is resent.
		using ControllerBridge next = new(session, pad) { Time = clock, TriggerRumbleTuning = InstantImpulse, RumbleLog = log.Enqueue };
		SendGip(pad, 10, HeldRumble());
		Assert.Equal(((byte)153, (byte)51), next.Motors);
		Assert.True(next.TriggerRumbleOn);
	}
}
