// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

public class LightHostTests
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
	public async Task RunningBridge_AnimatesTheLight_WithoutRumbleBits_AndStopsWithTheBridge()
	{
		await using BridgeHost host = Host();
		FakeLink link = new();
		controllers.Links.Enqueue(link);
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));

		host.ApplyLight(LightSettings.FromRgb(0xFF0000) with { Options = new LightOptions(LightMode.Rainbow, RainbowSpeed: 10) });
		Assert.True(SpinWait.SpinUntil(() => host.LightReportsSent >= 5, 3000));
		byte[][] light = link.Writes.Where(w => w[0] == BluetoothReports.OutputId).Select(Block).ToArray();
		Assert.All(light, b => Assert.Equal(0, b[OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect)));
		Assert.True(light.Select(b => (b[OutputBlock.Red], b[OutputBlock.Red + 1], b[OutputBlock.Red + 2])).Distinct().Count() > 2);

		// A static light is written once, not every tick.
		host.ApplyLight(LightSettings.FromRgb(0x00FF00));
		await Task.Delay(100);
		long sent = host.LightReportsSent;
		await Task.Delay(200);
		Assert.Equal(sent, host.LightReportsSent);

		await host.StopAsync();
		Assert.Equal(0, host.LightReportsSent);
	}
}
