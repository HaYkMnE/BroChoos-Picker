using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace BroChoos.Architecture.Tests;

public sealed partial class ForbiddenApiTests
{
	private static readonly Regex CoreForbiddenApiPattern = MyRegex();

	private static readonly Regex PickerForbiddenBlockingPattern = PickerForbiddenBlockingRegex();

	private static readonly Regex CoreSnapshotsForbiddenPlatformPattern = MyRegex();

	private static readonly Regex NetworkingApiPattern = NetworkingApiRegex();

	private static readonly Regex NetworkNativeLibraryNamePattern = NetworkNativeLibraryNameRegex();

	private static readonly Regex NativeLibraryLoadPattern = NativeLibraryLoadRegex();

	[Fact]
	public void Core_DoesNotUse_ForbiddenPlatformNamespaces()
	{
		foreach (string sourceFile in EnumerateCsFilesUnder("src", "BroChoos.Core"))
		{
			string sourceText = File.ReadAllText(sourceFile);
			_ = CoreForbiddenApiPattern.IsMatch(sourceText)
				.Should()
				.BeFalse($"Core must stay platform-neutral. Forbidden API found in {sourceFile}");
		}
	}

	[Fact]
	public void PickerNamespace_DoesNotUse_SynchronousDiskRegistryOrNetworkApis()
	{
		foreach (string sourceFile in EnumerateCsFilesUnder("src", "BroChoos.App", "Picker"))
		{
			string sourceText = File.ReadAllText(sourceFile);
			_ = PickerForbiddenBlockingPattern.IsMatch(sourceText)
				.Should()
				.BeFalse($"Picker first-frame path must not add sync disk/registry/network APIs. Forbidden usage in {sourceFile}");
		}
	}

	[Fact]
	public void CoreSnapshots_DoesNotUse_PlatformOrBlockingApis()
	{
		foreach (string sourceFile in EnumerateCsFilesUnder("src", "BroChoos.Core", "Snapshots"))
		{
			string sourceText = File.ReadAllText(sourceFile);
			_ = CoreSnapshotsForbiddenPlatformPattern.IsMatch(sourceText)
				.Should()
				.BeFalse($"Core snapshots must stay platform-neutral and non-blocking. Forbidden usage in {sourceFile}");
		}
	}

	// NETWORK CODE LIVES ONLY IN THE NETWORKHELPER PROCESS. Every other src project (Core, App,
	// Platform.Windows and the other helpers) is scanned. Named pipes (System.IO.Pipes, used by the
	// single-instance guard and helper IPC) are local IPC, not networking, and are not matched.
	//
	// The source is tokenized by Roslyn (the compiler's own lexer), not by a hand-written one: a
	// home-made lexer mis-read string literals nested in interpolation holes, took the "//" in
	// $"{(a ? "http://" : "")}" for a comment and blanked real code, so the guard failed OPEN.
	// Comments, doc comments, directives and inactive #if text are blanked; string literals stay.
	[Fact]
	public void SourceOutsideNetworkHelper_DoesNotUse_NetworkingApis()
	{
		List<string> violations = [];
		foreach (string sourceFile in EnumerateCsFilesOutsideNetworkHelper())
		{
			violations.AddRange(FindNetworkingViolations(File.ReadAllText(sourceFile), RelativeToRepository(sourceFile)));
		}

		_ = violations.Should().BeEmpty("network code must live only in BroChoos.NetworkHelper");
	}

	// Same boundary, native half: P/Invoke or a dynamic load of a Windows network DLL bypasses
	// every managed check above. On the Roslyn token stream:
	//   1. no string VALUE names a network DLL (bare or ".dll", any case). Values are read from
	//      every regular, verbatim, raw, UTF-8 and interpolated-text token, and also from
	//      constant-folded "a" + "b" and $"{"a"}b" expressions, so a name split across literal
	//      parts is still seen;
	//   2. no NativeLibrary and no LoadLibrary* identifier, and neither name in a string value
	//      (EntryPoint = "LoadLibraryW"), because a library name can be assembled at run time
	//      where rule 1 cannot see it.
	// The single-instance guard and every other src P/Invoke use kernel32/user32/shell32/
	// shcore/gdi32 by static DllImport only; none loads a library dynamically.
	[Fact]
	public void SourceOutsideNetworkHelper_DoesNotImport_NativeNetworkDlls()
	{
		List<string> violations = [];
		foreach (string sourceFile in EnumerateCsFilesOutsideNetworkHelper())
		{
			violations.AddRange(FindNativeLibraryViolations(File.ReadAllText(sourceFile), RelativeToRepository(sourceFile)));
		}

		_ = violations.Should().BeEmpty("native network libraries must be used only in BroChoos.NetworkHelper");
	}

