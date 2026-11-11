// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Audio;

// One active endpoint for the device pickers. Port: the virtual hub port of a virtual pad's endpoint, when known.
public sealed record AudioDeviceEntry(string Id, string Name, bool IsPad, int? Port);

// What the pickers show: the active endpoints of each flow, the defaults, whether the default microphone is muted
// (null: unknown), and whether setting the default works here.
public sealed record AudioDeviceSnapshot(
	IReadOnlyList<AudioDeviceEntry> Render,
	IReadOnlyList<AudioDeviceEntry> Capture,
	string? RenderDefault,
	string? CaptureDefault,
	bool? CaptureMuted,
	bool CanSetDefault)
{
	public static readonly AudioDeviceSnapshot Empty = new([], [], null, null, null, true);

	public IReadOnlyList<AudioDeviceEntry> Of(AudioFlow flow) => flow == AudioFlow.Render ? Render : Capture;

	public string? DefaultOf(AudioFlow flow) => flow == AudioFlow.Render ? RenderDefault : CaptureDefault;

	// The default recording device is a virtual pad's microphone, or there is none (then the mic button switches the
	// controller microphone, see MicButtonRules).
	public bool ControllerMicSelected => CaptureDefault == null || Capture.FirstOrDefault(e => e.Id == CaptureDefault)?.IsPad != false;
}

