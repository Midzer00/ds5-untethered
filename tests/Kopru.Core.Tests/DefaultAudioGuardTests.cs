// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Kopru.Core.Audio;
using Kopru.Core.Bridge;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// Windows' audio endpoints and defaults, with Windows' own switching played by the test.
internal sealed class FakeAudioSystem : IAudioDefaultsSystem
{
	private readonly Dictionary<string, AudioEndpointInfo> endpoints = [];
	private readonly Dictionary<(AudioFlow, AudioRole), string?> defaults = [];
	private readonly List<IAudioDefaultsListener> listeners = [];

	public List<(string Id, AudioRole Role)> Sets { get; } = [];

	public bool ThrowOnSet { get; set; }

	public bool ThrowOnWatch { get; set; }

	public int Watches { get; private set; }

	public void Add(string id, AudioFlow flow, bool pad = false, int? port = null, bool notify = true)
	{
		endpoints[id] = new AudioEndpointInfo(id, flow, true, id,
			pad ? new PadAudioMatch(true, port, PadMatchMethod.DeviceTree, "test") : PadAudioMatch.None);
		if (notify)
		{
			listeners.ForEach(l => l.EndpointChanged(id));
		}
	}

	public void Remove(string id)
	{
		endpoints[id] = endpoints[id] with { IsActive = false };
		listeners.ForEach(l => l.EndpointChanged(id));
	}

	// Windows (or the user) changes the default.
	public void Switch(AudioFlow flow, string id, params AudioRole[] roles)
	{
		foreach (AudioRole role in roles.Length > 0 ? roles : [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications])
		{
			defaults[(flow, role)] = id;
			listeners.ForEach(l => l.DefaultChanged(flow, role, id));
		}
	}

	public void Watch(IAudioDefaultsListener l)
	{
		Watches++;
		if (ThrowOnWatch)
		{
			throw new COMException("no notifications");
		}
		listeners.Add(l);
	}

	public string? GetDefault(AudioFlow flow, AudioRole role) => defaults.GetValueOrDefault((flow, role));

	public AudioEndpointInfo? Describe(string id) => endpoints.GetValueOrDefault(id);

	public IReadOnlyList<AudioEndpointInfo> Endpoints(AudioFlow flow) => [.. endpoints.Values.Where(e => e.Flow == flow && e.IsActive)];

	private readonly Dictionary<string, bool> mutes = [];

	public bool ThrowOnMute { get; set; }

	public bool? GetMute(string id) => endpoints.ContainsKey(id) ? mutes.GetValueOrDefault(id) : null;

	public void SetMute(string id, bool muted)
	{
		if (ThrowOnMute)
		{
			throw new COMException("refused");
		}
		mutes[id] = muted;
	}

	// Someone else (Windows' sound settings) mutes the microphone.
	public void MuteElsewhere(string id, bool muted) => mutes[id] = muted;

	// Windows takes the call but keeps its default (read back shows the old one).
	public bool IgnoreSet { get; set; }

	public void SetDefault(string id, AudioRole role)
	{
		if (IgnoreSet)
		{
			Sets.Add((id, role));
			return;
		}
		Sets.Add((id, role));
		if (ThrowOnSet)
		{
			throw new InvalidCastException("no IPolicyConfig");
		}
		AudioFlow flow = endpoints[id].Flow;
		defaults[(flow, role)] = id;
		listeners.ForEach(l => l.DefaultChanged(flow, role, id));
	}
}

