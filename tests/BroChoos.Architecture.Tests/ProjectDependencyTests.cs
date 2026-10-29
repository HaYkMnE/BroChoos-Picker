using System.Reflection;
using System.Xml.Linq;
using BroChoos.Core;
using FluentAssertions;
using Xunit;

namespace BroChoos.Architecture.Tests;

public sealed class ProjectDependencyTests
{
	private static readonly string[] TwoArgumentInvocation = ["a", "b"];
	private static readonly string[] UnrecognizedVerbInvocation = ["unrecognized-verb"];
	private static readonly string[] SingleCommandInvocation = ["any-command"];

	[Fact]
	public void Core_DoesNotReference_PlatformAppOrHelpers()
	{
		HashSet<string> forbidden = new(StringComparer.Ordinal)
		{
			"BroChoos.Platform.Windows",
			"BroChoos.App",
			"BroChoos.StorageHelper",
			"BroChoos.LaunchHelper",
			"BroChoos.NetworkHelper",
			"BroChoos.UpdateHelper",
		};

		_ = GetReferencedAssemblySimpleNames(typeof(Bootstrap).Assembly)
			.Should()
			.NotContain(name => forbidden.Contains(name));
	}

	[Fact]
	public void Platform_References_OnlyCoreAmongProductAssemblies()
	{
		HashSet<string> forbidden = new(StringComparer.Ordinal)
		{
			"BroChoos.App",
			"BroChoos.StorageHelper",
			"BroChoos.LaunchHelper",
			"BroChoos.NetworkHelper",
			"BroChoos.UpdateHelper",
		};

		HashSet<string> references = GetReferencedAssemblySimpleNames(typeof(Platform.Windows.ServiceCollectionExtensions).Assembly);

		_ = references.Should().Contain("BroChoos.Core");
		_ = references.Should().NotContain(name => forbidden.Contains(name));
	}

	[Fact]
	public void App_ReferencesHelperClientContracts_AndDoesNotReference_HelperImplementations()
	{
		HashSet<string> forbidden = new(StringComparer.Ordinal)
		{
			"BroChoos.StorageHelper",
			"BroChoos.LaunchHelper",
			"BroChoos.NetworkHelper",
			"BroChoos.UpdateHelper",
		};

		Assembly appAssembly = Assembly.Load("BroChoos");
		HashSet<string> references = GetReferencedAssemblySimpleNames(appAssembly);

		_ = references.Should().Contain("BroChoos.Core");
		_ = references.Should().Contain("BroChoos.Platform.Windows");
		_ = references.Should().NotContain(name => forbidden.Contains(name));

		Type[] helperClientContracts =
		[
			typeof(Core.Abstractions.IStorageHelperClient),
			typeof(Core.Abstractions.ILaunchHelperClient),
			typeof(Core.Abstractions.INetworkHelperClient),
			typeof(Core.Abstractions.IUpdateHelperClient),
		];

		foreach (Type contract in helperClientContracts)
		{
			_ = contract.Assembly.GetName().Name.Should().Be("BroChoos.Core");
		}
	}

	// NETWORK CODE LIVES ONLY IN THE NETWORKHELPER PROCESS (compiled-reference half; the source
	// half is ForbiddenApiTests.SourceOutsideNetworkHelper_DoesNotUse_NetworkingApis). An assembly
	// that calls HttpClient, Socket, Dns or a WebSocket gets a reference to one of these facades.
	// System.IO.Pipes is local IPC and is deliberately absent from the list, and so is
	// System.Net.Primitives: it carries IPAddress, a data type with no I/O.
	[Theory]
	[InlineData("BroChoos.Core")]
	[InlineData("BroChoos.Platform.Windows")]
	[InlineData("BroChoos")]
	[InlineData("BroChoos.StorageHelper")]
	[InlineData("BroChoos.LaunchHelper")]
	[InlineData("BroChoos.UpdateHelper")]
	public void AssemblyOutsideNetworkHelper_DoesNotReference_NetworkingAssemblies(string assemblySimpleName)
	{
		HashSet<string> forbidden = new(StringComparer.Ordinal)
		{
			"System.Net.Http",
			"System.Net.Sockets",
			"System.Net.WebSockets",
			"System.Net.WebSockets.Client",
			"System.Net.NameResolution",
			"System.Net.Requests",
			"System.Net.WebClient",
			"System.Net.Ping",
			"System.Net.NetworkInformation",
			"System.Net.Mail",
			"System.Net.HttpListener",
			"System.Net.Security",
			"System.Net.Quic",
		};

		_ = GetReferencedAssemblySimpleNames(Assembly.Load(assemblySimpleName))
			.Should()
			.NotContain(name => forbidden.Contains(name),
				"network code must live only in BroChoos.NetworkHelper");
	}

