// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Platform;
using Kopru.Core.Localization;

namespace Kopru.App.Services;

// One colour variant of the standard controller in the built-in pack (Assets/Pads/<id>/: body.webp, well-left.webp,
// well-right.webp, cap.webp, all at half the art's resolution). Special: shown under Special Editions in the picker.
// Folder: where its pictures are, when not under its own id (tests reuse pictures under other ids).
public sealed record PadVariant(string Id, bool IsSpecial, string NameEn, string NameTr, Color Swatch, string? Folder = null)
{
	public string Name => Localizer.Current == Language.Turkish ? NameTr : NameEn;
}

// The built-in pack: Assets/Pads/pads.json, an array in display order of
// { "id", "group": "standard" | "special", "nameEn", "nameTr", "swatch": "#RRGGBB" }. An entry that is malformed, repeats an
// id, or whose pictures are not all there is skipped; an unreadable manifest is an empty pack. Read once.
public static partial class PadPack
{
	public const string Root = "avares://Kopru.App/Assets/Pads/";
	public static readonly string[] Parts = ["body", "well-left", "well-right", "cap"];

	private static readonly Lazy<IReadOnlyList<PadVariant>> Loaded = new(Load);
	private static volatile IReadOnlyList<PadVariant>? _override;

	public static IReadOnlyList<PadVariant> Variants => _override ?? Loaded.Value;

	public static PadVariant? Find(string? id) => id == null ? null : Variants.FirstOrDefault(v => v.Id == id);

	public static Uri PartUri(string id, string part) => new($"{Root}{id}/{part}.webp");

	public static Uri PartUri(PadVariant variant, string part) => PartUri(variant.Folder ?? variant.Id, part);

	// Tests: another pack in place of the built-in one until the result is disposed.
	internal static IDisposable Override(IReadOnlyList<PadVariant> variants)
	{
		IReadOnlyList<PadVariant>? before = _override;
		_override = variants;
		return new Restore(() => _override = before);
	}

	private sealed class Restore(Action undo) : IDisposable
	{
		public void Dispose() => undo();
	}

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	private static partial Regex IdPattern();

	[GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
	private static partial Regex SwatchPattern();

	// The usable entries of a manifest; hasPictures tells whether an id's four pictures are all there.
	public static IReadOnlyList<PadVariant> Parse(string json, Func<string, bool> hasPictures)
	{
		List<PadVariant> result = [];
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		}
		catch (JsonException)
		{
			return result;
		}
		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				return result;
			}
			foreach (JsonElement entry in document.RootElement.EnumerateArray())
			{
				if (entry.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				string? id = Str(entry, "id");
				string? group = Str(entry, "group");
				string? nameEn = Str(entry, "nameEn")?.Trim();
				string? nameTr = Str(entry, "nameTr")?.Trim();
				string? swatch = Str(entry, "swatch");
				if (id == null || !IdPattern().IsMatch(id) || group is not ("standard" or "special") || string.IsNullOrEmpty(nameEn)
					|| string.IsNullOrEmpty(nameTr) || swatch == null || !SwatchPattern().IsMatch(swatch)
					|| result.Any(v => v.Id == id) || !hasPictures(id))
				{
					continue;
				}
				result.Add(new PadVariant(id, group == "special", nameEn, nameTr, Color.Parse(swatch)));
			}
		}
		return result;
	}

	private static string? Str(JsonElement entry, string name) =>
		entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static IReadOnlyList<PadVariant> Load()
	{
		try
		{
			var manifest = new Uri(Root + "pads.json");
			if (!AssetLoader.Exists(manifest))
			{
				return [];
			}
			using var reader = new StreamReader(AssetLoader.Open(manifest));
			return Parse(reader.ReadToEnd(), id => Parts.All(p => AssetLoader.Exists(PartUri(id, p))));
		}
		catch (Exception e) when (e is IOException or InvalidOperationException)
		{
			return [];
		}
	}
}
