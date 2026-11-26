// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// manifest.json for the translate page (format in docs/internal/tasks/translate-context/BRIEF.md), and coverage.json: the
// keys no shot shows, counted by prefix, and the visible texts that carry no key.
internal static class ContextManifest
{
	private static readonly JsonSerializerOptions Json = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public static void Write(string outDir, ContextRender render, double scale)
	{
		string[] keysSeen = render.Shots.SelectMany(s => s.Items).Select(i => i.Key).Distinct(StringComparer.Ordinal)
			.OrderBy(k => k, StringComparer.Ordinal).ToArray();
		var manifest = new Dictionary<string, object>
		{
			["generated"] = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
			["appVersion"] = TranslateContextTests.AppVersion(),
			["stringsHash"] = TranslateContextTests.StringsHash(),
			["width"] = (int)ContextScene.Width,
			["height"] = (int)ContextScene.Height,
			["scale"] = scale,
			["shots"] = render.Shots.Select(s => new Dictionary<string, object>
			{
				["id"] = s.Shot.Id,
				["group"] = s.Shot.Group,
				["title"] = s.Shot.Title,
				["image"] = s.Image,
				["items"] = s.Items.OrderBy(i => Math.Round(i.Bounds.Y)).ThenBy(i => i.Bounds.X).Select(Item).ToArray()
			}).ToArray(),
			["keysSeen"] = keysSeen
		};
		File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, Json));

		HashSet<string> seen = new(keysSeen, StringComparer.Ordinal);
		string[] unseen = KeyTagLocale.Keys.Where(k => !seen.Contains(k)).ToArray();
		var coverage = new Dictionary<string, object>
		{
			["keysTotal"] = KeyTagLocale.Keys.Count,
			["keysSeen"] = keysSeen.Length,
			["maxLayoutWidthDifference"] = Math.Round(render.MaxWidthDifferenceSeen, 2),
			["unseenByPrefix"] = unseen.GroupBy(Prefix).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
				.ToDictionary(g => g.Key, g => g.Count()),
			["unseen"] = unseen,
			["untagged"] = render.Shots.ToDictionary(s => s.Shot.Id, s => s.Untagged)
		};
		File.WriteAllText(Path.Combine(outDir, "coverage.json"), JsonSerializer.Serialize(coverage, Json));
	}

	public static string Prefix(string key) => key.Contains('.') ? key[..key.IndexOf('.')] : key;

	private static Dictionary<string, object> Item(ContextItem i) => new()
	{
		["key"] = i.Key,
		["x"] = Math.Round(i.Bounds.X, 1),
		["y"] = Math.Round(i.Bounds.Y, 1),
		["w"] = Math.Round(i.Bounds.Width, 1),
		["h"] = Math.Round(i.Bounds.Height, 1),
		["size"] = i.Size,
		["weight"] = i.Weight,
		["wrap"] = i.Wrap,
		["align"] = i.Align,
		["maxW"] = i.MaxW,
		["maxLines"] = i.MaxLines,
		["lineHeight"] = i.LineHeight,
		["trim"] = i.Trim,
		["formatted"] = i.Formatted,
		["composite"] = i.Composite
	};
}
