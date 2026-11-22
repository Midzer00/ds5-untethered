// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kopru.App.Services;

namespace Kopru.App.Controls;

public enum PadLookKind
{
	Default,
	Variant,
	Own
}

// How a controller is drawn: the app's own art (Default), a colour variant from the built-in pack (by id), or the user's
// own picture (a file in the app's settings folder).
public sealed record PadLook(PadLookKind Kind, string? VariantId = null, string? ImagePath = null)
{
	public static PadLook Default { get; } = new(PadLookKind.Default);

	public static PadLook Variant(string id) => new(PadLookKind.Variant, id);

	public static PadLook Own(string path) => new(PadLookKind.Own, null, path);
}

// What ControllerArt and the input overlay draw for a look: the body over the whole art box (the default art at full
// resolution, a pack variant at half, both in the same art-space rectangle) with the stick sprites, or the user's own
// picture alone (IsOwn: drawn fitted, with no light, LEDs or input on it).
public sealed class PadArtSet(Bitmap body, Func<Bitmap> wellLeft, Func<Bitmap> wellRight, Func<Bitmap> cap, bool isOwn = false)
{
	private readonly Lazy<Bitmap> _wellLeft = new(wellLeft);
	private readonly Lazy<Bitmap> _wellRight = new(wellRight);
	private readonly Lazy<Bitmap> _cap = new(cap);

	public Bitmap Body { get; } = body;
	public bool IsOwn { get; } = isOwn;
	public Bitmap WellLeft => _wellLeft.Value;
	public Bitmap WellRight => _wellRight.Value;
	public Bitmap Cap => _cap.Value;

	// Bitmap units per art pixel (1 for the default art, 0.5 for a pack variant).
	public double Density => Body.Size.Width / ControllerLayout.ArtWidth;
}

// The pictures of every look, decoded on first use and kept: only the variants actually drawn are decoded (the picker's
// thumbnails are separate, small decodes). An own picture that is missing or unreadable resolves to the default art.
public static class PadArt
{
	// Thumbnail width in pixels (a tile is about 104 DIP wide, so this stays sharp at 200 % scaling).
	public const int ThumbnailWidth = 220;
	private const int OwnKept = 4;

	private static readonly object Gate = new();
	private static readonly Lazy<PadArtSet> Standard = new(() => new PadArtSet(ControllerArt.ArtBitmap(false),
		() => ControllerArt.StickWellSprite(false, true), () => ControllerArt.StickWellSprite(false, false), () => ControllerArt.StickCapSprite(false)));
	private static readonly Lazy<PadArtSet> Edge = new(() => new PadArtSet(ControllerArt.ArtBitmap(true),
		() => ControllerArt.StickWellSprite(true, true), () => ControllerArt.StickWellSprite(true, false), () => ControllerArt.StickCapSprite(true)));
	private static readonly Dictionary<string, PadArtSet?> Variants = [];
	private static readonly List<(string Path, PadArtSet? Set)> Owns = [];
	private static readonly Dictionary<string, Bitmap> Thumbnails = [];

	// The variants decoded for drawing so far (tests: only the ones selected).
	public static IReadOnlyCollection<string> LoadedVariants
	{
		get
		{
			lock (Gate)
			{
				return [.. Variants.Where(v => v.Value != null).Select(v => v.Key)];
			}
		}
	}

	// The default art (the Edge's for an Edge).
	public static PadArtSet DefaultSet(bool edge) => (edge ? Edge : Standard).Value;

	// The pictures to draw for a look; the default art for null, a variant on an Edge (the pack is standard-controller art),
	// an unknown variant and an own picture that cannot be read.
	public static PadArtSet Resolve(PadLook? look, bool edge)
	{
		switch (look?.Kind)
		{
			case PadLookKind.Variant when !edge && look.VariantId != null:
				return VariantSet(look.VariantId) ?? DefaultSet(edge);
			case PadLookKind.Own when look.ImagePath != null:
				return OwnSet(look.ImagePath) ?? DefaultSet(edge);
			default:
				return DefaultSet(edge);
		}
	}

