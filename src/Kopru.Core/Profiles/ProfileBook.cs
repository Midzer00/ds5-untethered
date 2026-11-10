// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Lighting;

namespace Kopru.Core.Profiles;

// The settings a profile holds, in the units the app shows (levels in percent): the supported games profile, the other
// games profile, stick/trigger tuning, button remap, gyro aiming and the light with its effects. Fields missing from
// older saved or exported profiles read as the defaults.
public sealed record ProfileValues
{
	public double GameHapticsLevel { get; init; } = 150;
	public double TriggerStrength { get; init; } = 100;
	public bool LightFromGame { get; init; } = true;
	public double RumbleLevel { get; init; } = 100;
	public RumbleFeel RumbleFeel { get; init; } = RumbleFeel.Soft;
	public FallbackTrigger FallbackTrigger { get; init; } = FallbackTrigger.Off;
	public double FallbackStrength { get; init; } = 50;
	public InputTuning Tuning { get; init; } = InputTuning.Default;
	public uint LightbarColour { get; init; } = 0x2F6BFF;
	public double Brightness { get; init; } = 80;
	public bool PlayerLeds { get; init; } = true;
	public RemapSettings Remap { get; init; } = RemapSettings.Default;
	// The gyro biases are the controller's calibration; a profile switch keeps the current ones.
	public GyroSettings Gyro { get; init; } = GyroSettings.Default;
	public LightMode LightMode { get; init; } = LightMode.Static;
	public double BreathingPeriod { get; init; } = 4;
	public double BreathingMin { get; init; } = 10;
	public double BreathingMax { get; init; } = 100;
	public double RainbowSpeed { get; init; } = 3;
	// Null (profiles saved before the pattern existed): player 1 when PlayerLeds is on, otherwise off.
	public PlayerLedPattern? PlayerLedPattern { get; init; }
	public LedBrightness PlayerLedBrightness { get; init; } = LedBrightness.High;
	public MuteLedMode MuteLed { get; init; } = MuteLedMode.Off;
	// Experimental controller speaker (missing in older profiles: off); volumes in percent.
	public bool SpeakerEnabled { get; init; }
	public double SpeakerVolume { get; init; } = 80;
	public double HeadphoneVolume { get; init; } = 60;
	// Controller microphone (missing in older profiles: off); gain in percent.
	public bool MicEnabled { get; init; }
	public double MicGain { get; init; } = 100;
	public bool MicNoiseGate { get; init; } = true;
	public bool MicMuteWindows { get; init; }
}

// A named settings bundle. The default profile (Id "default") always exists; its name is shown localized.
public sealed record ControllerProfile(string Id, string Name, ProfileValues Values)
{
	[JsonIgnore]
	public bool IsDefault => Id == ProfileBook.DefaultId;
}

// What an exported profile file holds.
public sealed record ProfileFile(string Format, int Version, string Name, ProfileValues Values)
{
	public const string FormatName = "untethered-profile";
}

// The profiles, which one is active and the profile chosen per known game (by game key; a game without one uses the
// default profile). Thread-safe; Changed is raised after every change, outside the lock.
public sealed class ProfileBook
{
	public const string DefaultId = "default";
	public const int MaxNameLength = 40;

