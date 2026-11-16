// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// A clock moved by hand for both kinds of reading: timestamps (ControllerBridge) and wall time (LightSender).
internal sealed class GameClock : TimeProvider
{
	private long ticks = 1_000_000_000;
	private DateTimeOffset now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

	public override long TimestampFrequency => Stopwatch.Frequency;

	public override long GetTimestamp() => Interlocked.Read(ref ticks);

	public override DateTimeOffset GetUtcNow()
	{
		lock (this)
		{
			return now;
		}
	}

	public void Advance(TimeSpan by)
	{
		lock (this)
		{
			now += by;
		}
		Interlocked.Add(ref ticks, (long)(by.TotalSeconds * Stopwatch.Frequency));
	}
}

// The game's hold on the light bar (DualSense Mode on: the game's light let through, the app shows the mute LED), the
// user's light coming back, and the "game light" log lines.
public class GameLightTests
{
	private const uint UserColour = 0xFF0000;

	private sealed class Rig : IDisposable
	{
		public readonly FakeLink Link = new();
		public readonly GameClock Clock = new();
		public readonly BluetoothSession Session;
		public readonly VirtualDualSense Pad;
		public readonly ControllerBridge Bridge;
		public readonly LightSender Sender;
		public readonly ConcurrentQueue<string> Log = new();

		public Rig()
		{
			Session = new BluetoothSession(Link);
			Pad = NewPad();
			Bridge = new ControllerBridge(Session, Pad) { Time = Clock, LightLog = Log.Enqueue };
			// DualSense Mode on: the supported games profile lets the game's light through; the app owns the mute LED.
			Bridge.ApplyProfile(new BridgeProfile(LightFromGame: true));
			Bridge.AppMuteLed = OutputBlock.MuteLedOff;
			Sender = new LightSender(Session.UpdateOutput, () => LightSettings.FromRgb(UserColour), () => null, () => Bridge.GameControlsLight, Clock)
			{
				GameLightWrites = () => Bridge.GameLightWrites
			};
		}

		public byte[][] Reports => Link.Writes.Where(w => w[0] == BluetoothReports.OutputId).Select(Block).ToArray();

		public byte[] Last => Reports[^1];

		public void Dispose()
		{
			Sender.Dispose();
			Bridge.Dispose();
			Pad.Dispose();
			Session.Dispose();
		}
	}