	// FAIL-CLOSED. A file Roslyn cannot parse cleanly could hide code in skipped or mis-nested
	// tokens, so a syntax error in any scanned configuration is itself a failure of the guard.
	[Fact]
	public void SourceOutsideNetworkHelper_TokenizesWithoutSyntaxErrors()
	{
		List<string> errors = [];
		foreach (string sourceFile in EnumerateCsFilesOutsideNetworkHelper())
		{
			foreach (SyntaxTree tree in ParseAllConfigurations(File.ReadAllText(sourceFile), RelativeToRepository(sourceFile)))
			{
				errors.AddRange(tree.GetDiagnostics()
					.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
					.Select(diagnostic => diagnostic.ToString()));
			}
		}

		_ = errors.Should().BeEmpty("the network-boundary scan is only as good as its tokenization");
	}

	// FAIL-CLOSED on the preprocessor. ScanConfigurations parses each file under all four
	// DEBUG/TRACE combinations, so code under any OTHER symbol (WINDOWS, RELEASE,
	// NET10_0_OR_GREATER, a custom one) would be inactive - and therefore unscanned - in every pass. A literal condition
	// (#if false) hides code the same way. Either one fails here; to allow a new symbol, add a
	// parse configuration for it in ScanConfigurations first, then extend AllowedConditionSymbols.
	[Fact]
	public void SourceOutsideNetworkHelper_ConditionalDirectives_UseOnlyScannedSymbols()
	{
		List<string> violations = [];
		foreach (string sourceFile in EnumerateCsFilesOutsideNetworkHelper())
		{
			violations.AddRange(FindUnscannedConditionalSymbols(File.ReadAllText(sourceFile), RelativeToRepository(sourceFile)));
		}

		_ = violations.Should().BeEmpty("code under a symbol the scan does not toggle is never checked");
	}

	[Theory]
	[InlineData("#if DEBUG\nint a = 1;\n#endif")]
	[InlineData("#if !DEBUG && TRACE\nint a = 1;\n#elif DEBUG || TRACE\nint b = 1;\n#endif")]
	[InlineData("#if (DEBUG)\nint a = 1;\n#else\nint b = 1;\n#endif")]
	public void FindUnscannedConditionalSymbols_DebugAndTraceOnly_Pass(string snippet)
	{
		_ = FindUnscannedConditionalSymbols(WrapSnippet(snippet)).Should().BeEmpty();
	}

	[Theory]
	[InlineData("#if WINDOWS\nint a = 1;\n#endif", "WINDOWS")]
	[InlineData("#if DEBUG\nint a = 1;\n#elif NET10_0_OR_GREATER\nint b = 1;\n#endif", "NET10_0_OR_GREATER")]
	[InlineData("#if (DEBUG || RELEASE)\nint a = 1;\n#endif", "RELEASE")]
	[InlineData("#if !BROCHOOS_CUSTOM\nint a = 1;\n#endif", "BROCHOOS_CUSTOM")]
	[InlineData("#if false\nint a = 1;\n#endif", "false")]
	[InlineData("#if DEBUG\n#if WINDOWS\nint a = 1;\n#endif\n#endif", "WINDOWS")]
	public void FindUnscannedConditionalSymbols_OtherSymbolOrLiteral_IsDetected(string snippet, string expected)
	{
		_ = FindUnscannedConditionalSymbols(WrapSnippet(snippet)).Should().ContainSingle()
			.Which.Should().EndWith($"'{expected}'");
	}

	// Mentions in comments, doc comments, directives and identifiers that merely contain a
	// forbidden word must NOT turn the rule red.
	[Theory]
	[InlineData("int a = 1; // Ping the primary")]
	[InlineData("/* HttpClient */ int b = 1;")]
	[InlineData("var s = \"https://example\"; // Socket")]
	[InlineData("var s = @\"a\"\"b // c\"; /* Dns.GetHostAddresses */")]
	[InlineData("var s = \"\"\"\n// raw\n\"\"\"; // HttpClient")]
	[InlineData("var c = '\"'; // Dns.x")]
	[InlineData("/// <summary>Uses HttpClient.</summary>\nvoid Local() { }")]
	[InlineData("#region Ping helpers\nint a = 1;\n#endregion")]
	[InlineData("int PingTimeout = 1; string SocketPath = \"\";")]
	[InlineData("int a = 1; // HttpClient\rint b = 2;")]
	[InlineData("int a = 1; // HttpClient\u2028int b = 2;")]
	[InlineData("int a = 1; // HttpClient\u0085int b = 2;")]
	public void FindNetworkingViolations_CommentOnlyMentions_Pass(string snippet)
	{
		_ = FindNetworkingViolations(WrapSnippet(snippet)).Should().BeEmpty();
	}

