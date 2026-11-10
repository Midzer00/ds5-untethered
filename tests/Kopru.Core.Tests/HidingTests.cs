// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hid;
using Kopru.Core.Hiding;
using Xunit;

namespace Kopru.Core.Tests;

// Simulates HidHideCLI: keeps its own config and answers list commands in the CLI's output syntax.
internal sealed class FakeHidHide : ICommandRunner
{
	public bool CloakOn;
	public readonly List<string> Devices = [];
	public readonly List<string> Apps = [];
	public readonly List<string> Calls = [];
	public string? FailOn;

	public Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
	{
		Calls.Add(arguments);
		if (FailOn is not null && arguments.StartsWith(FailOn, StringComparison.Ordinal))
		{
			return Task.FromResult(new CommandResult(1, "", "boom"));
		}
		string output = "";
		int space = arguments.IndexOf(' ');
		string command = space < 0 ? arguments : arguments[..space];
		string value = space < 0 ? "" : arguments[(space + 1)..].Trim('"');
		switch (command)
		{
			case "--cloak-state": output = CloakOn ? "--cloak-on\r\n" : "--cloak-off\r\n"; break;
			case "--cloak-on": CloakOn = true; break;
			case "--cloak-off": CloakOn = false; break;
			case "--dev-list": output = string.Concat(Devices.Select(d => $"--dev-hide \"{d}\"\r\n")); break;
			case "--app-list": output = string.Concat(Apps.Select(a => $"--app-reg \"{a}\"\r\n")); break;
			case "--dev-hide": Devices.Add(value); break;
			case "--dev-unhide": Devices.Remove(value); break;
			case "--app-reg": Apps.Add(value); break;
			case "--app-unreg": Apps.Remove(value); break;
		}
		return Task.FromResult(new CommandResult(0, output, ""));
	}
}

// Reads text in the current language (LocalizationTests switch it): not in parallel with them.
[Collection("Localization")]
public sealed class HidingTests : IDisposable
{
	private const string BtPad = @"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\b&24ba9c0d&2&0000";
	private const string VirtualPad = @"HID\VID_054C&PID_0CE6&MI_03\4&DD106E1&0&0000";
	private const string OtherPad = @"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0df2\c&1&0&0000";
	private const string OurExe = @"C:\Program Files\Kopru App\Kopru.exe";
	private const string OtherExe = @"C:\Tools\Other.exe";

	private readonly string _dir = Path.Combine(Path.GetTempPath(), "kopru-hiding-" + Guid.NewGuid().ToString("N"));
	private readonly FakeHidHide _fake = new();
	private readonly HidHideControl _control;
	private readonly HidingStateStore _store;

	public HidingTests()
	{
		_control = new HidHideControl(_fake, @"C:\Program Files\HidHide\HidHideCLI.exe");
		_store = new HidingStateStore(Path.Combine(_dir, "hiding-state.json"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, true);
		}
	}

	// Simulated app processes sharing the ledger: Run starts one, Kill ends it without cleanup.
	private readonly HashSet<HidingOwner> _live = [];

	private HidingStateStore Run(int pid)
	{
		var owner = new HidingOwner(pid, pid * 1000L);
		_live.Add(owner);
		return new HidingStateStore(_store.Path, owner, _live.Contains);
	}

	private void Kill(HidingStateStore store) => _live.Remove(store.Owner);

	[Fact]
	public void Instance_ids_are_classified()
	{
		Assert.True(DeviceInstance.IsBluetoothHid(BtPad));
		Assert.False(DeviceInstance.IsBluetoothHid(VirtualPad));
		Assert.True(DeviceInstance.IsUsbDevice(VirtualPad));
		Assert.True(DeviceInstance.IsUsbDevice(@"USB\VID_054C&PID_0CE6\DPLS00"));
		Assert.False(DeviceInstance.IsUsbDevice(BtPad));
	}

	[Fact]
	public async Task Commands_quote_paths_with_spaces()
	{
		await _control.AllowAppAsync(OurExe);
		await _control.HideAsync(BtPad);
		await _control.UnhideAsync(BtPad);
		await _control.DisallowAppAsync(OurExe);
		Assert.Equal(new[]
		{
			"--app-reg \"C:\\Program Files\\Kopru App\\Kopru.exe\"",
			"--dev-hide \"" + BtPad + "\"",
			"--dev-unhide \"" + BtPad + "\"",
			"--app-unreg \"C:\\Program Files\\Kopru App\\Kopru.exe\""
		}, _fake.Calls);
		Assert.Throws<ArgumentException>(() => HidHideControl.Quote("a\"b"));
	}

	[Fact]
	public async Task State_is_parsed_from_cli_output()
	{
		_fake.CloakOn = true;
		_fake.Devices.Add(BtPad);
		_fake.Apps.Add(OtherExe);
		HidHideState state = await _control.GetStateAsync();
		Assert.True(state.CloakOn);
		Assert.Equal(new[] { BtPad }, state.HiddenDevices);
		Assert.Equal(new[] { OtherExe }, state.AllowedApps);
	}