// The Windows default playback and recording devices for the app's pickers, and the mute switch of the default
// microphone (the mic button on a PC microphone). Every Windows call runs on one background worker (post); Changed is
// raised there after every refresh. Choosing a default sets all three roles (console, multimedia, communications), so
// everything follows the choice, and reads it back. A failure of the set-default call itself (no IPolicyConfig) turns
// choosing off for the run (CanSetDefault); a choice Windows did not take is only reported to the caller.
public sealed class AudioDevices : IAudioDefaultsListener
{
	private static readonly AudioRole[] Roles = [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

	private readonly IAudioDefaultsSystem system;
	private readonly Action<Action> post;
	private readonly Action<string> log;
	private AudioDeviceSnapshot snapshot = AudioDeviceSnapshot.Empty;
	private bool canSet = true;
	private bool started;

	public AudioDevices(IAudioDefaultsSystem system, Action<Action>? post = null, Action<string>? log = null)
	{
		this.system = system;
		this.post = post ?? new SerialWorker("audio devices").Post;
		this.log = log ?? Diagnostics.AppLog.Write;
	}

	// The Windows devices, or null off Windows.
	public static AudioDevices? CreateDefault(IAudioDefaultsSystem? system = null) =>
		OperatingSystem.IsWindows() ? new AudioDevices(system ?? new WindowsAudioDefaults()) : null;

	// Told about choices made here (they are deliberate for it).
	public DefaultAudioGuard? Guard { get; set; }

	public AudioDeviceSnapshot Snapshot => Volatile.Read(ref snapshot);

	public event Action<AudioDeviceSnapshot>? Changed;

	// Starts the notifications and reads the devices once; safe to call again.
	public void Start() => post(() =>
	{
		if (!started)
		{
			started = true;
			try
			{
				system.Watch(this);
			}
			catch (Exception e)
			{
				log($"audio devices: no change notifications ({e.GetType().Name}); the lists refresh when the app looks again");
			}
		}
		Refresh();
	});

	// Reads everything again.
	public void RefreshNow() => post(Refresh);

	// The default microphone's mute switch has no notification here (that would need a callback per endpoint): it is
	// read again on every refresh and polled with this.
	public void RefreshMute() => post(() =>
	{
		AudioDeviceSnapshot s = Snapshot;
		bool? muted = s.CaptureDefault is { } id ? Safe(() => system.GetMute(id)) : null;
		if (muted != s.CaptureMuted)
		{
			Publish(s with { CaptureMuted = muted });
		}
	});

	// Polls RefreshMute (the app; tests call it directly). The timer is kept here, so it lives as long as this object.
	public IDisposable PollMute(TimeSpan interval)
	{
		pollTimer?.Dispose();
		pollTimer = new Timer(_ => RefreshMute(), null, interval, interval);
		return pollTimer;
	}

	private Timer? pollTimer;

	// done(true) once Windows reports the choice as the default of every role; false otherwise (the lists then show
	// what Windows has). Raised on the worker.
	public void Choose(AudioFlow flow, string id, Action<bool>? done = null) => post(() =>
	{
		bool ok = ChooseCore(flow, id);
		Refresh();
		done?.Invoke(ok);
	});

	// Mutes or unmutes the default microphone; done(the mute state read back, null when unknown).
	public void SetMicMute(bool muted, Action<bool?>? done = null) => post(() =>
	{
		string? id = CaptureDefault();
		bool? now = null;
		if (id != null)
		{
			try
			{
				system.SetMute(id, muted);
				now = Safe(() => system.GetMute(id));
				log($"mic button: PC microphone {(now == true ? "muted" : now == false ? "unmuted" : "mute state unknown")}");
			}
			catch (Exception e)
			{
				log($"mic button: could not {(muted ? "mute" : "unmute")} the PC microphone ({e.GetType().Name})");
			}
		}
		Refresh();
		done?.Invoke(now);
	});

	void IAudioDefaultsListener.DefaultChanged(AudioFlow flow, AudioRole role, string? id) => post(Refresh);

	void IAudioDefaultsListener.EndpointChanged(string id) => post(Refresh);

	private bool ChooseCore(AudioFlow flow, string id)
	{
		AudioEndpointInfo? info = Safe(() => system.Describe(id));
		if (!canSet || info is not { IsActive: true })
		{
			return false;
		}
		string what = info.Pad.IsVirtualPad ? $"the controller {Word(flow)}" : $"another {Word(flow)}";
		Guard?.UserChose(flow, info.Pad.IsVirtualPad);
		try
		{
			foreach (AudioRole role in Roles)
			{
				system.SetDefault(id, role);
			}
		}
		catch (Exception e)
		{
			canSet = false;
			log($"audio devices: setting the default {Word(flow)} failed ({e.GetType().Name}); the device pickers are off for this run");
			return false;
		}
		bool ok = Roles.All(r => Safe(() => system.GetDefault(flow, r)) == id);
		log(ok ? $"settings: default {Word(flow)} set to {what} (all roles)" : $"settings: default {Word(flow)} set to {what}, but Windows reports another device");
		return ok;
	}

	private void Refresh()
	{
		string? capture = CaptureDefault();
		bool? muted = capture != null ? Safe(() => system.GetMute(capture)) : null;
		var next = new AudioDeviceSnapshot(
			List(AudioFlow.Render),
			List(AudioFlow.Capture),
			Safe(() => system.GetDefault(AudioFlow.Render, AudioRole.Multimedia)) ?? Safe(() => system.GetDefault(AudioFlow.Render, AudioRole.Console)),
			capture,
			muted,
			canSet);
		if (!Same(next, Snapshot))
		{
			Publish(next);
		}
	}

	private void Publish(AudioDeviceSnapshot next)
	{
		Volatile.Write(ref snapshot, next);
		Changed?.Invoke(next);
	}

	// The microphone the pickers show and the mic button mutes: the console role's default (what Windows' sound settings
	// call the default device), else the communications one.
	private string? CaptureDefault() =>
		Safe(() => system.GetDefault(AudioFlow.Capture, AudioRole.Console)) ?? Safe(() => system.GetDefault(AudioFlow.Capture, AudioRole.Communications));

	private IReadOnlyList<AudioDeviceEntry> List(AudioFlow flow) =>
		[.. (Safe(() => system.Endpoints(flow)) ?? []).Select(e => new AudioDeviceEntry(e.Id, e.Name, e.Pad.IsVirtualPad, e.Pad.Port))];

	private static bool Same(AudioDeviceSnapshot a, AudioDeviceSnapshot b) =>
		a.Render.SequenceEqual(b.Render) && a.Capture.SequenceEqual(b.Capture) && a.RenderDefault == b.RenderDefault
		&& a.CaptureDefault == b.CaptureDefault && a.CaptureMuted == b.CaptureMuted && a.CanSetDefault == b.CanSetDefault;

	private static T? Safe<T>(Func<T?> read)
	{
		try
		{
			return read();
		}
		catch (Exception)
		{
			return default;
		}
	}

	private static string Word(AudioFlow flow) => flow == AudioFlow.Render ? "speaker" : "microphone";
}