	// Whether the look draws as asked (false: it falls back to the default art).
	public static bool IsUsable(PadLook look, bool edge) => look.Kind switch
	{
		PadLookKind.Variant => !edge && look.VariantId != null && VariantSet(look.VariantId) != null,
		PadLookKind.Own => look.ImagePath != null && OwnSet(look.ImagePath) != null,
		_ => true
	};

	// Drops a decoded own picture (it was removed or replaced).
	public static void Forget(string path)
	{
		lock (Gate)
		{
			Owns.RemoveAll(o => SamePath(o.Path, path));
			Thumbnails.Remove("own:" + path);
		}
	}

	// A small picture of a look for the picker (from the body, about ThumbnailWidth pixels wide), made once and kept; null
	// when it cannot be read (not kept: tried again next time).
	public static Bitmap? Thumbnail(PadLook look, bool edge)
	{
		string key = look.Kind switch
		{
			PadLookKind.Variant when !edge => "variant:" + look.VariantId,
			PadLookKind.Own => "own:" + look.ImagePath,
			_ => edge ? "default-edge" : "default"
		};
		lock (Gate)
		{
			if (Thumbnails.TryGetValue(key, out Bitmap? kept))
			{
				return kept;
			}
		}
		Bitmap? made = look.Kind switch
		{
			PadLookKind.Variant when !edge && PadPack.Find(look.VariantId) is { } variant =>
				Decode(() => AssetLoader.Open(PadPack.PartUri(variant, "body"))),
			PadLookKind.Variant when !edge => null,
			PadLookKind.Own => look.ImagePath != null ? Decode(() => OpenShared(look.ImagePath)) : null,
			_ => Decode(() => AssetLoader.Open(new Uri($"avares://Kopru.App/Assets/{(edge ? "controller-edge.png" : "controller.png")}")))
		};
		// Only a picture made is kept: an id the pack does not have, or a read that failed, is tried again next time (the
		// user may put a good own picture in place).
		if (made != null)
		{
			lock (Gate)
			{
				Thumbnails[key] = made;
			}
		}
		return made;

		static Bitmap? Decode(Func<Stream> open)
		{
			try
			{
				using Stream stream = open();
				return Bitmap.DecodeToWidth(stream, ThumbnailWidth, BitmapInterpolationMode.HighQuality);
			}
			catch (Exception)
			{
				return null;
			}
		}
	}

	private static PadArtSet? VariantSet(string id)
	{
		lock (Gate)
		{
			if (Variants.TryGetValue(id, out PadArtSet? kept))
			{
				return kept;
			}
		}
		PadArtSet? set = null;
		PadVariant? variant = PadPack.Find(id);
		if (variant == null)
		{
			// Not in the pack: drawn as the default, and not remembered (only what was tried is kept).
			return null;
		}
		try
		{
			Bitmap body = LoadPart(variant, "body");
			set = new PadArtSet(body, () => LoadPart(variant, "well-left"), () => LoadPart(variant, "well-right"), () => LoadPart(variant, "cap"));
		}
		catch (Exception)
		{
			set = null;
		}
		lock (Gate)
		{
			Variants[id] = set;
		}
		return set;

		static Bitmap LoadPart(PadVariant variant, string part) => new(AssetLoader.Open(PadPack.PartUri(variant, part)));
	}

	private static PadArtSet? OwnSet(string path)
	{
		lock (Gate)
		{
			int found = Owns.FindIndex(o => SamePath(o.Path, path));
			if (found >= 0)
			{
				return Owns[found].Set;
			}
		}
		PadArtSet? set = null;
		try
		{
			if (File.Exists(path))
			{
				using FileStream stream = OpenShared(path);
				var body = new Bitmap(stream);
				set = new PadArtSet(body, () => body, () => body, () => body, isOwn: true);
			}
		}
		catch (Exception)
		{
			set = null;
		}
		lock (Gate)
		{
			Owns.Add((path, set));
			while (Owns.Count > OwnKept)
			{
				Owns.RemoveAt(0);
			}
		}
		return set;
	}

	// An own picture is read so that it can still be deleted or replaced meanwhile.
	private static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

	private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
