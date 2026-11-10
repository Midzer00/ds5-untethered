// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Hid;
using Kopru.Core.Lighting;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// Several Bluetooth controllers that come and go; each open gives a new link.
internal sealed class MultiControllers(ConcurrentQueue<string> log) : IControllerSource
{
	private readonly object gate = new();
	private readonly List<string> present = [];

	public ConcurrentDictionary<string, RemovableLink> Open { get; } = new();

	public void Add(string id)
	{
		lock (gate)
		{
			present.Add(id);
		}
	}

	// Gone: no longer listed, and its open link fails.
	public void Remove(string id)
	{
		lock (gate)
		{
			present.Remove(id);
		}
		if (Open.TryRemove(id, out RemovableLink? link))
		{
			link.Remove();
		}
	}

	public IReadOnlyList<ControllerPresence> Enumerate()
	{
		lock (gate)
		{
			return [.. present.Select(id => new ControllerPresence(id, new RemovableLink(log, id).Device))];
		}
	}

	public IHidLink? TryOpen() => null;

	public IHidLink? TryOpen(string id)
	{
		lock (gate)
		{
			if (!present.Contains(id) || Open.ContainsKey(id))
			{
				return null;
			}
		}
		RemovableLink link = new(log, id);
		Open[id] = link;
		return link;
	}
}

// Records what was plugged in: the server's TCP port and the bus id.
internal sealed class RecordingAttacher : IUsbIpAttacher
{
	private int next;

	public ConcurrentQueue<(int Port, int TcpPort, string BusId)> Attached { get; } = new();

	public ConcurrentQueue<int> Detached { get; } = new();

	public Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel)
	{
		int port = Interlocked.Increment(ref next);
		Attached.Enqueue((port, tcpPort, busId));
		return Task.FromResult(port);
	}

	public Task DetachAsync(int port)
	{
		Detached.Enqueue(port);
		return Task.CompletedTask;
	}

	// No listing: the cleanup goes by the recorded state.
	public Task<IReadOnlyList<UsbIpImport>> ListAsync(CancellationToken cancel) => throw new InvalidOperationException("no listing");
}

public class BridgeManagerTests
{
	private readonly ConcurrentQueue<string> log = new();
	private readonly MultiControllers controllers;
	private readonly RecordingAttacher attacher = new();

