// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Games;

public enum GameImageSource
{
	// Steam's own library cache on this PC.
	SteamLibrary,
	// Downloaded from Steam's CDN earlier (disk cache) or just now.
	Downloaded,
	// The icon of a game the user added (its exe).
	ExeIcon,
	// Nothing found: the app draws a neutral tile.
	Placeholder
}

// What a row of the supported games list needs to find its picture: the catalog's Steam app id and, for a game the user
// added, its exe path (the app id is then looked up in the Steam library the exe is in).
public sealed record GameImageRequest(string Key, long? SteamAppId, string? ExePath = null);

// Where the picture is: a picture file (SteamLibrary, Downloaded), the exe to take the icon from (ExeIcon), or nothing.
// AppId: the app id it was found for (to drop a cached file that turns out to be unreadable).
public sealed record GameImageFile(GameImageSource Source, string? Path = null, long? AppId = null)
{
	public static GameImageFile Placeholder { get; } = new(GameImageSource.Placeholder);
}

// Picks a row's picture, in this order: Steam's library cache on this PC; a picture downloaded from Steam's CDN (disk cache,
// else a download when downloads are on, the PC is online and downloads are not paused); the exe's icon for a game the user
// added; otherwise the placeholder. Downloads: at most MaxParallel at a time; one still waiting for its turn is dropped when
// every row that asked for it is gone (cancelled); "Steam has none" is remembered on disk; a failed download pauses all
// downloads (2 min, doubling up to an hour) and an app id is tried at most MaxAttempts times per run. A picture past its age
// is still used while a new one can't be fetched. Thread-safe.
public sealed class GameImageResolver
{
	public const int MaxParallel = 3;
	public const int MaxAttempts = 2;
	public static readonly TimeSpan FirstPause = TimeSpan.FromMinutes(2);
	public static readonly TimeSpan LongestPause = TimeSpan.FromHours(1);

	private static readonly GameImageDownload Dropped = GameImageDownload.Fail("dropped");

	private readonly Func<long, string?> findLocal;
	private readonly GameImageCache? cache;
	private readonly IGameImageDownloader? downloader;
	private readonly Func<bool> downloadsOn;
	private readonly Func<bool> isOnline;
	private readonly Func<string, long?>? appIdForExe;
	private readonly Func<string, bool> exeExists;
	private readonly Func<long> clock;
	private readonly SemaphoreSlim network = new(MaxParallel, MaxParallel);
	private readonly object gate = new();
	private readonly Dictionary<long, int> attempts = [];
	private readonly Dictionary<long, Pending> running = [];
	private long pausedUntil = long.MinValue;
	private TimeSpan nextPause = FirstPause;
	private int downloaded;
	private int failed;

	public GameImageResolver(Func<long, string?> findLocal, GameImageCache? cache, IGameImageDownloader? downloader, Func<bool> downloadsOn,
		Func<bool>? isOnline = null, Func<string, long?>? appIdForExe = null, Func<string, bool>? exeExists = null, Func<long>? clockMs = null)
	{
		this.findLocal = findLocal;
		this.cache = cache;
		this.downloader = downloader;
		this.downloadsOn = downloadsOn;
		this.isOnline = isOnline ?? IsNetworkAvailable;
		this.appIdForExe = appIdForExe;
		this.exeExists = exeExists ?? File.Exists;
		clock = clockMs ?? (() => Environment.TickCount64);
	}

	// Steam's library cache, the default disk cache and Steam's CDN, the Steam library for added games' app ids.
	public static GameImageResolver CreateDefault(Func<bool> downloadsOn)
	{
		SteamArtwork local = SteamArtwork.CreateDefault();
		Steam.SteamLibrary library = Steam.SteamLibrary.CreateDefault();
		return new GameImageResolver(local.Find, GameImageCache.CreateDefault(), SteamHeaderDownloader.CreateDefault(), downloadsOn,
			appIdForExe: library.AppIdFor);
	}

	public GameImageCache? Cache => cache;

	// Downloads are paused after a failure.
	public bool IsPaused
	{
		get
		{
			lock (gate)
			{
				return clock() < pausedUntil;
			}
		}
	}

	public async Task<GameImageFile> ResolveAsync(GameImageRequest request, CancellationToken cancel = default)
	{
		cancel.ThrowIfCancellationRequested();
		long? appId = request.SteamAppId;
		if (appId == null && !string.IsNullOrEmpty(request.ExePath) && appIdForExe != null)
		{
			appId = SafeAppIdFor(request.ExePath);
		}
		if (appId is long id && id > 0)
		{
			if (SafeFindLocal(id) is string local)
			{
				return new GameImageFile(GameImageSource.SteamLibrary, local, id);
			}
			if (cache != null && downloadsOn())
			{
				CachedGameImage cached = cache.Lookup(id);
				if (cached is { Path: not null, Fresh: true })
				{
					return new GameImageFile(GameImageSource.Downloaded, cached.Path, id);
				}
				if (!cached.KnownMissing && await DownloadAsync(id, cancel).ConfigureAwait(false) is string fresh)
				{
					return new GameImageFile(GameImageSource.Downloaded, fresh, id);
				}
				if (cached.Path != null)
				{
					return new GameImageFile(GameImageSource.Downloaded, cached.Path, id);
				}
			}
		}
		cancel.ThrowIfCancellationRequested();
		if (!string.IsNullOrEmpty(request.ExePath) && SafeExists(request.ExePath))
		{
			return new GameImageFile(GameImageSource.ExeIcon, request.ExePath);
		}
		return GameImageFile.Placeholder;
	}

