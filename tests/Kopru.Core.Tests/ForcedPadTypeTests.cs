// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

/// The controller type model: a game's own choice, else the Automatic rules (AutoPadTypeTests), where the default type (ForcedPadType) is for games we know nothing about.
// The type is put in when the game starts; a change while it runs waits (pending) until it exits or "apply now".
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
	public void NoGameRunning_DefaultTypeSwapsAtOnce_OneSwapPerChange()
	{
		(GameModeController games, _, PadBridge bridge) = Make();
		Assert.Equal([VirtualPadType.DualSense], bridge.Pads);
		int saves = 0;
		games.MemoryChanged += () => saves++;
		List<PadSwap> swaps = [];
		games.PadSwapped += swaps.Add;
		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		Assert.Equal(VirtualPadType.DualShock4, games.CurrentPadType);
		Assert.Null(games.PendingPadType);
		Assert.Equal(1, saves);
		Assert.Equal(new PadSwap(VirtualPadType.DualSense, VirtualPadType.DualShock4, null, null), Assert.Single(swaps));
		Assert.Equal(PadTypeSource.DefaultSetting, games.PadStatus.Source);

		// The same value again: nothing.
		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.Equal(2, bridge.Pads.Count);
		Assert.Equal(1, saves);
	}

	[Fact]
	public void GamesOwnChoice_WinsOverTheDefaultType_AndIsPutInWhenTheGameStarts()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, PadBridge bridge) = Make();
		games.SetForcedPadType(VirtualPadType.DualShock4);
		games.SetPadType("Racer", VirtualPadType.Xbox360);
		games.SetPadType("Old Game", VirtualPadType.DualSense);
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("Racer"));
		Assert.Equal(VirtualPadType.DualShock4, games.PadTypeFor("Unknown Game"));
		Assert.Null(games.PadChoiceFor("Unknown Game"));

		processes.Show("racer.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		Assert.Equal(PadTypeSource.Game, games.PadStatus.Source);
		processes.Exit("racer.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);

		// An explicit DualSense for a game beats the default type too; back on Automatic a listed game gets DualSense, not the default.
		processes.Show("old.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
		processes.Exit("old.exe");
		games.Tick();
		games.SetPadType("Old Game", null);
		Assert.Null(games.PadChoiceFor("Old Game"));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("Old Game"));
	}

	[Fact]
	public void ChangeWhileAGameRuns_IsSavedButPending_UntilTheGameExits()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, PadBridge bridge) = Make();
		processes.Show("racer.exe");
		games.Tick();
		int saves = 0, states = 0;
		games.MemoryChanged += () => saves++;
		games.PadStateChanged += () => states++;
		int pads = bridge.Pads.Count;

		games.SetPadType("Racer", VirtualPadType.Xbox360);
		Assert.Equal(1, saves);
		Assert.Equal(pads, bridge.Pads.Count);
		Assert.Equal(VirtualPadType.DualSense, games.CurrentPadType);
		Assert.Equal(VirtualPadType.Xbox360, games.PendingPadType);
		Assert.True(games.PadStatus.HasPending);
		Assert.Equal("Racer", games.PadStatus.GameName);
		Assert.Equal(1, states);

		// The default type changing while the game runs waits too (and does not touch this game's choice).
		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.Equal(pads, bridge.Pads.Count);
		Assert.Equal(VirtualPadType.Xbox360, games.PendingPadType);

		// Still running at the next ticks: nothing changes.
		games.Tick();
		games.Tick();
		Assert.Equal(pads, bridge.Pads.Count);

		// The game exits: back to the default type at once, nothing pending.
		processes.Exit("racer.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		Assert.Null(games.PendingPadType);

		// Starting again: its choice is put in at the start.
		processes.Show("racer.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
	}

	[Fact]
	public void ChangingBack_ClearsThePending_AndApplyNowSwapsWithTheGameRunning()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, PadBridge bridge) = Make();
		processes.Show("racer.exe");
		games.Tick();
		games.SetPadType("Racer", VirtualPadType.Xbox360);
		games.SetPadType("Racer", VirtualPadType.DualSense);
		Assert.Null(games.PendingPadType);
		Assert.False(games.ApplyPadTypeNow());

		games.SetPadType("Racer", VirtualPadType.DualShock4);
		List<PadSwap> swaps = [];
		games.PadSwapped += swaps.Add;
		Assert.True(games.ApplyPadTypeNow());
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		Assert.Null(games.PendingPadType);
		Assert.Equal(new PadSwap(VirtualPadType.DualSense, VirtualPadType.DualShock4, "Racer", "Racer"), Assert.Single(swaps));
	}

	[Fact]
	public void AnotherGameTakingOver_WhileTheFirstRuns_Waits()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, PadBridge bridge) = Make();
		games.SetPadType("Old Game", VirtualPadType.Xbox360);
		processes.Show("racer.exe");
		games.Tick();
		int pads = bridge.Pads.Count;
		processes.Show("old.exe");
		games.Tick();
		Assert.Equal("Old Game", games.State.Name);
		Assert.Equal(pads, bridge.Pads.Count);
		Assert.Equal(VirtualPadType.Xbox360, games.PendingPadType);
	}

	// A game with its own type is found when its process appears, before it comes to the front (a launcher in front, or the
	// game still loading behind another window).
	[Fact]
	public void GameProcessAppearing_GetsItsTypeBeforeComingToTheFront()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, PadBridge bridge) = Make();
		long now = 0;
		games.ClockMs = () => now;
		games.SetPadType("Racer", VirtualPadType.Xbox360);
		processes.Show("explorer.exe");
		games.Tick();
		processes.Start("racer.exe");
		// The processes are listed again after LaunchScanInterval.
		now += (long)GameModeController.LaunchScanInterval.TotalMilliseconds;
		games.Tick();
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		Assert.Equal("Racer", games.State.Name);
		Assert.False(games.IsGameInFront);

		// It comes to the front: the same launch, no new swap.
		int pads = bridge.Pads.Count;
		processes.Show("racer.exe");
		games.Tick();
		Assert.Equal(pads, bridge.Pads.Count);

		processes.Exit("racer.exe");
		processes.Show("explorer.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
	}

	// No process look-up while nothing needs one: no game with its own type, or its type is the current one, or a game runs.
	[Fact]
	public void ProcessLookup_OnlyWhileAGameNeedsAnotherType()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, _) = Make();
		games.Tick();
		Assert.Equal(0, processes.AllProcessesCalls);
		games.SetPadType("Racer", VirtualPadType.DualSense);
		games.Tick();
		Assert.Equal(0, processes.AllProcessesCalls);
		games.SetPadType("Racer", VirtualPadType.Xbox360);
		games.Tick();
		Assert.Equal(1, processes.AllProcessesCalls);
		processes.Show("old.exe");
		games.Tick();
		Assert.Equal(1, processes.AllProcessesCalls);
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

	internal sealed class PadBridge : IGameBridge
	{
		public List<VirtualPadType> Pads { get; } = [];

		public void ApplyProfile(BridgeProfile profile)
		{
		}

		public GameSignals TakeGameSignals() => default;

		public void UsePad(VirtualPadType type) => Pads.Add(type);
	}
}
