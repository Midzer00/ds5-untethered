// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The mute LED while a game with native DualSense support streams output reports (haptics, triggers, lights): every
// Bluetooth 0x31 report that leaves after a mic button change carries the app's value with its valid bit, the first one
// included, whatever the game puts in its reports. Owner's report (2026-11-12, LEGO Batman): the app logged each change
// but the light did not follow while the game was running.
[Collection("AppLog")]
public class MuteLedGameTests
{
	private readonly QueueControllers controllers = new();

	private readonly ConcurrentQueue<string> log = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 3000), "timed out");

	private static VirtualDualSense Pad() => new(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00], new Dictionary<int, string> { [1] = "S", [2] = "D" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] }));

	private BridgeHost Host() =>
		new(controllers, new FakeIdentity(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
				[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00], new Dictionary<int, string> { [1] = "S", [2] = "D" },
				new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] })), new FakeAttacher(log))
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			HapticsStallTimeout = Timeout.InfiniteTimeSpan,
			SpeakerEncoderFactory = () => new FakeOpusEncoder(),
			MicDecoderFactory = () => new FakeMicDecoder()
		};

	// The kinds of report a game sends: what it does with the mute LED and the other LED flags.
	public enum GameMuteLed
	{
		// Mute LED valid bit set, value dark.
		Off,
		// Mute LED valid bit set, value lit.
		On,
		// Mute LED valid bit set, pulsing.
		Pulse,
		// No mute LED valid bit, junk in the value byte.
		NoFlag,
		// Light bar, player LEDs, LED brightness and light bar setup flags, mute LED dark.
		AllLedFlags,
		// "Release LEDs" with the light bar.
		ReleaseLeds
	}

	internal static byte[] GameReport(GameMuteLed kind, byte trigger)
	{
		byte[] block = new byte[OutputBlock.Size];
		// What a native game streams: haptics select, trigger effects, power-save / audio fields, the light bar.
		block[OutputBlock.Flag0] = OutputBlock.HapticsSelect | OutputBlock.RightTriggerEffect | OutputBlock.LeftTriggerEffect | 0x80;
		block[OutputBlock.Flag1] = OutputBlock.PowerSaveControl;
		block[OutputBlock.PowerSave] = 0x10;
		block[OutputBlock.RightTrigger] = 0x21;
		block[OutputBlock.RightTrigger + 1] = trigger;
		OutputBlock.SetLightbar(block, 0x11, 0x22, trigger);
		switch (kind)
		{
			case GameMuteLed.Off:
				OutputBlock.SetMuteLed(block, OutputBlock.MuteLedOff);
				break;
			case GameMuteLed.On:
				OutputBlock.SetMuteLed(block, OutputBlock.MuteLedOn);
				break;
			case GameMuteLed.Pulse:
				OutputBlock.SetMuteLed(block, 2);
				break;
			case GameMuteLed.NoFlag:
				block[OutputBlock.MuteLed] = 2;
				break;
			case GameMuteLed.AllLedFlags:
				OutputBlock.SetMuteLed(block, OutputBlock.MuteLedOff);
				OutputBlock.SetPlayerLeds(block, 0x04, OutputBlock.LedBrightnessHigh);
				block[OutputBlock.Flag2] |= OutputBlock.LightbarSetupControl;
				block[OutputBlock.LightbarSetup] = 1;
				break;
			case GameMuteLed.ReleaseLeds:
				block[OutputBlock.Flag1] |= OutputBlock.ReleaseLeds;
				break;
		}
		byte[] report = new byte[UsbReports.OutputSize];
		UsbReports.BuildOutput(block, report);
		return report;
	}

	internal static void Send(VirtualDualSense pad, byte[] report) =>
		pad.Submit(new SubmitCommand(5, 0, UsbIpCodes.DirOut, VirtualDualSense.HidOutEndpoint, 0, report.Length, 0, -1, 1,
			new byte[8], report, []), _ => { });

	private static byte[][] Outputs(FakeLink link) => [.. link.Writes.Where(w => w[0] == BluetoothReports.OutputId)];

	// The app's value with its valid bit, and none of the game's one-shot LED commands repeated with it.
	private static void AssertCarries(byte[] w, byte value)
	{
		Assert.NotEqual(0, w[3 + OutputBlock.Flag1] & OutputBlock.MuteLedControl);
		Assert.Equal(value, w[3 + OutputBlock.MuteLed]);
		Assert.Equal(0, w[3 + OutputBlock.Flag1] & OutputBlock.ReleaseLeds);
		Assert.Equal(0, w[3 + OutputBlock.Flag2] & OutputBlock.LightbarSetupControl);
	}

	// What BridgeHost.SyncMuteLed does on a change: the value for the bridge, then the app's own write.
	private static void Change(ControllerBridge bridge, BluetoothSession session, byte value)
	{
		bridge.AppMuteLed = value;
		session.UpdateOutput(b =>
		{
			OutputBlock.SetMuteLed(b, value);
			ControllerBridge.ClearOneShotLedCommands(b);
		});
	}

	// DualSense Mode on: a native game in front runs with the supported profile, so the game's light (and its LED flags)
	// goes through (LightFromGame true); off: the game's LED flags are taken out (LightFromGame false). Both with the game
	// streaming reports (and haptics) around the presses.
	public static TheoryData<GameMuteLed, bool> Kinds()
	{
		TheoryData<GameMuteLed, bool> data = new();
		foreach (GameMuteLed kind in Enum.GetValues<GameMuteLed>())
		{
			data.Add(kind, true);
			data.Add(kind, false);
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(Kinds))]
	public void Streaming_EveryReportAfterAPress_CarriesTheAppsValue(GameMuteLed kind, bool dualSenseMode)
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { AppMuteLed = OutputBlock.MuteLedOff, LightFromGame = dualSenseMode };
		session.Start();
		using CancellationTokenSource stop = new();
		Thread haptics = new(() =>
		{
			// The game's haptics keep the 0x32 stream running next to the 0x31 reports.
			while (!stop.IsCancellationRequested)
			{
				for (int i = 0; i < 32; i++)
				{
					session.Haptics.Push(40, -40);
				}
				session.WakeHaptics();
				Thread.Sleep(5);
			}
		}) { IsBackground = true };
		haptics.Start();
		byte trigger = 0;
		foreach (byte value in new[] { OutputBlock.MuteLedOn, OutputBlock.MuteLedOff, OutputBlock.MuteLedOn, OutputBlock.MuteLedOff })
		{
			for (int i = 0; i < 5; i++)
			{
				Send(pad, GameReport(kind, trigger++));
			}
			int before = Outputs(link).Length;
			Change(bridge, session, value);
			// Changing reports, then the same one again and again (games repeat their state every frame).
			for (int i = 0; i < 3; i++)
			{
				Send(pad, GameReport(kind, trigger++));
			}
			byte[] repeated = GameReport(kind, trigger++);
			for (int i = 0; i < 6; i++)
			{
				Send(pad, repeated);
			}
			byte[][] after = Outputs(link)[before..];
			Assert.True(after.Length >= 1 + 3 + 1, $"{after.Length} reports after the change");
			Assert.All(after, w => AssertCarries(w, value));
		}
		stop.Cancel();
		haptics.Join();
		Assert.True(session.HapticsReports > 0);
	}

	// Not streaming: the game sent its state (light bar setup included) once and went quiet; then the press. The app's own
	// write is the only report: it must carry the value and must not repeat the game's light bar setup from the output
	// state (before the fix it did, with DualSense Mode on, and the light did not follow).
	[Theory]
	[MemberData(nameof(Kinds))]
	public void GameQuiet_TheAppsOwnWrite_CarriesTheValue_WithoutTheGamesSetup(GameMuteLed kind, bool dualSenseMode)
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { AppMuteLed = OutputBlock.MuteLedOff, LightFromGame = dualSenseMode };
		session.Start();
		Send(pad, GameReport(kind, 1));
		foreach (byte value in new[] { OutputBlock.MuteLedOn, OutputBlock.MuteLedOff })
		{
			int before = Outputs(link).Length;
			Change(bridge, session, value);
			byte[][] after = Outputs(link)[before..];
			AssertCarries(Assert.Single(after), value);
		}
	}

	// The game's light bar setup goes out once per value (DualSense Mode on), directly followed by the mute LED again
	// without it; repeats of the same setup are taken out.
	[Fact]
	public void LightbarSetup_PassedOncePerValue_ThenTheMuteLedAgain()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { AppMuteLed = OutputBlock.MuteLedOn };
		session.Start();
		int before = Outputs(link).Length;
		for (byte i = 0; i < 5; i++)
		{
			Send(pad, GameReport(GameMuteLed.AllLedFlags, i));
		}
		byte[][] after = Outputs(link)[before..];
		Assert.Equal(1, after.Count(w => (w[3 + OutputBlock.Flag2] & OutputBlock.LightbarSetupControl) != 0));
		Assert.NotEqual(0, after[0][3 + OutputBlock.Flag2] & OutputBlock.LightbarSetupControl);
		Assert.Equal(1, after[0][3 + OutputBlock.LightbarSetup]);
		Assert.All(after, w => Assert.Equal(OutputBlock.MuteLedOn, w[3 + OutputBlock.MuteLed]));
		AssertCarries(after[1], OutputBlock.MuteLedOn);
		Assert.All(after[1..], w => AssertCarries(w, OutputBlock.MuteLedOn));
		Assert.Contains("game sent the light bar setup 5 times, passed 1 (mute led on sent again after each)", bridge.TakeMuteLedOverrides());
	}

	// A repeat of the game's last report right after a change is let through (a few times), so the new value goes out in
	// game reports too, not only in the app's single write.
	[Fact]
	public void RepeatedGameReport_AfterAChange_IsSentAgainWithTheNewValue()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { AppMuteLed = OutputBlock.MuteLedOff };
		session.Start();
		byte[] report = GameReport(GameMuteLed.Off, 7);
		// The initial value counts as a change too: the first repeats go out, after that they are dropped as before.
		for (int i = 0; i < ControllerBridge.MuteLedResendReports + 1; i++)
		{
			Send(pad, report);
		}
		long settled = bridge.Outputs;
		Send(pad, report);
		Assert.Equal(settled, bridge.Outputs);

		int before = Outputs(link).Length;
		bridge.AppMuteLed = OutputBlock.MuteLedOn;
		for (int i = 0; i < ControllerBridge.MuteLedResendReports + 3; i++)
		{
			Send(pad, report);
		}
		byte[][] after = Outputs(link)[before..];
		Assert.Equal(ControllerBridge.MuteLedResendReports, after.Length);
		Assert.All(after, w => AssertCarries(w, OutputBlock.MuteLedOn));
	}

	// The game owns the button: its mute LED and its "release LEDs" go through as sent.
	[Fact]
	public void GameOwnsTheButton_ItsMuteLedAndReleaseGoThrough()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		session.Start();
		Send(pad, GameReport(GameMuteLed.Pulse, 1));
		byte[] w = Outputs(link).Last();
		Assert.Equal(2, w[3 + OutputBlock.MuteLed]);
		Send(pad, GameReport(GameMuteLed.ReleaseLeds, 2));
		Assert.NotEqual(0, Outputs(link).Last()[3 + OutputBlock.Flag1] & OutputBlock.ReleaseLeds);
	}

	// The whole path through the host, DualSense Mode on (supported profile: the game's light goes through) and off (its
	// LED flags are taken out), with the game streaming reports from another thread or quiet after one report: the mic
	// button presses (MicSettings.Live, as the app pushes them) change the LED in the first report after the change and in
	// every later one; nothing built before the change is sent after it.
	[Theory]
	[InlineData(true, true)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(false, false)]
	public async Task Host_Presses_EveryLaterReportCarriesTheNewValue(bool dualSenseMode, bool streaming)
	{
		ConcurrentQueue<string> lines = new();
		void OnLine(string line)
		{
			if (line.StartsWith("mute led", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += OnLine;
		try
		{
			await using BridgeHost host = Host();
			FakeLink link = new();
			controllers.Links.Enqueue(link);
			host.ApplyProfile(new BridgeProfile(LightFromGame: dualSenseMode));
			host.ApplyMic(new MicSettings(false) { Live = false });
			await host.StartAsync();
			WaitUntil(() => host.State == BridgeState.Running);
			VirtualDualSense pad = host.VirtualDualSense!;

			using CancellationTokenSource stop = new();
			GameMuteLed[] kinds = Enum.GetValues<GameMuteLed>();
			Thread game = new(() =>
			{
				int n = 0;
				while (!stop.IsCancellationRequested)
				{
					Send(pad, GameReport(kinds[n / 4 % kinds.Length], (byte)(n / 2)));
					n++;
					Thread.Sleep(1);
				}
			}) { IsBackground = true };
			if (streaming)
			{
				game.Start();
				WaitUntil(() => Outputs(link).Length > 20);
			}
			else
			{
				Send(pad, GameReport(GameMuteLed.AllLedFlags, 1));
				Send(pad, GameReport(GameMuteLed.ReleaseLeds, 2));
				Send(pad, GameReport(GameMuteLed.Pulse, 3));
			}

			bool live = false;
			for (int press = 0; press < 6; press++)
			{
				live = !live;
				byte value = live ? OutputBlock.MuteLedOn : OutputBlock.MuteLedOff;
				host.ApplyMic(new MicSettings(false) { Live = live });
				int changeAt = Outputs(link).Length;
				// The app's own write (or a game report after it) is the last one before this point.
				AssertCarries(Outputs(link)[changeAt - 1], value);
				if (streaming)
				{
					WaitUntil(() => Outputs(link).Length > changeAt + 10);
					Assert.All(Outputs(link)[changeAt..], w => AssertCarries(w, value));
				}
			}
			stop.Cancel();
			if (streaming)
			{
				game.Join();
			}
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= OnLine;
		}
		string delivery = streaming ? "sent directly and merged into every game report (game streaming output)" : "sent directly";
		Assert.Contains(lines, l => l.StartsWith($"mute led: on (mic settings: PC microphone live; {delivery}", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("mute led: game asked for ", StringComparison.Ordinal) && l.EndsWith(" times, kept off", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("mute led: game asked to release the LEDs ", StringComparison.Ordinal));
		if (streaming)
		{
			Assert.Contains(lines, l => l.StartsWith("mute led: ", StringComparison.Ordinal) && l.Contains(" carried by the first game report after the change (+", StringComparison.Ordinal));
		}
	}
}
