using System.Xml.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace BroChoos.Architecture.Tests;

// F2: THE e + 1 RULE IN CultureGateState (tests/BroChoos.App.Tests/CultureGate.cs) RESTS ON HOW THE
// GATE IS TAKEN AND RELEASED. This parses the BroChoos.App.Tests sources with Roslyn and checks the
// syntax trees, so comments and string literals can never match or hide a call. Syntax only, no
// semantic model and no reflection: a call made through reflection is out of scope.
//
// SOURCES. Every .cs under the project (bin and obj excluded) plus every <Compile Include=..> item
// in the csproj and in each Directory.Build.props / Directory.Build.targets / Directory.Packages.props
// from the project folder up to the repository root (elements matched by local name). An Include
// that holds $(, @(, %(, * , ? or ; cannot be resolved here and fails the scan, so does a missing
// file and any <Import> in those MSBuild files. Every file is parsed twice, once per CI
// configuration: Debug (DEBUG;TRACE) and Release (TRACE), and every count below must hold in EACH.
//
// HOW CultureGate MEMBERS ARE FOUND. CultureGate is a static class, so its members are reachable as
// CultureGate.X (any qualifier ending in CultureGate), as a bare name inside class CultureGate, or
// through a using static / alias of it. Those are forbidden in source (using directives) and in
// MSBuild (any <Using> item naming CultureGate, which would land in obj\GlobalUsings.g.cs, outside
// the scan). As a second line, a bare Gate or State outside class CultureGate fails unless a
// member, local, parameter or other name Gate / State is declared in its scope (syntax level: the
// enclosing types' members and anything declared inside the enclosing member).
//
// THE RULES. Exactly one class CultureGate and one class CultureIsolatedAttribute are declared.
//   1. CultureGate.Gate outside class CultureGate, and a bare Gate inside it, may only be the
//      receiver of .CurrentCount, of an invoked .Wait(..) or of an invoked .Release(..). Anything
//      else (WaitAsync, AvailableWaitHandle, a method group, ?., parentheses, a local, an argument)
//      is a violation.
//   2. Wait and Release are called only inside CultureIsolatedAttribute: exactly 2 Wait and exactly
//      1 Release. Each Wait is the whole condition of an if: "if (Gate.Wait(..))" whose block starts
//      with TakeGate(..), or "if (!Gate.Wait(..))" with no else, whose block ends in throw (not
//      return: After would then release a gate this test never took) and whose next statement is
//      TakeGate(..).
//   3. The attribute declares exactly one method TakeGate and one method After, directly, with no
//      local function of either name. CultureGate.State.Take(..) is a top-level statement of
//      TakeGate's body, with no return, throw or goto before it.
//   4. The Release takes no arguments and is a direct statement of After's body (not in a local
//      function, lambda, if, loop or try), right after CultureGate.State.Release().
//   5. CultureGate.State (or a bare State inside class CultureGate) is only ever the receiver of a
//      member access; Take and Release on it are always invoked, Take exactly once (in TakeGate) and
//      Release exactly once (in After). Calls on other receivers (a test's own "state") are not
//      matched.
// The scan fails closed: any syntax error, any #define / #undef, and any #if / #elif whose
// condition names a symbol other than DEBUG or TRACE is a violation. With only those two symbols
// allowed, the two parses above see every branch the CI builds compile, in every file, whether or
// not it mentions CultureGate (deciding "mentions" would itself depend on the active branch).
public sealed class CultureGateInvariantTests(ITestOutputHelper output)
{
	private const string GateType = "CultureGate";
	private const string AttributeType = "CultureIsolatedAttribute";
	private const string GateFile = "CultureGate.cs";
	private const string ProjectName = "BroChoos.App.Tests";

	// ci.yml builds and tests Release and Debug. The SDK defines TRACE in both, DEBUG only in Debug.
	private static readonly (string Name, string[] Symbols)[] Configurations = [("Debug", ["DEBUG", "TRACE"]), ("Release", ["TRACE"])];

	private static readonly string[] KnownSymbols = ["DEBUG", "TRACE"];

	private static readonly string[] MsBuildFileNames = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

	private static readonly string[] UnresolvableInclude = ["$(", "@(", "%(", "*", "?", ";"];

