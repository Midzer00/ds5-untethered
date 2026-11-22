// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hiding;
using Xunit;

namespace Kopru.Core.Tests;

// The ledger never loses what our app put into HidHide: kills, several copies (other exe folders), older flat files.
// Simulated processes: Run starts one with its own owner, Kill ends it without any cleanup.
[Collection("Localization")]
public sealed class HidingOwnershipTests : IDisposable
{
	private const string BtPad = @"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\b&24ba9c0d&2&0000";
	private const string ExeA = @"C:\Builds\A\Kopru.App.exe";
	private const string ExeB = @"C:\Builds\B\Kopru.App.exe";
	private const string OtherExe = @"C:\Tools\Other.exe";

	private readonly string _dir = Path.Combine(Path.GetTempPath(), "kopru-owner-" + Guid.NewGuid().ToString("N"));
	private readonly FakeHidHide _fake = new();
	private readonly HidHideControl _control;
	private readonly HashSet<HidingOwner> _live = [];
	private readonly string _ledger;

	public HidingOwnershipTests()
	{
		_control = new HidHideControl(_fake, @"C:\HidHideCLI.exe");
		_ledger = Path.Combine(_dir, HidingStateStore.FileName);
	}

	public void Dispose()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, true);
		}
	}

	private HidingStateStore Run(int pid)
	{
		var owner = new HidingOwner(pid, pid * 1000L);
		_live.Add(owner);
		return new HidingStateStore(_ledger, owner, _live.Contains);
	}

	private void Kill(HidingStateStore store) => _live.Remove(store.Owner);

	private int Count(string prefix) => _fake.Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

	private HidingLedger Ledger() => new HidingStateStore(_ledger).Load();

	[Fact]
	public async Task Kill_while_running_is_recovered_by_a_build_in_another_folder()
	{
		HidingStateStore a = Run(1);
		await EarlyAppRegistration.EnsureAppAllowedAsync(_control, a, ExeA);
		await HidingSession.StartAsync(_control, a, [BtPad], ExeA);
		Kill(a);

		await HidingSession.RecoverAsync(_control, Run(2));
		Assert.False(_fake.CloakOn);
		Assert.Empty(_fake.Devices);
		Assert.Empty(_fake.Apps);
		Assert.Empty(Ledger().Claims);
		Assert.False(File.Exists(_ledger));
	}

	[Fact]
	public async Task Recovery_never_undoes_a_running_copy_or_this_runs_own_session()
	{
		HidingStateStore a = Run(1);
		HidingSession session = await HidingSession.StartAsync(_control, a, [BtPad], ExeA);

		await HidingSession.RecoverAsync(_control, Run(2)); // a second copy starting
		await HidingSession.RecoverAsync(_control, a);      // this run's own start-up recovery arriving late
		Assert.True(_fake.CloakOn);
		Assert.Equal([BtPad], _fake.Devices);
		Assert.Equal([ExeA], _fake.Apps);

		await session.DisposeAsync();
		Assert.False(_fake.CloakOn);
		Assert.Empty(_fake.Devices);
		Assert.Empty(_fake.Apps);
	}

	[Fact]
	public async Task Two_copies_share_entries_and_the_last_one_out_restores()
	{
		HidingSession first = await HidingSession.StartAsync(_control, Run(1), [BtPad], ExeA);
		HidingSession second = await HidingSession.StartAsync(_control, Run(2), [BtPad], ExeB);
		Assert.Empty(second.AddedDevices);
		Assert.False(second.TurnedCloakOn);
		Assert.Equal([ExeB], second.AddedApps);

		await first.DisposeAsync();
		Assert.True(_fake.CloakOn);
		Assert.Equal([BtPad], _fake.Devices);
		Assert.Equal([ExeB], _fake.Apps);

		await second.DisposeAsync();
		Assert.False(_fake.CloakOn);
		Assert.Empty(_fake.Devices);
		Assert.Empty(_fake.Apps);
		Assert.Empty(Ledger().Claims);
	}

	[Fact]
	public async Task Stop_of_one_copy_never_drops_another_copys_ownership()
	{
		// Before the ledger, the stopping copy rewrote the whole state file and the running copy's record was lost.
		HidingStateStore b = Run(2);
		await EarlyAppRegistration.EnsureAppAllowedAsync(_control, b, ExeB);
		HidingSession idle = await HidingSession.StartAsync(_control, b, [], ExeB);
		HidingStateStore a = Run(1);
		await HidingSession.StartAsync(_control, a, [BtPad], ExeA);

		await idle.DisposeAsync();
		await EarlyAppRegistration.ReleaseAsync(_control, b);
		Kill(b);
		Assert.True(_fake.CloakOn); // copy A still needs it
		Kill(a);

		await HidingSession.RecoverAsync(_control, Run(3));
		Assert.False(_fake.CloakOn);
		Assert.Empty(_fake.Devices);
		Assert.Empty(_fake.Apps);
	}

	[Fact]
	public async Task A_fresh_start_after_a_kill_recovers_before_deciding_what_is_foreign()
	{
		HidingStateStore a = Run(1);
		await HidingSession.StartAsync(_control, a, [BtPad], ExeA);
		Kill(a);

		// No start-up recovery ran (it raced or failed): the bridge start itself must still take the entries back.
		HidingStateStore b = Run(2);
		HidingSession session = await HidingSession.StartAsync(_control, b, [BtPad], ExeB);
		Assert.Equal([BtPad], session.AddedDevices);
		Assert.True(session.TurnedCloakOn);
		Kill(b);

		await HidingSession.RecoverAsync(_control, Run(3));
		Assert.False(_fake.CloakOn);
		Assert.Empty(_fake.Devices);
		Assert.Empty(_fake.Apps);
	}

	[Fact]
	public async Task Entries_set_by_another_tool_are_left_alone_even_after_a_kill()
	{
		_fake.CloakOn = true;
		_fake.Devices.Add(BtPad);
		_fake.Apps.Add(OtherExe);
		HidingStateStore a = Run(1);
		HidingSession session = await HidingSession.StartAsync(_control, a, [BtPad], ExeA);
		Assert.Empty(session.AddedDevices);
		Assert.False(session.TurnedCloakOn);
		Kill(a);

		await HidingSession.RecoverAsync(_control, Run(2));
		Assert.True(_fake.CloakOn);
		Assert.Equal([BtPad], _fake.Devices);
		Assert.Equal([OtherExe], _fake.Apps); // only our own app entry went
		Assert.Equal(0, Count("--dev-unhide"));
		Assert.Equal(0, Count("--cloak-off"));
	}

	[Fact]
	public async Task Kill_after_undo_but_before_the_ledger_write_is_harmless()
	{
		HidingStateStore a = Run(1);
		await HidingSession.StartAsync(_control, a, [BtPad], ExeA);
		_fake.CloakOn = false; // undone, then killed before the claims were dropped
		_fake.Devices.Clear();
		_fake.Apps.Clear();
		Kill(a);

		await HidingSession.RecoverAsync(_control, Run(2));
		Assert.Equal(0, Count("--dev-unhide"));
		Assert.Equal(0, Count("--cloak-off"));
		Assert.Equal(0, Count("--app-unreg"));
		Assert.Empty(Ledger().Claims);
	}

	[Fact]
	public async Task Failed_undo_keeps_ownership_and_the_next_start_retries()
	{
		HidingStateStore a = Run(1);
		HidingSession session = await HidingSession.StartAsync(_control, a, [BtPad], ExeA);
		_fake.FailOn = "--dev-unhide";
		await Assert.ThrowsAsync<HidHideException>(() => session.DisposeAsync().AsTask());
		Assert.Equal([BtPad], _fake.Devices);
		Assert.True(Ledger().IsClaimed(HidingClaimKind.Device, BtPad));
		Assert.False(_fake.CloakOn);

		_fake.FailOn = null;
		await using (await HidingSession.StartAsync(_control, a, [], ExeA))
		{
			Assert.Empty(_fake.Devices); // taken back before the new session decided anything
		}
		Assert.Empty(Ledger().Claims);
	}

	[Fact]
	public async Task Flat_file_of_an_older_version_is_imported_and_recovered()
	{
		string legacy = Path.Combine(_dir, HidingStateStore.LegacyFileName);
		Directory.CreateDirectory(_dir);
		File.WriteAllText(legacy, JsonSerializer.Serialize(new HidingChanges
		{
			AddedDevices = [BtPad], TurnedCloakOn = true, EarlyApps = [ExeA]
		}));
		_fake.CloakOn = true;
		_fake.Devices.Add(BtPad);
		_fake.Apps.AddRange([ExeA, OtherExe]);

		await HidingSession.RecoverAsync(_control, Run(2), [legacy]);
		Assert.False(_fake.CloakOn);
		Assert.Empty(_fake.Devices);
		Assert.Equal([OtherExe], _fake.Apps);
		Assert.False(File.Exists(legacy));
		Assert.Empty(Ledger().Claims);
	}

	[Fact]
	public async Task Older_record_that_owns_nothing_leaves_a_foreign_hidden_controller_alone()
	{
		// The state found on a test machine: the old record only held our exe, the controller was hidden by someone else.
		string legacy = Path.Combine(_dir, HidingStateStore.LegacyFileName);
		Directory.CreateDirectory(_dir);
		File.WriteAllText(legacy, JsonSerializer.Serialize(new HidingChanges { EarlyApps = [ExeA] }));
		_fake.CloakOn = true;
		_fake.Devices.Add(BtPad);
		_fake.Apps.AddRange([ExeA, OtherExe]);

		await HidingSession.RecoverAsync(_control, Run(2), [legacy]);
		Assert.True(_fake.CloakOn);
		Assert.Equal([BtPad], _fake.Devices);
		Assert.Equal([OtherExe], _fake.Apps);
	}

	[Fact]
	public async Task Hiding_log_lines_name_no_devices_or_paths()
	{
		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("hiding:", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		AppLog.Written += Watch;
		try
		{
			HidingStateStore a = Run(1);
			await EarlyAppRegistration.EnsureAppAllowedAsync(_control, a, ExeA);
			HidingSession session = await HidingSession.StartAsync(_control, a, [BtPad], ExeA);
			await session.DisposeAsync();
			await EarlyAppRegistration.ReleaseAsync(_control, a);
		}
		finally
		{
			AppLog.Written -= Watch;
		}
		lock (lines)
		{
			Assert.Contains(lines, l => l.Contains("adding 1 device, cloak", StringComparison.Ordinal));
			Assert.Contains(lines, l => l.Contains("stop: undid 1 device, cloak", StringComparison.Ordinal));
			Assert.DoesNotContain(lines, l => l.Contains("HID\\", StringComparison.OrdinalIgnoreCase) ||
				l.Contains("00001124", StringComparison.Ordinal) || l.Contains(":\\", StringComparison.Ordinal));
		}
	}
}