	[Fact]
	public async Task Virtual_pad_is_never_hidden()
	{
		await Assert.ThrowsAsync<ArgumentException>(() => _control.HideAsync(VirtualPad));
		await using (HidingSession session = await HidingSession.StartAsync(_control, _store, [VirtualPad, BtPad], OurExe))
		{
			Assert.Equal(new[] { BtPad }, _fake.Devices);
		}
		Assert.DoesNotContain(_fake.Calls, c => c.Contains(VirtualPad, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task Session_applies_then_restores_exactly_its_own_changes()
	{
		_fake.Devices.Add(OtherPad);
		_fake.Apps.Add(OtherExe);
		HidingSession session = await HidingSession.StartAsync(_control, _store, [BtPad], OurExe);
		Assert.True(_fake.CloakOn);
		Assert.Contains(BtPad, _fake.Devices);
		Assert.Contains(OurExe, _fake.Apps);
		Assert.True(_fake.Calls.IndexOf("--app-reg " + HidHideControl.Quote(OurExe)) < _fake.Calls.IndexOf("--dev-hide " + HidHideControl.Quote(BtPad)));
		Assert.True(File.Exists(_store.Path));

		session.Dispose();
		Assert.False(_fake.CloakOn);
		Assert.Equal(new[] { OtherPad }, _fake.Devices);
		Assert.Equal(new[] { OtherExe }, _fake.Apps);
		Assert.False(File.Exists(_store.Path));
	}

	[Fact]
	public async Task Existing_entries_are_not_removed_on_restore()
	{
		_fake.CloakOn = true;
		_fake.Devices.Add(BtPad);
		_fake.Apps.Add(OurExe);
		HidingSession session = await HidingSession.StartAsync(_control, _store, [BtPad], OurExe);
		Assert.True(session.AddedDevices.Count == 0 && session.AddedApps.Count == 0 && !session.TurnedCloakOn);
		await session.DisposeAsync();
		Assert.True(_fake.CloakOn);
		Assert.Equal(new[] { BtPad }, _fake.Devices);
		Assert.Equal(new[] { OurExe }, _fake.Apps);
		Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("--dev-unhide") || c.StartsWith("--app-unreg") || c == "--cloak-off");
	}

	[Fact]
	public async Task Crashed_run_is_recovered_from_state_file()
	{
		_fake.Apps.Add(OtherExe);
		HidingStateStore crashed = Run(1);
		await HidingSession.StartAsync(_control, crashed, [BtPad], OurExe); // never disposed = crash
		Assert.True(File.Exists(_store.Path));
		Kill(crashed);

		var fresh = new HidHideControl(_fake, _control.CliPath);
		await HidingSession.RecoverAsync(fresh, Run(2));
		Assert.False(_fake.CloakOn);
		Assert.Empty(_fake.Devices);
		Assert.Equal(new[] { OtherExe }, _fake.Apps);
		Assert.False(File.Exists(_store.Path));
	}

	[Fact]
	public async Task Failure_while_applying_rolls_back()
	{
		_fake.FailOn = "--dev-hide";
		await Assert.ThrowsAsync<HidHideException>(() => HidingSession.StartAsync(_control, _store, [BtPad], OurExe));
		Assert.Empty(_fake.Apps);
		Assert.False(_fake.CloakOn);
		Assert.False(File.Exists(_store.Path));
	}

	[Fact]
	public async Task Not_installed_reports_localized_error_and_runs_nothing()
	{
		var control = new HidHideControl(_fake, null);
		Assert.False(control.IsInstalled);
		HidHideException e = await Assert.ThrowsAsync<HidHideException>(() => HidingSession.StartAsync(control, _store, [BtPad], OurExe));
		Assert.Equal(Kopru.Core.Localization.Text.HidHideNotInstalled(), e.Message);
		await Assert.ThrowsAsync<HidHideException>(() => control.GetStateAsync());
		Assert.Empty(_fake.Calls);
	}

	[Fact]
	public async Task AddDevices_hides_more_and_restores_them_too()
	{
		HidingSession session = await HidingSession.StartAsync(_control, _store, [], OurExe);
		Assert.Contains(OurExe, _fake.Apps);
		Assert.Empty(_fake.Devices);

		await session.AddDevicesAsync([BtPad, VirtualPad, BtPad]);
		Assert.Equal([BtPad], _fake.Devices);
		Assert.True(_store.Load().IsClaimed(HidingClaimKind.Device, BtPad));

		await session.DisposeAsync();
		Assert.Empty(_fake.Devices);
		Assert.Empty(_fake.Apps);
		Assert.False(_fake.CloakOn);
		Assert.Empty(_store.Load().Claims);
		Assert.False(File.Exists(_store.Path));
	}

	[Fact]
	public void Default_state_file_is_in_the_untethered_folder()
	{
		Assert.Contains(Path.Combine("Untethered", "hiding-ledger.json"), HidingStateStore.CreateDefault().Path);
		Assert.Contains(HidingStateStore.LegacyPaths(), p => p.EndsWith(Path.Combine("Untethered", "hiding-state.json"), StringComparison.Ordinal));
		Assert.Contains(HidingStateStore.LegacyPaths(), p => p.EndsWith(Path.Combine("Kopru", "hiding-state.json"), StringComparison.Ordinal));
	}

	[Fact]
	public void Owner_of_this_process_is_running_and_a_reused_pid_is_not()
	{
		Assert.True(HidingOwner.IsRunning(HidingOwner.Current));
		Assert.False(HidingOwner.IsRunning(HidingOwner.Current with { StartedUtcTicks = HidingOwner.Current.StartedUtcTicks - TimeSpan.TicksPerHour }));
		Assert.False(HidingOwner.IsRunning(HidingOwner.Unknown));
	}
}
