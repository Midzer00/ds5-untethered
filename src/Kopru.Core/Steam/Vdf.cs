// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Kopru.Core.Steam;

// One node of Valve's text KeyValues format: either a string value or a set of children. Keys are case-insensitive.
public sealed class VdfNode
{
	public string? Value { get; init; }
	public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

	public VdfNode? this[string key] => Children.TryGetValue(key, out VdfNode? child) ? child : null;

	// Follows a path of keys; null if any step is missing.
	public VdfNode? Get(params string[] path)
	{
		VdfNode? node = this;
		foreach (string key in path)
		{
			node = node?[key];
		}
		return node;
	}

	public string? GetValue(params string[] path) => Get(path)?.Value;
}

public sealed class VdfFormatException(string message) : Exception(message);

// Minimal parser for text VDF: "key" "value", "key" { ... }, // comments, \" \\ \n \t escapes,
// unquoted tokens, and [$CONDITION] suffixes (ignored). Duplicate keys: the last one wins.
public static class Vdf
{
	public static VdfNode Parse(string text)
	{
		int pos = 0;
		VdfNode root = ParseBlock(text, ref pos, topLevel: true);
		return root;
	}

	private static VdfNode ParseBlock(string text, ref int pos, bool topLevel)
	{
		var node = new VdfNode();
		while (true)
		{
			string? key = NextToken(text, ref pos, out bool keyQuoted);
			if (key is null)
			{
				if (!topLevel)
				{
					throw new VdfFormatException("Unexpected end of file; missing '}'.");
				}
				return node;
			}
			if (key == "}" && !keyQuoted)
			{
				if (topLevel)
				{
					throw new VdfFormatException("Unexpected '}'.");
				}
				return node;
			}
			string? value = NextToken(text, ref pos, out bool valueQuoted);
			if (value is null)
			{
				throw new VdfFormatException("Missing value for key '" + key + "'.");
			}
			if (value == "{" && !valueQuoted)
			{
				node.Children[key] = ParseBlock(text, ref pos, topLevel: false);
			}
			else
			{
				node.Children[key] = new VdfNode { Value = value };
			}
		}
	}

	// Returns null at end of input. '{' and '}' are single tokens when unquoted.
	private static string? NextToken(string text, ref int pos, out bool quoted)
	{
		quoted = false;
		while (true)
		{
			while (pos < text.Length && char.IsWhiteSpace(text[pos]))
			{
				pos++;
			}
			if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
			{
				while (pos < text.Length && text[pos] != '\n')
				{
					pos++;
				}
				continue;
			}
			if (pos < text.Length && text[pos] == '[')
			{
				// Conditional like [$WIN32]; skip it.
				while (pos < text.Length && text[pos] != ']')
				{
					pos++;
				}
				pos++;
				continue;
			}
			break;
		}
		if (pos >= text.Length)
		{
			return null;
		}
		char c = text[pos];
		if (c is '{' or '}')
		{
			pos++;
			return c.ToString();
		}
		var sb = new StringBuilder();
		if (c == '"')
		{
			quoted = true;
			pos++;
			while (true)
			{
				if (pos >= text.Length)
				{
					throw new VdfFormatException("Unterminated string.");
				}
				char ch = text[pos++];
				if (ch == '"')
				{
					return sb.ToString();
				}
				if (ch == '\\' && pos < text.Length)
				{
					char e = text[pos++];
					sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
					continue;
				}
				sb.Append(ch);
			}
		}
		while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('"' or '{' or '}'))
		{
			sb.Append(text[pos++]);
		}
		return sb.ToString();
	}
}
