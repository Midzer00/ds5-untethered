// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Kopru.Core.Localization;

internal readonly struct TextArg
{
	private enum Kind : byte
	{
		String,
		Int64,
		Double,
		Single,
		Formattable
	}

	public string Name { get; }
	public string? Format { get; }
	private readonly Kind _kind;
	private readonly long _int;
	private readonly double _double;
	private readonly object? _obj;

	public TextArg(string name, string? value)
	{
		Name = name;
		Format = null;
		_kind = Kind.String;
		_obj = value ?? string.Empty;
	}

	public TextArg(string name, long value, string? format)
	{
		Name = name;
		Format = format;
		_kind = Kind.Int64;
		_int = value;
	}

	public TextArg(string name, double value, string? format)
	{
		Name = name;
		Format = format;
		_kind = Kind.Double;
		_double = value;
	}

	public TextArg(string name, float value, string? format)
	{
		Name = name;
		Format = format;
		_kind = Kind.Single;
		_double = value;
	}

	public TextArg(string name, IFormattable value, string? format)
	{
		Name = name;
		Format = format;
		_kind = Kind.Formattable;
		_obj = value;
	}

	public bool IsNumeric => _kind is Kind.Int64 or Kind.Double or Kind.Single;

	public string FormatValue(CultureInfo culture) => _kind switch
	{
		Kind.String => (string)_obj!,
		Kind.Int64 => _int.ToString(Format, culture),
		Kind.Double => _double.ToString(Format, culture),
		Kind.Single => ((float)_double).ToString(Format, culture),
		Kind.Formattable => ((IFormattable)_obj!).ToString(Format, culture),
		_ => string.Empty
	};
}

// Parser and evaluator for the ICU MessageFormat subset used in localization JSON files:
//   - named arguments: {count}, {name}
//   - plurals: {count, plural, =0 {...} one {# game} few {...} many {...} other {# games}}
//   - '#' inside plural branches for the formatted count
//   - ICU 4.8+ DOUBLE_OPTIONAL apostrophe escaping ('{' '}' '#' and '')
internal sealed class IcuMessage
{
	private abstract class Node;
	private sealed class LiteralNode(string text) : Node { public string Text { get; } = text; }
	private sealed class PoundNode : Node { public static readonly PoundNode Instance = new(); }
	private sealed class ArgNode(string name) : Node { public string Name { get; } = name; }

	private sealed class PluralBranch(double? exact, PluralCategory? category, Node[] nodes)
	{
		public double? Exact { get; } = exact;
		public PluralCategory? Category { get; } = category;
		public Node[] Nodes { get; } = nodes;
	}

	private sealed class PluralNode(string name, PluralBranch[] branches, PluralBranch other) : Node
	{
		public string Name { get; } = name;
		public PluralBranch[] Branches { get; } = branches;
		public PluralBranch Other { get; } = other;
	}

	private readonly Node[] _nodes;
	private readonly string? _plainLiteral;

	public IReadOnlySet<string> Placeholders { get; }

	private IcuMessage(Node[] nodes, HashSet<string> placeholders)
	{
		_nodes = nodes;
		Placeholders = placeholders;
		if (nodes.Length == 0)
		{
			_plainLiteral = string.Empty;
		}
		else if (nodes is [LiteralNode lit])
		{
			_plainLiteral = lit.Text;
		}
	}

	public static bool TryParse(string pattern, out IcuMessage? message, out string? error)
	{
		message = null;
		if (pattern == null)
		{
			error = "null pattern";
			return false;
		}
		var placeholders = new HashSet<string>(StringComparer.Ordinal);
		int index = 0;
		if (!TryParseNodes(pattern, ref index, inPlural: false, stopAtCloseBrace: false, placeholders, out List<Node>? nodes, out error))
		{
			return false;
		}
		message = new IcuMessage(nodes!.ToArray(), placeholders);
		return true;
	}

	public bool TryFormat(string languageCode, CultureInfo culture, ReadOnlySpan<TextArg> args, out string result)
	{
		if (_plainLiteral != null)
		{
			result = _plainLiteral;
			return true;
		}
		var sb = new StringBuilder();
		if (!TryAppendNodes(_nodes, languageCode, culture, args, poundValue: null, sb))
		{
			result = string.Empty;
			return false;
		}
		result = sb.ToString();
		return true;
	}