	// Every case here hid a violation from the hand-written lexer (B1-B3 from the review fuzz,
	// then the line endings C# treats as line terminators but the old lexer did not).
	[Theory]
	[InlineData("var u = $\"{(a ? \"http://\" : \"https://\")}{h}\"; var c = new HttpClient();")]
	[InlineData("var u = $\"{\"\\\"\"}//\"; var c = new HttpClient();")]
	[InlineData("var u = $\"{\"/*\"}\";\nvar c = new HttpClient();\nvar v = \"*/\";")]
	[InlineData("int a = 1; // note\rvar c = new HttpClient();")]
	[InlineData("int a = 1; // note\u2028var c = new HttpClient();")]
	[InlineData("int a = 1; // note\u0085var c = new HttpClient();")]
	[InlineData("var t = Type.GetType(\"System.Net.Http.HttpClient\");")]
	[InlineData("var s = @\"a\"\"b // c\"; var c = new HttpClient();")]
	[InlineData("var s = \"\"\"\n// raw\n\"\"\"; var p = new Ping();")]
	[InlineData("#if DEBUG\nvar c = new HttpClient();\n#endif")]
	[InlineData("#if !DEBUG\nvar c = new HttpClient();\n#endif")]
	// Active only when exactly one of DEBUG/TRACE is defined: missed by the old {} + {DEBUG,TRACE}.
	[InlineData("#if !DEBUG && TRACE\nvar c = new HttpClient();\n#endif")]
	[InlineData("#if DEBUG\nint a = 1;\n#elif TRACE\nvar c = new HttpClient();\n#endif")]
	[InlineData("#if DEBUG != TRACE\nvar c = new HttpClient();\n#endif")]
	public void FindNetworkingViolations_RealUse_IsDetected(string snippet)
	{
		_ = FindNetworkingViolations(WrapSnippet(snippet)).Should().NotBeEmpty();
	}

	[Theory]
	[InlineData("// \"winhttp.dll\"\nint a = 1;")]
	[InlineData("const string Lib = \"kernel32.dll\";")]
	[InlineData("int winhttpish = 1;")]
	[InlineData("/* LoadLibrary NativeLibrary.Load */ int a = 1;")]
	public void FindNativeLibraryViolations_NonUse_Passes(string snippet)
	{
		_ = FindNativeLibraryViolations(WrapSnippet(snippet)).Should().BeEmpty();
	}

	[Theory]
	[InlineData("const string Lib = \"winhttp.dll\";")]
	[InlineData("const string L = $\"{\"winhttp\"}.dll\";")]
	[InlineData("var l = $\"{h}\" + \"WinInet\";")]
	[InlineData("const string Lib = \"win\" + \"http.dll\";")]
	[InlineData("const string Lib = \"\"\"ws2_32\"\"\";")]
	[InlineData("var lib = \"dnsapi\"u8;")]
	[InlineData("const string Lib = @\"C:\\Windows\\System32\\ws2_32.dll\";")]
	[InlineData("[DllImport(\"WinHttp.dll\")] static extern void X();")]
	[InlineData("[DllImport(\"kernel32.dll\", EntryPoint = \"LoadLibraryW\")] static extern IntPtr L(string n);")]
	[InlineData("var h = NativeLibrary.Load(name);")]
	[InlineData("var h = LoadLibraryExW(name, 0, 0);")]
	public void FindNativeLibraryViolations_RealUse_IsDetected(string snippet)
	{
		_ = FindNativeLibraryViolations(WrapSnippet(snippet)).Should().NotBeEmpty();
	}