	private static readonly JsonSerializerOptions FileOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter() }
	};

	private readonly object gate = new();
	private readonly List<ControllerProfile> profiles = [];
	private readonly Dictionary<string, string> games = new(StringComparer.OrdinalIgnoreCase);
	private string activeId = DefaultId;

	public ProfileBook(ProfileValues? defaults = null)
	{
		profiles.Add(new ControllerProfile(DefaultId, "Default", defaults ?? new ProfileValues()));
	}

	// Saved profiles; settings from before profiles existed (no list) become the default profile (legacy values).
	public static ProfileBook Load(IEnumerable<ControllerProfile>? saved, string? active, IReadOnlyDictionary<string, string>? gameProfiles,
		ProfileValues legacy)
	{
		var book = new ProfileBook(legacy);
		foreach (ControllerProfile p in saved ?? [])
		{
			if (p?.Values == null || string.IsNullOrWhiteSpace(p.Id))
			{
				continue;
			}
			if (p.IsDefault)
			{
				book.profiles[0] = p with { Name = "Default" };
			}
			else if (!book.profiles.Any(x => x.Id == p.Id))
			{
				book.profiles.Add(p with { Name = Clean(p.Name) is { Length: > 0 } n ? n : p.Id });
			}
		}
		foreach ((string key, string id) in gameProfiles ?? new Dictionary<string, string>())
		{
			if (!string.IsNullOrWhiteSpace(key) && book.profiles.Any(p => p.Id == id))
			{
				book.games[key] = id;
			}
		}
		if (active != null && book.profiles.Any(p => p.Id == active))
		{
			book.activeId = active;
		}
		return book;
	}

	public event Action? Changed;

	public IReadOnlyList<ControllerProfile> Profiles
	{
		get
		{
			lock (gate)
			{
				return profiles.ToList();
			}
		}
	}

	public string ActiveId
	{
		get
		{
			lock (gate)
			{
				return activeId;
			}
		}
	}

	public ControllerProfile Active
	{
		get
		{
			lock (gate)
			{
				return profiles.First(p => p.Id == activeId);
			}
		}
	}

	public ControllerProfile Default
	{
		get
		{
			lock (gate)
			{
				return profiles[0];
			}
		}
	}

	public IReadOnlyDictionary<string, string> GameProfiles
	{
		get
		{
			lock (gate)
			{
				return new Dictionary<string, string>(games, StringComparer.OrdinalIgnoreCase);
			}
		}
	}

	public ControllerProfile? Find(string id)
	{
		lock (gate)
		{
			return profiles.FirstOrDefault(p => p.Id == id);
		}
	}

	// 1-based position in the list (the default profile is 1); 0 when unknown.
	public int IndexOf(string id)
	{
		lock (gate)
		{
			return profiles.FindIndex(p => p.Id == id) + 1;
		}
	}

	// A new profile with the given values; the name is trimmed and made unique. Null when the name is blank.
	public ControllerProfile? Create(string name, ProfileValues values)
	{
		ControllerProfile created;
		lock (gate)
		{
			string clean = Clean(name);
			if (clean.Length == 0)
			{
				return null;
			}
			created = new ControllerProfile(Guid.NewGuid().ToString("N"), Unique(clean, null), values);
			profiles.Add(created);
		}
		Changed?.Invoke();
		return created;
	}

	// A copy of a profile named copyName (made unique).
	public ControllerProfile? Duplicate(string id, string copyName)
	{
		ControllerProfile? source = Find(id);
		return source == null ? null : Create(copyName, source.Values);
	}

	// False for the default profile, an unknown one or a blank name.
	public bool Rename(string id, string name)
	{
		lock (gate)
		{
			int i = profiles.FindIndex(p => p.Id == id);
			string clean = Clean(name);
			if (i <= 0 || clean.Length == 0)
			{
				return false;
			}
			if (profiles[i].Name == clean)
			{
				return true;
			}
			profiles[i] = profiles[i] with { Name = Unique(clean, id) };
		}
		Changed?.Invoke();
		return true;
	}

	// Removes a profile (never the default one). Games that used it go back to the default; if it was active, the default
	// becomes active.
	public bool Delete(string id)
	{
		lock (gate)
		{
			int i = profiles.FindIndex(p => p.Id == id);
			if (i <= 0)
			{
				return false;
			}
			profiles.RemoveAt(i);
			foreach (string key in games.Where(g => g.Value == id).Select(g => g.Key).ToList())
			{
				games.Remove(key);
			}
			if (activeId == id)
			{
				activeId = DefaultId;
			}
		}
		Changed?.Invoke();
		return true;
	}

	// Stores new values for a profile (the app edits the active profile in place).
	public bool Update(string id, ProfileValues values)
	{
		lock (gate)
		{
			int i = profiles.FindIndex(p => p.Id == id);
			if (i < 0 || profiles[i].Values == values)
			{
				return i >= 0;
			}
			profiles[i] = profiles[i] with { Values = values };
		}
		Changed?.Invoke();
		return true;
	}

	public bool Activate(string id)
	{
		lock (gate)
		{
			if (activeId == id || !profiles.Any(p => p.Id == id))
			{
				return activeId == id;
			}
			activeId = id;
		}
		Changed?.Invoke();
		return true;
	}

	// The profile after (step 1) or before (step -1) the active one, wrapping around.
	public string Step(int step)
	{
		lock (gate)
		{
			int i = profiles.FindIndex(p => p.Id == activeId);
			int n = profiles.Count;
			return profiles[(((i + step) % n) + n) % n].Id;
		}
	}

	// The profile for a game (null id or the default: the game uses the default profile).
	public void AssignGame(string key, string? id)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return;
		}
		lock (gate)
		{
			bool known = id != null && id != DefaultId && profiles.Any(p => p.Id == id);
			if (known ? games.TryGetValue(key, out string? had) && had == id : !games.ContainsKey(key))
			{
				return;
			}
			if (known)
			{
				games[key] = id!;
			}
			else
			{
				games.Remove(key);
			}
		}
		Changed?.Invoke();
	}

	// The profile chosen for a game, or null when it has none.
	public string? ProfileForGame(string? key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return null;
		}
		lock (gate)
		{
			return games.TryGetValue(key, out string? id) ? id : null;
		}
	}

	// What should be active with this game in front (null: no known game): its profile, otherwise the default.
	public string ResolveForGame(string? key) => ProfileForGame(key) ?? DefaultId;

	// A profile as an export file (JSON).
	public string Export(string id)
	{
		ControllerProfile p = Find(id) ?? throw new ArgumentException("Unknown profile", nameof(id));
		return JsonSerializer.Serialize(new ProfileFile(ProfileFile.FormatName, 1, p.Name, p.Values), FileOptions);
	}

	// Adds a profile from an export file; null when the text is not a profile file. defaultName names a file without one.
	public ControllerProfile? Import(string json, string defaultName)
	{
		ProfileFile? file;
		try
		{
			file = JsonSerializer.Deserialize<ProfileFile>(json, FileOptions);
		}
		catch (JsonException)
		{
			return null;
		}
		if (file?.Values == null || file.Format != ProfileFile.FormatName)
		{
			return null;
		}
		string name = Clean(file.Name ?? string.Empty);
		return Create(name.Length > 0 ? name : defaultName, file.Values);
	}

	private static string Clean(string? name)
	{
		string s = (name ?? string.Empty).Trim();
		return s.Length > MaxNameLength ? s[..MaxNameLength].TrimEnd() : s;
	}

	// Caller holds the lock. "Name", then "Name (2)", "Name (3)"...
	private string Unique(string name, string? except)
	{
		bool Taken(string n) => profiles.Any(p => p.Id != except && !p.IsDefault && string.Equals(p.Name, n, StringComparison.CurrentCultureIgnoreCase));
		if (!Taken(name))
		{
			return name;
		}
		for (int i = 2; ; i++)
		{
			string candidate = $"{name} ({i})";
			if (!Taken(candidate))
			{
				return candidate;
			}
		}
	}
}

// Feedback shown on the controller for a profile.
public static class ProfileFeedback
{
	// The PS5 player LED patterns for players 1-5 (the five LEDs, bit 0 = left); higher numbers wrap around.
	private static readonly byte[] Patterns = [0x04, 0x0A, 0x15, 0x1B, 0x1F];

	public static byte PlayerLeds(int index) => Patterns[((Math.Max(index, 1) - 1) % Patterns.Length)];
}
