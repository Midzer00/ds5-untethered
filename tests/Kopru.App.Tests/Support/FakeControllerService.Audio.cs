// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;

namespace Kopru.App.Tests;

// Windows sound devices for the fake service: a PC speaker, a headset and a webcam microphone, plus the virtual pad's
// speaker and microphone (the defaults: the PC speaker and the controller microphone, as on a fresh install with the
// guard). Work runs at once, in order (no worker thread).
public sealed partial class FakeControllerService
{
	public FakeAudioEndpoints AudioEndpoints { get; } = new();

	private Kopru.Core.Audio.AudioDevices? _audioDevices;

	public Kopru.Core.Audio.AudioDevices? AudioDevices
	{
		get => _audioDevices ??= NoAudioDevices ? null : new Kopru.Core.Audio.AudioDevices(AudioEndpoints, Serial(), _ => { });
		set => _audioDevices = value;
	}

	// Leave the pickers without Windows devices (they are then disabled with their reason).
	public bool NoAudioDevices { get; set; }

	// Slots by virtual hub port (several pads).
	public Dictionary<int, int> AudioPortSlots { get; } = [];

	public int? SlotOfAudioPort(int port) => AudioPortSlots.TryGetValue(port, out int slot) ? slot : null;

	public static Action<Action> Serial()
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
}

public sealed class FakeAudioEndpoints : IAudioDefaultsSystem
{
	private readonly Dictionary<string, AudioEndpointInfo> endpoints = [];
	private readonly Dictionary<(AudioFlow, AudioRole), string?> defaults = [];
	private readonly Dictionary<string, bool> mutes = [];
	private readonly List<IAudioDefaultsListener> listeners = [];

	public FakeAudioEndpoints()
	{
		Add("speaker", AudioFlow.Render, "Hoparlör (Realtek(R) Audio)");
		Add("headset", AudioFlow.Render, "Kulaklık (2- HyperX Cloud II Wireless Gaming Headset)");
		Add("pad-speaker", AudioFlow.Render, "Hoparlör (DualSense Wireless Controller)", pad: true, port: 1);
		Add("webcam", AudioFlow.Capture, "Mikrofon (USB Web Kamerası)");
		Add("pad-mic", AudioFlow.Capture, "Kulaklık Mikrofon Bölümü (DualSense Wireless Controller)", pad: true, port: 1);
		SetAll(AudioFlow.Render, "speaker");
		SetAll(AudioFlow.Capture, "pad-mic");
	}

	public List<(string Id, AudioRole Role)> Sets { get; } = [];

	public bool ThrowOnSet { get; set; }

	public bool IgnoreSet { get; set; }

	public void Add(string id, AudioFlow flow, string name, bool pad = false, int? port = null)
	{
		endpoints[id] = new AudioEndpointInfo(id, flow, true, name,
			pad ? new PadAudioMatch(true, port, PadMatchMethod.DeviceTree, "test") : PadAudioMatch.None);
		listeners.ForEach(l => l.EndpointChanged(id));
	}

	public void Remove(string id)
	{
		endpoints[id] = endpoints[id] with { IsActive = false };
		listeners.ForEach(l => l.EndpointChanged(id));
	}

	// Windows (or its sound settings) changes the default of every role.
	public void SetAll(AudioFlow flow, string id)
	{
		foreach (AudioRole role in new[] { AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications })
		{
			defaults[(flow, role)] = id;
			listeners.ForEach(l => l.DefaultChanged(flow, role, id));
		}
	}

	public void MuteElsewhere(string id, bool muted) => mutes[id] = muted;

	public void Watch(IAudioDefaultsListener listener) => listeners.Add(listener);

	public string? GetDefault(AudioFlow flow, AudioRole role) => defaults.GetValueOrDefault((flow, role));

	public AudioEndpointInfo? Describe(string id) => endpoints.GetValueOrDefault(id);

	public IReadOnlyList<AudioEndpointInfo> Endpoints(AudioFlow flow) => [.. endpoints.Values.Where(e => e.Flow == flow && e.IsActive)];

	public bool? GetMute(string id) => endpoints.ContainsKey(id) ? mutes.GetValueOrDefault(id) : null;

	public void SetMute(string id, bool muted) => mutes[id] = muted;

	public void SetDefault(string id, AudioRole role)
	{
		Sets.Add((id, role));
		if (ThrowOnSet)
		{
			throw new InvalidCastException("no IPolicyConfig");
		}
		if (IgnoreSet)
		{
			return;
		}
		AudioFlow flow = endpoints[id].Flow;
		defaults[(flow, role)] = id;
		listeners.ForEach(l => l.DefaultChanged(flow, role, id));
	}
}