	// The scan's coverage of the preprocessor is only as wide as these configurations, so they are
	// pinned too: one parse per subset of AllowedConditionSymbols, no more, no fewer.
	[Fact]
	public void ScanConfigurations_CoverEverySubsetOfTheAllowedSymbols()
	{
		string[] symbols = [.. AllowedConditionSymbols.Order(StringComparer.Ordinal)];
		IEnumerable<string> expected = Enumerable.Range(0, 1 << symbols.Length)
			.Select(mask => string.Join(",", symbols.Where((_, bit) => (mask & (1 << bit)) != 0)));

		IEnumerable<string> actual = ScanConfigurations
			.Select(options => string.Join(",", options.PreprocessorSymbolNames.Order(StringComparer.Ordinal)));

		_ = actual.Should().BeEquivalentTo(expected);
	}

	private static readonly CSharpParseOptions[] ScanConfigurations =
	[
		// Every combination of DEBUG and TRACE, one parse each. Code inactive in one parse is
		// DisabledTextTrivia there and real tokens in another, so the union of the parses sees every
		// branch whose condition uses only these two symbols. Two parses ({} and {DEBUG,TRACE}) were
		// not enough: a build has each symbol on or off independently (Release defines TRACE only),
		// so code under "#if !DEBUG && TRACE", "#elif TRACE" or "#if DEBUG != TRACE" is active
		// only in {TRACE} or {DEBUG} and was never scanned.
		new(LanguageVersion.Latest, DocumentationMode.Parse),
		new(LanguageVersion.Latest, DocumentationMode.Parse, preprocessorSymbols: ["DEBUG"]),
		new(LanguageVersion.Latest, DocumentationMode.Parse, preprocessorSymbols: ["TRACE"]),
		new(LanguageVersion.Latest, DocumentationMode.Parse, preprocessorSymbols: ["DEBUG", "TRACE"]),
	];

	// The symbols ScanConfigurations toggles. Keep the two in step.
	private static readonly HashSet<string> AllowedConditionSymbols = new(StringComparer.Ordinal) { "DEBUG", "TRACE" };

	// Every #if/#elif condition symbol outside AllowedConditionSymbols, and every true/false literal
	// condition. Directives are read from both parses: Roslyn keeps a directive nested in an
	// inactive region as directive trivia, and the union covers whichever pass skipped it.
	internal static List<string> FindUnscannedConditionalSymbols(string source, string path = "<snippet>")
	{
		HashSet<string> violations = new(StringComparer.Ordinal);
		foreach (SyntaxTree tree in ParseAllConfigurations(source, path))
		{
			foreach (SyntaxTrivia trivia in tree.GetRoot().DescendantTrivia(descendIntoTrivia: false).Where(trivia => trivia.IsDirective))
			{
				ExpressionSyntax? condition = trivia.GetStructure() switch
				{
					IfDirectiveTriviaSyntax ifDirective => ifDirective.Condition,
					ElifDirectiveTriviaSyntax elifDirective => elifDirective.Condition,
					_ => null,
				};

				foreach (SyntaxNode node in condition?.DescendantNodesAndSelf() ?? [])
				{
					string? offending = node switch
					{
						IdentifierNameSyntax name when !AllowedConditionSymbols.Contains(name.Identifier.ValueText) => name.Identifier.ValueText,
						LiteralExpressionSyntax literal => literal.Token.ValueText,
						_ => null,
					};

					if (offending is not null)
					{
						_ = violations.Add($"{path}:{Line(node.GetLocation())}: conditional symbol '{offending}'");
					}
				}
			}
		}

		return [.. violations];
	}

	private static IEnumerable<string> EnumerateCsFilesOutsideNetworkHelper()
	{
		string networkHelperRoot = Path.Combine(GetRepositoryRoot(), "src", "BroChoos.NetworkHelper") + Path.DirectorySeparatorChar;
		return EnumerateCsFilesUnder("src")
			.Where(file => !file.StartsWith(networkHelperRoot, StringComparison.OrdinalIgnoreCase));
	}

	private static string RelativeToRepository(string path)
	{
		return Path.GetRelativePath(GetRepositoryRoot(), path);
	}

	// Test snippets are statements; a method body makes them a well-formed compilation unit, and
	// the assertion keeps every regression case honest: it must be C# the compiler accepts.
	private static string WrapSnippet(string snippet)
	{
		string source = "class C\n{\n\tvoid M()\n\t{\n" + snippet + "\n\t}\n}\n";
		foreach (SyntaxTree tree in ParseAllConfigurations(source, "<snippet>"))
		{
			_ = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
				.Should().BeEmpty("a regression snippet must be valid C# syntax: {0}", snippet);
		}

		return source;
	}

