// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// "Pause Haptics In Silence": off by default, an app setting (saved, not per profile), pushed to the service; Low Power
// turns it on while active without changing the setting; the service applies it to the host live.
public class PauseHapticsAppTests
{
	private readonly TestControllers controllers = new();

	[Fact]
	public void Setting_IsOffByDefault_Saved_Pushed_AndLowPowerTurnsItOn()
	{
		FakeControllerService fake = new();
		SettingsState settings = new(fake);
		Assert.False(settings.PauseHapticsInSilence);
		Assert.False(new AppSettings().PauseHapticsInSilence);
		Assert.Null(fake.Settings.PauseHapticsReason);

		settings.PauseHapticsInSilence = true;
		Assert.Equal("setting", fake.Settings.PauseHapticsReason);
		Assert.True(settings.ToAppSettings().PauseHapticsInSilence);

		settings.PauseHapticsInSilence = false;
		settings.LowPowerOn = true;
		Assert.Equal("low power", fake.Settings.PauseHapticsReason);
		Assert.False(settings.ToAppSettings().PauseHapticsInSilence);

		settings.LowPowerOn = false;
		Assert.Null(fake.Settings.PauseHapticsReason);
	}

	[Fact]
	public void Service_AppliesItToTheHost_Live()
	{
		BridgeHost host = new(controllers, new TestIdentity(true), new TestAttacher());
		using RealControllerService service = new(host, new TestHiding(), controllers, a => a(), _ => null, timers: false) { DsxPort = 0 };
		Assert.False(host.PauseHapticsInSilence);

		service.ApplySettings(new ControllerSettings { PauseHapticsInSilence = true });
		Assert.True(host.PauseHapticsInSilence);

		service.ApplySettings(LowPower.Apply(new ControllerSettings(), true));
		Assert.True(host.PauseHapticsInSilence);

		service.ApplySettings(new ControllerSettings());
		Assert.False(host.PauseHapticsInSilence);
	}

	[Fact]
	public void Texts_InBothLanguages()
	{
		Assert.Equal("Pause Haptics In Silence", Text.PauseHapticsInSilence(Language.English));
		Assert.Equal("Sessizlikte Haptiği Durdur", Text.PauseHapticsInSilence(Language.Turkish));
		Assert.StartsWith("Oyun sessizken kumandaya haptik gönderilmez", Text.PauseHapticsInSilenceHint(Language.Turkish), StringComparison.Ordinal);
	}
}