	public BridgeManagerTests()
	{
		controllers = new MultiControllers(log);
	}

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private static DeviceIdentity Identity() => DeviceIdentity.Create(
		BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3], [DeviceIdentity.PairingFeature] = new byte[20] });

	private BridgeHost Host() => new(controllers, new FakeIdentity(Identity()), attacher)
	{
		PollInterval = TimeSpan.FromMilliseconds(10),
		MonitorInterval = TimeSpan.FromMilliseconds(10),
		NoInputTimeout = Timeout.InfiniteTimeSpan
	};

	private BridgeManager Manager() => new(controllers, Host(), _ => Host());

	[Fact]
	public async Task ThreeControllers_AttachWithDistinctPortsBusIdsAndSlots()
	{
		await using BridgeManager manager = Manager();
		controllers.Add("a");
		controllers.Add("b");
		controllers.Add("c");

		await manager.StartAsync();
		WaitUntil(() => manager.Hosts.Take(3).All(h => h.State == BridgeState.Running));

		Assert.Equal([1, 2, 3], manager.Controllers.Select(c => c.Slot));
		Assert.Equal(["a", "b", "c"], manager.Controllers.Select(c => c.Id));
		(int Port, int TcpPort, string BusId)[] attached = [.. attacher.Attached];
		Assert.Equal(3, attached.Length);
		Assert.Equal(3, attached.Select(a => a.TcpPort).Distinct().Count());
		Assert.Equal(["1-1", "1-2", "1-3"], attached.Select(a => a.BusId).Order());
		Assert.Equal(BridgeState.Stopped, manager.Host(4).State);

		await manager.StopAsync();
		Assert.All(manager.Hosts, h => Assert.Equal(BridgeState.Stopped, h.State));
		Assert.Equal(3, attacher.Detached.Count);
	}

	[Fact]
	public async Task TwoControllers_EachHostOpensOnlyItsOwn()
	{
		await using BridgeManager manager = Manager();
		controllers.Add("a");
		controllers.Add("b");

		await manager.StartAsync();
		WaitUntil(() => manager.Host(1).State == BridgeState.Running && manager.Host(2).State == BridgeState.Running);

		Assert.EndsWith("_a", manager.Host(1).Device!.Path, StringComparison.Ordinal);
		Assert.EndsWith("_b", manager.Host(2).Device!.Path, StringComparison.Ordinal);
		Assert.Equal("1-1", manager.Host(1).BusId);
		Assert.Equal("1-2", manager.Host(2).BusId);
	}

	[Fact]
	public async Task LeaveAndRejoin_KeepsTheSlot_NewControllersTakeAnUnusedSlot()
	{
		await using BridgeManager manager = Manager();
		controllers.Add("a");
		controllers.Add("b");
		await manager.StartAsync();
		WaitUntil(() => manager.Host(2).State == BridgeState.Running);

		controllers.Remove("b");
		await manager.RefreshAsync();
		Assert.Equal(["a"], manager.Controllers.Select(c => c.Id));
		Assert.Equal(BridgeState.Stopped, manager.Host(2).State);

		controllers.Add("c");
		await manager.RefreshAsync();
		Assert.Equal(3, manager.SlotOf("c"));

		controllers.Add("b");
		await manager.RefreshAsync();
		Assert.Equal(2, manager.SlotOf("b"));
		WaitUntil(() => manager.Host(2).State == BridgeState.Running && manager.Host(3).State == BridgeState.Running);
		Assert.Equal([(1, "a"), (2, "b"), (3, "c")], manager.Controllers.Select(c => (c.Slot, c.Id)));
	}

	[Fact]
	public async Task FifthController_WaitsForAFreeSlot()
	{
		await using BridgeManager manager = Manager();
		foreach (string id in new[] { "a", "b", "c", "d", "e" })
		{
			controllers.Add(id);
		}
		await manager.RefreshAsync();
		Assert.Equal(4, manager.Controllers.Count);
		Assert.Null(manager.SlotOf("e"));

		controllers.Remove("c");
		await manager.RefreshAsync();
		Assert.Equal(3, manager.SlotOf("e"));
	}

	[Fact]
	public async Task WhileStopped_ControllersAreListedButNotBridged()
	{
		await using BridgeManager manager = Manager();
		controllers.Add("a");
		controllers.Add("b");
		int changes = 0;
		manager.ControllersChanged += () => changes++;

		await manager.RefreshAsync();
		await manager.RefreshAsync();

		Assert.Equal(2, manager.Controllers.Count);
		Assert.Equal(1, changes);
		Assert.All(manager.Hosts, h => Assert.Equal(BridgeState.Stopped, h.State));
		Assert.Empty(attacher.Attached);
	}

	[Fact]
	public void GameModeProfile_GoesToEverySlot_UnlessTheSlotHasItsOwn()
	{
		BridgeManager manager = Manager();
		manager.Mode = () => GameMode.Supported;
		BridgeProfile global = new(GameHapticsLevel: 2.0);
		manager.ApplyProfile(global);
		Assert.All(manager.Hosts, h => Assert.Same(global, h.Profile));

		SupportedProfile own = new(0.5, 0.25, false);
		UnsupportedProfile other = new(0.7, RumbleFeel.Punchy, FallbackTrigger.Off, 0.5);
		manager.SetProfileOverride(2, own, other);

		Assert.Equal(0.5, manager.Host(2).Profile.GameHapticsLevel);
		Assert.Same(global, manager.Host(1).Profile);
		Assert.Same(global, manager.Host(3).Profile);

		manager.SetProfileOverride(2, null, null);
		Assert.Same(global, manager.Host(2).Profile);
	}

	[Fact]
	public void PlayerLeds_ShowTheSlot_UnlessTheUserChoseAPattern()
	{
		LightSettings light = LightSettings.FromRgb(0x112233);
		Assert.Same(light, BridgeManager.LightForSlot(light, 1));
		Assert.Equal(PlayerLedPattern.Two, BridgeManager.LightForSlot(light, 2).Options.PlayerPattern);
		Assert.Equal(PlayerLedPattern.Four, BridgeManager.LightForSlot(light, 4).Options.PlayerPattern);

		LightSettings all = light with { Options = light.Options with { PlayerPattern = PlayerLedPattern.All } };
		Assert.Same(all, BridgeManager.LightForSlot(all, 3));
		LightSettings off = LightSettings.FromRgb(0x112233, playerLeds: false);
		Assert.Same(off, BridgeManager.LightForSlot(off, 3));
	}

	[Fact]
	public void EachSlot_HasItsOwnVirtualAddress()
	{
		DeviceIdentity identity = Identity();
		Assert.Same(identity, identity.ForSlot(1));
		byte[] pairing = identity.ForSlot(3).FeatureReports[DeviceIdentity.PairingFeature];
		Assert.Equal([0x02, 0x4B, 0x50, 0x52, 0x55, 0x03], pairing[1..7]);
		Assert.Equal([0x02, 0x4B, 0x50, 0x52, 0x55, 0x01], identity.FeatureReports[DeviceIdentity.PairingFeature][1..7]);
		Assert.Equal("1-4", VirtualDualSense.BusIdFor(4));
		Assert.Equal(VirtualDualSense.BusId, VirtualDualSense.BusIdFor(1));
	}

	[Fact]
	public void StaleCleanup_OfOneSlot_LeavesTheOtherSlotsPads()
	{
		UsbIpImport[] imports =
		[
			new(1, "127.0.0.1", 3240, "1-1"),
			new(2, "127.0.0.1", 3241, "1-2"),
			new(3, "127.0.0.1", 3242, "1-3"),
		];
		Assert.Equal([2], BridgeHost.StalePorts(imports, null, "1-2"));
		Assert.Equal([1], BridgeHost.StalePorts(imports, null));
		Assert.Equal([3], BridgeHost.StalePorts(imports, new BridgeAttachState(3, 3242, "1-3"), "1-3"));
	}

	[Fact]
	public async Task Cleanup_UsesEachSlotsStateFile()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-multi-" + Guid.NewGuid().ToString("N"));
		try
		{
			BridgeManager manager = new(controllers, Host(), slot => new BridgeHost(controllers, new FakeIdentity(Identity()), attacher)
			{
				StateFile = new BridgeStateFile(Path.Combine(dir, $"bridge-state-{slot}.json"))
			});
			manager.Host(1).StateFile = new BridgeStateFile(Path.Combine(dir, "bridge-state.json"));
			manager.Host(2).StateFile!.Save(new BridgeAttachState(7, 4000, "1-2"));
			manager.Host(1).StateFile!.Save(new BridgeAttachState(5, 4001, "1-1"));

			await manager.CleanupStaleAsync();

			Assert.Equal([5, 7], attacher.Detached.Order());
			Assert.Null(manager.Host(2).StateFile!.Load());
		}
		finally
		{
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
		}
	}
}
