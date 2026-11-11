// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;

// `Kopru.Probe audiodefaults` lists the active render and capture endpoints, which roles each is the default for, and
// whether it is a virtual pad endpoint (the same identification as the app's DefaultAudioGuard, with how it was decided).
// `Kopru.Probe audiodefaults roundtrip` checks that setting the default works on this Windows build (IPolicyConfig): per
// flow, console + multimedia go to another active endpoint and back within about a second, each step read back; the
// original defaults are always put back. Exit 0 works, 2 nothing to switch to, 3 setting failed, 4 restoring failed.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class AudioDefaultsProbe
{
	private static readonly AudioRole[] Roles = [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

	public static int Run(string[] args)
	{
		var system = new WindowsAudioDefaults();
		List();
		try
		{
			system.Watch(new NullListener());
			Console.WriteLine("Change notifications: registered OK");
		}
		catch (Exception e)
		{
			Console.WriteLine($"Change notifications: FAILED ({e.GetType().Name}: {e.Message})");
		}
		if (args.FirstOrDefault() != "roundtrip")
		{
			return 0;
		}
		int result = 0;
		foreach (AudioFlow flow in new[] { AudioFlow.Render, AudioFlow.Capture })
		{
			result = Math.Max(result, RoundTrip(system, flow));
		}
		Console.WriteLine();
		Console.WriteLine(result switch
		{
			0 => "VERDICT: OK - setting the default device works on this Windows build, original defaults restored",
			2 => "VERDICT: INCONCLUSIVE - a flow had no second active endpoint to switch to",
			3 => "VERDICT: FAILED - setting the default device does not work here (the app's protection will do nothing)",
			_ => "VERDICT: RESTORE FAILED - check the default devices in Windows Sound settings"
		});
		return result;

		void List()
		{
			foreach (AudioFlow flow in new[] { AudioFlow.Render, AudioFlow.Capture })
			{
				Console.WriteLine(flow == AudioFlow.Render ? "Render (speakers):" : "Capture (microphones):");
				Dictionary<AudioRole, string?> defaults = Roles.ToDictionary(r => r, r => Try(() => system.GetDefault(flow, r)));
				IReadOnlyList<AudioEndpointInfo> endpoints;
				try
				{
					endpoints = system.Endpoints(flow);
				}
				catch (Exception e)
				{
					Console.WriteLine($"  listing failed: {e.GetType().Name}: {e.Message}");
					continue;
				}
				foreach (AudioEndpointInfo e in endpoints)
				{
					string[] roles = [.. Roles.Where(r => defaults[r] == e.Id).Select(r => r.ToString().ToLowerInvariant())];
					Console.WriteLine($"  {e.Name}");
					Console.WriteLine($"    default for: {(roles.Length > 0 ? string.Join(", ", roles) : "-")}");
					Console.WriteLine($"    virtual pad: {(e.Pad.IsVirtualPad ? "YES" : "no")} ({e.Pad.Method}: {e.Pad.Detail})");
				}
			}
		}
	}

	private static int RoundTrip(WindowsAudioDefaults system, AudioFlow flow)
	{
		string word = flow == AudioFlow.Render ? "render" : "capture";
		Console.WriteLine();
		Console.WriteLine($"Round trip ({word}, console + multimedia):");
		string? console = system.GetDefault(flow, AudioRole.Console);
		string? multimedia = system.GetDefault(flow, AudioRole.Multimedia);
		AudioEndpointInfo? other = system.Endpoints(flow)
			.Where(e => e.Id != console && e.Id != multimedia)
			.OrderBy(e => e.Pad.IsVirtualPad)
			.FirstOrDefault();
		if (console == null || multimedia == null || other == null)
		{
			Console.WriteLine("  skipped: no default or no second active endpoint");
			return 2;
		}
		Console.WriteLine($"  original: {Name(system, console)}{(multimedia != console ? " / " + Name(system, multimedia) : string.Empty)}");
		Console.WriteLine($"  switching to: {other.Name}");
		int result = 0;
		try
		{
			system.SetDefault(other.Id, AudioRole.Console);
			system.SetDefault(other.Id, AudioRole.Multimedia);
			Thread.Sleep(200);
			bool switched = system.GetDefault(flow, AudioRole.Console) == other.Id && system.GetDefault(flow, AudioRole.Multimedia) == other.Id;
			Console.WriteLine($"  step 1 (switch): {(switched ? "OK, read back" : "FAILED, the default did not change")}");
			if (!switched)
			{
				result = 3;
			}
			Thread.Sleep(600);
		}
		catch (Exception e)
		{
			Console.WriteLine($"  step 1 (switch): FAILED ({e.GetType().Name}: {e.Message})");
			result = 3;
		}
		finally
		{
			if (!Restore(system, flow, console, multimedia))
			{
				result = 4;
			}
		}
		return result;
	}

	// Puts the original defaults back (twice if the first read back does not show them).
	private static bool Restore(WindowsAudioDefaults system, AudioFlow flow, string console, string multimedia)
	{
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			try
			{
				system.SetDefault(console, AudioRole.Console);
				system.SetDefault(multimedia, AudioRole.Multimedia);
				Thread.Sleep(200);
				if (system.GetDefault(flow, AudioRole.Console) == console && system.GetDefault(flow, AudioRole.Multimedia) == multimedia)
				{
					Console.WriteLine("  step 2 (restore): OK, read back");
					return true;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine($"  step 2 (restore) attempt {attempt}: {e.GetType().Name}: {e.Message}");
			}
		}
		Console.WriteLine("  step 2 (restore): FAILED - set the default device again in Windows Sound settings");
		return false;
	}

	private static string Name(WindowsAudioDefaults system, string id) => Try(() => system.Describe(id))?.Name ?? "(unknown)";

	private static T? Try<T>(Func<T?> read)
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

	private sealed class NullListener : IAudioDefaultsListener
	{
		public void DefaultChanged(AudioFlow flow, AudioRole role, string? id)
		{
		}

		public void EndpointChanged(string id)
		{
		}
	}
}