	// The saved picture, or null (offline, paused, tried enough, failed or none). Rows asking for the same app id share one
	// download.
	private async Task<string?> DownloadAsync(long appId, CancellationToken cancel)
	{
		if (downloader == null || cache == null || !SafeOnline())
		{
			return null;
		}
		Pending pending;
		lock (gate)
		{
			if (!running.TryGetValue(appId, out pending!))
			{
				if (clock() < pausedUntil || attempts.GetValueOrDefault(appId) >= MaxAttempts)
				{
					return null;
				}
				attempts[appId] = attempts.GetValueOrDefault(appId) + 1;
				pending = new Pending();
				running[appId] = pending;
				pending.Task = RunAsync(appId, pending);
			}
			pending.Waiters++;
		}
		try
		{
			GameImageDownload result = await pending.Task.WaitAsync(cancel).ConfigureAwait(false);
			return result.Status == GameImageDownloadStatus.Ok ? cache.Lookup(appId).Path : null;
		}
		finally
		{
			lock (gate)
			{
				// Nobody waits any more and it has not started: drop it (a later look asks again).
				if (--pending.Waiters == 0 && !pending.Started)
				{
					pending.Cancel.Cancel();
				}
			}
		}
	}

	private async Task<GameImageDownload> RunAsync(long appId, Pending pending)
	{
		await Task.Yield();
		GameImageDownload result;
		try
		{
			await network.WaitAsync(pending.Cancel.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			Finish(appId, Dropped, refund: true);
			return Dropped;
		}
		try
		{
			lock (gate)
			{
				// Dropped, or paused (another download failed) while this one waited for its turn.
				if (pending.Cancel.IsCancellationRequested || clock() < pausedUntil)
				{
					result = Dropped;
				}
				else
				{
					pending.Started = true;
					result = null!;
				}
			}
			if (result == null)
			{
				result = await downloader!.DownloadAsync(appId, CancellationToken.None).ConfigureAwait(false);
			}
		}
		catch (Exception e) when (e is not OutOfMemoryException)
		{
			result = GameImageDownload.Fail(e.GetType().Name);
		}
		finally
		{
			network.Release();
		}
		Finish(appId, result, refund: ReferenceEquals(result, Dropped));
		return result;
	}

	private void Finish(long appId, GameImageDownload result, bool refund)
	{
		string? log = null;
		if (!refund)
		{
			if (result.Status == GameImageDownloadStatus.Ok && cache!.Store(appId, result.Bytes!) == null)
			{
				result = GameImageDownload.Fail("save");
			}
			if (result.Status == GameImageDownloadStatus.NotFound)
			{
				cache!.MarkMissing(appId);
			}
		}
		lock (gate)
		{
			running.Remove(appId);
			if (refund)
			{
				attempts[appId] = Math.Max(0, attempts.GetValueOrDefault(appId) - 1);
			}
			else if (result.Status == GameImageDownloadStatus.Ok)
			{
				downloaded++;
				nextPause = FirstPause;
				if (downloaded is 1 or 25 or 100 or 250)
				{
					log = string.Create(CultureInfo.InvariantCulture, $"Game images: {downloaded} downloaded, {failed} failed this run");
				}
			}
			else if (result.Status == GameImageDownloadStatus.Failed)
			{
				failed++;
				if (clock() >= pausedUntil)
				{
					pausedUntil = clock() + (long)nextPause.TotalMilliseconds;
					log = string.Create(CultureInfo.InvariantCulture,
						$"Game images: download failed ({result.Reason}); downloads paused for {nextPause.TotalMinutes:0} min ({downloaded} downloaded, {failed} failed this run)");
					nextPause = TimeSpan.FromTicks(Math.Min(LongestPause.Ticks, nextPause.Ticks * 2));
				}
			}
		}
		if (log != null)
		{
			AppLog.Write(log);
		}
	}

	private string? SafeFindLocal(long appId)
	{
		try
		{
			return findLocal(appId);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
	}

	private long? SafeAppIdFor(string exe)
	{
		try
		{
			return appIdForExe!(exe);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
	}

	private bool SafeExists(string path)
	{
		try
		{
			return exeExists(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return false;
		}
	}

	private bool SafeOnline()
	{
		try
		{
			return isOnline();
		}
		catch (Exception e) when (e is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
		{
			return false;
		}
	}

	private static bool IsNetworkAvailable() => System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

	private sealed class Pending
	{
		public Task<GameImageDownload> Task = null!;
		public readonly CancellationTokenSource Cancel = new();
		public int Waiters;
		public bool Started;
	}
}
