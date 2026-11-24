// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// Classic rumble never outlives the game: the rumble bits left out with none following (how some game input layers stop rumble), no
// rumble report for the pad's hold limit (fade, then zero), the active game exiting, the host releasing the pad and the
// bridge stopping all zero the motors, with one log line each; a report without the bits alone never cuts a rumble.
public class StuckRumbleTests
{
	private sealed class Rig : IDisposable
	{
		public readonly FakeLink Link = new();
		public readonly GameClock Clock = new();
		public readonly BluetoothSession Session;
		public readonly VirtualDualSense Pad;
		public readonly ControllerBridge Bridge;
		public readonly ConcurrentQueue<string> Log = new();

		public Rig()
		{
			Session = new BluetoothSession(Link);
			Pad = GameLightTests.NewPad();
			Bridge = new ControllerBridge(Session, Pad) { Time = Clock, RumbleLog = Log.Enqueue };
		}

		public void Rumble(byte left, byte right) => GameLightTests.SendOutput(Pad, b => OutputBlock.SetRumble(b, left, right));

		public string[] Lines => [.. Log];

		public void Dispose()
		{
			Bridge.Dispose();
			Pad.Dispose();
			Session.Dispose();
		}
	}

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 3000), "timed out");

	[Fact]
	public void DualSense_AReportWithoutTheRumbleBits_LeavesTheMotors_WhileReportsWithThemFollow()
	{
		using Rig rig = new();
		rig.Rumble(200, 100);
		// A game that sends its light in reports of its own (every 100 ms) and its rumble every 500 ms: never cut.
		for (int i = 1; i <= 50; i++)
		{
			rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
			if (i % 5 == 0)
			{
				rig.Rumble(200, 100);
			}
			else
			{
				GameLightTests.SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, (byte)i, 2, 3));
			}
			rig.Bridge.CheckRumbleStale();
			Assert.Equal(1, rig.Bridge.RumbleGain);
		}
		Assert.Equal(((byte)200, (byte)100), rig.Bridge.Motors);
		Assert.Empty(rig.Lines);
	}

	[Fact]
	public void DualSense_AReportWithoutTheRumbleBits_AndNoneWithThemAfter_FadesOutAfterAboutOneSecond()
	{
		using Rig rig = new();
		rig.Rumble(200, 100);
		// Rumble stop by clearing the rumble bits (motor bytes 0). The motors stay for now.
		GameLightTests.SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 1, 2, 3));
		Assert.Equal(((byte)200, (byte)100), rig.Bridge.Motors);

		rig.Clock.Advance(ControllerBridge.BitsOffHold - TimeSpan.FromMilliseconds(100));
		// More reports without the bits do not push the deadline back.
		GameLightTests.SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 4, 5, 6));
		Assert.Equal(1, rig.Bridge.RumbleGain);
		rig.Clock.Advance(TimeSpan.FromMilliseconds(100) + ControllerBridge.BitsOffFadeTime / 2);
		Assert.InRange(rig.Bridge.RumbleGain, 0.45, 0.55);
		rig.Bridge.CheckRumbleStale();
		Assert.Equal(((byte)200, (byte)100), rig.Bridge.Motors);

		rig.Clock.Advance(ControllerBridge.BitsOffFadeTime);
		rig.Bridge.CheckRumbleStale();
		Assert.Equal(((byte)0, (byte)0), rig.Bridge.Motors);
		Assert.Null(rig.Bridge.GameUseNow);
		Assert.Single(rig.Lines, l => l == "rumble: stopped (a game report without the rumble bits, none with them for 1 s, faded out); motors were 200/100");
	}

	[Fact]
	public void DualSense_ZeroMotorsWithTheBits_StopAtOnce()
	{
		using Rig rig = new();
		rig.Rumble(200, 100);
		rig.Rumble(0, 0);
		Assert.Equal(((byte)0, (byte)0), rig.Bridge.Motors);
		Assert.Empty(rig.Lines);
	}

	[Fact]
	public void XboxPad_ARumbleSetOnce_IsHeldForTheLongLimit_ThenFades()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualXbox360 pad = new("1-1");
		GameClock clock = new();
		ConcurrentQueue<string> log = new();
		using ControllerBridge bridge = new(session, pad) { Time = clock, RumbleLog = log.Enqueue };

		pad.Submit(XboxRumble(150, 50), _ => { });
		clock.Advance(TimeSpan.FromSeconds(30));
		bridge.CheckRumbleStale();
		Assert.Equal(((byte)150, (byte)50), bridge.Motors);
		Assert.Equal(1, bridge.RumbleGain);

		clock.Advance(ControllerBridge.LongRumbleHoldLimit - TimeSpan.FromSeconds(30) + ControllerBridge.RumbleFadeTime);
		bridge.CheckRumbleStale();
		Assert.Equal(((byte)0, (byte)0), bridge.Motors);
		Assert.Single(log, l => l.Contains("no rumble report from the game for 60 s"));
	}

	[Fact]
	public void DualShock4Pad_KeepsItsRumble_PastTheDualSenseLimit()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualShock4 pad = new();
		GameClock clock = new();
		using ControllerBridge bridge = new(session, pad) { Time = clock, RumbleLog = _ => { } };

		pad.Submit(Ds4(1, 0x01, weak: 60, strong: 220), _ => { });
		pad.Submit(Ds4(2, 0x02, red: 9), _ => { });
		clock.Advance(ControllerBridge.RumbleHoldLimit * 2);
		bridge.CheckRumbleStale();
		Assert.Equal(((byte)220, (byte)60), bridge.Motors);
	}

	[Fact]
	public void DualShock4_ALightbarOnlyReport_KeepsTheRumble()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualShock4 pad = new();
		using ControllerBridge bridge = new(session, pad) { RumbleLog = _ => { } };

		pad.Submit(Ds4(1, 0x01, weak: 60, strong: 220), _ => { });
		Assert.Equal(((byte)220, (byte)60), bridge.Motors);
		// DS4's rumble flag is a valid bit: a report with only the lightbar leaves the motors alone.
		pad.Submit(Ds4(2, 0x02, red: 9), _ => { });
		Assert.Equal(((byte)220, (byte)60), bridge.Motors);
	}

	[Fact]
	public void NoRumbleReport_ForTheHoldLimit_FadesOut_ThenZeroesTheMotors_Once()
	{
		using Rig rig = new();
		rig.Rumble(180, 90);

		rig.Clock.Advance(ControllerBridge.RumbleHoldLimit - TimeSpan.FromSeconds(1));
		rig.Bridge.CheckRumbleStale();
		Assert.Equal(1, rig.Bridge.RumbleGain);
		Assert.Equal(((byte)180, (byte)90), rig.Bridge.Motors);

		// Half way through the fade.
		rig.Clock.Advance(TimeSpan.FromSeconds(1) + ControllerBridge.RumbleFadeTime / 2);
		Assert.InRange(rig.Bridge.RumbleGain, 0.45, 0.55);
		rig.Bridge.CheckRumbleStale();
		Assert.Equal(((byte)180, (byte)90), rig.Bridge.Motors);

		rig.Clock.Advance(ControllerBridge.RumbleFadeTime);
		Assert.Equal(0, rig.Bridge.RumbleGain);
		rig.Bridge.CheckRumbleStale();
		rig.Bridge.CheckRumbleStale();
		Assert.Equal(((byte)0, (byte)0), rig.Bridge.Motors);
		Assert.Single(rig.Lines, l => l.Contains("stopped (no rumble report from the game for 10 s, faded out); motors were 180/90"));
		Assert.Equal(1, rig.Bridge.RumbleStops);
	}

	[Fact]
	public void ARepeatedRumbleReport_KeepsTheRumbleUp_AndBringsItBackAfterTheTimeout()
	{
		using Rig rig = new();
		rig.Rumble(120, 0);
		for (int i = 0; i < 5; i++)
		{
			// The game holds its rumble with identical reports (dropped as repeats, but they still count).
			rig.Clock.Advance(TimeSpan.FromSeconds(4));
			rig.Rumble(120, 0);
			rig.Bridge.CheckRumbleStale();
			Assert.Equal(1, rig.Bridge.RumbleGain);
		}
		Assert.Equal(((byte)120, (byte)0), rig.Bridge.Motors);

		rig.Clock.Advance(ControllerBridge.RumbleHoldLimit + ControllerBridge.RumbleFadeTime);
		rig.Bridge.CheckRumbleStale();
		Assert.Equal(((byte)0, (byte)0), rig.Bridge.Motors);

		// The same report again (a held rumble resent): the motors are back.
		rig.Rumble(120, 0);
		Assert.Equal(((byte)120, (byte)0), rig.Bridge.Motors);
		Assert.Equal(1, rig.Bridge.RumbleGain);
	}

	[Fact]
	public void AStaleRumble_EndsTheHapticsStream_ByItself()
	{
		using Rig rig = new();
		rig.Rumble(200, 200);
		WaitUntil(() => rig.Session.IsHapticsStreaming && rig.Bridge.RumbleRefills > 3);

		// Nobody calls CheckRumbleStale: the rumble refill on the haptics thread finds the stale rumble itself.
		rig.Clock.Advance(ControllerBridge.RumbleHoldLimit + ControllerBridge.RumbleFadeTime + TimeSpan.FromSeconds(1));
		WaitUntil(() => rig.Bridge.Motors == (0, 0));
		WaitUntil(() => !rig.Session.IsHapticsStreaming);
		long refills = rig.Bridge.RumbleRefills;
		Thread.Sleep(50);
		Assert.Equal(refills, rig.Bridge.RumbleRefills);
		Assert.Single(rig.Lines, l => l.Contains("faded out"));
	}

	[Fact]
	public void TheActiveGameExiting_StopsTheRumble_WithItsReason()
	{
		using Rig rig = new();
		rig.Rumble(255, 255);

		Assert.True(rig.Bridge.StopRumble("the active game exited"));
		Assert.False(rig.Bridge.StopRumble("the active game exited"));

		Assert.Equal(((byte)0, (byte)0), rig.Bridge.Motors);
		Assert.Single(rig.Lines, l => l == "rumble: stopped (the active game exited); motors were 255/255");
	}

	[Fact]
	public void TheHostReleasingThePad_StopsTheRumble()
	{
		using Rig rig = new();
		rig.Rumble(90, 40);

		rig.Pad.Released();

		Assert.Equal(((byte)0, (byte)0), rig.Bridge.Motors);
		Assert.Single(rig.Lines, l => l.Contains("stopped (the host released the virtual pad); motors were 90/40"));
	}

	[Fact]
	public void TheHostReleasingAnXboxPad_StopsTheRumble()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualXbox360 pad = new("1-1");
		ConcurrentQueue<string> log = new();
		using ControllerBridge bridge = new(session, pad) { RumbleLog = log.Enqueue };

		pad.Submit(XboxRumble(200, 100), _ => { });
		Assert.Equal(((byte)200, (byte)100), bridge.Motors);

		pad.Released();

		Assert.Equal(((byte)0, (byte)0), bridge.Motors);
		Assert.Single(log, l => l.Contains("the host released the virtual pad"));
	}

	[Fact]
	public void TheBridgeStopping_ZeroesTheRumble_AndLogsIt()
	{
		Rig rig = new();
		rig.Rumble(70, 0);

		rig.Bridge.Dispose();

		Assert.Equal(((byte)0, (byte)0), rig.Bridge.Motors);
		Assert.Single(rig.Lines, l => l.Contains("stopped (bridge stopped); motors were 70/0"));
		rig.Dispose();
		Assert.Single(rig.Lines);
	}

	[Fact]
	public void NothingRunning_NothingLogged()
	{
		using Rig rig = new();
		rig.Rumble(0, 0);
		rig.Clock.Advance(TimeSpan.FromMinutes(1));
		rig.Bridge.CheckRumbleStale();
		rig.Pad.Released();
		Assert.False(rig.Bridge.StopRumble("the active game exited"));
		Assert.Empty(rig.Lines);
	}

	private static SubmitCommand XboxRumble(byte large, byte small) =>
		new(1, 0, UsbIpCodes.DirOut, VirtualXbox360.OutEndpoint, 0, 8, 0, -1, 1, new byte[8], [0x00, 0x08, 0x00, large, small, 0, 0, 0], []);

	private static SubmitCommand Ds4(uint seq, byte flags, byte weak = 0, byte strong = 0, byte red = 0)
	{
		byte[] data = new byte[32];
		data[0] = 0x05;
		data[1] = flags;
		data[4] = weak;
		data[5] = strong;
		data[6] = red;
		return new SubmitCommand(seq, 0, UsbIpCodes.DirOut, VirtualDualShock4.OutEndpoint, 0, data.Length, 0, -1, 1, new byte[8], data, []);
	}
}
