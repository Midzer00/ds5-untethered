// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Profiles;

namespace Kopru.App.ViewModels;

// Per-controller profiles: a controller can run one saved profile instead of the global settings (kept by controller id).
// Everything not in a profile (idle time, shortcuts...) stays global.
public sealed partial class SettingsState
{
	private readonly Dictionary<string, string> _controllerProfiles = new(StringComparer.OrdinalIgnoreCase);

	// The profile id a controller runs, or null for the global settings.
	public string? ControllerProfileId(string controllerId) => _controllerProfiles.TryGetValue(controllerId, out string? id) ? id : null;

	public void AssignControllerProfile(string controllerId, string? profileId)
	{
		if (profileId == null || _profiles.Find(profileId) == null)
		{
			if (!_controllerProfiles.Remove(controllerId))
			{
				return;
			}
			_pushedProfiles.Remove(controllerId);
			_service.SetControllerProfile(controllerId, null);
		}
		else
		{
			_controllerProfiles[controllerId] = profileId;
			PushControllerProfiles();
		}
		ScheduleSave();
	}

	// The global settings with a profile's values.
	public ControllerSettings SettingsFor(ProfileValues v) => ToSettings() with
	{
		GameHapticsLevel = v.GameHapticsLevel,
		RumbleLevel = v.RumbleLevel,
		TriggerStrength = Math.Clamp(v.TriggerStrength, 0, 100),
		LightbarColour = v.LightbarColour,
		Brightness = v.Brightness,
		PlayerLeds = v.PlayerLeds,
		LightFromGame = v.LightFromGame,
		RumbleFeel = v.RumbleFeel,
		FallbackTrigger = v.FallbackTrigger,
		FallbackStrength = v.FallbackStrength,
		Tuning = v.Tuning,
		Remap = v.Remap,
		Gyro = v.Gyro,
		LightOptions = new LightOptions(v.LightMode, v.BreathingPeriod, Math.Clamp(v.BreathingMin / 100, 0, 1), Math.Clamp(v.BreathingMax / 100, 0, 1),
			v.RainbowSpeed, v.PlayerLedPattern ?? (v.PlayerLeds ? PlayerLedPattern.One : PlayerLedPattern.Off), v.PlayerLedBrightness, v.MuteLed)
	};

	private void InitControllerProfiles(AppSettings? saved)
	{
		foreach ((string controller, string profile) in saved?.ControllerProfiles ?? [])
		{
			if (_profiles.Find(profile) != null)
			{
				_controllerProfiles[controller] = profile;
			}
		}
		ProfilesChanged += PushControllerProfiles;
		InitKnownControllers(saved);
	}

	// The last settings sent per controller: a profile is sent again only when what it gives the controller changed (a
	// settings change also raises ProfilesChanged, which calls this a second time).
	private readonly Dictionary<string, ControllerSettings> _pushedProfiles = new(StringComparer.OrdinalIgnoreCase);

	private void PushControllerProfiles()
	{
		foreach ((string controller, string profile) in _controllerProfiles.ToList())
		{
			ControllerProfile? p = _profiles.Find(profile);
			if (p == null)
			{
				_controllerProfiles.Remove(controller);
				_pushedProfiles.Remove(controller);
				_service.SetControllerProfile(controller, null);
				continue;
			}
			ControllerSettings next = Effective(SettingsFor(p.Values));
			if (_pushedProfiles.TryGetValue(controller, out ControllerSettings? last) && last == next)
			{
				continue;
			}
			_pushedProfiles[controller] = next;
			_service.SetControllerProfile(controller, next);
		}
	}
}

public static class ControllerProfileSettingsExtensions
{
	public static AppSettings WithControllerProfiles(this AppSettings settings, IReadOnlyDictionary<string, string> profiles)
	{
		settings.ControllerProfiles = profiles.Count == 0 ? null : new Dictionary<string, string>(profiles, StringComparer.OrdinalIgnoreCase);
		return settings;
	}
}
