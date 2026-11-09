// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hiding;
using Xunit;

namespace Kopru.Core.Tests;

public sealed class EarlyRegistrationTests : IDisposable
{
	private const string BtPad = @"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\b&24ba9c0d&2&0000";
	private const string OurExe = @"C:\Program Files\Kopru App\Kopru.exe";
	private const string OtherExe = @"C:\Tools\Other.exe";

	private readonly string _dir = Path.Combine(Path.GetTempPath(), "kopru-early-" + Guid.NewGuid().ToString("N"));
	private readonly FakeHidHide _fake = new();
	private readonly HidHideControl _control;
	private readonly HidingStateStore _store;

	public EarlyRegistrationTests()
	{
		_control = new HidHideControl(_fake, @"C:\HidHideCLI.exe");
		_store = new HidingStateStore(Path.Combine(_dir, "hiding-state.json"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, true);
		}
	}

	private int Count(string prefix) => _fake.Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

	[Fact]
	public async Task Registers_at_start_and_records_it()
	{
		Assert.True(await EarlyAppRegistration.EnsureAppAllowedAsync(_control, _store, OurExe));
		Assert.Equal(new[] { OurExe }, _fake.Apps);
		Assert.Equal(new[] { OurExe }, _store.Load()!.EarlyApps);
		Assert.True(await EarlyAppRegistration.EnsureAppAllowedAsync(_control, _store, OurExe));
		Assert.Equal(1, Count("--app-reg"));
	}

	[Fact]
	public async Task Start_and_stop_reuse_and_keep_the_registration()
	{
		await EarlyAppRegistration.EnsureAppAllowedAsync(_control, _store, OurExe);
		HidingSession session = await HidingSession.StartAsync(_control, _store, [BtPad], OurExe);
		Assert.Empty(session.AddedApps);
		Assert.Equal(new[] { OurExe }, _store.Load()!.EarlyApps);
		await session.DisposeAsync();
		Assert.Equal(new[] { OurExe }, _fake.Apps);
		Assert.Empty(_fake.Devices);
		Assert.Equal(new[] { OurExe }, _store.Load()!.EarlyApps);

		await using (await HidingSession.StartAsync(_control, _store, [BtPad], OurExe))
		{
		}
		Assert.Equal(1, Count("--app-reg"));
		Assert.Equal(0, Count("--app-unreg"));
	}

	[Fact]
	public async Task Exit_unregisters()
	{
		_fake.Apps.Add(OtherExe);
		await EarlyAppRegistration.EnsureAppAllowedAsync(_control, _store, OurExe);
		await EarlyAppRegistration.ReleaseAsync(_control, _store);
		Assert.Equal(new[] { OtherExe }, _fake.Apps);
		Assert.False(File.Exists(_store.Path));
	}

	[Fact]
	public async Task Crash_recovery_unregisters()
	{
		await EarlyAppRegistration.EnsureAppAllowedAsync(_control, _store, OurExe);
		await HidingSession.StartAsync(_control, _store, [BtPad], OurExe); // never disposed: crash
		Assert.True(_fake.CloakOn);

		await HidingSession.RecoverAsync(_control, _store);
		Assert.False(_fake.CloakOn);
		Assert.Empty(_fake.Devices);
		Assert.Empty(_fake.Apps);
		Assert.False(File.Exists(_store.Path));
	}

	[Fact]
	public async Task Already_present_app_is_untouched()
	{
		_fake.Apps.Add(OurExe);
		Assert.True(await EarlyAppRegistration.EnsureAppAllowedAsync(_control, _store, OurExe));
		Assert.Null(_store.Load());
		await EarlyAppRegistration.ReleaseAsync(_control, _store);
		await HidingSession.RecoverAsync(_control, _store);
		Assert.Equal(new[] { OurExe }, _fake.Apps);
		Assert.Equal(0, Count("--app-reg"));
		Assert.Equal(0, Count("--app-unreg"));
	}

	[Fact]
	public async Task Not_installed_returns_false()
	{
		var control = new HidHideControl(_fake, null);
		Assert.False(await EarlyAppRegistration.EnsureAppAllowedAsync(control, _store, OurExe));
		Assert.Empty(_fake.Calls);
	}
}