	private static bool TryAppendNodes(
		Node[] nodes,
		string languageCode,
		CultureInfo culture,
		ReadOnlySpan<TextArg> args,
		string? poundValue,
		StringBuilder sb)
	{
		foreach (Node node in nodes)
		{
			switch (node)
			{
				case LiteralNode lit:
					sb.Append(lit.Text);
					break;
				case PoundNode:
					if (poundValue == null)
					{
						return false;
					}
					sb.Append(poundValue);
					break;
				case ArgNode arg:
					if (!TryFindArg(args, arg.Name, out TextArg found))
					{
						return false;
					}
					sb.Append(found.FormatValue(culture));
					break;
				case PluralNode plural:
					if (!TryFindArg(args, plural.Name, out TextArg numArg) || !numArg.IsNumeric)
					{
						return false;
					}
					string inv = numArg.FormatValue(CultureInfo.InvariantCulture);
					if (!PluralOperands.TryParse(inv, out PluralOperands operands))
					{
						return false;
					}
					string formatted = numArg.FormatValue(culture);
					PluralBranch chosen = SelectBranch(plural, languageCode, operands);
					if (!TryAppendNodes(chosen.Nodes, languageCode, culture, args, formatted, sb))
					{
						return false;
					}
					break;
			}
		}
		return true;
	}

	private static PluralBranch SelectBranch(PluralNode plural, string languageCode, PluralOperands operands)
	{
		foreach (PluralBranch b in plural.Branches)
		{
			if (b.Exact is { } exact && operands.N == exact)
			{
				return b;
			}
		}
		PluralCategory cat = PluralRules.Select(languageCode, operands);
		foreach (PluralBranch b in plural.Branches)
		{
			if (b.Category == cat)
			{
				return b;
			}
		}
		return plural.Other;
	}

	private static bool TryFindArg(ReadOnlySpan<TextArg> args, string name, out TextArg found)
	{
		for (int i = 0; i < args.Length; i++)
		{
			if (string.Equals(args[i].Name, name, StringComparison.Ordinal))
			{
				found = args[i];
				return true;
			}
		}
		found = default;
		return false;
	}

	private static bool TryParseNodes(
		string pattern,
		ref int i,
		bool inPlural,
		bool stopAtCloseBrace,
		HashSet<string> placeholders,
		out List<Node>? nodes,
		out string? error)
	{
		var list = new List<Node>();
		nodes = list;
		error = null;
		var literal = new StringBuilder();
		bool inQuote = false;

		void FlushLiteral()
		{
			if (literal.Length > 0)
			{
				list.Add(new LiteralNode(literal.ToString()));
				literal.Clear();
			}
		}

		while (i < pattern.Length)
		{
			char c = pattern[i];
			if (inQuote)
			{
				if (c == '\'')
				{
					if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
					{
						literal.Append('\'');
						i += 2;
					}
					else
					{
						inQuote = false;
						i++;
					}
				}
				else
				{
					literal.Append(c);
					i++;
				}
				continue;
			}

			if (c == '\'')
			{
				if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
				{
					literal.Append('\'');
					i += 2;
					continue;
				}
				if (i + 1 < pattern.Length && (pattern[i + 1] is '{' or '}' || (inPlural && pattern[i + 1] == '#')))
				{
					inQuote = true;
					i++;
					continue;
				}
				literal.Append('\'');
				i++;
				continue;
			}

			if (c == '}' && stopAtCloseBrace)
			{
				i++; // consume '}'
				FlushLiteral();
				return true;
			}

			if (c == '}')
			{
				error = $"Unexpected '}}' at position {i}.";
				return false;
			}

			if (inPlural && c == '#')
			{
				FlushLiteral();
				nodes.Add(PoundNode.Instance);
				i++;
				continue;
			}

			if (c == '{')
			{
				FlushLiteral();
				i++; // consume '{'
				if (!TryParseHole(pattern, ref i, placeholders, out Node? holeNode, out error))
				{
					return false;
				}
				nodes.Add(holeNode!);
				continue;
			}

			literal.Append(c);
			i++;
		}

		if (inQuote)
		{
			error = "Unclosed single-quoted literal.";
			return false;
		}
		if (stopAtCloseBrace)
		{
			error = "Missing closing '}' in plural branch.";
			return false;
		}

		FlushLiteral();
		return true;
	}

