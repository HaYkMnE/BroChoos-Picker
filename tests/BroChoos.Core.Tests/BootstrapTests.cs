using BroChoos.Core.Abstractions;
using BroChoos.Core.Cli;
using BroChoos.Core.Ingress;
using BroChoos.Core.Runners;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.Core.Tests;

// Redirects Console.Out; see ConsoleCaptureCollection.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class BootstrapTests
{
	[Fact]
	public async Task RunAsync_SafeModeWithGarbageUrl_ReturnsSafeModeOk()
	{
		TestRun run = await RunBootstrapAsync(["--safe-mode", "not-a-url"], urlValid: false);

		_ = run.ExitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = run.ConsoleOutput.Should().Contain("[safe-mode] entry=accepted");
		_ = run.ConsoleOutput.Should().Contain("[safe-mode] routing=user-requested");
	}

	// An explicit --safe-mode is the user's own request on a configuration nobody
	// found corrupt, so the routing marker must not claim a corruption recovery.
	// Before the fix the runner printed `routing=config-corruption-recovery`
	// unconditionally, which told a user with a healthy config that it was corrupt.
	[Theory]
	[InlineData("--safe-mode")]
	[InlineData("--safe-mode", "--safe-mode-action", "diagnostics-export")]
	public async Task RunAsync_ExplicitSafeMode_DoesNotClaimConfigCorruptionRecovery(params string[] args)
	{
		TestRun run = await RunBootstrapAsync(args);

		_ = run.ConsoleOutput.Should().Contain("[safe-mode] routing=user-requested");
		_ = run.ConsoleOutput.Should().NotContain("config-corruption");
	}

	[Fact]
	public async Task RunAsync_SafeModeWithUrl_WarnsAboutIgnoredInputWithoutValue()
	{
		TestRun run = await RunBootstrapAsync(["https://example.com", "--safe-mode"]);

		_ = run.ExitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = run.Warnings.Should().ContainSingle(w =>
			w.Contains("--safe-mode", StringComparison.Ordinal)
			&& w.Contains("ignored", StringComparison.Ordinal));
		_ = run.Warnings.Should().OnlyContain(w => !w.Contains("example.com", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunAsync_UrlPathConfigCorruption_AutoEntersSafeMode()
	{
		TestRun run = await RunBootstrapAsync(["https://example.com"], pickerExit: ExitCodes.ConfigCorruption);

		_ = run.ExitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = run.ConsoleOutput.Should().Contain("[safe-mode] entry=accepted");
		_ = run.ConsoleOutput.Should().Contain("[safe-mode] routing=config-corruption-recovery");
		_ = run.ConsoleOutput.Should().NotContain("routing=user-requested");
	}

	// (F4) The CLI exit-code contract, pinned THROUGH Bootstrap rather than on the
	// ExitCodes constants. ExitCodesTests only proves InvalidUrl still equals 3; it says
	// nothing about which path returns it, so a regression that mapped a rejected URL to
	// GeneralError (1) — or let the ArgumentException escape entirely — would leave a
	// green suite. This asserts the mapping itself: rejected URL -> 3.
	// The javascript:/file: values are illustrative only — the fake validator rejects every input; real scheme rejection is pinned in DefaultUrlValidatorTests.
	[Theory]
	[InlineData("not-a-url")]
	[InlineData("javascript:alert(1)")]
	[InlineData("file:///C:/Windows/System32/calc.exe")]
	public async Task RunAsync_InvalidUrl_ReturnsInvalidUrlExitCode(string url)
	{
		TestRun run = await RunBootstrapAsync([url], urlValid: false);

		_ = run.ExitCode.Should().Be(ExitCodes.InvalidUrl);
		_ = run.ExitCode.Should().Be(3);
		// Privacy: the rejected value is the user's URL and must not reach a log line.
		_ = run.Warnings.Should().OnlyContain(w => !w.Contains(url, StringComparison.Ordinal));
	}

	// (F4) A second positional argument is rejected by the same path and must map to the
	// same code, not to GeneralError.
	[Fact]
	public async Task RunAsync_UnexpectedSecondPositionalArgument_ReturnsInvalidUrlExitCode()
	{
		TestRun run = await RunBootstrapAsync(["https://example.com", "https://other.example"]);

		_ = run.ExitCode.Should().Be(ExitCodes.InvalidUrl);
	}

	// (F4) An unknown option is an ArgumentException from the parser too: same mapping.
	[Fact]
	public async Task RunAsync_UnknownOption_ReturnsInvalidUrlExitCode()
	{
		TestRun run = await RunBootstrapAsync(["--not-a-real-option"]);

		_ = run.ExitCode.Should().Be(ExitCodes.InvalidUrl);
	}

	// 0A-4: two default-apps actions at once are a conflicting request, rejected
	// by the parser with the SAME code as any other malformed command line (3),
	// never GeneralError, and no registration service is reached.
	[Fact]
	public async Task RunAsync_ConflictingDefaultsOptions_ReturnsInvalidUrlAndNeverRegisters()
	{
		TestRun run = await RunBootstrapAsync(["--register", "--unregister"]);

		_ = run.ExitCode.Should().Be(ExitCodes.InvalidUrl);
		_ = run.RegistrationCalls.Should().Be(0);
		_ = run.UnregistrationCalls.Should().Be(0);
	}

	// A malformed selector inside safe mode must not silently fall back to the full
	// recovery contract: the run is reported as failed (no throw), nothing is performed,
	// and the dropped selector is named in the ignored-input warning.
	[Theory]
	[InlineData("--safe-mode", "--safe-mode-action")]
	[InlineData("--safe-mode", "--safe-mode-action", "--no-ui")]
	[InlineData("--safe-mode", "--safe-mode-action", "   ")]
	public async Task RunAsync_SafeModeWithMalformedSelector_FailsWithoutThrowing(params string[] args)
	{
		TestRun run = await RunBootstrapAsync(args);

		_ = run.ExitCode.Should().Be(ExitCodes.SafeModeFailed);
		_ = run.ConsoleOutput.Should().Contain("selector=rejected reason=invalid-selector");
		_ = run.ConsoleOutput.Should().NotContain("unknown-action");
		_ = run.Warnings.Should().Contain(w => w.Contains("invalid selector makes the run fail", StringComparison.Ordinal));
		_ = run.Warnings.Should().NotContain(w => w.Contains("the rest is ignored", StringComparison.Ordinal));
		_ = run.Warnings.Should().Contain(w => w.Contains("invalid --safe-mode-action selector", StringComparison.Ordinal));
	}

	// With nothing else on the command line the warning must not claim other input was
	// ignored; with other input it names that input and says it is ignored.
	[Theory]
	[InlineData(false, "--safe-mode", "--safe-mode-action")]
	// A blank value is not taken as the selector, so it is a leftover positional argument
	// and the warning correctly names it as ignored.
	[InlineData(true, "--safe-mode", "--safe-mode-action", "   ")]
	[InlineData(true, "--safe-mode", "--safe-mode-action", "--help")]
	[InlineData(true, "--safe-mode", "--safe-mode-action", "--register")]
	public async Task RunAsync_SafeModeWithMalformedSelector_WarnsAboutOtherInputOnlyWhenPresent(bool hasOtherInput, params string[] args)
	{
		TestRun run = await RunBootstrapAsync(args);

		_ = run.ExitCode.Should().Be(ExitCodes.SafeModeFailed);
		string warning = run.Warnings.Should().ContainSingle(w => w.Contains("invalid --safe-mode-action selector", StringComparison.Ordinal)).Which;
		_ = warning.Contains("other input is ignored", StringComparison.Ordinal).Should().Be(hasOtherInput);
	}

	// --safe-mode-action without --safe-mode used to print help and exit NoArgs (2),
	// performing nothing and telling the user nothing. It is now a usage error: exit 3
	// like any other invalid combination, one warning line naming the missing flag, no
	// help dump, and the action value never echoed.
	[Theory]
	[InlineData("--safe-mode-action", "reset-defaults")]
	[InlineData("https://example.com", "--safe-mode-action", "reset-defaults")]
	// --settings is NOT an exemption: the selector still selects nothing, and a parser that
	// let it through would return Settings=true, which Bootstrap answers with help + exit 2.
	[InlineData("--settings", "--safe-mode-action", "reset-defaults")]
	public async Task RunAsync_SafeModeActionWithoutSafeMode_ReturnsUsageErrorAndNamesTheMissingFlag(params string[] args)
	{
		TestRun run = await RunBootstrapAsync(args);

		_ = run.ExitCode.Should().Be(ExitCodes.InvalidUrl);
		_ = run.ExitCode.Should().Be(3);
		_ = run.Warnings.Should().ContainSingle(w => w.Contains("--safe-mode-action requires --safe-mode", StringComparison.Ordinal));
		_ = run.Warnings.Should().OnlyContain(w => !w.Contains("reset-defaults", StringComparison.Ordinal));
		_ = run.ConsoleOutput.Should().NotContain(HelpText.Render());
		_ = run.ConsoleOutput.Should().NotContain("INGRESS_ACCEPTED");
	}

	// 0A-4: a default-apps action routes to its runner path. The fake service
	// reports success, so the exit code is the runner's 0.
	[Fact]
	public async Task RunAsync_RegisterOption_RoutesToRegistrationAndReturnsSuccess()
	{
		TestRun run = await RunBootstrapAsync(["--register"]);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.RegistrationCalls.Should().Be(1);
	}

	[Fact]
	public async Task RunAsync_VerifyDefaultsOption_RoutesToStatusWithoutRegistering()
	{
		TestRun run = await RunBootstrapAsync(["--verify-defaults"]);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.RegistrationCalls.Should().Be(0);
		_ = run.StatusCalls.Should().BeGreaterOrEqualTo(1);
	}

	// 0A-4: a URL combined with a default-apps action is ignored with a
	// value-free warning; the action still runs and no URL surfaces in logs.
	[Fact]
	public async Task RunAsync_RegisterWithUrl_RegistersAndWarnsWithoutValue()
	{
		TestRun run = await RunBootstrapAsync(["https://example.com", "--register"]);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.RegistrationCalls.Should().Be(1);
		_ = run.Warnings.Should().ContainSingle(w => w.Contains("ignored", StringComparison.Ordinal));
		_ = run.Warnings.Should().OnlyContain(w => !w.Contains("example.com", StringComparison.Ordinal));
	}

	// 0A-4: a command line containing --help NEVER mutates anything, even when a
	// registration flag came along.
	[Fact]
	public async Task RunAsync_HelpWithRegister_PrintsHelpAndNeverRegisters()
	{
		TestRun run = await RunBootstrapAsync(["--register", "--help"]);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.ConsoleOutput.Should().Contain("Usage:");
		_ = run.RegistrationCalls.Should().Be(0);
		_ = run.UnregistrationCalls.Should().Be(0);
	}

	// 0A-4: safe mode still wins over a default-apps action; the action is named
	// among the ignored input, never executed.
	[Fact]
	public async Task RunAsync_SafeModeWithRegister_SafeModeWinsAndNamesTheIgnoredOption()
	{
		TestRun run = await RunBootstrapAsync(["--safe-mode", "--register"]);

		_ = run.ExitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = run.RegistrationCalls.Should().Be(0);
		_ = run.Warnings.Should().ContainSingle(w => w.Contains("--register", StringComparison.Ordinal));
	}

	// (F4) No arguments -> 2, asserted through Bootstrap. Previously only the constant
	// was covered, so nothing pinned that the empty-args path is what returns it.
	[Fact]
	public async Task RunAsync_NoArguments_PrintsHelpAndReturnsNoArgs()
	{
		TestRun run = await RunBootstrapAsync([]);

		_ = run.ExitCode.Should().Be(ExitCodes.NoArgs);
		_ = run.ExitCode.Should().Be(2);
		_ = run.ConsoleOutput.Should().Contain("Usage:");
	}

	[Fact]
	public async Task RunAsync_HelpWithUrl_PrintsHelpAndWarnsAboutIgnoredUrl()
	{
		TestRun run = await RunBootstrapAsync(["https://example.com", "--help"]);

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.ConsoleOutput.Should().Contain("Usage:");
		_ = run.Warnings.Should().ContainSingle(w =>
			w.Contains("URL", StringComparison.Ordinal)
			&& w.Contains("ignored", StringComparison.Ordinal));
		_ = run.Warnings.Should().OnlyContain(w => !w.Contains("example.com", StringComparison.Ordinal));
	}

	// 0A-5: a malformed --portable-data is a malformed command line, so it travels
	// the mechanism that already exists — ArgumentException from the parser, mapped
	// by the catch in Bootstrap to ExitCodes.InvalidUrl (3). No new exit code, and
	// the path never reaches a log line (a data root is the user's data, like a URL).
	[Theory]
	[InlineData("--portable-data")]
	[InlineData("--portable-data", "--no-ui")]
	[InlineData("--portable-data", "")]
	[InlineData("--portable-data", "   ")]
	public async Task RunAsync_MalformedPortableDataOption_ReturnsInvalidUrlExitCode(params string[] args)
	{
		TestRun run = await RunBootstrapAsync(args);

		_ = run.ExitCode.Should().Be(ExitCodes.InvalidUrl);
		_ = run.ExitCode.Should().Be(3);
	}

	[Fact]
	public async Task RunAsync_PortableDataOptionWithoutValueAfterPath_NeverEchoesThePath()
	{
		TestRun run = await RunBootstrapAsync([@"D:\BroChoosData", "--portable-data"], urlValid: false);

		_ = run.ExitCode.Should().Be(ExitCodes.InvalidUrl);
		_ = run.Warnings.Should().OnlyContain(w => !w.Contains(@"D:\BroChoosData", StringComparison.Ordinal));
		_ = run.ConsoleOutput.Should().NotContain(@"D:\BroChoosData");
	}

	// 0A-5: safe mode still wins and still names only KINDS of ignored input. The
	// data root travels the same privacy rule as the URL: it is the user's data, so
	// neither the warning nor stdout may carry it. Safe mode also gains no storage
	// behavior from this slice — the runner (W8.2) DESCRIBES the recovery contract
	// through the provider and performs nothing on a bare run, so this invocation
	// never provisions the parsed root.
	[Fact]
	public async Task RunAsync_SafeModeWithPortableDataOption_SafeModeWinsAndNeverEchoesThePath()
	{
		TestRun run = await RunBootstrapAsync(["--safe-mode", "--portable-data", @"D:\BroChoosData"]);

		_ = run.ExitCode.Should().Be(ExitCodes.SafeModeOk);
		// W8.2: the parsed root now travels to the recovery provider, so Bootstrap no
		// longer emits an ignored-input warning for it. The privacy assertion is what
		// must hold either way: the value never reaches a warning or stdout.
		_ = run.Warnings.Should().NotContain(w => w.Contains(@"D:\BroChoosData", StringComparison.Ordinal));
		_ = run.ConsoleOutput.Should().NotContain(@"D:\BroChoosData");
	}

	// 0A-5: this slice adds a CLI VALUE and nothing else. No Bootstrap branch may
	// provision the data root or otherwise touch it: eager provisioning of
	// Root/backups/logs/temp belongs to StorageRootResolver, which nothing here
	// constructs, and a Bootstrap that created the directory would leave one behind
	// for --help (a command line that must never mutate anything) and for a rejected
	// invocation alike.
	//
	// Every accepting branch is listed because the branches are independent: help,
	// a default-apps action, the no-URL fallback and the URL path each return from
	// their own block, so a regression in one is invisible to the others. The
	// candidate lives under the temp directory and does not exist, so its appearance
	// is the assertion. The same cases carry the privacy assertion, since the value
	// is the user's data on every one of these paths, not only in safe mode.
	[Theory]
	[InlineData("")]
	[InlineData("--help")]
	[InlineData("--register")]
	[InlineData("--verify-defaults")]
	[InlineData("https://example.com")]
	public async Task RunAsync_PortableDataOption_ProvisionsNothingAndNeverEchoesThePath(string companionArgument)
	{
		string candidate = Path.Combine(Path.GetTempPath(), $"brochoos-bootstrap-purity-{Guid.NewGuid():N}");
		// An empty companion is the no-URL fallback: --portable-data alone parses, so
		// Bootstrap prints help and returns NoArgs.
		string[] args = companionArgument.Length == 0
			? ["--portable-data", candidate]
			: ["--portable-data", candidate, companionArgument];

		try
		{
			TestRun run = await RunBootstrapAsync(args);

			_ = Directory.Exists(candidate).Should().BeFalse();
			_ = File.Exists(candidate).Should().BeFalse();
			_ = run.ConsoleOutput.Should().NotContain(candidate);
			// NotContain, not the OnlyContain idiom the other privacy assertions in this
			// file use: every branch listed above emits NO warning at all, and
			// OnlyContain FAILS on an empty collection. The property being pinned is
			// "no warning carries the value", which an empty warning list satisfies.
			_ = run.Warnings.Should().NotContain(w => w.Contains(candidate, StringComparison.Ordinal));
		}
		finally
		{
			// Reachable only if a regression provisioned it; leaves temp clean either way.
			if (Directory.Exists(candidate))
			{
				Directory.Delete(candidate, recursive: true);
			}
		}
	}

	// W8.2: the safe-mode selector reaches the runner, and the parsed --portable-data
	// value is handed to the recovery provider so the acting side resolves the right
	// root. Both facts are asserted through Bootstrap, which is the wiring under test.
	[Fact]
	public async Task RunAsync_SafeModeWithAction_ForwardsTheSelectorAndPortableRootToRecovery()
	{
		RecordingRecoveryProvider provider = new();
		Bootstrap bootstrap = BuildBootstrap(provider);

		int exitCode = await RunCaptured(bootstrap, ["--safe-mode", "--safe-mode-action", "diagnostics-export", "--portable-data", @"D:\BroChoosData"]);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = provider.LastActions!.Performed.Should().Equal("diagnostics-export");
		_ = provider.LastPortableDataArgument.Should().Be(@"D:\BroChoosData");
	}

	// The bare rescue hatch still publishes the full contract and performs nothing —
	// and it never even obtains the acting side, so it cannot provision a root.
	[Fact]
	public async Task RunAsync_SafeModeWithoutAction_PerformsNoRecoveryAction()
	{
		RecordingRecoveryProvider provider = new();
		Bootstrap bootstrap = BuildBootstrap(provider);

		int exitCode = await RunCaptured(bootstrap, ["--safe-mode"]);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = provider.CreateCalls.Should().Be(0, "a bare safe-mode run must not compose a provisioning recovery environment");
	}

	// A Bootstrap whose safe-mode runner is backed by a recording provider, so the
	// selector/portable-data wiring can be observed rather than inferred.
	private static Bootstrap BuildBootstrap(RecordingRecoveryProvider provider)
	{
		FakePicker fakePicker = new();
		UrlRunner urlRunner = new(
			new ListLogger<UrlRunner>(),
			new FakeSingleInstanceGuard(),
			new FakeBrowserDetector(),
			fakePicker);
		SafeModeRunner safeModeRunner = new(new ListLogger<SafeModeRunner>(), provider);
		DefaultsRunner defaultsRunner = new(
			new ListLogger<DefaultsRunner>(),
			new FakeCapabilityRegistrationService(),
			new FakeDefaultAppsStatusService(),
			new FakeDefaultAppsGuide());

		return new Bootstrap(
			new ListLogger<Bootstrap>(),
			new CliParser(new FakeUrlValidator(true)),
			urlRunner,
			safeModeRunner,
			defaultsRunner);
	}

	private static async Task<int> RunCaptured(Bootstrap bootstrap, string[] args)
	{
		TextWriter originalOut = Console.Out;
		StringWriter console = new();

		try
		{
			Console.SetOut(console);
			return await bootstrap.RunAsync(args, CancellationToken.None);
		}
		finally
		{
			Console.SetOut(originalOut);
		}
	}

	// Records what the runner actually asked the acting side to do, and forwards the
	// parsed portable-data value so the wiring can be asserted.
	private sealed class RecordingRecoveryProvider : ISafeModeRecoveryActionsProvider
	{
		public RecordingActions? LastActions { get; private set; }

		public int CreateCalls { get; private set; }

		public string? LastPortableDataArgument { get; private set; }

		public SafeModeActionOutcome Describe(string actionId)
		{
			return new SafeModeActionOutcome(actionId, SafeModeActionStatus.Ready, "ready: recorded");
		}

		public ISafeModeRecoveryActions Create(string? portableDataArgument)
		{
			CreateCalls++;
			LastPortableDataArgument = portableDataArgument;
			LastActions = new RecordingActions();
			return LastActions;
		}
	}

	private sealed class RecordingActions : ISafeModeRecoveryActions
	{
		private readonly List<string> _performed = [];

		public IReadOnlyList<string> Performed => _performed;

		public SafeModeActionOutcome Perform(string actionId)
		{
			_performed.Add(actionId);
			return new SafeModeActionOutcome(actionId, SafeModeActionStatus.Completed, "recorded");
		}
	}

	private static async Task<TestRun> RunBootstrapAsync(string[] args, bool urlValid = true, int pickerExit = ExitCodes.Success)
	{
		ListLogger<Bootstrap> bootstrapLogger = new();
		CliParser parser = new(new FakeUrlValidator(urlValid));
		FakePicker fakePicker = new() { ExitCode = pickerExit };
		UrlRunner urlRunner = new(
			new ListLogger<UrlRunner>(),
			new FakeSingleInstanceGuard(),
			new FakeBrowserDetector(),
			fakePicker);
		SafeModeRunner safeModeRunner = new(
			new ListLogger<SafeModeRunner>(),
			new UnavailableSafeModeRecoveryActionsProvider("the platform recovery implementation is not composed"));
		FakeCapabilityRegistrationService registration = new();
		FakeDefaultAppsStatusService status = new();
		FakeDefaultAppsGuide guide = new();
		DefaultsRunner defaultsRunner = new(new ListLogger<DefaultsRunner>(), registration, status, guide);
		Bootstrap bootstrap = new(bootstrapLogger, parser, urlRunner, safeModeRunner, defaultsRunner);

		TextWriter originalOut = Console.Out;
		StringWriter console = new();

		try
		{
			Console.SetOut(console);
			int exitCode = await bootstrap.RunAsync(args, CancellationToken.None);
			return new TestRun(
				exitCode,
				console.ToString(),
				[.. bootstrapLogger.Warnings],
				registration.RegisterCalls,
				registration.UnregisterCalls,
				status.GetStatusCalls);
		}
		finally
		{
			Console.SetOut(originalOut);
		}
	}

	private sealed record TestRun(
		int ExitCode,
		string ConsoleOutput,
		IReadOnlyList<string> Warnings,
		int RegistrationCalls,
		int UnregistrationCalls,
		int StatusCalls);

	// Captures warnings so precedence rules can be asserted without a logging backend.
	private sealed class ListLogger<T> : ILogger<T>
	{
		private readonly List<string> _warnings = [];

		public IReadOnlyList<string> Warnings => _warnings;

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return logLevel >= LogLevel.Warning;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (logLevel >= LogLevel.Warning)
			{
				_warnings.Add(formatter(state, exception));
			}
		}
	}

	private sealed class FakeSingleInstanceGuard : ISingleInstanceGuard
	{
		public Task<SingleInstanceRole> AcquireOrForwardAsync(ActivationRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(SingleInstanceRole.Primary);
		}
	}

	private sealed class FakeUrlValidator(bool urlValid) : IUrlValidator
	{
		private readonly bool _urlValid = urlValid;

		public bool IsAcceptable(string raw, out string normalized)
		{
			normalized = _urlValid && Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
				? uri.AbsoluteUri
				: string.Empty;

			return _urlValid;
		}
	}

	// Bootstrap tests only cover precedence and never reach the URL path, so the
	// browser seams stay inert here; UrlRunnerTests covers detection and launching.
	private sealed class FakeBrowserDetector : IBrowserDetector
	{
		public IReadOnlyList<BrowserCandidate> Detect()
		{
			return [];
		}
	}

	private sealed class FakeBrowserLauncher : IBrowserLauncher
	{
		public LaunchResult Launch(BrowserCandidate target, string url)
		{
			return LaunchResult.Succeeded(target.BrowserId);
		}
	}

	// Bootstrap tests never reach the URL path, so the picker seam stays inert here;
	// UrlRunnerTests covers the picker routing.
	private sealed class FakePicker : IPicker
	{
		public int ExitCode { get; set; } = ExitCodes.Success;

		public Task<int> ShowAsync(PickerRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(ExitCode);
		}
	}

	// 0A-4: call-counting fakes for the default-apps surface. The status service
	// reports one static entry with no selection; contract tests of the real
	// three-step mapping live in the platform test project.
	private sealed class FakeCapabilityRegistrationService : ICapabilityRegistrationService
	{
		public int RegisterCalls { get; private set; }
		public int UnregisterCalls { get; private set; }

		public CapabilityRegistrationResult RegisterAll()
		{
			RegisterCalls++;
			return new CapabilityRegistrationResult(true, [], [], null, null);
		}

		public CapabilityRegistrationResult UnregisterAll()
		{
			UnregisterCalls++;
			return new CapabilityRegistrationResult(true, [], [], null, null);
		}
	}

	private sealed class FakeDefaultAppsStatusService : IDefaultAppsStatusService
	{
		public int GetStatusCalls { get; private set; }

		public IReadOnlyList<DefaultAppsEntryStatus> GetStatus()
		{
			GetStatusCalls++;
			return
			[
				new DefaultAppsEntryStatus(DefaultAppsTarget.Http, "http", "BroChoosURL", false, null, "no activation recorded yet"),
				new DefaultAppsEntryStatus(DefaultAppsTarget.Https, "https", "BroChoosURL", false, null, "no activation recorded yet"),
				new DefaultAppsEntryStatus(DefaultAppsTarget.UrlFile, ".url", "BroChoosURL.File", false, null, "no activation recorded yet"),
			];
		}
	}

	private sealed class FakeDefaultAppsGuide : IDefaultAppsGuide
	{
		public int OpenCalls { get; private set; }

		public bool TryOpenDefaultAppsPage()
		{
			OpenCalls++;
			return true;
		}
	}
}