	private static IEnumerable<SyntaxTree> ParseAllConfigurations(string source, string path)
	{
		return ScanConfigurations.Select(options => CSharpSyntaxTree.ParseText(source, options, path));
	}

	internal static List<string> FindNetworkingViolations(string source, string path = "<snippet>")
	{
		HashSet<string> violations = new(StringComparer.Ordinal);
		foreach (SyntaxTree tree in ParseAllConfigurations(source, path))
		{
			SourceText text = tree.GetText();
			foreach (Match match in NetworkingApiPattern.Matches(BuildCodeView(tree.GetRoot())))
			{
				_ = violations.Add($"{path}:{text.Lines.GetLinePosition(match.Index).Line + 1}: '{match.Value}'");
			}
		}

		return [.. violations];
	}

	internal static List<string> FindNativeLibraryViolations(string source, string path = "<snippet>")
	{
		HashSet<string> violations = new(StringComparer.Ordinal);
		foreach (SyntaxTree tree in ParseAllConfigurations(source, path))
		{
			foreach ((string value, Location location) in CollectStringValues(tree.GetRoot()))
			{
				Match match = NetworkNativeLibraryNamePattern.Match(value);
				if (!match.Success)
				{
					match = NativeLibraryLoadPattern.Match(value);
				}

				if (match.Success)
				{
					_ = violations.Add($"{path}:{Line(location)}: string value names '{match.Value}'");
				}
			}

			foreach (SyntaxToken token in tree.GetRoot().DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken)))
			{
				if (token.ValueText == "NativeLibrary" || token.ValueText.StartsWith("LoadLibrary", StringComparison.Ordinal))
				{
					_ = violations.Add($"{path}:{Line(token.GetLocation())}: identifier '{token.ValueText}'");
				}
			}
		}

		return [.. violations];
	}

	private static int Line(Location location)
	{
		return location.GetLineSpan().StartLinePosition.Line + 1;
	}

	// The file text with every non-code trivia (comments, doc comments, directives, inactive #if
	// text) overwritten by spaces. Line terminators inside it are kept and nothing is removed, so
	// a match index is still an index into the original file. Tokens - including every string
	// literal and interpolated-string part - are untouched.
	private static string BuildCodeView(SyntaxNode root)
	{
		char[] code = root.ToFullString().ToCharArray();
		foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: false))
		{
			if (!IsNonCodeTrivia(trivia))
			{
				continue;
			}

			for (int i = trivia.FullSpan.Start; i < trivia.FullSpan.End; i++)
			{
				if (code[i] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029'))
				{
					code[i] = ' ';
				}
			}
		}

		return new string(code);
	}

	private static bool IsNonCodeTrivia(SyntaxTrivia trivia)
	{
		return trivia.IsDirective
			|| trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
			|| trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
			|| trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
			|| trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
			|| trivia.IsKind(SyntaxKind.DisabledTextTrivia);
	}

	// Every string value in code: each literal token's value (regular, verbatim, raw, UTF-8, and
	// the text parts of interpolated strings), plus the folded value of every interpolated string
	// and "+" concatenation, so $"{"winhttp"}.dll" and "win" + "http.dll" are seen whole.
	private static IEnumerable<(string Value, Location Location)> CollectStringValues(SyntaxNode root)
	{
		foreach (SyntaxToken token in root.DescendantTokens())
		{
			if (token.Kind() is SyntaxKind.StringLiteralToken
				or SyntaxKind.Utf8StringLiteralToken
				or SyntaxKind.SingleLineRawStringLiteralToken
				or SyntaxKind.MultiLineRawStringLiteralToken
				or SyntaxKind.Utf8SingleLineRawStringLiteralToken
				or SyntaxKind.Utf8MultiLineRawStringLiteralToken
				or SyntaxKind.InterpolatedStringTextToken)
			{
				yield return (token.ValueText, token.GetLocation());
			}
		}

		foreach (ExpressionSyntax expression in root.DescendantNodes().OfType<ExpressionSyntax>()
			.Where(node => node is InterpolatedStringExpressionSyntax || node.IsKind(SyntaxKind.AddExpression)))
		{
			if (FoldStringValue(expression) is { } folded)
			{
				yield return (folded, expression.GetLocation());
			}
		}
	}

	// A hole whose value is unknown at compile time folds to "\0", a non-word character, so the
	// literal text on each side of it still matches "\b...\b" but is never glued to its neighbour.
	private static string? FoldStringValue(ExpressionSyntax expression)
	{
		return expression switch
		{
			LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression)
				|| literal.IsKind(SyntaxKind.Utf8StringLiteralExpression) => literal.Token.ValueText,
			ParenthesizedExpressionSyntax parenthesized => FoldStringValue(parenthesized.Expression),
			BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
				FoldStringValue(binary.Left) is { } left && FoldStringValue(binary.Right) is { } right ? left + right : null,
			InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(content => content switch
			{
				InterpolatedStringTextSyntax part => part.TextToken.ValueText,
				InterpolationSyntax hole => FoldStringValue(hole.Expression) ?? "\0",
				_ => "\0",
			})),
			_ => null,
		};
	}

	private static IEnumerable<string> EnumerateCsFilesUnder(params string[] relativeSegments)
	{
		string path = Path.Combine([GetRepositoryRoot(), .. relativeSegments]);
		return Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
			.Where(file => !file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
				&& !file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase));
	}

	private static string GetRepositoryRoot()
	{
		string? overrideRoot = Environment.GetEnvironmentVariable("BROCHOOS_ARCH_SOURCE_ROOT");
		if (!string.IsNullOrWhiteSpace(overrideRoot))
		{
			string fullOverridePath = Path.GetFullPath(overrideRoot);
			return !File.Exists(Path.Combine(fullOverridePath, "BroChoos.sln"))
				? throw new InvalidOperationException($"BROCHOOS_ARCH_SOURCE_ROOT must point to a folder containing BroChoos.sln: {fullOverridePath}")
				: fullOverridePath;
		}

		DirectoryInfo? current = new(AppContext.BaseDirectory);

		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "BroChoos.sln")))
			{
				return current.FullName;
			}

			current = current.Parent;
		}

		throw new InvalidOperationException("Could not locate BroChoos.sln from test execution directory.");
	}

	[GeneratedRegex(@"(^\s*using\s+System\.IO(\.|;))|(^\s*using\s+System\.Net(\.|;))|(^\s*using\s+System\.IO\.Pipes(\.|;))|(^\s*using\s+Microsoft\.Win32(\.|;))|\bSystem\.IO\.|\bSystem\.Net\.|\bSystem\.IO\.Pipes\.|\bMicrosoft\.Win32\.|\bSystem\.Diagnostics\.Process\b|\bProcess\.", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex MyRegex();

	[GeneratedRegex(@"\bSystem\.IO\.|\bFile\.|\bDirectory\.|\bFileInfo\b|\bDirectoryInfo\b|\bFileStream\b|\bMicrosoft\.Win32\.Registry\b|\bRegistry\.|\bWebRequest\b|\bHttpWebRequest\b|\bWebClient\b|\bDns\.|\bSocket\b|\bHttpClient\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex PickerForbiddenBlockingRegex();

	[GeneratedRegex(@"\bSystem\.Net\.Http\b|\bHttpClient\b|\bWebClient\b|\bWebRequest\b|\bHttpWebRequest\b|\bSystem\.Net\.Sockets\b|\bSocket\b|\bTcpClient\b|\bUdpClient\b|\bDns\.|\bClientWebSocket\b|\bSystem\.Net\.WebSockets\b|\bPing\b|\bNetworkInterface\b|\bSmtpClient\b|\bHttpListener\b|\bSslStream\b|\bQuicConnection\b|\bHttpMessageInvoker\b|\bSocketsHttpHandler\b|\bServicePointManager\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex NetworkingApiRegex();

	// Applied to the CONTENT of one string literal: a Windows network DLL named anywhere in it,
	// bare or with ".dll", any case ("winhttp", "WinHttp.dll", @"C:\Windows\System32\ws2_32.dll").
	[GeneratedRegex(@"\b(winhttp|wininet|urlmon|ws2_32|wsock32|mswsock|httpapi|iphlpapi|dnsapi|netapi32)(\.dll)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex NetworkNativeLibraryNameRegex();

	// Applied to comment-stripped code: System.Runtime.InteropServices.NativeLibrary.* and any
	// LoadLibrary* (LoadLibrary, LoadLibraryW, LoadLibraryEx...), as a call or an EntryPoint name.
	[GeneratedRegex(@"\bNativeLibrary\.|\bLoadLibrary", RegexOptions.CultureInvariant)]
	private static partial Regex NativeLibraryLoadRegex();
}
