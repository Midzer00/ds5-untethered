// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// EXPERIMENTAL, only while the Xbox One pad's developer switch is on (ExperimentalPads): the impulse trigger tuning live
// from ImpulseTuning.DefaultPath, which the WGI probe's sliders write. The file is checked every PollMs (its time and
// size); a changed file is read again and Changed is raised, so the bridge re-maps a rumble that is playing. No file, or
// one that cannot be read, means the Xbox-like default (or the last good tuning). Every change is logged with its numbers.
public sealed class ImpulseTuningFile : IDisposable
{
	public const int PollMs = 100;

	private static readonly Lazy<ImpulseTuningFile?> shared = new(() =>
		ExperimentalPads.XboxOne != null ? new ImpulseTuningFile(ImpulseTuning.DefaultPath) : null);

	private readonly string path;
	private readonly Timer? timer;
	private readonly object gate = new();
	private (DateTime Time, long Length) seen = (DateTime.MinValue, -1);
	private volatile ImpulseTuning current = ImpulseTuning.XboxLike;
	private bool failureLogged;

	// The app's watcher; null unless the experimental Xbox One pad is on.
	public static ImpulseTuningFile? Shared => shared.Value;

	// poll false: only Reload reads the file (tests).
	public ImpulseTuningFile(string path, bool poll = true)
	{
		this.path = path;
		Reload();
		Diagnostics.AppLog.Write($"EXPERIMENTAL impulse trigger tuning: {current.Describe()} (live from {path})");
		if (poll)
		{
			timer = new Timer(_ => Reload(), null, PollMs, PollMs);
		}
	}

	public ImpulseTuning Current => current;

	public event Action<ImpulseTuning>? Changed;

	// Reads the file when it changed since the last look; true when the tuning changed.
	public bool Reload()
	{
		ImpulseTuning next;
		lock (gate)
		{
			(DateTime, long) stamp;
			try
			{
				FileInfo info = new(path);
				stamp = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
			}
			catch (Exception)
			{
				return false;
			}
			if (stamp == seen)
			{
				return false;
			}
			try
			{
				next = stamp.Item2 < 0 ? ImpulseTuning.XboxLike : ImpulseTuning.FromJson(File.ReadAllText(path));
				seen = stamp;
				failureLogged = false;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
			{
				// Half written or locked: tried again on the next poll; a broken file is logged once.
				if (!failureLogged && e is System.Text.Json.JsonException)
				{
					failureLogged = true;
					Diagnostics.AppLog.Write($"impulse trigger tuning: {path} is not valid ({e.Message}); keeping {current.Describe()}");
				}
				return false;
			}
			if (next == current)
			{
				return false;
			}
			current = next;
		}
		Diagnostics.AppLog.Write($"impulse trigger tuning: {next.Describe()}");
		Changed?.Invoke(next);
		return true;
	}

	public void Dispose() => timer?.Dispose();
}
