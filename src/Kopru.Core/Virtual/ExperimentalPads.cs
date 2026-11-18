// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Virtual;

// Developer override for the Xbox Series pad type (a normal choice since 0.2). There is no UI for it; nothing changes
// unless the environment variable is set before the app starts.
//
//   KOPRU_EXPERIMENTAL_XBOXONE=1        the Xbox Series type presents a generic GIP device (pid.codes test VID/PID 1209:0001)
//   KOPRU_EXPERIMENTAL_XBOXONE=one      the Xbox One S identity 045E:02EA (no Share button)
//   KOPRU_EXPERIMENTAL_XBOXONE=series   the Xbox Series identity 045E:0B12 (the default anyway)
//
// With the switch on, the trigger rumble tuning is also read live from %LOCALAPPDATA%\Untethered\impulse-tuning.json
// (ImpulseTuningFile; the WGI probe's sliders write it) instead of the profile's Trigger Rumble preset. See
// docs/research/xbox-one-probe.md.
public static class ExperimentalPads
{
	public const string XboxOneVariable = "KOPRU_EXPERIMENTAL_XBOXONE";

	// The identity of the experimental Xbox One pad, or null when the switch is off.
	public static GipIdentity? XboxOne { get; } = ParseXboxOne(Environment.GetEnvironmentVariable(XboxOneVariable));

	public static GipIdentity? ParseXboxOne(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		null or "" or "0" or "false" or "off" or "no" => null,
		"one" or "ones" or "ms" or "02ea" or "045e:02ea" => GipIdentity.XboxOneS,
		"series" or "0b12" or "045e:0b12" => GipIdentity.XboxSeries,
		_ => GipIdentity.Generic
	};
}
