// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media.Imaging;

namespace Kopru.App.Services;

// The users' own controller pictures: each one copied into the app's looks folder (so the original may move or go) as a
// PNG under a random name, downscaled to MaxWidth when wider. Only PNG, JPEG and WebP files are taken (the picker offers
// those; anything that does not decode is refused).
public sealed class OwnImageStore(string folder)
{
	// The art's own width: a wider picture would only cost memory.
	public const int MaxWidth = 2290;
	// Larger files are refused before decoding.
	public const long MaxBytes = 40L * 1024 * 1024;

	public string Folder { get; } = folder;

	public string PathOf(string fileName) => Path.Combine(Folder, Path.GetFileName(fileName));

	// Copies a picture in; its new file name, or null when it is not a picture that can be read (nothing is written then).
	public string? Import(Stream source)
	{
		byte[] data;
		try
		{
			using var buffer = new MemoryStream();
			source.CopyTo(buffer);
			if (buffer.Length == 0 || buffer.Length > MaxBytes)
			{
				return null;
			}
			data = buffer.ToArray();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return null;
		}

		Bitmap picture;
		try
		{
			picture = new Bitmap(new MemoryStream(data));
			if (picture.PixelSize.Width > MaxWidth)
			{
				picture.Dispose();
				picture = Bitmap.DecodeToWidth(new MemoryStream(data), MaxWidth, BitmapInterpolationMode.HighQuality);
			}
		}
		catch (Exception)
		{
			return null;
		}

		using (picture)
		{
			if (picture.PixelSize.Width < 1 || picture.PixelSize.Height < 1)
			{
				return null;
			}
			string name = Guid.NewGuid().ToString("N")[..16] + ".png";
			string path = PathOf(name);
			string temp = path + ".tmp";
			try
			{
				Directory.CreateDirectory(Folder);
				using (FileStream file = File.Create(temp))
				{
					picture.Save(file);
				}
				File.Move(temp, path, overwrite: true);
				return name;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				TryDelete(temp);
				return null;
			}
		}
	}

	public void Delete(string? fileName)
	{
		if (!string.IsNullOrEmpty(fileName))
		{
			TryDelete(PathOf(fileName));
		}
	}

	// Removes the pictures no controller uses any more (a forgotten controller's, or one left by a crash).
	public void DeleteAllBut(IEnumerable<string> keep)
	{
		HashSet<string> kept = new(keep.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
		try
		{
			if (!Directory.Exists(Folder))
			{
				return;
			}
			foreach (string file in Directory.EnumerateFiles(Folder))
			{
				if (!kept.Contains(Path.GetFileName(file)))
				{
					TryDelete(file);
				}
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
	}
}
