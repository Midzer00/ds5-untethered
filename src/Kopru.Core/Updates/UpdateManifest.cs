// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Kopru.Core.Localization;

namespace Kopru.Core.Updates;

// One download of a release: where it is, its SHA-256 (64 hex digits, upper case here) and its exact size in bytes.
public sealed record UpdateAsset(string Url, string Sha256, long Size)
{
	// The file name at the end of the URL ("DS5-Untethered-0.1.6-Setup.exe").
	public string FileName => Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) ? Path.GetFileName(uri.LocalPath) : string.Empty;
}

// update.json (docs/RELEASING.md): the newest release, as the app reads it.
//   version         "0.1.6"
//   published       "2026-11-20" (shown as is)
//   setup, zip      { url, sha256, size } (zip is optional)
//   page            the release page (opened for zip copies)
//   notes           { tr, en }: a few short plain lines (optional)
//   minimumVersion  optional: copies older than this do not update in place; they get the download page instead
public sealed record UpdateManifest(AppVersion Version, string Published, UpdateAsset Setup, UpdateAsset? Zip, string Page,
	string NotesTr, string NotesEn, AppVersion? MinimumVersion)
{
	// Larger than any installer we would ever ship; also the cap of a download.
	public const long MaxAssetBytes = 300L * 1024 * 1024;
	// The manifest itself is tiny.
	public const int MaxManifestBytes = 64 * 1024;
	// Notes longer than this are cut (they are a line or two in the Settings page).
	public const int MaxNotesChars = 400;

	public string Notes(Language? l = null) => (l ?? Localizer.Current) == Language.Turkish ? NotesTr : NotesEn;

	// Whether a copy at `current` may install this one in place (minimumVersion).
	public bool CanUpdateInPlace(AppVersion current) => MinimumVersion is null || current >= MinimumVersion;

	// Null and a short English reason when json is not a usable manifest (garbage, missing fields, a URL outside the
	// allowed prefix, http instead of https, a bad hash or size).
	public static UpdateManifest? Parse(string json, UpdateEndpoints endpoints, out string? problem)
	{
		problem = null;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				problem = "not an object";
				return null;
			}
			if (!AppVersion.TryParse(Str(root, "version"), out AppVersion version))
			{
				problem = "bad version";
				return null;
			}
			UpdateAsset? setup = Asset(root, "setup", endpoints, out problem);
			if (setup == null)
			{
				problem = "setup: " + (problem ?? "missing");
				return null;
			}
			UpdateAsset? zip = null;
			if (root.TryGetProperty("zip", out JsonElement z) && z.ValueKind != JsonValueKind.Null)
			{
				zip = Asset(root, "zip", endpoints, out problem);
				if (zip == null)
				{
					problem = "zip: " + (problem ?? "missing");
					return null;
				}
			}
			string page = Str(root, "page") ?? string.Empty;
			if (!endpoints.IsAllowedPage(page))
			{
				problem = "page not on the project's GitHub";
				return null;
			}
			AppVersion? minimum = null;
			string? min = Str(root, "minimumVersion");
			if (!string.IsNullOrWhiteSpace(min) && !AppVersion.TryParse(min, out minimum))
			{
				problem = "bad minimumVersion";
				return null;
			}
			string tr = string.Empty, en = string.Empty;
			if (root.TryGetProperty("notes", out JsonElement notes) && notes.ValueKind == JsonValueKind.Object)
			{
				tr = CleanNotes(Str(notes, "tr"));
				en = CleanNotes(Str(notes, "en"));
			}
			return new UpdateManifest(version, (Str(root, "published") ?? string.Empty).Trim(), setup, zip, page, tr, en, minimum);
		}
		catch (JsonException)
		{
			problem = "not JSON";
			return null;
		}
	}

	private static UpdateAsset? Asset(JsonElement root, string name, UpdateEndpoints endpoints, out string? problem)
	{
		problem = null;
		if (!root.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Object)
		{
			problem = "missing";
			return null;
		}
		string? url = Str(a, "url");
		if (!endpoints.IsAllowedDownload(url))
		{
			problem = "url not allowed";
			return null;
		}
		string? hash = Str(a, "sha256")?.Trim();
		if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
		{
			problem = "bad sha256";
			return null;
		}
		if (!a.TryGetProperty("size", out JsonElement s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out long size) || size <= 0 || size > MaxAssetBytes)
		{
			problem = "bad size";
			return null;
		}
		return new UpdateAsset(url!, hash.ToUpperInvariant(), size);
	}

	private static string? Str(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	// Plain text only: control characters (but line breaks) dropped, lines trimmed, empty lines removed, cut to the limit.
	private static string CleanNotes(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}
		IEnumerable<string> lines = text.Replace("\r", string.Empty).Split('\n')
			.Select(l => new string(l.Where(c => !char.IsControl(c)).ToArray()).Trim())
			.Where(l => l.Length > 0);
		string joined = string.Join("\n", lines);
		return joined.Length <= MaxNotesChars ? joined : joined[..MaxNotesChars].TrimEnd() + "…";
	}
}