	private static bool TryParseHole(
		string pattern,
		ref int i,
		HashSet<string> placeholders,
		out Node? node,
		out string? error)
	{
		node = null;
		error = null;
		SkipSpaces(pattern, ref i);
		string? name = ReadIdentifier(pattern, ref i);
		if (string.IsNullOrEmpty(name))
		{
			error = $"Expected placeholder identifier at position {i}.";
			return false;
		}
		SkipSpaces(pattern, ref i);

		if (i >= pattern.Length)
		{
			error = $"Unclosed placeholder '{{{name}'.";
			return false;
		}

		if (pattern[i] == '}')
		{
			i++;
			placeholders.Add(name);
			node = new ArgNode(name);
			return true;
		}

		if (pattern[i] != ',')
		{
			error = $"Expected '}}' or ',' after placeholder '{name}' at position {i}.";
			return false;
		}
		i++; // consume ','
		SkipSpaces(pattern, ref i);
		string? kind = ReadIdentifier(pattern, ref i);
		if (kind != "plural")
		{
			error = $"Unsupported placeholder format '{kind}' on '{name}' (only 'plural' is supported).";
			return false;
		}
		SkipSpaces(pattern, ref i);
		if (i >= pattern.Length || pattern[i] != ',')
		{
			error = $"Expected ',' after 'plural' in '{name}'.";
			return false;
		}
		i++; // consume ','

		placeholders.Add(name);
		var branches = new List<PluralBranch>();
		var seenCategories = new HashSet<PluralCategory>();
		var seenExacts = new HashSet<double>();
		PluralBranch? otherBranch = null;

		while (true)
		{
			SkipSpaces(pattern, ref i);
			if (i >= pattern.Length)
			{
				error = $"Unclosed plural block for '{name}'.";
				return false;
			}
			if (pattern[i] == '}')
			{
				i++;
				break;
			}

			double? exact = null;
			PluralCategory? cat = null;
			if (pattern[i] == '=')
			{
				i++;
				int numStart = i;
				while (i < pattern.Length && (char.IsAsciiDigit(pattern[i]) || pattern[i] == '.'))
				{
					i++;
				}
				if (i == numStart || !double.TryParse(pattern.AsSpan(numStart, i - numStart), NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
				{
					error = $"Invalid '=N' plural selector in '{name}'.";
					return false;
				}
				if (!seenExacts.Add(val))
				{
					error = $"Duplicate '={val}' plural selector in '{name}'.";
					return false;
				}
				exact = val;
			}
			else
			{
				string? selector = ReadIdentifier(pattern, ref i);
				cat = selector switch
				{
					"zero" => PluralCategory.Zero,
					"one" => PluralCategory.One,
					"two" => PluralCategory.Two,
					"few" => PluralCategory.Few,
					"many" => PluralCategory.Many,
					"other" => PluralCategory.Other,
					_ => null
				};
				if (cat == null)
				{
					error = $"Unknown plural category '{selector}' in '{name}'.";
					return false;
				}
				if (!seenCategories.Add(cat.Value))
				{
					error = $"Duplicate plural category '{selector}' in '{name}'.";
					return false;
				}
			}

			SkipSpaces(pattern, ref i);
			if (i >= pattern.Length || pattern[i] != '{')
			{
				error = $"Expected '{{' after plural selector in '{name}' at position {i}.";
				return false;
			}
			i++; // consume '{'
			if (!TryParseNodes(pattern, ref i, inPlural: true, stopAtCloseBrace: true, placeholders, out List<Node>? branchNodes, out error))
			{
				return false;
			}

			var branch = new PluralBranch(exact, cat, branchNodes!.ToArray());
			branches.Add(branch);
			if (cat == PluralCategory.Other)
			{
				otherBranch = branch;
			}
		}

		if (otherBranch == null)
		{
			error = $"Plural block for '{name}' is missing required 'other' branch.";
			return false;
		}

		node = new PluralNode(name, branches.ToArray(), otherBranch);
		return true;
	}

	private static void SkipSpaces(string s, ref int i)
	{
		while (i < s.Length && char.IsWhiteSpace(s[i]))
		{
			i++;
		}
	}

	private static string? ReadIdentifier(string s, ref int i)
	{
		int start = i;
		if (i < s.Length && (char.IsAsciiLetter(s[i]) || s[i] == '_'))
		{
			i++;
			while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_'))
			{
				i++;
			}
			return s[start..i];
		}
		return null;
	}
}