	[Fact]
	public void CultureGate_IsTakenAndReleasedOnlyOnThePinnedPaths()
	{
		string root = RepositoryRoot();
		List<string> violations = [];
		List<(string Path, string Text)> sources = [.. Sources(root, violations).Select(path => (Path.GetRelativePath(root, path), File.ReadAllText(path)))];

		_ = sources.Should().NotBeEmpty("the scan must see the App.Tests sources, or it proves nothing");
		_ = sources.Select(source => Path.GetFileName(source.Path)).Should().Contain(GateFile);

		foreach ((string name, string[] symbols) in Configurations)
		{
			CSharpParseOptions options = new(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Regular, symbols);
			List<SyntaxTree> trees = [.. sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, options, source.Path))];

			Scan scan = new(name);
			foreach (SyntaxTree tree in trees)
			{
				Analyze(tree, scan);
			}

			CheckDeclarations(trees, scan);
			scan.ExpectCount("Gate.Wait in " + AttributeType, scan.Waits, 2);
			scan.ExpectCount("Gate.Release in " + AttributeType, scan.Releases, 1);
			scan.ExpectCount("CultureGate.State.Take in TakeGate", scan.Takes, 1);
			scan.ExpectCount("CultureGate.State.Release in After", scan.StateReleases, 1);
			output.WriteLine($"{name}: files={trees.Count} waits={scan.Waits} releases={scan.Releases} takes={scan.Takes} stateReleases={scan.StateReleases} violations={scan.Violations.Count}");
			violations.AddRange(scan.Violations);
		}

		_ = violations.Should().BeEmpty("every acquisition must record itself in CultureGateState right after Gate.Wait, and the holder must be cleared right before Gate.Release (the e + 1 rule depends on it)");
	}

	private static void Analyze(SyntaxTree tree, Scan scan)
	{
		SyntaxNode root = tree.GetRoot();
		foreach (Diagnostic error in tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
		{
			scan.Add(tree.FilePath, error.Location, $"syntax error {error.Id}");
		}

		foreach (SyntaxTrivia trivia in root.DescendantTrivia())
		{
			CheckDirective(tree.FilePath, trivia, scan);
		}

		foreach (UsingDirectiveSyntax usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
		{
			if ((usingDirective.StaticKeyword != default || usingDirective.Alias is not null) && RightmostName(usingDirective.NamespaceOrType) == GateType)
			{
				scan.Add(tree.FilePath, usingDirective.GetLocation(), $"using static / alias of {GateType}");
			}
		}

		foreach (IdentifierNameSyntax name in root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(name => name.Identifier.ValueText is "Gate" or "State"))
		{
			if (IsBare(name) && !InsideType(name, GateType) && !IsDeclaredInScope(name))
			{
				scan.Add(tree.FilePath, name.GetLocation(), $"bare {name.Identifier.ValueText} outside {GateType} with nothing of that name declared in scope (a using static of {GateType}?): {Context(name)}");
			}
		}

		foreach (ExpressionSyntax gate in References(root, "Gate"))
		{
			CheckGateReference(tree.FilePath, gate, scan);
		}

		foreach (ExpressionSyntax state in References(root, "State"))
		{
			CheckStateReference(tree.FilePath, state, scan);
		}
	}

	private static void CheckDirective(string path, SyntaxTrivia trivia, Scan scan)
	{
		switch (trivia.GetStructure())
		{
			case DefineDirectiveTriviaSyntax or UndefDirectiveTriviaSyntax:
				scan.Add(path, trivia.GetLocation(), "#define / #undef (the scan parses only the CI symbol sets)");
				break;
			case ConditionalDirectiveTriviaSyntax conditional:
				foreach (IdentifierNameSyntax symbol in conditional.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Where(symbol => !KnownSymbols.Contains(symbol.Identifier.ValueText)))
				{
					scan.Add(path, trivia.GetLocation(), $"conditional directive on symbol {symbol.Identifier.ValueText} (only DEBUG and TRACE are evaluated)");
				}

				break;
			default:
				break;
		}
	}

	private static void CheckDeclarations(List<SyntaxTree> trees, Scan scan)
	{
		List<BaseTypeDeclarationSyntax> types = [.. trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())];
		scan.ExpectCount("class " + GateType, types.Count(type => type.Identifier.ValueText == GateType && type is ClassDeclarationSyntax), 1);
		scan.ExpectCount("type named " + GateType, types.Count(type => type.Identifier.ValueText == GateType), 1);
		scan.ExpectCount("class " + AttributeType, types.Count(type => type.Identifier.ValueText == AttributeType && type is ClassDeclarationSyntax), 1);
		scan.ExpectCount("type named " + AttributeType, types.Count(type => type.Identifier.ValueText == AttributeType), 1);

		foreach (TypeDeclarationSyntax attribute in types.OfType<TypeDeclarationSyntax>().Where(type => type.Identifier.ValueText == AttributeType))
		{
			string path = attribute.SyntaxTree.FilePath;
			foreach (string method in (string[])["TakeGate", "After"])
			{
				List<MethodDeclarationSyntax> methods = [.. attribute.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(declaration => declaration.Identifier.ValueText == method)];
				if (methods.Count != 1 || methods[0].Parent != attribute)
				{
					scan.Add(path, attribute.Identifier.GetLocation(), $"{AttributeType} must declare exactly one method {method}, directly (found {methods.Count})");
				}

				foreach (LocalFunctionStatementSyntax local in attribute.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Where(local => local.Identifier.ValueText == method))
				{
					scan.Add(path, local.GetLocation(), $"local function {method} inside {AttributeType}");
				}
			}
		}
	}

	// Every expression that denotes CultureGate.<member>: a member access whose receiver ends in
	// CultureGate, or the bare name inside class CultureGate. A qualified NAME (type syntax) that
	// ends in CultureGate.<member> is returned too, and is then rejected as an unknown shape.
	private static IEnumerable<ExpressionSyntax> References(SyntaxNode root, string member)
	{
		foreach (IdentifierNameSyntax name in root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(name => name.Identifier.ValueText == member))
		{
			ExpressionSyntax? reference = name.Parent switch
			{
				MemberAccessExpressionSyntax access when access.Name == name => IsCultureGate(access.Expression) ? access : null,
				QualifiedNameSyntax qualified when qualified.Right == name => RightmostName(qualified.Left) == GateType ? qualified : null,
				AliasQualifiedNameSyntax or MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax => null,
				_ => InsideType(name, GateType) ? name : null,
			};

			if (reference is not null)
			{
				yield return reference;
			}
		}
	}

	private static void CheckGateReference(string path, ExpressionSyntax gate, Scan scan)
	{
		if (gate.Parent is MemberAccessExpressionSyntax access && access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && access.Expression == gate)
		{
			string name = access.Name.Identifier.ValueText;
			if (name == "CurrentCount" && access.Parent is not InvocationExpressionSyntax)
			{
				return;
			}

			if (name is "Wait" or "Release" && access.Parent is InvocationExpressionSyntax call && call.Expression == access)
			{
				if (!InsideType(call, AttributeType))
				{
					scan.Add(path, call.GetLocation(), $"Gate.{name} outside {AttributeType}");
				}
				else if (name == "Wait")
				{
					scan.Waits++;
					CheckWait(path, call, scan);
				}
				else
				{
					scan.Releases++;
					CheckRelease(path, call, scan);
				}

				return;
			}
		}

		scan.Add(path, gate.GetLocation(), $"CultureGate.Gate used other than as .CurrentCount / .Wait(..) / .Release(): {Context(gate)}");
	}

	private static void CheckWait(string path, InvocationExpressionSyntax wait, Scan scan)
	{
		bool negated = wait.Parent is PrefixUnaryExpressionSyntax not && not.IsKind(SyntaxKind.LogicalNotExpression);
		ExpressionSyntax condition = negated ? (ExpressionSyntax)wait.Parent! : wait;
		if (condition.Parent is not IfStatementSyntax ifStatement || ifStatement.Condition != condition)
		{
			scan.Add(path, wait.GetLocation(), $"Gate.Wait is not the whole condition of an if (Gate.Wait(..) or !Gate.Wait(..)): {Context(wait)}");
			return;
		}

		if (!negated)
		{
			if (!IsTakeGateCall(FirstStatement(ifStatement.Statement)))
			{
				scan.Add(path, wait.GetLocation(), "the block of if (Gate.Wait(..)) does not start with TakeGate(..)");
			}

			return;
		}

		if (ifStatement.Else is not null)
		{
			scan.Add(path, wait.GetLocation(), "if (!Gate.Wait(..)) has an else branch");
		}

		if (LastStatement(ifStatement.Statement) is not ThrowStatementSyntax)
		{
			scan.Add(path, wait.GetLocation(), "the block of if (!Gate.Wait(..)) does not end in throw (a return would let After release a gate this test never took)");
		}

		StatementSyntax? next = ifStatement.Parent is BlockSyntax block ? block.Statements.ElementAtOrDefault(block.Statements.IndexOf(ifStatement) + 1) : null;
		if (!IsTakeGateCall(next))
		{
			scan.Add(path, wait.GetLocation(), $"the statement after if (!Gate.Wait(..)) is not TakeGate(..) (it is: {next?.ToString() ?? "none"})");
		}
	}

	private static void CheckRelease(string path, InvocationExpressionSyntax release, Scan scan)
	{
		if (release.ArgumentList.Arguments.Count != 0)
		{
			scan.Add(path, release.GetLocation(), $"Gate.Release is called with arguments: {Context(release)}");
		}

		if (DiscardOrSelf(release).Parent is not ExpressionStatementSyntax statement
			|| statement.Parent is not BlockSyntax body
			|| body.Parent is not MethodDeclarationSyntax method
			|| method.Body != body
			|| method.Identifier.ValueText != "After")
		{
			scan.Add(path, release.GetLocation(), $"Gate.Release is not a direct statement of the body of After: {Context(release)}");
			return;
		}

		int index = body.Statements.IndexOf(statement);
		StatementSyntax? previous = index > 0 ? body.Statements[index - 1] : null;
		if (!IsStateReleaseCall(previous))
		{
			scan.Add(path, release.GetLocation(), $"Gate.Release is not preceded at once by CultureGate.State.Release() (previous statement: {previous?.ToString() ?? "none"})");
		}
	}

	private static void CheckStateReference(string path, ExpressionSyntax state, Scan scan)
	{
		if (state.Parent is not MemberAccessExpressionSyntax access || !access.IsKind(SyntaxKind.SimpleMemberAccessExpression) || access.Expression != state)
		{
			scan.Add(path, state.GetLocation(), $"CultureGate.State used other than as the receiver of a member access: {Context(state)}");
			return;
		}

		string name = access.Name.Identifier.ValueText;
		if (name is not ("Take" or "Release"))
		{
			return;
		}

		if (access.Parent is not InvocationExpressionSyntax call || call.Expression != access)
		{
			scan.Add(path, access.GetLocation(), $"CultureGate.State.{name} used without being invoked (a method group or delegate): {Context(access)}");
			return;
		}

		string expected = name == "Take" ? "TakeGate" : "After";
		if (EnclosingFunction(call) is MethodDeclarationSyntax method && method.Identifier.ValueText == expected && InsideType(method, AttributeType))
		{
			if (name == "Release")
			{
				scan.StateReleases++;
				return;
			}

			scan.Takes++;
			if (!IsUnconditionalTopLevelStatement(call, method))
			{
				scan.Add(path, call.GetLocation(), $"CultureGate.State.Take is not a top-level statement of TakeGate's body reached on every path: {Context(call)}");
			}

			return;
		}

		scan.Add(path, call.GetLocation(), $"CultureGate.State.{name} outside {AttributeType}.{expected}: {Context(call)}");
	}

	// "x;" / "_ = x;" / "T v = x;" directly in the method body, with no return, throw or goto in any
	// statement before it.
	private static bool IsUnconditionalTopLevelStatement(InvocationExpressionSyntax call, MethodDeclarationSyntax method)
	{
		StatementSyntax? statement = DiscardOrSelf(call).Parent switch
		{
			ExpressionStatementSyntax expression => expression,
			EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax local } } } => local,
			_ => null,
		};

		return statement is not null
			&& method.Body is not null
			&& statement.Parent == method.Body
			&& !method.Body.Statements.TakeWhile(before => before != statement)
				.SelectMany(before => before.DescendantNodesAndSelf())
				.Any(node => node is ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax or GotoStatementSyntax);
	}

	private static SyntaxNode DiscardOrSelf(InvocationExpressionSyntax call)
	{
		return call.Parent is AssignmentExpressionSyntax assignment
			&& assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
			&& assignment.Right == call
			&& assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_" }
				? assignment
				: call;
	}

	private static bool IsTakeGateCall(StatementSyntax? statement)
	{
		return statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "TakeGate" } } };
	}

	// Only CultureGate.State.Release() (the qualifier may be namespace-qualified). A bare State is
	// refused: inside the attribute it could be a decoy member of that name.
	private static bool IsStateReleaseCall(StatementSyntax? statement)
	{
		return statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0, Expression: MemberAccessExpressionSyntax access } }
			&& access.IsKind(SyntaxKind.SimpleMemberAccessExpression)
			&& access.Name.Identifier.ValueText == "Release"
			&& access.Expression is MemberAccessExpressionSyntax receiver
			&& receiver.IsKind(SyntaxKind.SimpleMemberAccessExpression)
			&& receiver.Name.Identifier.ValueText == "State"
			&& IsCultureGate(receiver.Expression);
	}

	// A simple name in expression position: not the right side of a member access or qualified
	// name, not a named argument, member binding or object-initializer target.
	private static bool IsBare(IdentifierNameSyntax name)
	{
		return name.Parent switch
		{
			MemberAccessExpressionSyntax access => access.Name != name,
			QualifiedNameSyntax qualified => qualified.Right != name,
			AliasQualifiedNameSyntax aliased => aliased.Name != name,
			MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax => false,
			ExpressionColonSyntax colon => colon.Expression != name,
			AssignmentExpressionSyntax assignment when assignment.Left == name && assignment.Parent is InitializerExpressionSyntax initializer
				&& (initializer.IsKind(SyntaxKind.ObjectInitializerExpression) || initializer.IsKind(SyntaxKind.WithInitializerExpression)) => false,
			_ => true,
		};
	}

	// Syntax level: a member of an enclosing type, a primary-constructor or type parameter, or any
	// name declared inside the enclosing member (or the whole file for top-level statements).
	private static bool IsDeclaredInScope(IdentifierNameSyntax name)
	{
		string id = name.Identifier.ValueText;
		SyntaxNode scope = name.Ancestors().FirstOrDefault(ancestor => ancestor.Parent is TypeDeclarationSyntax) ?? name.SyntaxTree.GetRoot();
		return scope.DescendantNodesAndSelf().Any(node => LocalName(node) == id) || name.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.Members.SelectMany(MemberNames).Contains(id)
			|| (type.ParameterList?.Parameters.Any(parameter => parameter.Identifier.ValueText == id) ?? false)
			|| (type.TypeParameterList?.Parameters.Any(parameter => parameter.Identifier.ValueText == id) ?? false));
	}

	private static string? LocalName(SyntaxNode node)
	{
		return node switch
		{
			VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
			ParameterSyntax parameter => parameter.Identifier.ValueText,
			SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
			ForEachStatementSyntax forEach => forEach.Identifier.ValueText,
			CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.ValueText,
			LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
			TypeParameterSyntax typeParameter => typeParameter.Identifier.ValueText,
			FromClauseSyntax from => from.Identifier.ValueText,
			LetClauseSyntax let => let.Identifier.ValueText,
			JoinClauseSyntax join => join.Identifier.ValueText,
			JoinIntoClauseSyntax into => into.Identifier.ValueText,
			QueryContinuationSyntax continuation => continuation.Identifier.ValueText,
			_ => null,
		};
	}

	private static IEnumerable<string> MemberNames(MemberDeclarationSyntax member)
	{
		return member switch
		{
			BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText),
			PropertyDeclarationSyntax property => [property.Identifier.ValueText],
			EventDeclarationSyntax eventDeclaration => [eventDeclaration.Identifier.ValueText],
			MethodDeclarationSyntax method => [method.Identifier.ValueText],
			BaseTypeDeclarationSyntax type => [type.Identifier.ValueText],
			DelegateDeclarationSyntax type => [type.Identifier.ValueText],
			_ => [],
		};
	}

	private static StatementSyntax? FirstStatement(StatementSyntax statement)
	{
		return statement is BlockSyntax block ? block.Statements.FirstOrDefault() : statement;
	}

	private static StatementSyntax? LastStatement(StatementSyntax statement)
	{
		return statement is BlockSyntax block ? block.Statements.LastOrDefault() : statement;
	}

	private static SyntaxNode? EnclosingFunction(SyntaxNode node)
	{
		return node.Ancestors().FirstOrDefault(ancestor => ancestor is BaseMethodDeclarationSyntax
			or LocalFunctionStatementSyntax
			or AnonymousFunctionExpressionSyntax
			or AccessorDeclarationSyntax
			or BasePropertyDeclarationSyntax
			or BaseFieldDeclarationSyntax
			or TypeDeclarationSyntax);
	}

	private static bool InsideType(SyntaxNode node, string typeName)
	{
		return node.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.Identifier.ValueText == typeName);
	}

	private static bool IsCultureGate(ExpressionSyntax expression)
	{
		return RightmostName(expression) == GateType;
	}

	private static string? RightmostName(SyntaxNode node)
	{
		return node switch
		{
			IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
			QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
			AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
			MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) => access.Name.Identifier.ValueText,
			_ => null,
		};
	}

	private static string Context(SyntaxNode node)
	{
		SyntaxNode shown = node.FirstAncestorOrSelf<StatementSyntax>() ?? node.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? node;
		return shown.ToString().ReplaceLineEndings(" ");
	}

	// The App.Tests sources: every .cs under the project (bin and obj excluded), plus every file a
	// <Compile Include=..> item of the project's MSBuild files names. MSBuild problems the scan
	// cannot see past are added to `violations`.
	private static List<string> Sources(string root, List<string> violations)
	{
		string project = Path.Combine(root, "tests", ProjectName);
		List<string> files = [.. Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
			.Where(path => !Path.GetRelativePath(project, path).Split(Path.DirectorySeparatorChar)
				.Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))];

		foreach (string msbuild in MsBuildFiles(root, project))
		{
			string where = Path.GetRelativePath(root, msbuild);
			foreach (XElement element in XDocument.Load(msbuild).Descendants())
			{
				string? include = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Include")?.Value;
				switch (element.Name.LocalName)
				{
					case "Import":
						violations.Add($"{where}: <Import> (the scan does not follow imported MSBuild files)");
						break;
					case "Using" when element.Attributes().Any(attribute => attribute.Name.LocalName is "Include" or "Update" && (attribute.Value.Contains(GateType, StringComparison.Ordinal) || UnresolvableInclude.Take(3).Any(attribute.Value.Contains))):
						violations.Add($"{where}: an MSBuild <Using> item names {GateType} or a computed value ({element}); it would be emitted to obj\\GlobalUsings.g.cs, which the scan does not read");
						break;
					case "Compile" when include is not null:
						if (UnresolvableInclude.Any(include.Contains))
						{
							violations.Add($"{where}: <Compile Include=\"{include}\"> uses a property, item, wildcard or list the scan cannot resolve; name each file literally");
						}
						else
						{
							string path = Path.GetFullPath(Path.Combine(project, include));
							if (File.Exists(path))
							{
								files.Add(path);
							}
							else
							{
								violations.Add($"{where}: <Compile Include=\"{include}\"> names a file that does not exist ({path})");
							}
						}

						break;
					default:
						break;
				}
			}
		}

		return [.. files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
	}

	// The csproj, and each Directory.Build.props / .targets / Directory.Packages.props from the
	// project folder up to the repository root (a superset of what MSBuild imports).
	private static IEnumerable<string> MsBuildFiles(string root, string project)
	{
		yield return Path.Combine(project, ProjectName + ".csproj");
		for (DirectoryInfo? directory = new(project); directory is not null; directory = directory.Parent)
		{
			foreach (string candidate in MsBuildFileNames.Select(file => Path.Combine(directory.FullName, file)).Where(File.Exists))
			{
				yield return candidate;
			}

			if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
			{
				yield break;
			}
		}
	}

	private static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate BroChoos.sln from the test execution directory.");
	}

	private sealed class Scan(string configuration)
	{
		public List<string> Violations { get; } = [];

		public int Waits { get; set; }

		public int Releases { get; set; }

		public int Takes { get; set; }

		public int StateReleases { get; set; }

		public void Add(string path, Location location, string message)
		{
			Violations.Add($"[{configuration}] {path}:{location.GetLineSpan().StartLinePosition.Line + 1}: {message}");
		}

		public void ExpectCount(string what, int actual, int expected)
		{
			if (actual != expected)
			{
				Violations.Add($"[{configuration}] expected exactly {expected} {what}, found {actual}");
			}
		}
	}
}
