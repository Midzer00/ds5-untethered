// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Audio;
using Kopru.Core.Bridge;

namespace Kopru.Core.Tests;

// The device pickers' model: lists and defaults follow Windows live, a choice sets every role and is read back, a failing
// set-default turns choosing off, the guard learns the choice, and the default microphone's mute switch.
public class AudioDevicesTests
{
	private static readonly AudioRole[] AllRoles = [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

	private readonly FakeAudioSystem audio = new();
	private readonly ConcurrentQueue<string> log = new();
	private readonly List<AudioDeviceSnapshot> changes = [];
	private readonly AudioDevices devices;

	public AudioDevicesTests()
	{
		audio.Add("pc-speaker", AudioFlow.Render, notify: false);
		audio.Add("headset", AudioFlow.Render, notify: false);
		audio.Add("pc-mic", AudioFlow.Capture, notify: false);
		audio.Add("pad-mic", AudioFlow.Capture, pad: true, port: 1, notify: false);
		audio.Switch(AudioFlow.Render, "pc-speaker");
		audio.Switch(AudioFlow.Capture, "pc-mic");
		devices = new AudioDevices(audio, Serial(), log.Enqueue);
		devices.Changed += changes.Add;
		devices.Start();
	}

	// One at a time, in order, like the real worker.
	internal static Action<Action> Serial()
	{
		Queue<Action> queue = new();
		bool running = false;
		return a =>
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
		};
	}

	[Fact]
	public void Lists_TheActiveEndpoints_AndTheDefaults()
	{
		AudioDeviceSnapshot s = devices.Snapshot;
		Assert.Equal(["pc-speaker", "headset"], s.Render.Select(e => e.Id));
		Assert.Equal("pc-speaker", s.RenderDefault);
		Assert.Equal("pc-mic", s.CaptureDefault);
		Assert.True(s.Capture.Single(e => e.Id == "pad-mic").IsPad);
		Assert.False(s.ControllerMicSelected);
		Assert.True(s.CanSetDefault);
		Assert.Equal(1, audio.Watches);
	}

	[Fact]
	public void Follows_ArrivalRemovalAndDefaultChangesMadeElsewhere()
	{
		int before = changes.Count;
		audio.Add("usb-dac", AudioFlow.Render);
		Assert.Contains(devices.Snapshot.Render, e => e.Id == "usb-dac");
		audio.Switch(AudioFlow.Render, "usb-dac");
		Assert.Equal("usb-dac", devices.Snapshot.RenderDefault);
		audio.Remove("headset");
		Assert.DoesNotContain(devices.Snapshot.Render, e => e.Id == "headset");
		Assert.Equal(before + 3, changes.Count);
	}

	[Fact]
	public void Choosing_SetsEveryRole_AndReadsItBack()
	{
		bool? result = null;
		devices.Choose(AudioFlow.Render, "headset", ok => result = ok);

		Assert.True(result);
		Assert.Equal(AllRoles.Select(r => ("headset", r)), audio.Sets);
		Assert.All(AllRoles, r => Assert.Equal("headset", audio.GetDefault(AudioFlow.Render, r)));
		Assert.Equal("headset", devices.Snapshot.RenderDefault);
	}

	[Fact]
	public void AChoiceWindowsDidNotTake_IsReported_AndTheListShowsWhatWindowsHas()
	{
		audio.IgnoreSet = true;
		bool? result = null;
		devices.Choose(AudioFlow.Render, "headset", ok => result = ok);

		Assert.False(result);
		Assert.Equal("pc-speaker", devices.Snapshot.RenderDefault);
		Assert.True(devices.Snapshot.CanSetDefault);
	}

	[Fact]
	public void SetDefaultFailing_TurnsChoosingOff_LoggedOnce()
	{
		audio.ThrowOnSet = true;
		bool? first = null, second = null;
		devices.Choose(AudioFlow.Render, "headset", ok => first = ok);
		devices.Choose(AudioFlow.Capture, "pad-mic", ok => second = ok);

		Assert.False(first);
		Assert.False(second);
		Assert.False(devices.Snapshot.CanSetDefault);
		Assert.Single(audio.Sets);
		Assert.Single(log, l => l.Contains("setting the default speaker failed"));
	}

	[Fact]
	public void PickingTheController_IsTheUsersChoiceForTheGuard_EvenInsideTheArrivalWindow()
	{
		var guard = new DefaultAudioGuard(audio, new ManualTime(), Serial(), log.Enqueue);
		devices.Guard = guard;
		guard.PadAttaching(1, true);
		guard.PadAttached(1, 1);
		audio.Add("pad-speaker", AudioFlow.Render, pad: true, port: 1);

		devices.Choose(AudioFlow.Capture, "pad-mic");
		Assert.True(guard.Preferences.ControllerMicChosen);
		// The guard does not fight the picker.
		Assert.Equal("pad-mic", audio.GetDefault(AudioFlow.Capture, AudioRole.Console));
		Assert.True(devices.Snapshot.ControllerMicSelected);

		devices.Choose(AudioFlow.Capture, "pc-mic");
		Assert.False(guard.Preferences.ControllerMicChosen);
		Assert.Contains(log, l => l.Contains("you picked another microphone in the app"));
	}