public class DefaultAudioGuardTests
{
	private static readonly AudioRole[] AllRoles = [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

	private readonly FakeAudioSystem audio = new();
	private readonly ManualTime time = new();
	private readonly ConcurrentQueue<string> log = new();
	private readonly List<AudioGuardPreferences> learned = [];
	private readonly DefaultAudioGuard guard;

	public DefaultAudioGuardTests()
	{
		// One at a time, in order, like the real worker (a callback during a call runs after it).
		Queue<Action> queue = new();
		bool running = false;
		void Post(Action a)
		{
			queue.Enqueue(a);
			if (running)
			{
				return;
			}
			running = true;
			while (queue.TryDequeue(out Action? next))
			{
				next();
			}
			running = false;
		}
		guard = new DefaultAudioGuard(audio, time, Post, log.Enqueue);
		guard.PreferencesChanged += learned.Add;
		audio.Add("pc-speaker", AudioFlow.Render, notify: false);
		audio.Add("pc-mic", AudioFlow.Capture, notify: false);
		audio.Switch(AudioFlow.Render, "pc-speaker");
		audio.Switch(AudioFlow.Capture, "pc-mic");
	}

	// The pad of a slot arrives: attach, then its endpoints show up a moment later.
	private void Attach(int slot = 1, int port = 1, bool endpoints = true)
	{
		guard.PadAttaching(slot, true);
		guard.PadAttached(slot, port);
		if (endpoints)
		{
			time.Advance(TimeSpan.FromSeconds(2));
			audio.Add(Speaker(port), AudioFlow.Render, pad: true, port: port);
			audio.Add(Mic(port), AudioFlow.Capture, pad: true, port: port);
		}
	}

	private static string Speaker(int port) => $"pad{port}-speaker";

	private static string Mic(int port) => $"pad{port}-mic";

	private string? Default(AudioFlow flow, AudioRole role) => audio.GetDefault(flow, role);

	[Theory]
	[InlineData(AudioFlow.Render, AudioRole.Console)]
	[InlineData(AudioFlow.Render, AudioRole.Multimedia)]
	[InlineData(AudioFlow.Render, AudioRole.Communications)]
	[InlineData(AudioFlow.Capture, AudioRole.Console)]
	[InlineData(AudioFlow.Capture, AudioRole.Multimedia)]
	[InlineData(AudioFlow.Capture, AudioRole.Communications)]
	public void AutoSwitchInsideTheWindow_IsPutBack(AudioFlow flow, AudioRole role)
	{
		Attach();
		time.Advance(TimeSpan.FromSeconds(1));
		string pad = flow == AudioFlow.Render ? Speaker(1) : Mic(1);
		audio.Switch(flow, pad, role);

		string original = flow == AudioFlow.Render ? "pc-speaker" : "pc-mic";
		Assert.Equal(original, Default(flow, role));
		Assert.Equal([(original, role)], audio.Sets);
		Assert.Contains(log, l => l.Contains("put the previous device back") && l.Contains(role.ToString().ToLowerInvariant()));
		Assert.Empty(learned);
	}

	[Fact]
	public void EveryRoleOfBothFlows_IsPutBack_AndTheRememberedDevicesAreLogged()
	{
		Attach();
		audio.Switch(AudioFlow.Render, Speaker(1));
		audio.Switch(AudioFlow.Capture, Mic(1));

		foreach (AudioRole role in AllRoles)
		{
			Assert.Equal("pc-speaker", Default(AudioFlow.Render, role));
			Assert.Equal("pc-mic", Default(AudioFlow.Capture, role));
		}
		Assert.Contains(log, l => l.Contains("remembered the previous devices") && l.Contains("speaker 3/3") && l.Contains("microphone 3/3"));
		Assert.DoesNotContain(log, l => l.Contains("pc-speaker") || l.Contains(Speaker(1)));
	}

	[Fact]
	public void SwitchBeforeTheAttachReturns_IsPutBack()
	{
		guard.PadAttaching(1, true);
		audio.Add(Speaker(1), AudioFlow.Render, pad: true, port: 1);
		audio.Switch(AudioFlow.Render, Speaker(1));
		guard.PadAttached(1, 1);

		Assert.Equal("pc-speaker", Default(AudioFlow.Render, AudioRole.Multimedia));
	}

	[Fact]
	public void SlowDriverInstall_KeepsTheWindowOpenUntilTheEndpointsArrive()
	{
		guard.PadAttaching(1, true);
		guard.PadAttached(1, 1);
		time.Advance(TimeSpan.FromSeconds(60));
		audio.Add(Speaker(1), AudioFlow.Render, pad: true, port: 1);
		time.Advance(TimeSpan.FromSeconds(5));
		audio.Switch(AudioFlow.Render, Speaker(1));

		Assert.Equal("pc-speaker", Default(AudioFlow.Render, AudioRole.Console));
	}

	[Fact]
	public void ChangeOutsideTheWindow_IsKept_AndRemembered()
	{
		Attach();
		time.Advance(DefaultAudioGuard.Settle + TimeSpan.FromSeconds(1));
		audio.Switch(AudioFlow.Render, Speaker(1));

		Assert.Equal(Speaker(1), Default(AudioFlow.Render, AudioRole.Console));
		Assert.Empty(audio.Sets);
		Assert.Equal([new AudioGuardPreferences(ControllerSpeakerChosen: true)], learned);
		Assert.Contains(log, l => l.Contains("you made the controller speaker"));
	}

	[Fact]
	public void RememberedChoice_SurvivesReattach_AndIsForgottenWhenTheUserPicksAnother()
	{
		Attach();
		time.Advance(TimeSpan.FromSeconds(30));
		audio.Switch(AudioFlow.Render, Speaker(1));
		Assert.True(guard.Preferences.ControllerSpeakerChosen);

		// Sleep / reconnect: detached (Windows falls back), attached again, Windows selects the controller again.
		guard.PadDetaching(1);
		audio.Remove(Speaker(1));
		audio.Switch(AudioFlow.Render, "pc-speaker");
		time.Advance(TimeSpan.FromSeconds(30));
		Attach();
		audio.Switch(AudioFlow.Render, Speaker(1));
		Assert.Equal(Speaker(1), Default(AudioFlow.Render, AudioRole.Multimedia));
		Assert.Empty(audio.Sets);
		Assert.True(guard.Preferences.ControllerSpeakerChosen);
		Assert.Contains(log, l => l.Contains("you chose the controller speaker before"));

		// Later the user picks the PC speaker while the controller is still there.
		time.Advance(TimeSpan.FromSeconds(30));
		audio.Switch(AudioFlow.Render, "pc-speaker");
		Assert.False(guard.Preferences.ControllerSpeakerChosen);
		Assert.Equal(new AudioGuardPreferences(), learned[^1]);
		Assert.Contains(log, l => l.Contains("no longer kept"));

		// And the next attach is protected again.
		guard.PadDetaching(1);
		audio.Remove(Speaker(1));
		time.Advance(TimeSpan.FromSeconds(30));
		Attach();
		audio.Switch(AudioFlow.Render, Speaker(1));
		Assert.Equal("pc-speaker", Default(AudioFlow.Render, AudioRole.Console));
	}

	[Fact]
	public void SavedChoice_IsRespectedFromTheStart_PerFlow()
	{
		guard.LoadPreferences(new AudioGuardPreferences(ControllerSpeakerChosen: true));
		Attach();
		audio.Switch(AudioFlow.Render, Speaker(1));
		audio.Switch(AudioFlow.Capture, Mic(1));

		Assert.Equal(Speaker(1), Default(AudioFlow.Render, AudioRole.Console));
		Assert.Equal("pc-mic", Default(AudioFlow.Capture, AudioRole.Console));
	}

	[Fact]
	public void WindowsFallbackAfterDetach_DoesNotForgetTheChoice()
	{
		guard.LoadPreferences(new AudioGuardPreferences(ControllerSpeakerChosen: true));
		Attach();
		audio.Switch(AudioFlow.Render, Speaker(1));
		time.Advance(TimeSpan.FromSeconds(30));

		guard.PadDetaching(1);
		audio.Remove(Speaker(1));
		audio.Switch(AudioFlow.Render, "pc-speaker");

		Assert.True(guard.Preferences.ControllerSpeakerChosen);
		Assert.Empty(learned);
	}

	[Fact]
	public void RememberedDeviceGone_NothingIsDone()
	{
		Attach();
		audio.Remove("pc-speaker");
		audio.Switch(AudioFlow.Render, Speaker(1));

		Assert.Equal(Speaker(1), Default(AudioFlow.Render, AudioRole.Console));
		Assert.Empty(audio.Sets);
		Assert.Contains(log, l => l.Contains("the previous device is gone"));
	}

	[Fact]
	public void SettingOff_NothingIsDone()
	{
		guard.Enabled = false;
		Attach();
		audio.Switch(AudioFlow.Render, Speaker(1));
		time.Advance(TimeSpan.FromSeconds(30));
		audio.Switch(AudioFlow.Capture, Mic(1));

		Assert.Equal(Speaker(1), Default(AudioFlow.Render, AudioRole.Console));
		Assert.Empty(audio.Sets);
		Assert.Empty(learned);
		Assert.Equal(0, audio.Watches);
	}

	[Fact]
	public void SetDefaultThrowing_DoesNotCrash_AndIsLoggedOnce()
	{
		audio.ThrowOnSet = true;
		Attach();
		audio.Switch(AudioFlow.Render, Speaker(1));
		audio.Switch(AudioFlow.Capture, Mic(1));

		Assert.Single(audio.Sets);
		Assert.Single(log, l => l.Contains("could not put the previous device back") && l.Contains(nameof(InvalidCastException)));
		Assert.Equal(Speaker(1), Default(AudioFlow.Render, AudioRole.Console));
	}

	[Fact]
	public void NotificationsUnavailable_DoesNothing_AndIsLoggedOnce()
	{
		audio.ThrowOnWatch = true;
		Attach();
		Attach(slot: 2, port: 2);
		audio.Switch(AudioFlow.Render, Speaker(1));

		Assert.Equal(1, audio.Watches);
		Assert.Single(log, l => l.Contains("cannot watch"));
		Assert.Empty(audio.Sets);
	}

	[Fact]
	public void TwoSlots_EachAttachIsProtected()
	{
		Attach(slot: 1, port: 1);
		audio.Switch(AudioFlow.Render, Speaker(1));
		time.Advance(TimeSpan.FromSeconds(40));
		Attach(slot: 2, port: 2);
		audio.Switch(AudioFlow.Render, Speaker(2));
		audio.Switch(AudioFlow.Capture, Mic(2), AudioRole.Communications);

		Assert.Equal("pc-speaker", Default(AudioFlow.Render, AudioRole.Console));
		Assert.Equal("pc-mic", Default(AudioFlow.Capture, AudioRole.Communications));
		Assert.Empty(learned);
	}

	[Fact]
	public void OtherSlotsEndpoint_OutsideItsWindow_IsTheUsersChoice()
	{
		Attach(slot: 1, port: 1);
		time.Advance(TimeSpan.FromSeconds(40));
		Attach(slot: 2, port: 2, endpoints: false);
		time.Advance(TimeSpan.FromSeconds(10));
		// Slot 2's window is still open (no endpoints yet), but the user picked slot 1's speaker.
		audio.Switch(AudioFlow.Render, Speaker(1));

		Assert.Equal(Speaker(1), Default(AudioFlow.Render, AudioRole.Console));
		Assert.True(guard.Preferences.ControllerSpeakerChosen);
	}

	[Fact]
	public void DetachDuringTheWindow_StopsWatching()
	{
		guard.PadAttaching(1, true);
		guard.PadAttached(1, 1);
		guard.PadDetaching(1);
		audio.Add(Speaker(1), AudioFlow.Render, pad: true, port: 1);
		audio.Switch(AudioFlow.Render, Speaker(1));

		Assert.Empty(audio.Sets);
		Assert.Empty(learned);
		Assert.Contains(log, l => l.Contains("detached while watching"));
	}

	[Fact]
	public void EmulatedPadsWithoutAudio_AreNotWatched()
	{
		guard.PadAttaching(1, false);
		guard.PadAttached(1, 1);
		audio.Add(Speaker(1), AudioFlow.Render, pad: true, port: 1);
		audio.Switch(AudioFlow.Render, Speaker(1));

		Assert.Empty(audio.Sets);
		Assert.Empty(learned);
		Assert.Equal(0, audio.Watches);
		Assert.Empty(log);
	}

	[Fact]
	public void DefaultAlreadyTheController_KeepsTheEarlierRememberedDevice()
	{
		Attach();
		audio.Switch(AudioFlow.Render, Speaker(1));
		guard.PadDetaching(1);
		time.Advance(TimeSpan.FromSeconds(30));
		// The revert did not happen (say the app was closed): the controller is the default at the next attach.
		audio.Switch(AudioFlow.Render, Speaker(1));
		time.Advance(TimeSpan.FromSeconds(30));
		guard.PadAttaching(1, true);
		guard.PadAttached(1, 1);
		audio.Remove(Speaker(1));
		audio.Add(Speaker(1), AudioFlow.Render, pad: true, port: 1);
		audio.Switch(AudioFlow.Render, "pc-speaker");
		audio.Switch(AudioFlow.Render, Speaker(1));

		Assert.Equal("pc-speaker", Default(AudioFlow.Render, AudioRole.Console));
	}

	[Fact]
	public void Identity_DeviceTreeOfTheVirtualPad_GivesItsPort()
	{
		// As seen on the dev machine with the virtual pad attached on usbip port 1.
		DeviceNodeInfo[] chain =
		[
			new(@"USB\VID_054C&PID_0CE6&MI_00\3&314D5FD0&0&0000", [@"USB\VID_054C&PID_0CE6&REV_0100&MI_00"], "usbaudio", "0000.0000.0000.001.000.000.000.000.000"),
			new(@"USB\VID_054C&PID_0CE6\2&22E30EF8&0&1", [@"USB\VID_054C&PID_0CE6&REV_0100"], "usbccgp", "Port_#0001.Hub_#0003"),
			new(@"USB\ROOT_HUB30\1&2B53A856&1&0", [@"USB\ROOT_HUB30"], "USBHUB3", null),
			new(@"ROOT\USB\0000", [@"ROOT\USBIP_WIN2\UDE"], "usbip2_ude", null),
			new(@"HTREE\ROOT\0", [], null, null)
		];
		PadAudioMatch match = PadAudioIdentity.Classify(chain);
		Assert.True(match.IsVirtualPad);
		Assert.Equal(1, match.Port);
		Assert.Equal(PadMatchMethod.DeviceTree, match.Method);
	}

	[Fact]
	public void Identity_RealDualSenseOnACable_IsNotOurs()
	{
		DeviceNodeInfo[] chain =
		[
			new(@"USB\VID_054C&PID_0CE6&MI_00\7&1&0&0000", [], "usbaudio", null),
			new(@"USB\VID_054C&PID_0CE6\6&2&0&3", [], "usbccgp", "Port_#0003.Hub_#0001"),
			new(@"USB\ROOT_HUB30\5&19AA5C54&0&0", [@"USB\ROOT_HUB30"], "USBHUB3", null),
			new(@"PCI\VEN_1022&DEV_149C\4&1&0&0341", [@"PCI\VEN_1022&DEV_149C"], "USBXHCI", null)
		];
		Assert.False(PadAudioIdentity.Classify(chain).IsVirtualPad);
	}

	[Theory]
	[InlineData("{1}.USB\\VID_054C&PID_0CE6&MI_00\\3&1&0&0000", "USB\\VID_054C&PID_0CE6&MI_00\\3&1&0&0000")]
	[InlineData("USB\\VID_1&PID_2", "USB\\VID_1&PID_2")]
	public void Identity_StripsThePropertyPrefix(string value, string expected) => Assert.Equal(expected, PadAudioIdentity.StripPrefix(value));

	[Fact]
	public void Identity_NameFallback_AndPortParsing()
	{
		Assert.Equal(PadMatchMethod.NameOnly, PadAudioIdentity.NameMatch("Hoparlör (2- DualSense Wireless Controller)").Method);
		Assert.False(PadAudioIdentity.NameMatch("Hoparlör (Realtek)").IsVirtualPad);
		Assert.Equal(12, PadAudioIdentity.ParsePort("Port_#0012.Hub_#0003"));
		Assert.Null(PadAudioIdentity.ParsePort("0000.0000.0000.001.000"));
	}

	[Fact]
	public async Task BridgeHost_TellsTheGuardAboutEveryAttachAndDetach_WithAudioOnlyForDualSense()
	{
		ConcurrentQueue<string> bridgeLog = new();
		ConcurrentQueue<string> events = new();
		QueueControllers controllers = new();
		DeviceIdentity identity = DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
			[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "Sony Interactive Entertainment" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });
		await using BridgeHost host = new(controllers, new FakeIdentity(identity), new FakeAttacher(bridgeLog), new ManualTime())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			PadAudio = new RecordingPadAudio(events)
		};
		ConcurrentQueue<VirtualPadType> changed = new();
		host.PadTypeChanged += changed.Enqueue;
		controllers.Links.Enqueue(new RemovableLink(bridgeLog, "a"));
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));
		host.UsePad(VirtualPadType.DualShock4);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 1, 5000));
		await host.StopAsync();

		Assert.Equal(["attaching 1 audio", "attached 1 port 1", "detaching 1", "attaching 1 no audio", "attached 1 port 2", "detaching 1"], events);
	}

	private sealed class RecordingPadAudio(ConcurrentQueue<string> events) : IPadAudioEvents
	{
		public void PadAttaching(int slot, bool hasAudio) => events.Enqueue($"attaching {slot} {(hasAudio ? "audio" : "no audio")}");

		public void PadAttached(int slot, int port) => events.Enqueue($"attached {slot} port {port}");

		public void PadDetaching(int slot) => events.Enqueue($"detaching {slot}");
	}
}
