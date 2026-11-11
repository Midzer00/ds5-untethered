// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The global controller type ("Kumanda Türü"): Automatic follows the per-game choice; any other value wins everywhere.
public class ForcedPadTypeTests
{
	private static readonly GameCatalog Catalog = new([
		new CatalogGame("Old Game", ["old.exe"], null, false, true),
		new CatalogGame("Racer", ["racer.exe"], null, false, true)
	]);

	private static (GameModeController Games, GameModeTests.FakeProcesses Processes, PadBridge Bridge) Make()
	{
		var processes = new GameModeTests.FakeProcesses();
		var bridge = new PadBridge();
		return (new GameModeController(Catalog, new GameWatcher(Catalog, processes), bridge), processes, bridge);
	}

	[Fact]
	public void Forced_WinsOverThePerGameChoice_AndOneSwapPerChange()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, PadBridge bridge) = Make();
		processes.Show("old.exe");
		games.Tick();
		string key = games.ActiveGame!.Key;
		games.SetPadType(key, VirtualPadType.Xbox360);
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);

		int saves = 0;
		games.MemoryChanged += () => saves++;
		int before = bridge.Pads.Count;
		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.Equal(before + 1, bridge.Pads.Count);
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		Assert.Equal(VirtualPadType.DualShock4, games.CurrentPadType);
		Assert.Equal(1, saves);
		// The per-game choice is kept.
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor(key));

		// The same value again: nothing.
		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.Equal(before + 1, bridge.Pads.Count);
		Assert.Equal(1, saves);

		// Forced DualSense also wins over the game's Xbox 360.
		games.SetForcedPadType(VirtualPadType.DualSense);
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
	}

	[Fact]
	public void GameStartingWhileForced_StaysForced_AndAutomaticBringsTheGameChoiceBackAtOnce()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, PadBridge bridge) = Make();
		processes.Show("racer.exe");
		games.Tick();
		string racer = games.ActiveGame!.Key;
		games.SetPadType(racer, VirtualPadType.Xbox360);
		processes.Exit("racer.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);

		games.SetForcedPadType(VirtualPadType.DualShock4);
		int count = bridge.Pads.Count;
		processes.Show("racer.exe");
		games.Tick();
		// The game's own Xbox 360 choice does not apply: no swap at all.
		Assert.Equal(count, bridge.Pads.Count);
		Assert.Equal(VirtualPadType.DualShock4, games.CurrentPadType);

		// Back to Automatic while the game runs: its choice applies at once.
		games.SetForcedPadType(null);
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		Assert.Equal(count + 1, bridge.Pads.Count);
		Assert.Null(games.ForcedPadType);

		// Without a game, Automatic is DualSense.
		processes.Exit("racer.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
	}

	[Fact]
	public void Forced_IsSavedWithTheGameMemory_OlderMemoryIsAutomatic()
	{
		(GameModeController games, _, _) = Make();
		games.SetForcedPadType(VirtualPadType.Xbox360);
		GameMemory memory = games.SnapshotMemory();
		Assert.Equal(VirtualPadType.Xbox360, memory.ForcedPad);

		(GameModeController again, _, PadBridge bridge) = Make();
		again.LoadMemory(memory);
		Assert.Equal(VirtualPadType.Xbox360, again.ForcedPadType);
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);

		again.LoadMemory(GameMemory.Default);
		Assert.Null(again.ForcedPadType);
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);

		// An unknown value (a newer file) is treated as Automatic.
		again.LoadMemory(GameMemory.Default with { ForcedPad = (VirtualPadType)42 });
		Assert.Null(again.ForcedPadType);
		again.SetForcedPadType((VirtualPadType)42);
		Assert.Null(again.ForcedPadType);
	}

	[Fact]
	public async Task SeveralControllers_EverySlotGetsTheForcedType()
	{
		ConcurrentQueue<string> log = new();
		MultiControllers controllers = new(log);
		RecordingAttacher attacher = new();
		BridgeHost Host() => new(controllers, new FakeIdentity(Identity(BridgeTests.Device)), attacher)
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		await using BridgeManager manager = new(controllers, Host(), _ => Host());
		var processes = new GameModeTests.FakeProcesses();
		var games = new GameModeController(Catalog, new GameWatcher(Catalog, processes), manager);
		controllers.Add("a");
		controllers.Add("b");
		await manager.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => manager.Hosts.Take(2).All(h => h.State == BridgeState.Running), 5000));
		ConcurrentQueue<VirtualPadType> changed = new();
		foreach (BridgeHost h in manager.Hosts)
		{
			h.PadTypeChanged += changed.Enqueue;
		}

		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 2, 5000));
		Assert.All(manager.Hosts, h => Assert.Equal(VirtualPadType.DualShock4, h.PadType));
		Assert.Equal([VirtualPadType.DualShock4, VirtualPadType.DualShock4], changed);
		await Task.Delay(100);
		Assert.Equal(2, changed.Count);
		Assert.All(manager.Hosts.Take(2), h => Assert.Equal(BridgeState.Running, h.State));
	}

	[Fact]
	public async Task DualSenseEdge_ForcedTypesSwapAndComeBackAsTheEdge()
	{
		ConcurrentQueue<string> log = new();
		QueueControllers controllers = new();
		DeviceIdentity edge = CalibrationOverlay.Apply(EdgeIdentity.Load(), new FeatureLink(() => new byte[64]));
		await using BridgeHost host = new(controllers, new FakeIdentity(edge), new FakeAttacher(log), new ManualTime())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		ConcurrentQueue<VirtualPadType> changed = new();
		host.PadTypeChanged += changed.Enqueue;
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));

		host.UsePad(VirtualPadType.DualShock4);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 1, 5000));
		host.UsePad(VirtualPadType.Xbox360);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 2, 5000));
		host.UsePad(VirtualPadType.DualSense);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 3, 5000));

		Assert.Equal([VirtualPadType.DualShock4, VirtualPadType.Xbox360, VirtualPadType.DualSense], changed);
		Assert.Equal(BridgeState.Running, host.State);
		Assert.DoesNotContain("dispose a", log);
		// The emulated types have fixed identities (a DualShock 4 v2, a wired Xbox 360 pad) whatever the real controller
		// is; DualSense on an Edge is the Edge's own identity.
		using VirtualDualShock4 ds4 = new("1-1", null);
		Assert.Equal((0x054C, 0x09CC), (ds4.Info.VendorId, ds4.Info.ProductId));
		using VirtualXbox360 xbox = new("1-1");
		Assert.Equal((0x045E, 0x028E), (xbox.Info.VendorId, xbox.Info.ProductId));
		using VirtualDualSense back = new(edge);
		Assert.Equal((0x054C, 0x0DF2), (back.Info.VendorId, back.Info.ProductId));
	}

	private static DeviceIdentity Identity(byte[] device) => DeviceIdentity.Create(device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3], [DeviceIdentity.PairingFeature] = new byte[20] });

	private sealed class PadBridge : IGameBridge
	{
		public List<VirtualPadType> Pads { get; } = [];

		public void ApplyProfile(BridgeProfile profile)
		{
		}

		public GameSignals TakeGameSignals() => default;

		public void UsePad(VirtualPadType type) => Pads.Add(type);
	}
}
