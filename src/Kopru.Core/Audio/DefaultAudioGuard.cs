// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;

namespace Kopru.Core.Audio;

// Windows audio data flows and device roles (the values of EDataFlow and ERole).
public enum AudioFlow
{
	Render = 0,
	Capture = 1
}

public enum AudioRole
{
	Console = 0,
	Multimedia = 1,
	Communications = 2
}

// How an endpoint was tied to a virtual pad: DeviceTree = its USB audio device sits on the usbip-win2 virtual host
// (see PadAudioIdentity); NameOnly = the device tree could not be read, so the name decided.
public enum PadMatchMethod
{
	NotPad,
	DeviceTree,
	NameOnly
}

// Port: the virtual hub port of the USB device (the port "usbip attach" returned), when known.
public sealed record PadAudioMatch(bool IsVirtualPad, int? Port, PadMatchMethod Method, string Detail)
{
	public static readonly PadAudioMatch None = new(false, null, PadMatchMethod.NotPad, "not a virtual pad");
}

public sealed record AudioEndpointInfo(string Id, AudioFlow Flow, bool IsActive, string Name, PadAudioMatch Pad);

// Change notifications (may arrive on any thread).
public interface IAudioDefaultsListener
{
	void DefaultChanged(AudioFlow flow, AudioRole role, string? id);

	// An endpoint was added, removed or changed state.
	void EndpointChanged(string id);
}

// The Windows calls the guard needs (WindowsAudioDefaults; tests use a fake).
public interface IAudioDefaultsSystem
{
	// Starts the notifications; throws when they are not available.
	void Watch(IAudioDefaultsListener listener);

	// The default endpoint id, or null when there is none.
	string? GetDefault(AudioFlow flow, AudioRole role);

	// Null when the endpoint is unknown.
	AudioEndpointInfo? Describe(string id);

	// The active endpoints of a flow.
	IReadOnlyList<AudioEndpointInfo> Endpoints(AudioFlow flow);

	// Throws on failure (including a missing IPolicyConfig).
	void SetDefault(string id, AudioRole role);

	// The endpoint's mute switch (IAudioEndpointVolume); null when it can't be read.
	bool? GetMute(string id) => null;

	// Throws on failure.
	void SetMute(string id, bool muted) => throw new NotSupportedException();
}

// The bridge's virtual pad lifecycle (BridgeHost calls these for every slot; they never block nor throw).
public interface IPadAudioEvents
{
	// Just before "usbip attach"; hasAudio: the pad has a USB audio device (DualSense, not DualShock 4 / Xbox 360).
	void PadAttaching(int slot, bool hasAudio);

	// The attach returned the virtual hub port.
	void PadAttached(int slot, int port);

	// Just before "usbip detach" (or after a failed attach).
	void PadDetaching(int slot);
}

// The user chose the controller as the default device on purpose (per flow, saved in the settings).
public sealed record AudioGuardPreferences(bool ControllerSpeakerChosen = false, bool ControllerMicChosen = false);

