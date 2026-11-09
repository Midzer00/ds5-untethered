// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.Core.Tests;

[Collection("Localization")]
public class SetupTests
{
	private const string BtPath = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#1";
	private const string UsbPath = @"\\?\hid#vid_054c&pid_0ce6#1";

	private static HidDevice Pad(string path, int pid = HidDevice.DualSenseProductId) => new(path, HidDevice.SonyVendorId, pid, 78, 547, 64);

	private sealed class FakeRegistry : IInstallRegistry
	{
		public Dictionary<string, (string? Version, string? Location)> Programs { get; } = new();
		public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
		public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

		public (string? Version, string? Location)? FindProgram(string prefix) =>
			Programs.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(p => ((string?, string?)?)p.Value).FirstOrDefault();

		public bool ServiceExists(string serviceName) => Services.Contains(serviceName);
		public bool FileExists(string path) => Files.Contains(path);

		public FakeRegistry WithUsbIp()
		{
			Programs["USBip 0.9.8.1"] = ("0.9.8.1", @"C:\Program Files\USBip\");
			Files.Add(@"C:\Program Files\USBip\usbip.exe");
			Services.Add("usbip2_ude");
			return this;
		}

		public FakeRegistry WithHidHide()
		{
			Programs["HidHide"] = ("1.5.230", @"C:\Program Files\Nefarius Software Solutions\HidHide\");
			Services.Add("HidHide");
			return this;
		}
	}

	private sealed class Fakes(RadioState radio, IReadOnlyList<HidDevice> pads, bool? restart) : IBluetoothRadio, IControllerFinder, IRestartProbe
	{
		public RadioState Query() => radio;
		public IReadOnlyList<HidDevice> Find() => pads;
		public bool? IsRestartPending() => restart;
	}

	private sealed class FakeLauncher : IUrlLauncher
	{
		public List<string> Opened { get; } = [];
		public void Open(string target) => Opened.Add(target);
	}

	private static SetupWizard Wizard(FakeRegistry reg, RadioState radio, HidDevice[] pads, bool? restart = false, FakeLauncher? launcher = null)
	{
		var f = new Fakes(radio, pads, restart);
		return new SetupWizard(new SystemCheck(reg, f, f, f), launcher ?? new FakeLauncher());
	}

	private static StepStatus StatusOf(SetupWizard w, SetupStepId id) => w.Steps.Single(s => s.Id == id).Status;

	private static SetupWizard AllGood(FakeLauncher? launcher = null)
	{
		var w = Wizard(new FakeRegistry().WithUsbIp().WithHidHide(), RadioState.On, [Pad(BtPath)], launcher: launcher);
		w.LanguageChosen = true;
		w.AdapterVerdict = AdapterVerdict.Suitable;
		return w;
	}

	[Fact]
	public void StepsAreOrdered()
	{
		Assert.Equal(
			[SetupStepId.Language, SetupStepId.Drivers, SetupStepId.PairController, SetupStepId.AdapterTest, SetupStepId.Done],
			AllGood().Steps.Select(s => s.Id));
	}

	[Fact]
	public void NothingInstalled()
	{
		var w = Wizard(new FakeRegistry(), RadioState.On, []);
		Assert.False(w.Status.UsbIp.Installed);
		Assert.False(w.Status.HidHide.Installed);
		Assert.All(w.Steps, s => Assert.Equal(StepStatus.Needed, s.Status));
		Assert.Equal(SetupStepId.Language, w.FirstIncomplete?.Id);
		Assert.False(w.CanFinish);
		var links = w.Steps.Single(s => s.Id == SetupStepId.Drivers).Actions.Where(a => a.Kind == SetupActionKind.OpenLink).Select(a => a.Target);
		Assert.Equal([SetupLinks.UsbIpReleases, SetupLinks.HidHideReleases], links);
	}

	[Fact]
	public void OnlyUsbIpInstalled()
	{
		var w = Wizard(new FakeRegistry().WithUsbIp(), RadioState.On, [Pad(BtPath)]);
		w.LanguageChosen = true;
		Assert.Equal("0.9.8.1", w.Status.UsbIp.Version);
		Assert.Equal(@"C:\Program Files\USBip\usbip.exe", w.Status.UsbIp.Location);
		var drivers = w.Steps.Single(s => s.Id == SetupStepId.Drivers);
		Assert.Equal(StepStatus.Needed, drivers.Status);
		Assert.Equal([SetupLinks.HidHideReleases], drivers.Actions.Where(a => a.Kind == SetupActionKind.OpenLink).Select(a => a.Target));
		Assert.Equal(SetupStepId.Drivers, w.FirstIncomplete?.Id);
	}

	[Fact]
	public void OnlyHidHideInstalled()
	{
		var w = Wizard(new FakeRegistry().WithHidHide(), RadioState.On, [Pad(BtPath)]);
		Assert.True(w.Status.HidHide.Ready);
		Assert.Equal("1.5.230", w.Status.HidHide.Version);
		Assert.Equal(StepStatus.Needed, StatusOf(w, SetupStepId.Drivers));
	}

	[Fact]
	public void ProgramWithoutDriverIsNotReady()
	{
		var reg = new FakeRegistry().WithUsbIp().WithHidHide();
		reg.Services.Remove("usbip2_ude");
		var w = Wizard(reg, RadioState.On, [Pad(BtPath)]);
		Assert.True(w.Status.UsbIp.Installed);
		Assert.False(w.Status.UsbIp.Ready);
		Assert.Equal(StepStatus.Needed, StatusOf(w, SetupStepId.Drivers));
	}

	[Fact]
	public void RestartPendingIsWarningNotBlocking()
	{
		var w = Wizard(new FakeRegistry().WithUsbIp().WithHidHide(), RadioState.On, [Pad(BtPath)], restart: true);
		w.LanguageChosen = true;
		w.AdapterVerdict = AdapterVerdict.Suitable;
		Assert.Equal(StepStatus.Warning, StatusOf(w, SetupStepId.Drivers));
		Assert.True(w.CanFinish);
	}

	[Theory]
	[InlineData(RadioState.Off)]
	[InlineData(RadioState.Missing)]
	public void BluetoothNotUsable(RadioState radio)
	{
		var w = Wizard(new FakeRegistry().WithUsbIp().WithHidHide(), radio, []);
		w.LanguageChosen = true;
		var pair = w.Steps.Single(s => s.Id == SetupStepId.PairController);
		Assert.Equal(StepStatus.Needed, pair.Status);
		Assert.Contains(pair.Notes, n => n(null) == (radio == RadioState.Off ? Text.SetupRadioOff() : Text.SetupRadioMissing()));
		Assert.Equal(SetupStepId.PairController, w.FirstIncomplete?.Id);
		Assert.False(w.CanFinish);
	}

	[Fact]
	public void ControllerMissingOrOnUsb()
	{
		var missing = Wizard(new FakeRegistry().WithUsbIp().WithHidHide(), RadioState.On, []);
		Assert.Equal(StepStatus.Needed, StatusOf(missing, SetupStepId.PairController));
		var usb = Wizard(new FakeRegistry().WithUsbIp().WithHidHide(), RadioState.On, [Pad(UsbPath)]);
		var pair = usb.Steps.Single(s => s.Id == SetupStepId.PairController);
		Assert.Equal(StepStatus.Needed, pair.Status);
		Assert.Contains(pair.Notes, n => n(null) == Text.SetupControllerOnUsb());
		Assert.Empty(usb.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Actions);
	}

	[Fact]
	public void EdgeConnected()
	{
		var w = Wizard(new FakeRegistry().WithUsbIp().WithHidHide(), RadioState.On, [Pad(BtPath, HidDevice.DualSenseEdgeProductId)]);
		Assert.True(w.Status.EdgeOnBluetooth);
		var pair = w.Steps.Single(s => s.Id == SetupStepId.PairController);
		Assert.Equal(StepStatus.Done, pair.Status);
		Assert.Contains(pair.Notes, n => n(null) == Text.SetupEdgeConnected());
	}

	[Fact]
	public void AdapterVerdictMapsToStatus()
	{
		var w = AllGood();
		w.AdapterVerdict = null;
		Assert.Equal(StepStatus.Needed, StatusOf(w, SetupStepId.AdapterTest));
		Assert.Equal(SetupStepId.AdapterTest, w.FirstIncomplete?.Id);
		w.AdapterVerdict = AdapterVerdict.Borderline;
		Assert.Equal(StepStatus.Warning, StatusOf(w, SetupStepId.AdapterTest));
		w.AdapterVerdict = AdapterVerdict.Unsuitable;
		Assert.Equal(StepStatus.Warning, StatusOf(w, SetupStepId.AdapterTest));
		Assert.True(w.CanFinish);
	}

	[Fact]
	public void AllGoodCanFinish()
	{
		var w = AllGood();
		Assert.All(w.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
		Assert.Null(w.FirstIncomplete);
		Assert.True(w.CanFinish);
	}

	[Fact]
	public void LinksAreOfficial()
	{
		Assert.Equal("https://github.com/vadimgrn/usbip-win2/releases", SetupLinks.UsbIpReleases);
		Assert.Equal("https://github.com/nefarius/HidHide/releases", SetupLinks.HidHideReleases);
		Assert.Equal("ms-settings:bluetooth", SetupLinks.BluetoothSettings);
	}

	[Fact]
	public void ActionsCallLauncherWithTargets()
	{
		var launcher = new FakeLauncher();
		var w = Wizard(new FakeRegistry(), RadioState.Off, [], launcher: launcher);
		foreach (SetupAction a in w.Steps.SelectMany(s => s.Actions).Where(a => a.Kind == SetupActionKind.OpenLink))
		{
			Assert.False(w.Execute(a));
		}
		Assert.Equal([SetupLinks.UsbIpReleases, SetupLinks.HidHideReleases, SetupLinks.BluetoothSettings], launcher.Opened);
	}

	[Fact]
	public void RecheckPicksUpChanges()
	{
		var reg = new FakeRegistry();
		var w = Wizard(reg, RadioState.On, [Pad(BtPath)]);
		Assert.Equal(StepStatus.Needed, StatusOf(w, SetupStepId.Drivers));
		reg.WithUsbIp().WithHidHide();
		Assert.False(w.Execute(w.Steps.Single(s => s.Id == SetupStepId.Drivers).Actions.Single(a => a.Kind == SetupActionKind.Recheck)));
		Assert.Equal(StepStatus.Done, StatusOf(w, SetupStepId.Drivers));
	}

	[Fact]
	public void AdapterTestActionAsksCaller()
	{
		var w = AllGood();
		w.AdapterVerdict = null;
		SetupAction run = w.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Actions.Single();
		Assert.True(w.Execute(run));
	}

	[Fact]
	public void StepTextsDifferPerLanguage()
	{
		foreach (SetupStep s in AllGood().Steps)
		{
			Assert.NotEqual(s.Title(Language.Turkish), s.Title(Language.English));
			Assert.NotEqual(s.Instructions(Language.Turkish), s.Instructions(Language.English));
		}
	}
}

