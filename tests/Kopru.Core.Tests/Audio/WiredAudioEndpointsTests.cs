// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// Finding the controller's own USB audio endpoint: by the plain USB device above its HID interface, never the
// virtual pad's endpoint, reading the audio system only.
public class WiredAudioEndpointsTests
{
	private const string UsbDevice = @"USB\VID_054C&PID_0CE6\7&cable";

	private static AudioEndpointInfo Endpoint(string id, AudioFlow flow, bool active, string? usbDevice, bool virtualPad = false) =>
		new(id, flow, active, "name", virtualPad
			? new PadAudioMatch(true, 1, PadMatchMethod.DeviceTree, "virtual pad")
			: PadAudioMatch.None, usbDevice);

	private static DeviceNodeInfo Node(string id) => new(id, [], null, null);

	[Fact]
	public void Pick_TakesTheEndpointOnTheSameUsbDevice()
	{
		AudioEndpointInfo ours = Endpoint("a", AudioFlow.Render, true, UsbDevice);
		AudioEndpointInfo[] endpoints =
		[
			Endpoint("b", AudioFlow.Render, true, @"USB\VID_0000&PID_0000\other"),
			ours,
			Endpoint("c", AudioFlow.Capture, true, UsbDevice),
			Endpoint("d", AudioFlow.Render, false, UsbDevice)
		];
		Assert.Same(ours, WiredAudioEndpoints.Pick(endpoints, AudioFlow.Render, UsbDevice));
	}

	[Fact]
	public void Pick_RefusesTheVirtualPadsEndpoint_EvenOnTheSameUsbDevice()
	{
		AudioEndpointInfo[] endpoints = [Endpoint("a", AudioFlow.Render, true, UsbDevice, virtualPad: true)];
		Assert.Null(WiredAudioEndpoints.Pick(endpoints, AudioFlow.Render, UsbDevice));
	}

	[Fact]
	public void Pick_TwoMatches_OrNoUsbDevice_IsNull()
	{
		AudioEndpointInfo[] two =
		[
			Endpoint("a", AudioFlow.Render, true, UsbDevice),
			Endpoint("b", AudioFlow.Render, true, UsbDevice)
		];
		Assert.Null(WiredAudioEndpoints.Pick(two, AudioFlow.Render, UsbDevice));
		Assert.Null(WiredAudioEndpoints.Pick(two, AudioFlow.Render, null));
		Assert.Null(WiredAudioEndpoints.Pick(two, AudioFlow.Render, ""));
		Assert.Null(WiredAudioEndpoints.Pick(two, AudioFlow.Render, @"USB\VID_9999&PID_9999\elsewhere"));
	}

	[Fact]
	public void UsbDeviceOf_TakesTheFirstPlainUsbDevice_InAChain()
	{
		Assert.Equal(UsbDevice, WiredAudioEndpoints.UsbDeviceOf(
		[
			Node(@"USB\VID_054C&PID_0CE6&MI_00\8&audio"),
			Node(UsbDevice),
			Node(@"USB\ROOT_HUB30\hub")
		]));
		Assert.Null(WiredAudioEndpoints.UsbDeviceOf([Node(@"HID\VID_054C&PID_0CE6&MI_03\9"), Node(@"ACPI\PNP0C08\0")]));
		Assert.Null(WiredAudioEndpoints.UsbDeviceOf([]));
	}

	[Fact]
	public void UsbDeviceOfHid_WalksFromTheHidInterface_UpToTheUsbDevice()
	{
		Dictionary<string, string> parents = new(StringComparer.OrdinalIgnoreCase)
		{
			[@"HID\VID_054C&PID_0CE6&MI_03\inst"] = @"USB\VID_054C&PID_0CE6&MI_03\7",
			[@"USB\VID_054C&PID_0CE6&MI_03\7"] = UsbDevice
		};
		Assert.Equal(UsbDevice, WiredAudioEndpoints.UsbDeviceOfHid("path",
			_ => @"HID\VID_054C&PID_0CE6&MI_03\inst", id => parents.GetValueOrDefault(id)));
	}

	[Fact]
	public void UsbDeviceOfHid_MissingInstance_MissingParent_OrTooDeep_IsNull()
	{
		Assert.Null(WiredAudioEndpoints.UsbDeviceOfHid("path", _ => null, _ => "x"));
		Assert.Null(WiredAudioEndpoints.UsbDeviceOfHid("path",
			_ => @"HID\VID_054C&PID_0CE6&MI_03\inst", _ => null));
		// Five non-USB parents in a row (only four are walked): null.
		Assert.Null(WiredAudioEndpoints.UsbDeviceOfHid("path",
			_ => @"HID\VID_054C&PID_0CE6&MI_03\inst", _ => @"HID\SOMETHING\deeper"));
	}

	[Fact]
	public void Find_OnlyReads_NeverSetsADefaultOrMute()
	{
		FakeAudioSystem system = new(
		[
			Endpoint("a", AudioFlow.Render, true, UsbDevice),
			Endpoint("b", AudioFlow.Render, true, @"USB\VID_0000&PID_0000\other")
		]);
		Assert.Equal("a", WiredAudioEndpoints.Find(system, AudioFlow.Render, UsbDevice)?.Id);
		Assert.Equal(0, system.SetDefaults);
		Assert.Equal(0, system.SetMutes);
	}

	private sealed class FakeAudioSystem(IReadOnlyList<AudioEndpointInfo> endpoints) : IAudioDefaultsSystem
	{
		public int SetDefaults, SetMutes;

		public void Watch(IAudioDefaultsListener listener) => throw new NotSupportedException();
		public string? GetDefault(AudioFlow flow, AudioRole role) => null;
		public AudioEndpointInfo? Describe(string id) => null;
		public IReadOnlyList<AudioEndpointInfo> Endpoints(AudioFlow flow) => endpoints;
		public void SetDefault(string id, AudioRole role) => SetDefaults++;
		public void SetMute(string id, bool muted) => SetMutes++;
	}
}