// Keeps Windows from making the virtual controller the default speaker or microphone (Windows does that by itself when a
// new audio device arrives). Just before a pad with audio is attached, the default render and capture endpoints of every
// role are remembered; while that pad's arrival window is open, a default that moves to a virtual pad endpoint is put back.
//
// Arrival window: from PadAttaching until Settle after the last of the pad's endpoints arrived, at most MaxWindow after
// PadAttaching (the first attach on a new install also installs the USB audio driver, which can take a while). Closed
// at once when the pad is detached.
//
// Deliberate choice: a default that moves to a virtual pad endpoint outside every arrival window, while a pad with audio is
// attached, at least Quiet after the last endpoint arrival/removal or pad detach, and while the previous default is still
// active, is the user's own choice: it is kept and remembered per flow (later attaches leave that flow alone). It is
// forgotten when, under the same conditions, the default of that flow moves from the controller to another device while
// the controller's endpoint is still there.
//
// All work runs on one background worker (post); the COM callbacks only queue.
public sealed class DefaultAudioGuard : IPadAudioEvents, IAudioDefaultsListener
{
	public static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(90);
	public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);

	private static readonly AudioFlow[] Flows = [AudioFlow.Render, AudioFlow.Capture];
	private static readonly AudioRole[] Roles = [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

	private readonly IAudioDefaultsSystem system;
	private readonly TimeProvider time;
	private readonly Action<Action> post;
	private readonly Action<string> log;
	private readonly Dictionary<(AudioFlow, AudioRole), string> remembered = [];
	private readonly Dictionary<(AudioFlow, AudioRole), string?> current = [];
	private readonly Dictionary<int, Pad> pads = [];
	private DateTimeOffset quietUntil = DateTimeOffset.MinValue;
	private AudioGuardPreferences preferences = new();
	private volatile bool enabled = true;
	private bool watching;
	private bool watchFailed;
	private bool setFailed;

	// post: runs the work in order off the caller's thread (default: one background thread); log: AppLog.Write by default.
	public DefaultAudioGuard(IAudioDefaultsSystem system, TimeProvider? time = null, Action<Action>? post = null, Action<string>? log = null)
	{
		this.system = system;
		this.time = time ?? TimeProvider.System;
		this.post = post ?? new SerialWorker("audio defaults").Post;
		this.log = log ?? Diagnostics.AppLog.Write;
	}

	// The Windows guard, or null off Windows.
	public static DefaultAudioGuard? CreateDefault(IAudioDefaultsSystem? system = null) =>
		OperatingSystem.IsWindows() ? new DefaultAudioGuard(system ?? new WindowsAudioDefaults()) : null;

	// The "protect my default audio device" setting (on by default).
	public bool Enabled
	{
		get => enabled;
		set => enabled = value;
	}

	// Raised on the worker when a deliberate choice was learned or forgotten.
	public event Action<AudioGuardPreferences>? PreferencesChanged;

	public AudioGuardPreferences Preferences => preferences;

	// The saved choices (at app start).
	public void LoadPreferences(AudioGuardPreferences value) => post(() => preferences = value);

	// The user picked a default in the app (Quick Settings / Settings): the controller's endpoint is a deliberate choice
	// for that flow at once (also inside an arrival window), another device clears it. Call before setting the default,
	// so the change notifications that follow already see the choice.
	public void UserChose(AudioFlow flow, bool controller) => post(() =>
	{
		if (Chosen(flow) == controller)
		{
			return;
		}
		SetChosen(flow, controller);
		log(controller
			? $"audio defaults: you picked the controller {Word(flow)} in the app: kept, and kept on later attaches"
			: $"audio defaults: you picked another {Word(flow)} in the app: the controller {Word(flow)} is no longer kept as the default");
	});

	public void PadAttaching(int slot, bool hasAudio) => post(() =>
	{
		pads[slot] = new Pad(hasAudio, time.GetUtcNow());
		if (!hasAudio || !enabled || !EnsureWatching())
		{
			return;
		}
		Remember(slot);
	});

	public void PadAttached(int slot, int port) => post(() =>
	{
		if (!pads.TryGetValue(slot, out Pad? pad))
		{
			return;
		}
		pad.Port = port;
		if (pad.HasAudio && enabled && watching)
		{
			CheckAll();
		}
	});

	public void PadDetaching(int slot) => post(() =>
	{
		DateTimeOffset now = time.GetUtcNow();
		if (pads.Remove(slot, out Pad? pad) && pad.HasAudio && enabled && watching && pad.IsOpen(now))
		{
			log($"audio defaults{SlotText(slot)}: virtual controller detached while watching, stopped watching");
		}
		quietUntil = Later(quietUntil, now + Quiet);
	});

	void IAudioDefaultsListener.DefaultChanged(AudioFlow flow, AudioRole role, string? id) => post(() => Evaluate(flow, role, id));

	void IAudioDefaultsListener.EndpointChanged(string id) => post(() => OnEndpointChanged(id));

	private bool EnsureWatching()
	{
		if (watching)
		{
			return true;
		}
		if (watchFailed)
		{
			return false;
		}
		try
		{
			system.Watch(this);
			watching = true;
			return true;
		}
		catch (Exception e)
		{
			watchFailed = true;
			log($"audio defaults: cannot watch the default devices ({e.GetType().Name}); protecting the default audio device is off for this run");
			return false;
		}
	}

	// Before the attach: the defaults now, unless one already is a virtual pad's (then the earlier remembered one stays).
	private void Remember(int slot)
	{
		var parts = new List<string>();
		foreach (AudioFlow flow in Flows)
		{
			int kept = 0;
			int alreadyController = 0;
			foreach (AudioRole role in Roles)
			{
				string? id = Safe(() => system.GetDefault(flow, role));
				current[(flow, role)] = id;
				if (id == null)
				{
					continue;
				}
				if (Safe(() => system.Describe(id))?.Pad.IsVirtualPad == true)
				{
					alreadyController++;
					continue;
				}
				remembered[(flow, role)] = id;
				kept++;
			}
			string text = $"{Word(flow)} {kept}/3 roles";
			if (alreadyController > 0)
			{
				text += $" ({alreadyController} already the controller {Word(flow)}{(Chosen(flow) ? ", chosen by the user" : string.Empty)})";
			}
			parts.Add(text);
		}
		log($"audio defaults{SlotText(slot)}: remembered the previous devices before the virtual controller attaches: {string.Join(", ", parts)}");
	}

	private void OnEndpointChanged(string id)
	{
		DateTimeOffset now = time.GetUtcNow();
		quietUntil = Later(quietUntil, now + Quiet);
		if (!enabled || !watching)
		{
			return;
		}
		AudioEndpointInfo? info = Safe(() => system.Describe(id));
		if (info is not { IsActive: true, Pad.IsVirtualPad: true })
		{
			return;
		}
		bool any = false;
		foreach (Pad pad in pads.Values)
		{
			if (pad.Matches(info.Pad.Port) && pad.IsOpen(now))
			{
				pad.LastArrival = now;
				any = true;
			}
		}
		if (any)
		{
			// A default switch can come before the arrival notification, or be missed: look at every default again.
			CheckAll();
		}
	}

	private void CheckAll()
	{
		foreach (AudioFlow flow in Flows)
		{
			foreach (AudioRole role in Roles)
			{
				Evaluate(flow, role, Safe(() => system.GetDefault(flow, role)));
			}
		}
	}

	private void Evaluate(AudioFlow flow, AudioRole role, string? id)
	{
		current.TryGetValue((flow, role), out string? previous);
		current[(flow, role)] = id;
		if (!enabled || !watching || id == null || id == previous)
		{
			return;
		}
		DateTimeOffset now = time.GetUtcNow();
		AudioEndpointInfo? info = Safe(() => system.Describe(id));
		if (info == null)
		{
			return;
		}
		bool audioPadAttached = pads.Values.Any(p => p.HasAudio);
		string what = $"controller {Word(flow)} ({Name(role)})";
		if (info.Pad.IsVirtualPad)
		{
			Pad? arriving = pads.Values.FirstOrDefault(p => p.HasAudio && p.Matches(info.Pad.Port) && p.IsOpen(now));
			if (arriving != null)
			{
				if (Chosen(flow))
				{
					log($"audio defaults: left alone: Windows made the {what} the default; you chose the controller {Word(flow)} before");
					return;
				}
				Revert(flow, role, id);
				return;
			}
			if (!audioPadAttached)
			{
				return;
			}
			if (!IsDeliberate(previous, now))
			{
				log($"audio defaults: left alone: the {what} became the default right after a device change (not counted as your choice)");
				return;
			}
			if (!Chosen(flow))
			{
				SetChosen(flow, true);
				log($"audio defaults: you made the {what} the default: kept, and kept on later attaches");
			}
			return;
		}
		if (!Chosen(flow) || !audioPadAttached || pads.Values.Any(p => p.HasAudio && p.IsOpen(now)))
		{
			return;
		}
		bool fromController = previous != null && Safe(() => system.Describe(previous))?.Pad.IsVirtualPad == true;
		bool controllerHere = Safe(() => system.Endpoints(flow))?.Any(e => e.Pad.IsVirtualPad) == true;
		if (fromController && controllerHere && IsDeliberate(previous, now))
		{
			SetChosen(flow, false);
			log($"audio defaults: you picked another default {Word(flow)} ({Name(role)}): the controller {Word(flow)} is no longer kept as the default");
		}
	}

	// The user's own change: no endpoint came or went (nor a pad was detached) just before, and the previous default is
	// still active (Windows' own fallback after a removal is not).
	private bool IsDeliberate(string? previous, DateTimeOffset now) =>
		now >= quietUntil && (previous == null || Safe(() => system.Describe(previous))?.IsActive == true);

	private void Revert(AudioFlow flow, AudioRole role, string controllerId)
	{
		string what = $"controller {Word(flow)} ({Name(role)})";
		if (setFailed)
		{
			return;
		}
		if (!remembered.TryGetValue((flow, role), out string? back))
		{
			log($"audio defaults: left alone: Windows made the {what} the default, but no previous device was remembered");
			return;
		}
		AudioEndpointInfo? backInfo = Safe(() => system.Describe(back));
		if (backInfo is not { IsActive: true } || backInfo.Pad.IsVirtualPad)
		{
			log($"audio defaults: left alone: Windows made the {what} the default, but the previous device is gone");
			return;
		}
		try
		{
			system.SetDefault(back, role);
		}
		catch (Exception e)
		{
			setFailed = true;
			log($"audio defaults: could not put the previous device back for the {what} ({e.GetType().Name}); protecting the default audio device is off for this run");
			return;
		}
		string? now = Safe(() => system.GetDefault(flow, role));
		current[(flow, role)] = now;
		log(now == back
			? $"audio defaults: Windows made the {what} the default: put the previous device back"
			: $"audio defaults: tried to put the previous device back for the {what}, but Windows still reports {(now == controllerId ? "the controller" : "another device")}");
	}

	private bool Chosen(AudioFlow flow) => flow == AudioFlow.Render ? preferences.ControllerSpeakerChosen : preferences.ControllerMicChosen;

	private void SetChosen(AudioFlow flow, bool value)
	{
		preferences = flow == AudioFlow.Render ? preferences with { ControllerSpeakerChosen = value } : preferences with { ControllerMicChosen = value };
		PreferencesChanged?.Invoke(preferences);
	}

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

	private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

	private static string Word(AudioFlow flow) => flow == AudioFlow.Render ? "speaker" : "microphone";

	private static string Name(AudioRole role) => role switch
	{
		AudioRole.Console => "console",
		AudioRole.Multimedia => "multimedia",
		_ => "communications"
	};

	private static string SlotText(int slot) => slot > 1 ? string.Create(CultureInfo.InvariantCulture, $" (slot {slot})") : string.Empty;

	private sealed class Pad(bool hasAudio, DateTimeOffset start)
	{
		public bool HasAudio { get; } = hasAudio;

		public int? Port { get; set; }

		public DateTimeOffset? LastArrival { get; set; }

		// An endpoint with an unknown port matches every pad; a pad whose port is not known yet matches every endpoint.
		public bool Matches(int? port) => port == null || Port == null || Port == port;

		public bool IsOpen(DateTimeOffset now)
		{
			DateTimeOffset end = start + MaxWindow;
			if (LastArrival is { } arrival && arrival + Settle < end)
			{
				end = arrival + Settle;
			}
			return HasAudio && now < end;
		}
	}
}

// Runs posted actions one at a time on a background thread (MTA, as the Core Audio objects expect).
internal sealed class SerialWorker
{
	private readonly BlockingCollection<Action> queue = [];

	public SerialWorker(string name)
	{
		var thread = new Thread(Run) { IsBackground = true, Name = name };
		if (OperatingSystem.IsWindows())
		{
			thread.SetApartmentState(ApartmentState.MTA);
		}
		thread.Start();
	}

	public void Post(Action action) => queue.Add(action);

	private void Run()
	{
		foreach (Action action in queue.GetConsumingEnumerable())
		{
			try
			{
				action();
			}
			catch (Exception e)
			{
				Diagnostics.AppLog.Write($"audio defaults: unexpected {e.GetType().Name}: {e.Message}");
			}
		}
	}
}