	[Fact]
	public void MicMute_IsSetAndReadBack_AndFollowsChangesMadeElsewhereWhenPolled()
	{
		bool? read = null;
		devices.SetMicMute(true, m => read = m);
		Assert.True(read);
		Assert.True(devices.Snapshot.CaptureMuted);

		audio.MuteElsewhere("pc-mic", false);
		devices.RefreshMute();
		Assert.False(devices.Snapshot.CaptureMuted);
	}

	[Fact]
	public void MicMuteFailing_DoesNotThrow_AndIsLogged()
	{
		audio.ThrowOnMute = true;
		bool? read = true;
		devices.SetMicMute(true, m => read = m);
		Assert.Null(read);
		Assert.Contains(log, l => l.Contains("could not mute the PC microphone"));
	}

	[Fact]
	public void NoDefaultMicrophone_CountsAsTheController()
	{
		Assert.True(AudioDeviceSnapshot.Empty.ControllerMicSelected);
	}

	// Each mode × selected microphone (and Low Power) for the mic button and the tile.
	[Theory]
	[InlineData(true, MicButtonMode.TurnOff, false, false, false, MicPress.ControllerOn)]
	[InlineData(true, MicButtonMode.TurnOff, true, false, false, MicPress.ControllerOff)]
	[InlineData(true, MicButtonMode.TurnOff, true, true, false, MicPress.ControllerOff)]
	[InlineData(true, MicButtonMode.Mute, false, false, false, MicPress.ControllerOn)]
	[InlineData(true, MicButtonMode.Mute, true, false, false, MicPress.ControllerMuted)]
	[InlineData(true, MicButtonMode.Mute, true, true, false, MicPress.ControllerUnmuted)]
	[InlineData(true, MicButtonMode.TurnOff, false, false, true, MicPress.Ignored)]
	[InlineData(true, MicButtonMode.Mute, true, false, true, MicPress.Ignored)]
	[InlineData(false, MicButtonMode.TurnOff, false, false, false, MicPress.PcMuted)]
	[InlineData(false, MicButtonMode.Mute, true, false, false, MicPress.PcMuted)]
	[InlineData(false, MicButtonMode.Mute, false, false, true, MicPress.PcMuted)]
	public void MicButtonRules_Press(bool controller, MicButtonMode mode, bool enabled, bool muted, bool lowPower, MicPress expected) =>
		Assert.Equal(expected, MicButtonRules.Press(controller, mode, enabled, muted, pcMuted: false, lowPower));

	[Fact]
	public void MicButtonRules_PcMicrophone_Unmutes_AndStates()
	{
		Assert.Equal(MicPress.PcUnmuted, MicButtonRules.Press(false, MicButtonMode.TurnOff, false, false, true, false));
		Assert.Equal(MicState.Muted, MicButtonRules.State(false, false, false, true, false));
		Assert.Equal(MicState.On, MicButtonRules.State(false, false, false, false, true));
		Assert.Equal(MicState.On, MicButtonRules.State(false, false, false, null, false));
		Assert.Equal(MicState.Off, MicButtonRules.State(true, true, false, null, true));
		Assert.Equal(MicState.Muted, MicButtonRules.State(true, true, true, null, false));
		Assert.Equal(MicState.Off, MicButtonRules.State(true, false, false, null, false));
	}

	[Fact]
	public void MicSettings_Live_DecidesTheLed()
	{
		Assert.True(new MicSettings(true).IsLive);
		Assert.False(new MicSettings(true, Muted: true).IsLive);
		Assert.False(new MicSettings(false).IsLive);
		Assert.True(new MicSettings(false) { Live = true }.IsLive);
		Assert.False(new MicSettings(true) { Live = false }.IsLive);
		// Low Power turns the controller mic off but keeps the button and a PC microphone's state.
		MicSettings low = LowPower.Apply(new ControllerSettings { Mic = new MicSettings(true) { Live = true } }, true).Mic;
		Assert.True(low.Button);
		Assert.False(low.Enabled);
		Assert.True(low.IsLive);
	}

	[Fact]
	public void MicPipeline_Muted_FeedsSilence()
	{
		var p = new MicPipeline(null);
		p.PushPcm(Enumerable.Repeat((short)5000, 48 * 60).ToArray());
		p.Muted = true;
		byte[] pcm = new byte[48 * 2 * 10];
		p.Fill(pcm, 1);
		Assert.All(pcm, b => Assert.Equal(0, b));
	}
}
