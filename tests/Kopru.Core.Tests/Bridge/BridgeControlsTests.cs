// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Haptics;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

public class BridgeControlsTests
{
	private readonly QueueControllers controllers = new();

	private readonly ConcurrentQueue<string> log = new();

	private BridgeHost Host() =>
		new(controllers, new FakeIdentity(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
				[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00], new Dictionary<int, string> { [1] = "S", [2] = "D" },
				new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] })), new FakeAttacher(log))
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};

	private static byte[] Block(byte[] report) => report.AsSpan(3, OutputBlock.Size).ToArray();

	[Fact]
	public void Mixer_ReportsPeaksOfTakenFrames_AndResets()
	{
		HapticsMixer mixer = new();
		mixer.Push(-127, 10);
		mixer.Push(20, 0);
		Assert.Equal((0.0, 0.0), mixer.TakePeaks());
		mixer.Take(new sbyte[4], new sbyte[4]);
		(double l, double r) = mixer.TakePeaks();
		Assert.Equal(1.0, l, 3);
		Assert.Equal(10 / 127.0, r, 3);
		Assert.Equal((0.0, 0.0), mixer.TakePeaks());
	}

	[Fact]
	public void LightSettings_ScaleColour_AndSetPlayerLed()
	{
		byte[] block = new byte[OutputBlock.Size];
		LightSettings.FromRgb(0xFF8000, 0.5, playerLeds: true).WriteTo(block);
		Assert.Equal([128, 64, 0], block.AsSpan(OutputBlock.Red, 3).ToArray());
		Assert.NotEqual(0, block[OutputBlock.Flag1] & OutputBlock.LightbarControl);
		Assert.Equal(LightSettings.PlayerOneLed, block[OutputBlock.PlayerLeds]);
		LightSettings.FromRgb(0xFF8000, 1, playerLeds: false).WriteTo(block);
		Assert.Equal(0, block[OutputBlock.PlayerLeds]);
	}

	[Fact]
	public async Task HapticsTest_WhileStopped_OpensTheController_StreamsHaptics_AndCloses()
	{
		await using BridgeHost host = Host();
		FakeLink link = new();
		controllers.Links.Enqueue(link);
		host.ApplyLight(LightSettings.FromRgb(0x00FF00));

		Assert.True(await host.TestHapticsAsync(true, false, TimeSpan.FromMilliseconds(100)));

		byte[][] writes = [.. link.Writes];
		Assert.Contains(writes, w => w[0] == BluetoothReports.HapticsId);
		// The user's light went out with the session, and no rumble bits ever did.
		Assert.Contains(writes, w => w[0] == BluetoothReports.OutputId && Block(w)[OutputBlock.Red + 1] == 255);
		Assert.All(writes.Where(w => w[0] == BluetoothReports.OutputId),
			w => Assert.Equal(0, Block(w)[OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect)));
	}

	[Fact]
	public async Task Tests_WithoutController_ReturnFalse()
	{
		await using BridgeHost host = Host();
		Assert.False(await host.TestHapticsAsync(true, true, TimeSpan.FromMilliseconds(10)));
		Assert.False(await host.TestTriggersAsync(TriggerEffects.Feedback(2, 4), TimeSpan.Zero));
	}

	[Fact]
	public async Task TriggerTest_SetsTheEffect_ThenTurnsItOff()
	{
		await using BridgeHost host = Host();
		FakeLink link = new();
		controllers.Links.Enqueue(link);
		byte[] effect = TriggerEffects.Weapon(2, 6, 5);

		Assert.True(await host.TestTriggersAsync(effect, TimeSpan.FromMilliseconds(20)));

		byte[][] blocks = link.Writes.Where(w => w[0] == BluetoothReports.OutputId).Select(Block).ToArray();
		Assert.Contains(blocks, b => b.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size).SequenceEqual(effect));
		Assert.Equal(TriggerEffects.Off(), blocks[^1].AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray());
	}

	[Fact]
	public async Task RunningBridge_CallsTheOpenedHook_AppliesLight_AndShowsTheDevice()
	{
		await using BridgeHost host = Host();
		HidDevice? opened = null;
		host.ControllerOpened = (d, _) =>
		{
			opened = d;
			return Task.CompletedTask;
		};
		host.ApplyLight(LightSettings.FromRgb(0x0000FF));
		FakeLink link = new();
		controllers.Links.Enqueue(link);

		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));
		Assert.Same(link.Device, opened);
		Assert.Same(link.Device, host.Device);
		Assert.Contains(link.Writes, w => w[0] == BluetoothReports.OutputId && Block(w)[OutputBlock.Red + 2] == 255);

		// While running, tests use the bridge's own session.
		Assert.True(await host.TestHapticsAsync(false, true, TimeSpan.FromMilliseconds(50)));
		Assert.True(SpinWait.SpinUntil(() => link.Writes.Any(w => w[0] == BluetoothReports.HapticsId), 2000));
		await host.StopAsync();
		Assert.Null(host.Device);
	}

	[Fact]
	public void CaptureIdentitySource_LooksInTheAppDataFolderFirst()
	{
		Assert.EndsWith(Path.Combine("Untethered", "captures"), AppPaths.Captures);
	}
}
