// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Games;

// What the disk cache knows about one app id: a picture (Path, fresh or past MaxAge) and/or a recent "Steam has none" mark.
public readonly record struct CachedGameImage(string? Path, bool Fresh, bool KnownMissing);

// Downloaded header pictures, one file per Steam app id (<appid>.jpg), in the user's data folder only. A picture is fresh
// for MaxAge; "Steam has no picture" is remembered for MissingAge (<appid>.none) so it is not asked for again. Trim keeps
// the folder under MaxBytes (oldest first). Never throws on IO errors: a cache that can't be read or written just misses.
public sealed class GameImageCache(string directory, TimeSpan? maxAge = null, TimeSpan? missingAge = null, long maxBytes = GameImageCache.DefaultMaxBytes,
	Func<DateTime>? utcNow = null)
{
	public const long DefaultMaxBytes = 32L * 1024 * 1024;
	public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);
	public static readonly TimeSpan DefaultMissingAge = TimeSpan.FromDays(7);

	// %LOCALAPPDATA%\Untethered\cache\game-images
	public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "cache", "game-images");

	private const string ImageExtension = ".jpg";
	private const string MissingExtension = ".none";

	private readonly TimeSpan maxAge = maxAge ?? DefaultMaxAge;
	private readonly TimeSpan missingAge = missingAge ?? DefaultMissingAge;
	private readonly Func<DateTime> now = utcNow ?? (() => DateTime.UtcNow);
	private readonly object gate = new();

	public string Directory { get; } = directory;

	public long MaxBytes { get; } = maxBytes;

	public static GameImageCache CreateDefault() => new(DefaultDirectory);

	public CachedGameImage Lookup(long appId)
	{
		try
		{
			string image = ImagePath(appId);
			string missing = MissingPath(appId);
			DateTime at = now();
			bool knownMissing = File.Exists(missing) && at - File.GetLastWriteTimeUtc(missing) < missingAge;
			if (File.Exists(image))
			{
				return new CachedGameImage(image, at - File.GetLastWriteTimeUtc(image) < maxAge, knownMissing);
			}
			return new CachedGameImage(null, false, knownMissing);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return default;
		}
	}

	// Saves a downloaded picture (atomically: a reader never sees half a file); null when it can't be written.
	public string? Store(long appId, byte[] bytes)
	{
		try
		{
			string path = ImagePath(appId);
			lock (gate)
			{
				System.IO.Directory.CreateDirectory(Directory);
				string temp = path + ".tmp";
				File.WriteAllBytes(temp, bytes);
				File.SetLastWriteTimeUtc(temp, now());
				File.Move(temp, path, overwrite: true);
				File.Delete(MissingPath(appId));
			}
			return path;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	// Remembers that Steam has no header picture for appId (for MissingAge).
	public void MarkMissing(long appId)
	{
		try
		{
			lock (gate)
			{
				System.IO.Directory.CreateDirectory(Directory);
				string path = MissingPath(appId);
				File.WriteAllBytes(path, []);
				File.SetLastWriteTimeUtc(path, now());
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Asked again next time.
		}
	}

	// Drops one picture (e.g. one that could not be decoded).
	public void Remove(long appId)
	{
		try
		{
			lock (gate)
			{
				File.Delete(ImagePath(appId));
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Left for Trim.
		}
	}

	// Deletes old "none" marks, pictures far past their age, then the oldest pictures until the folder is under MaxBytes.
	// Returns the pictures kept, their total size and how many files were deleted.
	public (int Files, long Bytes, int Removed) Trim()
	{
		int removed = 0;
		try
		{
			lock (gate)
			{
				if (!System.IO.Directory.Exists(Directory))
				{
					return (0, 0, 0);
				}
				DateTime at = now();
				var info = new DirectoryInfo(Directory);
				foreach (FileInfo file in info.EnumerateFiles("*" + MissingExtension).Concat(info.EnumerateFiles("*.tmp")).ToList())
				{
					if (at - file.LastWriteTimeUtc >= missingAge || file.Extension == ".tmp")
					{
						removed += TryDelete(file);
					}
				}
				List<FileInfo> images = info.EnumerateFiles("*" + ImageExtension).OrderBy(f => f.LastWriteTimeUtc).ToList();
				long total = images.Sum(f => f.Length);
				foreach (FileInfo file in images.ToList())
				{
					if (total <= MaxBytes && at - file.LastWriteTimeUtc < maxAge * 3)
					{
						continue;
					}
					long length = file.Length;
					if (TryDelete(file) == 1)
					{
						removed++;
						total -= length;
						images.Remove(file);
					}
				}
				return (images.Count, total, removed);
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return (0, 0, removed);
		}
	}

	// Deletes every cached picture and mark.
	public void Clear()
	{
		try
		{
			lock (gate)
			{
				if (System.IO.Directory.Exists(Directory))
				{
					foreach (FileInfo file in new DirectoryInfo(Directory).EnumerateFiles().ToList())
					{
						TryDelete(file);
					}
				}
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Best effort.
		}
	}

	private string ImagePath(long appId) => Path.Combine(Directory, appId.ToString(CultureInfo.InvariantCulture) + ImageExtension);

	private string MissingPath(long appId) => Path.Combine(Directory, appId.ToString(CultureInfo.InvariantCulture) + MissingExtension);

	private static int TryDelete(FileInfo file)
	{
		try
		{
			file.Delete();
			return 1;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return 0;
		}
	}
}