	internal static VirtualDualSense NewPad() => new(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] }));

	internal static void SendOutput(VirtualDualSense pad, Action<byte[]> change)
	{
		byte[] block = new byte[OutputBlock.Size];
		change(block);
		byte[] report = new byte[UsbReports.OutputSize];
		UsbReports.BuildOutput(block, report);
		pad.Submit(new SubmitCommand(5, 0, UsbIpCodes.DirOut, VirtualDualSense.HidOutEndpoint, 0, report.Length, 0, -1, 1, new byte[8], report, []), _ => { });
	}

	private static byte[] Block(byte[] write) => write.AsSpan(3, OutputBlock.Size).ToArray();

	private static (int R, int G, int B) Rgb(byte[] block) => (block[OutputBlock.Red], block[OutputBlock.Red + 1], block[OutputBlock.Red + 2]);

	private static bool IsUserColour(byte[] block) =>
		(block[OutputBlock.Flag1] & OutputBlock.LightbarControl) != 0 && Rgb(block) == (0xFF, 0, 0);

	// The sender's timer runs at SteadyInterval while nothing animates; the tests tick it by hand.
	private static void TickFor(Rig rig, TimeSpan span)
	{
		for (TimeSpan t = TimeSpan.Zero; t < span; t += LightSender.SteadyInterval)
		{
			rig.Clock.Advance(LightSender.SteadyInterval);
			rig.Sender.Tick();
		}
	}

	[Fact]
	public void GameSetsBlack_ThenStops_TheUsersColourComesBack_AfterTheIdleTime()
	{
		using Rig rig = new();
		Assert.True(rig.Sender.Tick());
		Assert.True(IsUserColour(rig.Last));

		SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 0, 0, 0));
		Assert.Equal((0, 0, 0), Rgb(rig.Last));
		Assert.True(rig.Bridge.GameControlsLight);
		long sent = rig.Sender.Sent;

		// The game falls silent (exited without a word): nothing from the sender until GameLightIdle has passed...
		TickFor(rig, ControllerBridge.DefaultGameLightIdle - LightSender.SteadyInterval);
		Assert.Equal(sent, rig.Sender.Sent);
		Assert.Equal((0, 0, 0), Rgb(rig.Last));

		// ...then the user's colour, although the sender's own frame never changed.
		TickFor(rig, LightSender.SteadyInterval);
		Assert.Equal(sent + 1, rig.Sender.Sent);
		Assert.True(IsUserColour(rig.Last));

		// The monitor's pass closes the session with one summary line.
		rig.Bridge.CheckGameOutputIdle();
		Assert.Contains(rig.Log, l => l.StartsWith("game light: session ended (no game output for 3 s): 1 reports, light bar in 1,", StringComparison.Ordinal)
			&& l.EndsWith("; the user's light is back", StringComparison.Ordinal));
		rig.Bridge.CheckGameOutputIdle();
		Assert.Single(rig.Log, l => l.Contains("session ended", StringComparison.Ordinal));
	}

	[Fact]
	public void GameKeepsStreaming_KeepsItsColour()
	{
		using Rig rig = new();
		rig.Sender.Tick();
		long sent = rig.Sender.Sent;
		for (int i = 0; i < 40; i++)
		{
			// About 4 reports a second, all the same (repeats are not relayed but keep the hold).
			SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 0, 0, 0x40));
			TickFor(rig, LightSender.SteadyInterval);
			rig.Bridge.CheckGameOutputIdle();
		}
		Assert.Equal(sent, rig.Sender.Sent);
		Assert.Equal((0, 0, 0x40), Rgb(rig.Last));
		Assert.DoesNotContain(rig.Log, l => l.Contains("session ended", StringComparison.Ordinal));
	}

	[Fact]
	public void ActiveGameEnds_TheUsersColourComesBack_AndTheGameCanTakeItAgain()
	{
		using Rig rig = new();
		rig.Sender.Tick();
		SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 0, 0, 0));
		TickFor(rig, LightSender.SteadyInterval);
		Assert.Equal((0, 0, 0), Rgb(rig.Last));

		rig.Bridge.EndGameSession("the active game ended");
		Assert.False(rig.Bridge.GameControlsLight);
		TickFor(rig, LightSender.SteadyInterval);
		Assert.True(IsUserColour(rig.Last));
		Assert.Contains(rig.Log, l => l.StartsWith("game light: session ended (the active game ended): ", StringComparison.Ordinal));

		// A game still writing the same light takes it back with its very next report.
		SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 0, 0, 0));
		Assert.True(rig.Bridge.GameControlsLight);
		Assert.Equal((0, 0, 0), Rgb(rig.Last));
	}

	[Fact]
	public void GameLightOutSetup_WithoutTheLightBar_TheUsersColourIsSentAgain()
	{
		using Rig rig = new();
		Assert.True(rig.Sender.Tick());
		long sent = rig.Sender.Sent;
		TickFor(rig, TimeSpan.FromSeconds(1));
		Assert.Equal(sent, rig.Sender.Sent);

		SendOutput(rig.Pad, b =>
		{
			b[OutputBlock.Flag2] |= OutputBlock.LightbarSetupControl;
			b[OutputBlock.LightbarSetup] = OutputBlock.LightOut;
		});
		Assert.False(rig.Bridge.GameControlsLight);
		Assert.Contains(rig.Reports, r => (r[OutputBlock.Flag2] & OutputBlock.LightbarSetupControl) != 0 && r[OutputBlock.LightbarSetup] == OutputBlock.LightOut);

		TickFor(rig, LightSender.SteadyInterval);
		Assert.Equal(sent + 1, rig.Sender.Sent);
		byte[] last = rig.Last;
		Assert.True(IsUserColour(last));
		// The light out is not repeated with the colour.
		Assert.Equal(0, last[OutputBlock.Flag2] & OutputBlock.LightbarSetupControl);

		// Nothing more while no new setup comes (the game repeating its report is not relayed).
		SendOutput(rig.Pad, b =>
		{
			b[OutputBlock.Flag2] |= OutputBlock.LightbarSetupControl;
			b[OutputBlock.LightbarSetup] = OutputBlock.LightOut;
		});
		TickFor(rig, TimeSpan.FromSeconds(1));
		Assert.Equal(sent + 1, rig.Sender.Sent);
	}

	[Fact]
	public void GameLightLine_OnChangeOnly_RateLimited_WithTheSkippedCount()
	{
		using Rig rig = new();
		for (int i = 0; i < 5; i++)
		{
			SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 0, 0, 0));
		}
		Assert.Equal(["game light: flags1 0x04 flags2 0x00, setup 0x00, rgb 0,0,0, player leds 0x00, brightness 0x00; forwarded: rgb 0,0,0, no setup, no release"],
			rig.Log.ToArray());

		// Three changes within the interval: none written yet.
		rig.Clock.Advance(TimeSpan.FromMilliseconds(50));
		SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 1, 0, 0));
		SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 2, 0, 0));
		SendOutput(rig.Pad, b =>
		{
			OutputBlock.SetLightbar(b, 3, 0, 0);
			OutputBlock.SetPlayerLeds(b, 0x04, OutputBlock.LedBrightnessMedium);
			b[OutputBlock.Flag1] |= OutputBlock.ReleaseLeds;
			b[OutputBlock.Flag2] |= OutputBlock.LightbarSetupControl;
			b[OutputBlock.LightbarSetup] = 0x01;
		});
		Assert.Single(rig.Log);

		// The interval is over: the next report (a repeat) writes the latest value with the count of the ones skipped.
		rig.Clock.Advance(ControllerBridge.GameLightLogInterval);
		SendOutput(rig.Pad, b =>
		{
			OutputBlock.SetLightbar(b, 3, 0, 0);
			OutputBlock.SetPlayerLeds(b, 0x04, OutputBlock.LedBrightnessMedium);
			b[OutputBlock.Flag1] |= OutputBlock.ReleaseLeds;
			b[OutputBlock.Flag2] |= OutputBlock.LightbarSetupControl;
			b[OutputBlock.LightbarSetup] = 0x01;
		});
		Assert.Equal(2, rig.Log.Count);
		Assert.Equal("game light: flags1 0x1C flags2 0x03, setup 0x01, rgb 3,0,0, player leds 0x04, brightness 0x01; forwarded: rgb 3,0,0, setup sent, release dropped (2 changes not logged)",
			rig.Log.Last());

		// The session line counts the setup and release requests although the mute LED never changed.
		rig.Clock.Advance(ControllerBridge.DefaultGameLightIdle);
		rig.Bridge.CheckGameOutputIdle();
		Assert.Equal("game light: session ended (no game output for 3 s): 9 reports, light bar in 9, light bar setup 2 times (sent 1), release LEDs 2 times (sent 0), 4 light changes; the user's light is back",
			rig.Log.Last());
	}

	// A plausible game start: a neutral first report, then player LEDs, light bar setup and the colour together, then a
	// few colour changes streamed with the triggers. Every report with the light bar goes out with flag1 0x04 and the
	// game's colour in bytes 44-46.
	[Fact]
	public void Replay_GameStart_ForwardsTheLightBarFlagAndColour()
	{
		using Rig rig = new();
		rig.Sender.Tick();
		List<(int R, int G, int B)?> script =
		[
			null,
			(0, 0, 0x40),
			(0, 0, 0x40),
			(0x10, 0x20, 0xC0),
			(0, 0, 0),
			(0xFF, 0x80, 0x00),
		];
		int step = 0;
		foreach ((int R, int G, int B)? colour in script)
		{
			int before = rig.Link.Writes.Count;
			SendOutput(rig.Pad, b =>
			{
				OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off());
				if (colour is { } c)
				{
					OutputBlock.SetLightbar(b, (byte)c.R, (byte)c.G, (byte)c.B);
					if (step == 1)
					{
						OutputBlock.SetPlayerLeds(b, 0x04, OutputBlock.LedBrightnessHigh);
						b[OutputBlock.Flag2] |= OutputBlock.LightbarSetupControl;
						b[OutputBlock.LightbarSetup] = OutputBlock.LightOut;
					}
				}
			});
			rig.Clock.Advance(TimeSpan.FromMilliseconds(16));
			if (colour is { } expected && rig.Link.Writes.Count > before)
			{
				byte[] game = Block(rig.Link.Writes.Skip(before).First(w => w[0] == BluetoothReports.OutputId));
				Assert.Equal(OutputBlock.LightbarControl, game[OutputBlock.Flag1] & OutputBlock.LightbarControl);
				Assert.Equal(expected, Rgb(game));
			}
			step++;
		}
		Assert.True(rig.Bridge.GameControlsLight);
		Assert.Equal((0xFF, 0x80, 0x00), Rgb(rig.Last));
		Assert.Equal(OutputBlock.MuteLedOff, rig.Last[OutputBlock.MuteLed]);
		// The sender stays out of the game's way meanwhile.
		long sent = rig.Sender.Sent;
		rig.Sender.Tick();
		Assert.Equal(sent, rig.Sender.Sent);
	}

	private sealed class EndCountingBridge : IGameBridge
	{
		public int Ended;

		public void ApplyProfile(BridgeProfile profile)
		{
		}

		public GameSignals TakeGameSignals() => default;

		public void GameEnded() => Ended++;
	}

	[Fact]
	public void GameModeController_TellsTheBridge_WhenTheActiveGameEnds()
	{
		Kopru.Core.Games.GameCatalog catalog = new([new Kopru.Core.Games.CatalogGame("Returnal", ["Returnal-Win64-Shipping.exe"], 1649240, true, true)]);
		GameModeTests.FakeProcesses processes = new();
		EndCountingBridge bridge = new();
		Kopru.Core.Games.GameModeController games = new(catalog, new Kopru.Core.Games.GameWatcher(catalog, processes, log: _ => { }), bridge) { Log = _ => { } };
		processes.Show("Returnal-Win64-Shipping.exe");
		games.Tick();
		games.Tick();
		Assert.Equal(0, bridge.Ended);
		processes.Exit("Returnal-Win64-Shipping.exe");
		games.Tick();
		games.Tick();
		Assert.Equal(1, bridge.Ended);
	}

	[Fact]
	public void SliderChange_WhileTheGameHoldsTheLight_TheGamesColourStays()
	{
		using Rig rig = new();
		rig.Sender.Tick();
		SendOutput(rig.Pad, b =>
		{
			OutputBlock.SetLightbar(b, 0, 0x40, 0x80);
			OutputBlock.SetPlayerLeds(b, 0x04, OutputBlock.LedBrightnessLow);
		});
		long sent = rig.Sender.Sent;

		// A strength slider: a new profile that still lets the game's light through.
		rig.Bridge.ApplyProfile(new BridgeProfile(LightFromGame: true, TriggerIntensity: 0.5));
		Assert.True(rig.Bridge.GameControlsLight);
		TickFor(rig, TimeSpan.FromSeconds(1));
		Assert.Equal(sent, rig.Sender.Sent);
		byte[] last = rig.Last;
		Assert.Equal((0, 0x40, 0x80), Rgb(last));
		Assert.Equal(0x04, last[OutputBlock.PlayerLeds]);

		// The game sent its colour once and nothing since: the hold still runs out.
		TickFor(rig, ControllerBridge.DefaultGameLightIdle);
		Assert.True(IsUserColour(rig.Last));
	}

	[Fact]
	public void ProfileKeepingTheUsersLight_TheUsersColour_ThenTheGamesAgainWhenAllowed()
	{
		using Rig rig = new();
		rig.Sender.Tick();
		SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 0, 0x40, 0x80));

		rig.Bridge.ApplyProfile(new BridgeProfile(LightFromGame: false));
		// The host has the sender send the user's light at once on such a profile.
		rig.Sender.Invalidate();
		Assert.False(rig.Bridge.GameControlsLight);
		Assert.True(IsUserColour(rig.Last));

		// Allowed again while the hold runs: the game's last colour comes back without a new report.
		rig.Clock.Advance(TimeSpan.FromSeconds(1));
		rig.Bridge.ApplyProfile(new BridgeProfile(LightFromGame: true));
		Assert.True(rig.Bridge.GameControlsLight);
		Assert.Equal((0, 0x40, 0x80), Rgb(rig.Last));
		Assert.NotEqual(0, rig.Last[OutputBlock.Flag1] & OutputBlock.LightbarControl);
	}

	[Fact]
	public void Reconnect_DualSenseToDualSense_KeepsTheGamesLight_UntilTheHoldRunsOut()
	{
		using Rig rig = new();
		rig.Sender.Tick();
		SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 0, 0x40, 0x80));
		GameLightCarry? carry = rig.Bridge.CaptureGameLight();
		Assert.NotNull(carry);

		// The next connection: a new controller session and pad, the user's light written on connect first.
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = NewPad();
		using ControllerBridge next = new(session, pad) { Time = rig.Clock, LightLog = _ => { } };
		next.SeedGameLight(carry!);
		session.UpdateOutput(LightSettings.FromRgb(UserColour).WriteTo);
		using LightSender sender = new(session.UpdateOutput, () => LightSettings.FromRgb(UserColour), () => null, () => next.GameControlsLight, rig.Clock)
		{
			GameLightWrites = () => next.GameLightWrites
		};
		Assert.True(next.RestoreGameLight());
		byte[] shown = Block(link.Writes.Last(w => w[0] == BluetoothReports.OutputId));
		Assert.Equal((0, 0x40, 0x80), Rgb(shown));
		Assert.False(sender.Tick());

		// The hold runs out GameLightIdle after the game's last output on the old pad.
		rig.Clock.Advance(ControllerBridge.DefaultGameLightIdle);
		Assert.True(sender.Tick());
		Assert.True(IsUserColour(Block(link.Writes.Last(w => w[0] == BluetoothReports.OutputId))));
		Assert.Null(next.CaptureGameLight());
	}
}