	[Theory]
	[InlineData("BroChoos.StorageHelper")]
	[InlineData("BroChoos.LaunchHelper")]
	[InlineData("BroChoos.NetworkHelper")]
	[InlineData("BroChoos.UpdateHelper")]
	public void HelperProject_ReferencesOnlyCoreAndPlatform_NotAppOrHelpers(string helperAssemblySimpleName)
	{
		string repositoryRoot = GetRepositoryRoot();
		string helperProjectPath = Path.Combine(repositoryRoot, "src", helperAssemblySimpleName, helperAssemblySimpleName + ".csproj");

		HashSet<string> productReferences = ReadProjectReferences(helperProjectPath)
			.Where(reference => reference.StartsWith("BroChoos.", StringComparison.Ordinal))
			.ToHashSet(StringComparer.Ordinal);

		HashSet<string> allowed = new(StringComparer.Ordinal)
		{
			"BroChoos.Core",
			"BroChoos.Platform.Windows",
		};

		_ = productReferences.Should().Contain("BroChoos.Core");
		_ = productReferences.Should().OnlyContain(reference => allowed.Contains(reference),
			"helper projects may reference Core (contracts) and Platform.Windows (OS adapters), never the App UI project or another helper");
	}

	[Theory]
	[InlineData("BroChoos.StorageHelper", "discover-persist")]
	[InlineData("BroChoos.LaunchHelper", "launch")]
	[InlineData("BroChoos.NetworkHelper", "resolve-short-link")]
	[InlineData("BroChoos.UpdateHelper", null)]
	public void HelperProject_RejectsZeroOrTooManyArguments_AndEnforcesSingleCommand(string helperAssemblySimpleName, string? recognizedVerb)
	{
		Assembly helperAssembly = LoadHelperAssembly(helperAssemblySimpleName);
		Type programType = helperAssembly.GetType($"{helperAssemblySimpleName}.Program", throwOnError: true)!;
		MethodInfo main = programType.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

		int noArgsExit = InvokeMain(main, []);
		int tooManyArgsExit = InvokeMain(main, TwoArgumentInvocation);

		_ = noArgsExit.Should().NotBe(0);
		_ = tooManyArgsExit.Should().NotBe(0);

		if (recognizedVerb is not null)
		{
			// A verb-dispatched one-shot helper rejects an unrecognized verb.
			int wrongVerbExit = InvokeMain(main, UnrecognizedVerbInvocation);
			_ = wrongVerbExit.Should().NotBe(0);
		}
		else
		{
			// A not-yet-dispatched helper accepts exactly one argument.
			int oneArgExit = InvokeMain(main, SingleCommandInvocation);
			_ = oneArgExit.Should().Be(0);
		}
	}

	private static int InvokeMain(MethodInfo main, string[] args)
	{
		object? result = main.Invoke(null, [args]);

		return result switch
		{
			int exitCode => exitCode,
			Task<int> asyncExit => asyncExit.GetAwaiter().GetResult(),
			_ => throw new InvalidOperationException($"Unsupported helper Main return type: {main.ReturnType.FullName}"),
		};
	}

	private static Assembly LoadHelperAssembly(string helperAssemblySimpleName)
	{
		// THE PATH IS ASKED OF THE ASSEMBLY, NOT ASSEMBLED FROM A GUESS ABOUT THE BUILD.
		// This used to be Path.Combine(repositoryRoot, "src", name, "bin", "Debug",
		// net10.0-windows10.0.19041.0, win-x64, name + ".dll"), which hardcoded the
		// configuration. CI builds Release, so on a clean clone the file was not there and
		// four tests died with FileNotFoundException before reaching a single assertion —
		// they never tested the helpers at all, they only tested that Debug had been built.
		//
		// The helpers are project-referenced by this test project, so the test host already
		// knows how to find them. Assembly.Load resolves each one exactly as the host does,
		// and Location reports the path the build ACTUALLY produced — Debug, Release, or any
		// other configuration — with no configuration name written down anywhere in the test.
		Assembly helperAssembly = Assembly.Load(helperAssemblySimpleName);

		return !string.IsNullOrWhiteSpace(helperAssembly.Location)
			? helperAssembly
			: throw new InvalidOperationException(
				$"Helper assembly '{helperAssemblySimpleName}' has no location on disk, so it cannot be exercised as a process entry point.");
	}

	private static IEnumerable<string> ReadProjectReferences(string projectPath)
	{
		XNamespace msbuildNs = "http://schemas.microsoft.com/developer/msbuild/2003";
		XDocument project = XDocument.Load(projectPath);

		IEnumerable<XElement> includeElements = project.Descendants("ProjectReference");

		if (!includeElements.Any())
		{
			includeElements = project.Descendants(msbuildNs + "ProjectReference");
		}

		return includeElements
			.Select(item => item.Attribute("Include")?.Value)
			.Where(include => !string.IsNullOrWhiteSpace(include))
			.Select(include => Path.GetFileNameWithoutExtension(include!));
	}

	private static HashSet<string> GetReferencedAssemblySimpleNames(Assembly assembly)
	{
		return assembly
			.GetReferencedAssemblies()
			.Select(reference => reference.Name)
			.OfType<string>()
			.ToHashSet(StringComparer.Ordinal);
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
}
