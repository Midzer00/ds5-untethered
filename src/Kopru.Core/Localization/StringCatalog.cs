// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Kopru.Core.Localization;

internal sealed class LanguageBundle
{
	public string Code { get; }
	public string NativeName { get; }
	public IReadOnlyDictionary<string, string> Raw { get; }
	public IReadOnlyDictionary<string, IcuMessage> Messages { get; }

	public LanguageBundle(string code, string nativeName, Dictionary<string, string> raw, Dictionary<string, IcuMessage> messages)
	{
		Code = code;
		NativeName = nativeName;
		Raw = raw;
		Messages = messages;
	}
}

internal static class StringCatalog
{
	public const string ResourcePrefix = "Kopru.Core.Localization.strings.";
	public const string ResourceSuffix = ".json";
	public const string NameMetadataKey = "@name";

	private static readonly Assembly CoreAssembly = typeof(StringCatalog).Assembly;
	private static readonly ConcurrentDictionary<string, LanguageBundle?> Cache = new(StringComparer.OrdinalIgnoreCase);

	// Test hook: temporarily overrides or injects bundles (e.g. to test missing keys or malformed translations).
	private static volatile IReadOnlyDictionary<string, LanguageBundle?>? _testOverrides;

	internal static IDisposable WithOverride(string code, string json)
	{
		LanguageBundle? parsed = ParseBundle(code, json);
		IReadOnlyDictionary<string, LanguageBundle?>? before = _testOverrides;
		var next = before != null
			? new Dictionary<string, LanguageBundle?>(before, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, LanguageBundle?>(StringComparer.OrdinalIgnoreCase);
		next[code] = parsed;
		_testOverrides = next;
		return new Restore(() => _testOverrides = before);
	}

	private sealed class Restore(Action undo) : IDisposable
	{
		public void Dispose() => undo();
	}

	public static IReadOnlyList<string> EmbeddedCodes()
	{
		IEnumerable<string> codes = CoreAssembly.GetManifestResourceNames()
			.Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
			.Select(n => n[ResourcePrefix.Length..^ResourceSuffix.Length]);
		if (_testOverrides != null)
		{
			codes = codes
				.Where(c => !_testOverrides.TryGetValue(c, out LanguageBundle? b) || b != null)
				.Concat(_testOverrides.Where(kv => kv.Value != null).Select(kv => kv.Value!.Code));
		}
		return codes
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public static LanguageBundle? GetBundle(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return null;
		}
		if (_testOverrides != null && _testOverrides.TryGetValue(code, out LanguageBundle? overridden))
		{
			return overridden;
		}
		return Cache.GetOrAdd(code, LoadEmbeddedBundle);
	}

	public static LanguageBundle English => GetBundle("en")
		?? throw new InvalidOperationException("Embedded English string resource (en.json) is missing.");

	public static string Format(Language? language, string key, ReadOnlySpan<TextArg> args)
	{
		Language lang = language ?? Localizer.Current;
		string code = Localizer.Code(lang);
		CultureInfo culture = Localizer.Culture(lang);

		if (PseudoLocale.IsPseudo(code))
		{
			LanguageBundle en = English;
			CultureInfo enCulture = Localizer.Culture(Language.English);
			if (en.Messages.TryGetValue(key, out IcuMessage? enMsg)
				&& enMsg.TryFormat("en", enCulture, args, out string enText))
			{
				return PseudoLocale.Transform(enText);
			}
			return PseudoLocale.Transform(key);
		}

		LanguageBundle? bundle = GetBundle(code);
		if (bundle != null
			&& bundle.Messages.TryGetValue(key, out IcuMessage? msg)
			&& msg.TryFormat(code, culture, args, out string translated))
		{
			return translated;
		}

		if (!string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
		{
			LanguageBundle en = English;
			CultureInfo enCulture = Localizer.Culture(Language.English);
			if (en.Messages.TryGetValue(key, out IcuMessage? enMsg)
				&& enMsg.TryFormat("en", enCulture, args, out string fallback))
			{
				return fallback;
			}
		}

		return key;
	}

	private static LanguageBundle? LoadEmbeddedBundle(string code)
	{
		// Exact case match first, then case-insensitive resource lookup (e.g. "pt-br" -> "pt-BR.json").
		string exactName = ResourcePrefix + code + ResourceSuffix;
		string? match = CoreAssembly.GetManifestResourceNames()
			.FirstOrDefault(n => string.Equals(n, exactName, StringComparison.Ordinal))
			?? CoreAssembly.GetManifestResourceNames()
				.FirstOrDefault(n => string.Equals(n, exactName, StringComparison.OrdinalIgnoreCase));
		if (match == null)
		{
			return null;
		}
		string canonicalCode = match[ResourcePrefix.Length..^ResourceSuffix.Length];
		using Stream? stream = CoreAssembly.GetManifestResourceStream(match);
		if (stream == null)
		{
			return null;
		}
		using var reader = new StreamReader(stream, Encoding.UTF8);
		return ParseBundle(canonicalCode, reader.ReadToEnd());
	}

	internal static LanguageBundle? ParseBundle(string code, string json)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(json, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip
			});
		}
		catch (JsonException)
		{
			return null;
		}

		using (doc)
		{
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			string nativeName = code;
			var raw = new Dictionary<string, string>(StringComparer.Ordinal);
			var messages = new Dictionary<string, IcuMessage>(StringComparer.Ordinal);
			foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
			{
				if (prop.Value.ValueKind != JsonValueKind.String)
				{
					continue;
				}
				string value = prop.Value.GetString() ?? string.Empty;
				if (prop.Name == NameMetadataKey)
				{
					if (!string.IsNullOrWhiteSpace(value))
					{
						nativeName = value.Trim();
					}
					continue;
				}
				raw[prop.Name] = value;
				// Empty or malformed translations are omitted from Messages so Format falls back to English.
				if (!string.IsNullOrWhiteSpace(value) && IcuMessage.TryParse(value, out IcuMessage? parsed, out _))
				{
					messages[prop.Name] = parsed!;
				}
			}
			return new LanguageBundle(code, nativeName, raw, messages);
		}
	}
}
