// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kopru.Core.Diagnostics;
using Kopru.Core.Games;

namespace Kopru.App.Services;

// A decoded row picture: a header picture filling the tile, an exe icon shown small in its middle, or nothing (the
// placeholder).
public sealed record GameThumb(Bitmap? Image, bool IsIcon, GameImageSource Source)
{
	public static GameThumb Placeholder { get; } = new(null, false, GameImageSource.Placeholder);
}

// One row's picture: what to look for and the service that finds it (bound to a GameThumbnail).
public sealed record GameImageHandle(GameImageService Service, GameImageRequest Request);

// Pictures for the supported games list: finds them with the resolver (off the UI thread), decodes them at the size they
// are shown, keeps the last MemoryLimit in memory and decodes at most MaxDecoding at a time.
public sealed class GameImageService
{
	public const int MemoryLimit = 96;
	public const int MaxDecoding = 3;
	public const int IconSize = 48;

	private readonly GameImageResolver resolver;
	private readonly Func<string, IconPixels?> readIcon;
	private readonly SemaphoreSlim decoding = new(MaxDecoding, MaxDecoding);
	private readonly object gate = new();
	private readonly LinkedList<(string Key, GameThumb Thumb)> order = new();
	private readonly Dictionary<string, LinkedListNode<(string Key, GameThumb Thumb)>> memory = [];
	private int trimmed;

	public GameImageService(GameImageResolver resolver, Func<string, IconPixels?>? readIcon = null)
	{
		this.resolver = resolver;
		this.readIcon = readIcon ?? (path => ExeIcon.Read(path, IconSize));
	}

	public GameImageResolver Resolver => resolver;

	// Loads asked for (tests and the log).
	public int Loads { get; private set; }

	// The picture already in memory, or null.
	public GameThumb? Cached(GameImageRequest request, int pixelWidth)
	{
		lock (gate)
		{
			if (memory.TryGetValue(MemoryKey(request, pixelWidth), out var node))
			{
				order.Remove(node);
				order.AddFirst(node);
				return node.Value.Thumb;
			}
			return null;
		}
	}

	// Finds and decodes the picture of one row (pixelWidth: the tile's width in device pixels). Runs off the UI thread;
	// throws OperationCanceledException when cancelled.
	public async Task<GameThumb> LoadAsync(GameImageRequest request, int pixelWidth, CancellationToken cancel)
	{
		if (Cached(request, pixelWidth) is GameThumb hit)
		{
			return hit;
		}
		lock (gate)
		{
			Loads++;
		}
		TrimDiskOnce();
		GameImageFile file = await Task.Run(() => resolver.ResolveAsync(request, cancel), cancel).ConfigureAwait(false);
		GameThumb thumb;
		await decoding.WaitAsync(cancel).ConfigureAwait(false);
		try
		{
			cancel.ThrowIfCancellationRequested();
			thumb = await Task.Run(() => Decode(file, pixelWidth), cancel).ConfigureAwait(false);
		}
		finally
		{
			decoding.Release();
		}
		Remember(MemoryKey(request, pixelWidth), thumb);
		return thumb;
	}

	// Drops the pictures in memory (the setting changed: rows load again).
	public void Forget()
	{
		lock (gate)
		{
			order.Clear();
			memory.Clear();
		}
	}

	// Deletes the downloaded pictures on disk and the ones in memory.
	public void ClearCache()
	{
		Forget();
		resolver.Cache?.Clear();
	}

	private GameThumb Decode(GameImageFile file, int pixelWidth)
	{
		try
		{
			switch (file.Source)
			{
				case GameImageSource.SteamLibrary or GameImageSource.Downloaded when file.Path != null:
					using (FileStream stream = File.OpenRead(file.Path))
					{
						return new GameThumb(Bitmap.DecodeToWidth(stream, Math.Clamp(pixelWidth, 16, 460), BitmapInterpolationMode.HighQuality), false, file.Source);
					}
				case GameImageSource.ExeIcon when file.Path != null:
					return readIcon(file.Path) is IconPixels icon ? new GameThumb(ToBitmap(icon), true, file.Source) : GameThumb.Placeholder;
				default:
					return GameThumb.Placeholder;
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
		{
			// An unreadable downloaded picture is dropped (fetched again later).
			if (file.Source == GameImageSource.Downloaded && file.AppId is long appId)
			{
				resolver.Cache?.Remove(appId);
			}
			return GameThumb.Placeholder;
		}
	}

	private static Bitmap ToBitmap(IconPixels icon)
	{
		var bitmap = new WriteableBitmap(new PixelSize(icon.Width, icon.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
		using ILockedFramebuffer buffer = bitmap.Lock();
		int row = icon.Width * 4;
		for (int y = 0; y < icon.Height; y++)
		{
			System.Runtime.InteropServices.Marshal.Copy(icon.Bgra, y * row, buffer.Address + y * buffer.RowBytes, row);
		}
		return bitmap;
	}

	private void Remember(string key, GameThumb thumb)
	{
		lock (gate)
		{
			if (memory.TryGetValue(key, out var old))
			{
				order.Remove(old);
			}
			memory[key] = order.AddFirst((key, thumb));
			while (order.Count > MemoryLimit)
			{
				// Not disposed: a row may still be drawing it; the collector frees it.
				memory.Remove(order.Last!.Value.Key);
				order.RemoveLast();
			}
		}
	}

	// Once per run, in the background: keeps the disk cache under its size and age limits.
	private void TrimDiskOnce()
	{
		if (resolver.Cache is not GameImageCache cache || Interlocked.Exchange(ref trimmed, 1) == 1)
		{
			return;
		}
		_ = Task.Run(() =>
		{
			(int files, long bytes, int removed) = cache.Trim();
			if (removed > 0 || files > 0)
			{
				AppLog.Write(FormattableString.Invariant($"Game images: cache {files} files, {bytes / 1024} KB, {removed} removed"));
			}
		});
	}

	private static string MemoryKey(GameImageRequest request, int pixelWidth) =>
		request.Key + "|" + request.SteamAppId + "|" + request.ExePath + "|" + pixelWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
