// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class KopruI18nExtract
{
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	// Explicit suffixes for multi-Pick methods whose branches aren't simple single enum member switch arms.
	private static readonly Dictionary<(string Method, int Index), string> ExplicitSuffixes = new()
	{
		[("ControllerSpeakerDevice", 0)] = ".Slot",
		[("ControllerSpeakerDevice", 1)] = "",
		[("ControllerMicDevice", 0)] = ".Slot",
		[("ControllerMicDevice", 1)] = "",
		[("MicToastTitle", 0)] = ".On",
		[("MicToastTitle", 1)] = ".Muted",
		[("MicToastTitle", 2)] = ".Muted",
		[("MicToastTitle", 3)] = ".Off",
		[("MicToastBody", 0)] = ".Pc",
		[("MicToastBody", 1)] = ".LowPower",
		[("MicToastBody", 2)] = ".Controller",
		[("SteamInputAdvice", 4)] = ".Unknown",
		[("DsxStatus", 0)] = ".PortInUse",
		[("DsxStatus", 1)] = ".Failed",
		[("DsxStatus", 2)] = ".Off",
		[("DsxStatus", 3)] = ".ActiveSeconds",
		[("DsxStatus", 4)] = ".ActiveMinutes",
		[("DsxStatus", 5)] = ".Listening",
		[("CurveName", 3)] = ".Linear",
		[("InstalledFrom", 4)] = ".OpenedBefore",
		[("DesktopRightClickOption", 2)] = ".Off",
		[("DesktopTouchpadHint", 0)] = ".RightSide",
		[("DesktopTouchpadHint", 1)] = ".TwoFingers",
		[("DesktopTouchpadHint", 2)] = ".Off",
		[("DesktopTouchpadHint", 3)] = "",
		[("DesktopActionName", 23)] = ".None",
		[("GyroTargetName", 1)] = ".RightStick",
		[("ButtonName", 12)] = ".None",
		[("GyroModeName", 3)] = ".Off",
		[("GyroCalibrationResultText", 2)] = ".NoData",
		[("GameListInfo", 0)] = ".Updated",
		[("GameListInfo", 1)] = "",
	};

	public static int Main(string[] args)
	{
		if (args.Length >= 1 && args[0] == "snapshot")
		{
			if (args.Length != 3)
			{
				Console.Error.WriteLine("Usage: Kopru.I18nExtract snapshot <Kopru.Core.dll> <output.txt>");
				return 1;
			}
			Snapshot(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
			return 0;
		}
		if (args.Length >= 1 && args[0] == "verify")
		{
			if (args.Length != 3)
			{
				Console.Error.WriteLine("Usage: Kopru.I18nExtract verify <before.txt> <after.txt>");
				return 1;
			}
			return Verify(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
		}

		string repo = FindRepoRoot(args.Length >= 2 && args[0] == "--repo" ? args[1] : Directory.GetCurrentDirectory());
		return Extract(repo);
	}

	private static string FindRepoRoot(string start)
	{
		string? dir = Path.GetFullPath(start);
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException($"Could not find Kopru.sln above '{start}'.");
	}

	private static int Extract(string repo)
	{
		string locDir = Path.Combine(repo, "src", "Kopru.Core", "Localization");
		string stringsDir = Path.Combine(locDir, "strings");
		Directory.CreateDirectory(stringsDir);

		string enPath = Path.Combine(stringsDir, "en.json");
		string trPath = Path.Combine(stringsDir, "tr.json");
		SortedDictionary<string, string> enStrings = LoadJson(enPath, "English");
		SortedDictionary<string, string> trStrings = LoadJson(trPath, "Türkçe");

		string[] files = Directory.GetFiles(locDir, "Text*.cs").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

		// First pass: find overloaded method names across all Text*.cs files (both already-converted and new).
		var methodCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		var parsedFiles = new List<(string Path, string OriginalText, SyntaxTree Tree)>();
		foreach (string file in files)
		{
			string source = File.ReadAllText(file, Encoding.UTF8);
			SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: file);
			parsedFiles.Add((file, source, tree));
			foreach (MethodDeclarationSyntax method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
			{
				if (IsPublicTextMethod(method))
				{
					string name = method.Identifier.ValueText;
					methodCounts[name] = methodCounts.GetValueOrDefault(name) + 1;
				}
			}
		}

		int convertedMethods = 0;
		int convertedPicks = 0;
		int modifiedFiles = 0;

		foreach ((string file, string originalSource, SyntaxTree tree) in parsedFiles)
		{
			CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
			var replacements = new Dictionary<SyntaxNode, string>();

			// Remove the private En(...) helper if still present in Text.cs.
			foreach (MethodDeclarationSyntax helper in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
			{
				if (helper.Identifier.ValueText == "En" && helper.Modifiers.Any(SyntaxKind.PrivateKeyword))
				{
					replacements[helper] = string.Empty;
				}
			}

			foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
			{
				if (!IsPublicTextMethod(method))
				{
					continue;
				}

				List<InvocationExpressionSyntax> picks = method.DescendantNodes()
					.OfType<InvocationExpressionSyntax>()
					.Where(IsPickCall)
					.ToList();
				if (picks.Count == 0)
				{
					continue;
				}

				convertedMethods++;
				bool isOverloaded = methodCounts[method.Identifier.ValueText] > 1;
				string baseKey = DeriveBaseKey(method, isOverloaded);

				// Special case: `param == 1 ? Pick(l, $"1 ...", $"... 1 ...") : Pick(l, $"{param} ...", $"... {param} ...")`
				if (picks.Count == 2 && method.ExpressionBody?.Expression is ConditionalExpressionSyntax cond
					&& TryConvertSingularTernary(method, cond, baseKey, out string? singleCall, out string? trIcu, out string? enIcu))
				{
					enStrings[baseKey] = enIcu!;
					trStrings[baseKey] = trIcu!;
					replacements[cond] = singleCall!;
					convertedPicks += 2;
					continue;
				}

				for (int i = 0; i < picks.Count; i++)
				{
					InvocationExpressionSyntax pick = picks[i];
					string key = picks.Count == 1
						? baseKey
						: baseKey + DeriveSuffix(method, pick, i, file);

					(string callCode, string trPattern, string enPattern) = ConvertPick(method, pick, key);
					if (enStrings.TryGetValue(key, out string? existingEn) && existingEn != enPattern && picks.Count > 1)
					{
						// Keep intentional duplicate keys only when both en and tr match.
						if (!trStrings.TryGetValue(key, out string? existingTr) || existingTr != trPattern)
						{
							throw new InvalidOperationException($"Key collision on '{key}' in {Path.GetFileName(file)}: '{existingEn}' vs '{enPattern}'.");
						}
					}
					enStrings[key] = enPattern;
					trStrings[key] = trPattern;
					replacements[pick] = callCode;
					convertedPicks++;
				}
			}

			if (replacements.Count == 0)
			{
				continue;
			}

			string updated = ApplySpanReplacements(originalSource, replacements);
			if (updated != originalSource)
			{
				File.WriteAllText(file, updated, Utf8NoBom);
				modifiedFiles++;
			}
		}

		WriteJson(enPath, enStrings);
		WriteJson(trPath, trStrings);

		Console.WriteLine($"Extracted {convertedPicks} Pick calls in {convertedMethods} methods across {modifiedFiles} files.");
		Console.WriteLine($"en.json: {enStrings.Count - 1} keys, tr.json: {trStrings.Count - 1} keys.");
		return 0;
	}

	private static bool IsPublicTextMethod(MethodDeclarationSyntax method) =>
		method.Modifiers.Any(SyntaxKind.PublicKeyword)
		&& method.Modifiers.Any(SyntaxKind.StaticKeyword)
		&& method.Parent is ClassDeclarationSyntax cls
		&& cls.Identifier.ValueText == "Text";

	private static bool IsPickCall(InvocationExpressionSyntax invocation) =>
		invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "Pick" }
		&& invocation.ArgumentList.Arguments.Count == 3;

	public static string DeriveBaseKey(MethodDeclarationSyntax method, bool isOverloaded)
	{
		string name = method.Identifier.ValueText;
		if (isOverloaded)
		{
			ParameterSyntax? firstValueParam = method.ParameterList.Parameters
				.FirstOrDefault(p => p.Type?.ToString() is not ("Language?" or "Language"));
			if (firstValueParam != null)
			{
				string pName = firstValueParam.Identifier.ValueText;
				name += char.ToUpperInvariant(pName[0]) + pName[1..];
			}
		}
		return SplitFirstWord(name);
	}

	public static string SplitFirstWord(string name)
	{
		// Standard PascalCase: FirstWord + Rest (e.g. CableModeLabel -> Cable.ModeLabel, LiveLast60s -> Live.Last60s).
		int i = 1;
		while (i < name.Length && (char.IsLower(name[i]) || char.IsDigit(name[i])))
		{
			i++;
		}
		if (i > 1 && i < name.Length)
		{
			return name[..i] + "." + name[i..];
		}

		// Acronym prefix: e.g. PSButton -> PS.Button.
		if (name.Length >= 3 && char.IsUpper(name[0]) && char.IsUpper(name[1]))
		{
			int upperEnd = 1;
			while (upperEnd < name.Length && char.IsUpper(name[upperEnd]))
			{
				upperEnd++;
			}
			if (upperEnd > 1 && upperEnd < name.Length)
			{
				return name[..(upperEnd - 1)] + "." + name[(upperEnd - 1)..];
			}
		}

		return "Common." + name;
	}

	private static string DeriveSuffix(MethodDeclarationSyntax method, InvocationExpressionSyntax pick, int index, string file)
	{
		string methodName = method.Identifier.ValueText;
		if (ExplicitSuffixes.TryGetValue((methodName, index), out string? explicitSuffix))
		{
			return explicitSuffix;
		}

		SwitchExpressionArmSyntax? arm = pick.Ancestors().OfType<SwitchExpressionArmSyntax>().FirstOrDefault();
		if (arm != null)
		{
			string? enumMember = TryGetSingleEnumMember(arm.Pattern);
			if (enumMember != null)
			{
				return "." + enumMember;
			}
			if (arm.Pattern is DiscardPatternSyntax)
			{
				return ".Default";
			}
		}

		int line = pick.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
		string fallback = $".{index + 1}";
		Console.WriteLine($"NOTE: numbered key suffix '{fallback}' for {methodName} at {Path.GetFileName(file)}:{line} (rename if desired).");
		return fallback;
	}

	private static string? TryGetSingleEnumMember(PatternSyntax pattern)
	{
		if (pattern is ConstantPatternSyntax { Expression: MemberAccessExpressionSyntax member })
		{
			return member.Name.Identifier.ValueText;
		}
		return null;
	}

	// Converts `count == 1 ? Pick(l, $"1 oyunda kullanılıyor", $"Used by 1 game") : Pick(l, $"{count} oyunda kullanılıyor", $"Used by {count} games")`
	// into a single ICU plural key so other languages can use full CLDR plural forms.
	private static bool TryConvertSingularTernary(
		MethodDeclarationSyntax method,
		ConditionalExpressionSyntax cond,
		string key,
		out string? callCode,
		out string? trIcu,
		out string? enIcu)
	{
		callCode = trIcu = enIcu = null;
		if (cond.Condition is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } eq)
		{
			return false;
		}
		if (eq.Left is not IdentifierNameSyntax paramId || eq.Right is not LiteralExpressionSyntax { Token.ValueText: "1" })
		{
			return false;
		}
		if (cond.WhenTrue is not InvocationExpressionSyntax truePick || !IsPickCall(truePick)
			|| cond.WhenFalse is not InvocationExpressionSyntax falsePick || !IsPickCall(falsePick))
		{
			return false;
		}

		string varName = paramId.Identifier.ValueText;
		string langExpr = falsePick.ArgumentList.Arguments[0].Expression.ToString();
		(_, string trMany, string enMany) = ConvertPick(method, falsePick, key);
		(_, string trOne, string enOne) = ConvertPick(method, truePick, key);

		string expectedTrOne = trMany.Replace($"{{{varName}}}", "1", StringComparison.Ordinal);
		if (trOne != expectedTrOne || !enOne.Contains('1') || !enMany.Contains($"{{{varName}}}", StringComparison.Ordinal))
		{
			return false;
		}

		string enOneHash = ReplaceStandaloneOne(enOne);
		string enManyHash = enMany.Replace($"{{{varName}}}", "#", StringComparison.Ordinal);
		trIcu = trMany;
		enIcu = $"{{{varName}, plural, one {{{enOneHash}}} other {{{enManyHash}}}}}";
		callCode = $"T({langExpr}, \"{key}\", A(\"{varName}\", {varName}))";
		return true;
	}

	private static string ReplaceStandaloneOne(string s) =>
		Regex.Replace(s, @"\b1\b", "#");

	private sealed record ExtractedHole(string Name, string CodeExpr, string? Format, bool IsEnCall = false, string? OneWord = null, string? OtherWord = null);

	private sealed record Part(bool IsText, string Text, ExtractedHole? Hole);

	private static (string CallCode, string TrIcu, string EnIcu) ConvertPick(
		MethodDeclarationSyntax method,
		InvocationExpressionSyntax pick,
		string key)
	{
		SeparatedSyntaxList<ArgumentSyntax> args = pick.ArgumentList.Arguments;
		string langExpr = args[0].Expression.ToString();
		List<Part> trParts = ParseInterpolated(method, args[1].Expression);
		List<Part> enParts = ParseInterpolated(method, args[2].Expression);

		var collectedArgs = new List<(string Name, string CodeExpr, string? Format)>();
		void RecordArg(ExtractedHole hole)
		{
			if (hole.IsEnCall)
			{
				return;
			}
			int existing = collectedArgs.FindIndex(a => a.Name == hole.Name);
			if (existing < 0)
			{
				collectedArgs.Add((hole.Name, hole.CodeExpr, hole.Format));
			}
			else if (collectedArgs[existing].Format != hole.Format)
			{
				string? mergedFmt = collectedArgs[existing].Format ?? hole.Format;
				collectedArgs[existing] = (hole.Name, hole.CodeExpr, mergedFmt);
			}
		}

		foreach (Part p in enParts.Concat(trParts))
		{
			if (!p.IsText && p.Hole != null)
			{
				RecordArg(p.Hole);
			}
		}

		string trIcu = BuildIcuPattern(trParts);
		string enIcu = BuildIcuPattern(enParts);

		var sb = new StringBuilder();
		sb.Append("T(").Append(langExpr).Append(", \"").Append(key).Append('"');
		foreach ((string name, string codeExpr, string? fmt) in collectedArgs)
		{
			sb.Append(", A(\"").Append(name).Append("\", ").Append(codeExpr);
			if (fmt != null)
			{
				sb.Append(", \"").Append(EscapeCSharpString(fmt)).Append('"');
			}
			sb.Append(')');
		}
		sb.Append(')');
		return (sb.ToString(), trIcu, enIcu);
	}

	private static List<Part> ParseInterpolated(MethodDeclarationSyntax method, ExpressionSyntax expr)
	{
		var parts = new List<Part>();
		if (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
		{
			parts.Add(new Part(true, lit.Token.ValueText, null));
			return parts;
		}
		if (expr is not InterpolatedStringExpressionSyntax interp)
		{
			throw new InvalidOperationException($"Expected string or interpolated string in {method.Identifier.ValueText}, got: {expr}");
		}

		foreach (InterpolatedStringContentSyntax content in interp.Contents)
		{
			switch (content)
			{
				case InterpolatedStringTextSyntax text:
					parts.Add(new Part(true, text.TextToken.ValueText, null));
					break;
				case InterpolationSyntax hole:
					parts.Add(new Part(false, string.Empty, ParseHole(method, hole)));
					break;
				default:
					throw new InvalidOperationException($"Unexpected interpolation content: {content}");
			}
		}
		return parts;
	}

	private static ExtractedHole ParseHole(MethodDeclarationSyntax method, InterpolationSyntax hole)
	{
		string? fmt = hole.FormatClause?.FormatStringToken.ValueText;
		ExpressionSyntax expr = hole.Expression;

		// En(count, "one", "many")
		if (expr is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "En" } } enCall
			&& enCall.ArgumentList.Arguments.Count == 3)
		{
			string countName = ResolveHoleName(method, enCall.ArgumentList.Arguments[0].Expression);
			string oneWord = ((LiteralExpressionSyntax)enCall.ArgumentList.Arguments[1].Expression).Token.ValueText;
			string otherWord = ((LiteralExpressionSyntax)enCall.ArgumentList.Arguments[2].Expression).Token.ValueText;
			return new ExtractedHole(countName, enCall.ArgumentList.Arguments[0].Expression.ToString(), null, true, oneWord, otherWord);
		}

		string name = ResolveHoleName(method, expr);
		return new ExtractedHole(name, expr.ToString(), fmt);
	}

	private static string ResolveHoleName(MethodDeclarationSyntax method, ExpressionSyntax expr)
	{
		if (expr is IdentifierNameSyntax id)
		{
			string raw = id.Identifier.ValueText;
			// Pattern variable `slot is int s` in ControllerSpeakerDevice / ControllerMicDevice -> `slot`
			if (raw == "s" && method.ParameterList.Parameters.Any(p => p.Identifier.ValueText == "slot"))
			{
				return "slot";
			}
			return raw;
		}
		if (expr.ToString().Replace(" ", "") == "seconds/60")
		{
			return "minutes";
		}
		throw new InvalidOperationException($"Unsupported interpolation expression '{expr}' in {method.Identifier.ValueText}. Extract a local variable first.");
	}

	private static string BuildIcuPattern(List<Part> parts)
	{
		var sb = new StringBuilder();
		for (int i = 0; i < parts.Count; i++)
		{
			Part p = parts[i];
			if (p.IsText)
			{
				sb.Append(EscapeIcuLiteral(p.Text, inPlural: false));
				continue;
			}

			ExtractedHole hole = p.Hole!;
			if (hole.IsEnCall)
			{
				throw new InvalidOperationException($"Unpaired En({hole.Name}, ...) call without a preceding {{{hole.Name}}} placeholder.");
			}

			// Look ahead for `<optional literal text>{En(sameVar, one, other)}`
			if (i + 2 < parts.Count && parts[i + 1].IsText && !parts[i + 2].IsText
				&& parts[i + 2].Hole is { IsEnCall: true } enHole && enHole.Name == hole.Name)
			{
				string mid = EscapeIcuLiteral(parts[i + 1].Text, inPlural: true);
				string one = EscapeIcuLiteral(enHole.OneWord!, inPlural: true);
				string other = EscapeIcuLiteral(enHole.OtherWord!, inPlural: true);
				sb.Append('{').Append(hole.Name).Append(", plural, one {#").Append(mid).Append(one)
					.Append("} other {#").Append(mid).Append(other).Append("}}");
				i += 2;
				continue;
			}
			if (i + 1 < parts.Count && !parts[i + 1].IsText
				&& parts[i + 1].Hole is { IsEnCall: true } adjEn && adjEn.Name == hole.Name)
			{
				string one = EscapeIcuLiteral(adjEn.OneWord!, inPlural: true);
				string other = EscapeIcuLiteral(adjEn.OtherWord!, inPlural: true);
				sb.Append('{').Append(hole.Name).Append(", plural, one {#").Append(one)
					.Append("} other {#").Append(other).Append("}}");
				i += 1;
				continue;
			}

			sb.Append('{').Append(hole.Name).Append('}');
		}
		return sb.ToString();
	}

	// ICU 4.8+ DOUBLE_OPTIONAL escaping: single quotes only need doubling before '{', '}', '#' (in plural), or another '''.
	private static string EscapeIcuLiteral(string text, bool inPlural)
	{
		var sb = new StringBuilder(text.Length);
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c is '{' or '}' || (inPlural && c == '#'))
			{
				sb.Append('\'').Append(c).Append('\'');
			}
			else if (c == '\'')
			{
				char? next = i + 1 < text.Length ? text[i + 1] : null;
				if (next is '\'' or '{' or '}' || (inPlural && next == '#'))
				{
					sb.Append("''");
				}
				else
				{
					sb.Append('\'');
				}
			}
			else
			{
				sb.Append(c);
			}
		}
		return sb.ToString();
	}

	private static string EscapeCSharpString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

	private static string ApplySpanReplacements(string source, Dictionary<SyntaxNode, string> replacements)
	{
		var ordered = replacements
			.OrderByDescending(kv => kv.Key.Span.Start)
			.ToList();
		var sb = new StringBuilder(source);
		foreach ((SyntaxNode node, string replacement) in ordered)
		{
			if (replacement.Length == 0 && node is MethodDeclarationSyntax)
			{
				// Remove surrounding blank line(s) and leading trivia cleanly.
				int start = node.FullSpan.Start;
				int end = node.FullSpan.End;
				while (end < sb.Length && sb[end] is '\r' or '\n')
				{
					end++;
				}
				sb.Remove(start, end - start);
			}
			else
			{
				sb.Remove(node.Span.Start, node.Span.Length);
				sb.Insert(node.Span.Start, replacement);
			}
		}
		return sb.ToString();
	}

	private static SortedDictionary<string, string> LoadJson(string path, string nativeName)
	{
		var dict = new SortedDictionary<string, string>(StringComparer.Ordinal)
		{
			["@name"] = nativeName
		};
		if (!File.Exists(path))
		{
			return dict;
		}
		using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
		foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
		{
			if (prop.Value.ValueKind == JsonValueKind.String)
			{
				dict[prop.Name] = prop.Value.GetString()!;
			}
		}
		dict["@name"] = nativeName;
		return dict;
	}

	private static void WriteJson(string path, SortedDictionary<string, string> entries)
	{
		var options = new JsonWriterOptions
		{
			Indented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};
		using var ms = new MemoryStream();
		using (var writer = new Utf8JsonWriter(ms, options))
		{
			writer.WriteStartObject();
			foreach ((string k, string v) in entries)
			{
				writer.WriteString(k, v);
			}
			writer.WriteEndObject();
		}
		string json = Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
		File.WriteAllText(path, json, Utf8NoBom);
	}

	// Snapshot & Verify commands for 100% lossless conversion verification.

	private static void Snapshot(string dllPath, string outPath)
	{
		var alc = new AssemblyLoadContext("snapshot", isCollectible: true);
		string dir = Path.GetDirectoryName(dllPath)!;
		alc.Resolving += (ctx, name) =>
		{
			string candidate = Path.Combine(dir, name.Name + ".dll");
			return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
		};
		try
		{
			Assembly asm = alc.LoadFromAssemblyPath(dllPath);
			Type textType = asm.GetType("Kopru.Core.Localization.Text", throwOnError: true)!;
			Type localizerType = asm.GetType("Kopru.Core.Localization.Localizer", throwOnError: true)!;
			MethodInfo fromCode = localizerType.GetMethod("FromCode", BindingFlags.Public | BindingFlags.Static)!;

			var lines = new List<string>();
			foreach (string code in new[] { "tr", "en" })
			{
				object? langVal = fromCode.Invoke(null, [code]);
				MethodInfo[] methods = textType.GetMethods(BindingFlags.Public | BindingFlags.Static)
					.Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName)
					.OrderBy(m => m.Name, StringComparer.Ordinal)
					.ThenBy(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)), StringComparer.Ordinal)
					.ToArray();

				foreach (MethodInfo m in methods)
				{
					ParameterInfo[] ps = m.GetParameters();
					List<object?[]> sampleSets = ps.Select(p => SamplesFor(p.ParameterType, langVal)).ToList();
					foreach (object?[] callArgs in Cartesian(sampleSets, 0))
					{
						string desc = string.Join("|", callArgs.Select(a => a switch
						{
							null => "null",
							IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
							_ => a.ToString()
						}));
						string result = (string)m.Invoke(null, callArgs)!;
						lines.Add($"{code} :: {m.Name}({ps.Length}) [{desc}] => {result.Replace("\r", "\\r").Replace("\n", "\\n")}");
					}
				}
			}

			Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
			File.WriteAllLines(outPath, lines, Utf8NoBom);
			Console.WriteLine($"Snapshot wrote {lines.Count} lines to {outPath}");
		}
		finally
		{
			alc.Unload();
		}
	}

	private static object?[] SamplesFor(Type type, object? langVal)
	{
		Type? underlying = Nullable.GetUnderlyingType(type);
		if (underlying != null)
		{
			if (underlying.Name == "Language")
			{
				return [langVal];
			}
			if (underlying == typeof(int))
			{
				return [null, 0, 1, 2, 45, 125];
			}
			return new object?[] { null }.Concat(SamplesFor(underlying, langVal)).ToArray();
		}
		if (type.Name == "Language") return [langVal];
		if (type == typeof(string)) return ["x", "2026-11-09"];
		if (type == typeof(double)) return [0.0, 1.0, 2.0, 4.5];
		if (type == typeof(float)) return [1.0f, 4.5f];
		if (type == typeof(int)) return [-5, 0, 1, 2, 5, 65];
		if (type == typeof(long)) return [1L, 4L];
		if (type == typeof(bool)) return [false, true];
		if (type == typeof(TimeSpan)) return [TimeSpan.FromSeconds(90)];
		if (type.IsEnum) return Enum.GetValues(type).Cast<object?>().ToArray();
		throw new NotSupportedException($"No sample values for {type}");
	}

	private static IEnumerable<object?[]> Cartesian(List<object?[]> sets, int index)
	{
		if (index >= sets.Count)
		{
			yield return [];
			yield break;
		}
		foreach (object? head in sets[index])
		{
			foreach (object?[] tail in Cartesian(sets, index + 1))
			{
				var row = new object?[tail.Length + 1];
				row[0] = head;
				Array.Copy(tail, 0, row, 1, tail.Length);
				yield return row;
			}
		}
	}

	private static int Verify(string beforePath, string afterPath)
	{
		string[] before = File.ReadAllLines(beforePath, Encoding.UTF8);
		string[] after = File.ReadAllLines(afterPath, Encoding.UTF8);
		int diffs = 0;
		int max = Math.Max(before.Length, after.Length);
		for (int i = 0; i < max; i++)
		{
			string b = i < before.Length ? before[i] : "<missing>";
			string a = i < after.Length ? after[i] : "<missing>";
			if (b != a)
			{
				if (diffs < 30)
				{
					Console.Error.WriteLine($"DIFF at line {i + 1}:");
					Console.Error.WriteLine($"  BEFORE: {b}");
					Console.Error.WriteLine($"   AFTER: {a}");
				}
				diffs++;
			}
		}
		if (diffs == 0)
		{
			Console.WriteLine($"VERIFY OK: all {before.Length} outputs match identically.");
			return 0;
		}
		Console.Error.WriteLine($"VERIFY FAILED: {diffs} differences out of {max} lines.");
		return 1;
	}
}
