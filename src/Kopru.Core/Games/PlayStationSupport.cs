// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kopru.Core.Games;

// The PlayStation controller a Steam game's store page says it supports: Steam's categories 57/58 ("DualSense Controller
// Support", USB / Bluetooth) give DualSense; 55/56 ("DualShock Controller Support") without 57/58 give DualShock 4.
public enum PlayStationPad
{
	DualSense,
	DualShock4
}

// Steam app id -> PlayStationPad, from playstation-support.json (embedded; made at build time by tools/Kopru.DetectStudy
// "playstation" from the public Steam store search, see docs/RELEASING.md). Nothing is looked up online at run time.
// File format: { "generated": "yyyy-MM-dd", "source": "...", "ds": [appid, ...], "ds4": [appid, ...] } (sorted; an id in
// both lists counts as DualSense).
public sealed class PlayStationSupport
{
	public const string ResourceName = "Kopru.Core.Games.playstation-support.json";

	private readonly Dictionary<long, PlayStationPad> byApp;

	public PlayStationSupport(IEnumerable<KeyValuePair<long, PlayStationPad>> entries, string? generated = null)
	{
		byApp = [];
		foreach ((long appId, PlayStationPad pad) in entries)
		{
			if (appId > 0 && Enum.IsDefined(pad) && (!byApp.TryGetValue(appId, out PlayStationPad old) || pad == PlayStationPad.DualSense && old != pad))
			{
				byApp[appId] = pad;
			}
		}
		Generated = generated;
	}

	public static PlayStationSupport Empty { get; } = new([]);

	// The date the list was made ("yyyy-MM-dd"), or null.
	public string? Generated { get; }

	public int Count => byApp.Count;

	public int DualSenseCount => byApp.Values.Count(p => p == PlayStationPad.DualSense);

	public int DualShock4Count => byApp.Values.Count(p => p == PlayStationPad.DualShock4);

	public IReadOnlyDictionary<long, PlayStationPad> Entries => byApp;

	// What the store says the app supports, or null (no PlayStation controller support listed, or unknown).
	public PlayStationPad? For(long appId) => byApp.TryGetValue(appId, out PlayStationPad pad) ? pad : null;

	// This list with the entries of other for the apps this one does not know (this one wins where both know an app).
	public PlayStationSupport With(IReadOnlyDictionary<long, PlayStationPad>? other)
	{
		if (other == null || other.Count == 0)
		{
			return this;
		}
		var merged = new Dictionary<long, PlayStationPad>(byApp);
		foreach ((long appId, PlayStationPad pad) in other)
		{
			merged.TryAdd(appId, pad);
		}
		return new PlayStationSupport(merged, Generated);
	}

	// Throws JsonException on malformed JSON.
	public static PlayStationSupport Parse(string json)
	{
		using JsonDocument doc = JsonDocument.Parse(json);
		JsonElement root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException("playstation-support.json: not an object");
		}
		var entries = new List<KeyValuePair<long, PlayStationPad>>();
		Read(root, "ds", PlayStationPad.DualSense, entries);
		Read(root, "ds4", PlayStationPad.DualShock4, entries);
		string? generated = root.TryGetProperty("generated", out JsonElement g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
		return new PlayStationSupport(entries, generated);
	}

	private static void Read(JsonElement root, string name, PlayStationPad pad, List<KeyValuePair<long, PlayStationPad>> into)
	{
		if (!root.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
		{
			return;
		}
		foreach (JsonElement item in list.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long id) && id > 0)
			{
				into.Add(new(id, pad));
			}
		}
	}

	private static PlayStationSupport? embedded;

	// The embedded list (read once); empty when it is missing or unreadable.
	public static PlayStationSupport LoadEmbedded()
	{
		if (embedded != null)
		{
			return embedded;
		}
		PlayStationSupport list;
		try
		{
			using Stream? stream = typeof(PlayStationSupport).Assembly.GetManifestResourceStream(ResourceName);
			if (stream == null)
			{
				list = Empty;
			}
			else
			{
				using StreamReader reader = new(stream);
				list = Parse(reader.ReadToEnd());
			}
		}
		catch (JsonException)
		{
			list = Empty;
		}
		return embedded = list;
	}

	// The file text for these app ids (sorted, an id in both lists kept as DualSense only), 20 ids per line.
	public static string Format(string generated, string source, IEnumerable<long> dualSense, IEnumerable<long> dualShock4)
	{
		var ds = new SortedSet<long>(dualSense.Where(id => id > 0));
		var ds4 = new SortedSet<long>(dualShock4.Where(id => id > 0 && !ds.Contains(id)));
		var text = new StringBuilder();
		text.Append("{\n");
		text.Append("  \"generated\": ").Append(JsonSerializer.Serialize(generated)).Append(",\n");
		text.Append("  \"source\": ").Append(JsonSerializer.Serialize(source)).Append(",\n");
		AppendList(text, "ds", ds);
		text.Append(",\n");
		AppendList(text, "ds4", ds4);
		text.Append("\n}\n");
		return text.ToString();
	}

	private static void AppendList(StringBuilder text, string name, IReadOnlyCollection<long> ids)
	{
		text.Append("  \"").Append(name).Append("\": [");
		int i = 0;
		foreach (long id in ids)
		{
			text.Append(i == 0 ? "\n    " : i % 20 == 0 ? ",\n    " : ", ");
			text.Append(id.ToString(CultureInfo.InvariantCulture));
			i++;
		}
		text.Append(ids.Count == 0 ? "]" : "\n  ]");
	}
}
