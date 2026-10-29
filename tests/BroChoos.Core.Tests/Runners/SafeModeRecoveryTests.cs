using BroChoos.Core.Runners;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Core.Tests.Runners;

// W8.2 RED-first behavioural suite. The prior tests asserted only the printed
// strings and the exit code, which is exactly why a runner that executed nothing
// stayed green. These tests assert BEHAVIOUR:
//
//   * a bare run DESCRIBES every route through the provider and PERFORMS none of
//     them (Safe Mode is a non-mutating entry point; describing must not provision);
//   * a selected action is actually PERFORMED, and its real outcome is reported;
//   * a no-op route reports unavailable and never claims available/completed;
//   * a requested action that fails makes the exit code reflect the failure.
//
// Redirects Console.Out; see ConsoleCaptureCollection.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class SafeModeRecoveryTests
{
	[Fact]
	public async Task RunAsync_BareSafeMode_DescribesEveryActionAndPerformsNone()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions);
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		int exitCode = await RunCaptured(runner, SafeModeRequest.All);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = provider.Described.Should().Equal(SafeModeActionIds.All);
		_ = actions.Performed.Should().BeEmpty("a bare safe-mode run must not perform any recovery action");
		_ = provider.CreateCalls.Should().Be(0, "describing must not provision a storage root");
	}

	[Fact]
	public async Task RunAsync_ReportsTheDescribedOutcomeOfEachAction()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions)
		{
			DescribeHandler = actionId => new SafeModeActionOutcome(actionId, SafeModeActionStatus.Ready, "ready: described-by-test"),
		};
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		(string output, int exitCode) = await RunCapturedWithOutput(runner, SafeModeRequest.All);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = output.Should().Contain("action=diagnostics-export status=ready reason=ready: described-by-test");
		_ = output.Should().Contain("action=synthetic-link-probe status=ready reason=ready: described-by-test");
		// The old placeholder line must be GONE: nothing may report "available".
		_ = output.Should().NotContain("status=available");
	}

	// F1 (W8.2 interface truthfulness): the status vocabulary MUST NOT let a DESCRIBE
	// render a route as performed. Ready is the describe-time status — "offered; it
	// runs when selected" — and it is the ONLY status a bare contract report may carry
	// for a route this build CAN offer. If the mapping ever folds Ready back into
	// completed, this fails even though the enum member still exists.
	[Fact]
	public async Task RunAsync_BareDescribe_RendersReadyAndNeverCompleted()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions)
		{
			DescribeHandler = actionId => new SafeModeActionOutcome(actionId, SafeModeActionStatus.Ready, "offered; runs when selected"),
		};
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		(string output, int exitCode) = await RunCapturedWithOutput(runner, SafeModeRequest.All);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = output.Should().Contain("action=diagnostics-export status=ready reason=offered; runs when selected");
		// A bare run performs NOTHING, so no line may claim a completed recovery.
		_ = output.Should().NotContain("status=completed");
		_ = actions.Performed.Should().BeEmpty();
		// completed=0 proves the describe half never counts as a performed action.
		_ = output.Should().Contain("[safe-mode] summary=actions-performed completed=0");
	}

	// F2 (residual contract gap closed): the RUNNER itself must not trust a
	// DESCRIBE-time Completed as an effect claim. Before this hardening, only the
	// production provider's own implementation refused to answer Completed from
	// Describe, so a provider defect or a lying provider made a bare --safe-mode
	// print `status=completed` and exit SafeModeOk for routes nothing had
	// performed. The describe path now rejects that answer as the untrustworthy
	// report it is: the route is Failed, no line claims completed, and the run
	// does not exit SafeModeOk. The perform path is deliberately untouched, so a
	// real Completed from Perform still yields SafeModeOk (see the selector test
	// below).
	[Fact]
	public async Task RunAsync_BareDescribe_RejectsAProviderSuppliedCompletedAsAnEffectClaim()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions)
		{
			DescribeHandler = actionId => new SafeModeActionOutcome(actionId, SafeModeActionStatus.Completed, "nothing was performed by this describe"),
		};
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		(string output, int exitCode) = await RunCapturedWithOutput(runner, SafeModeRequest.All);

		_ = exitCode.Should().Be(
			ExitCodes.SafeModeFailed,
			"a describe answer that claims an effect cannot be trusted as a successful contract report");
		_ = output.Should().NotContain("status=completed");
		_ = output.Should().Contain("action=diagnostics-export status=failed");
		_ = output.Should().Contain("read-only description");
		_ = actions.Performed.Should().BeEmpty("a bare run performs no recovery action");
		_ = output.Should().Contain("[safe-mode] summary=actions-performed completed=0");
	}

	// The other half of F1: a SELECTED action's success is still measured by
	// Completed. Ready is a describe-time offer, and a selection that only reported
	// Ready did NOT perform the user's request, so the exit code must reflect that.
	[Fact]
	public async Task RunAsync_WhenASelectedActionReportsReady_ReturnsSafeModeFailed()
	{
		RecordingActions actions = new()
		{
			PerformHandler = actionId => new SafeModeActionOutcome(actionId, SafeModeActionStatus.Ready, "offered but not performed"),
		};
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, new FixedProvider(actions));

		(string output, int exitCode) = await RunCapturedWithOutput(
			runner,
			SafeModeRequest.Only(SafeModeActionIds.DiagnosticsExport));

		_ = exitCode.Should().Be(ExitCodes.SafeModeFailed);
		_ = output.Should().Contain("action=diagnostics-export status=ready");
	}

	[Fact]
	public async Task RunAsync_WhenAnActionIsUnavailable_ReportsUnavailableWithAReasonAndDoesNotClaimAvailable()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions)
		{
			DescribeHandler = actionId => actionId == SafeModeActionIds.RestoreBackup
				? new SafeModeActionOutcome(actionId, SafeModeActionStatus.Unavailable, "no backup journey exists in this build")
				: new SafeModeActionOutcome(actionId, SafeModeActionStatus.Ready, "ok"),
		};
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		(string output, int exitCode) = await RunCapturedWithOutput(runner, SafeModeRequest.All);

		// An honestly-unavailable integration is not a run failure for the full
		// contract: the contract was reported truthfully and nothing failed.
		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = output.Should().Contain("action=restore-backup status=unavailable reason=no backup journey exists in this build");
		_ = output.Should().NotContain("status=available");
	}

	[Fact]
	public async Task RunAsync_WhenAnAttemptedActionFails_ReturnsSafeModeFailed()
	{
		RecordingActions actions = new()
		{
			PerformHandler = actionId => actionId == SafeModeActionIds.ResetDefaults
				? new SafeModeActionOutcome(actionId, SafeModeActionStatus.Failed, "defaults could not be written")
				: new SafeModeActionOutcome(actionId, SafeModeActionStatus.Completed, "ok"),
		};
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, new FixedProvider(actions));

		(string output, int exitCode) = await RunCapturedWithOutput(
			runner,
			SafeModeRequest.Only(SafeModeActionIds.ResetDefaults));

		_ = exitCode.Should().Be(ExitCodes.SafeModeFailed);
		_ = output.Should().Contain("action=reset-defaults status=failed");
	}

	[Fact]
	public async Task RunAsync_WithSelector_PerformsOnlyTheSelectedAction()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions);
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		(string output, int exitCode) = await RunCapturedWithOutput(
			runner,
			SafeModeRequest.Only(SafeModeActionIds.DiagnosticsExport));

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = actions.Performed.Should().Equal(SafeModeActionIds.DiagnosticsExport);
		_ = provider.Described.Should().BeEmpty();
		_ = output.Should().Contain("action=diagnostics-export status=completed");
		_ = output.Should().Contain("action=restore-backup status=not-requested");
	}

	[Fact]
	public async Task RunAsync_WithSelectorForAnUnavailableAction_ReturnsSafeModeFailed()
	{
		RecordingActions actions = new()
		{
			PerformHandler = actionId => new SafeModeActionOutcome(actionId, SafeModeActionStatus.Unavailable, "this build cannot do that"),
		};
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, new FixedProvider(actions));

		int exitCode = await RunCaptured(runner, SafeModeRequest.Only(SafeModeActionIds.OpenDataFolder));

		// The user explicitly asked for this action. It did not happen, so the run
		// must not report success.
		_ = exitCode.Should().Be(ExitCodes.SafeModeFailed);
	}

	[Fact]
	public async Task RunAsync_WithUnknownSelector_ReturnsSafeModeFailedWithoutEchoingTheValue()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions);
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		(string output, int exitCode) = await RunCapturedWithOutput(
			runner,
			SafeModeRequest.Only("../../etc/passwd"));

		_ = exitCode.Should().Be(ExitCodes.SafeModeFailed);
		_ = actions.Performed.Should().BeEmpty();
		_ = provider.Described.Should().BeEmpty();
		_ = output.Should().NotContain("passwd");
		_ = output.Should().Contain("selector=rejected reason=unknown-action");
	}

	[Fact]
	public async Task RunAsync_WithInvalidSelectorState_FailsWithPreciseReasonAndPerformsNothing()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions);
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		(string output, int exitCode) = await RunCapturedWithOutput(runner, SafeModeRequest.InvalidSelector);

		_ = exitCode.Should().Be(ExitCodes.SafeModeFailed);
		_ = actions.Performed.Should().BeEmpty();
		_ = provider.Described.Should().BeEmpty();
		_ = output.Should().Contain("selector=rejected reason=invalid-selector");
		_ = output.Should().NotContain("unknown-action");
	}

	[Fact]
	public async Task RunAsync_WhenTheActingSideThrows_ReportsFailureWithoutLeakingTheMessage()
	{
		RecordingActions actions = new()
		{
			PerformHandler = actionId => actionId == SafeModeActionIds.SyntheticLinkProbe
				? throw new IOException(@"C:\Users\Alice\secret\path.txt could not be read")
				: new SafeModeActionOutcome(actionId, SafeModeActionStatus.Completed, "ok"),
		};
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, new FixedProvider(actions));

		(string output, int exitCode) = await RunCapturedWithOutput(
			runner,
			SafeModeRequest.Only(SafeModeActionIds.SyntheticLinkProbe));

		_ = exitCode.Should().Be(ExitCodes.SafeModeFailed);
		_ = output.Should().Contain("action=synthetic-link-probe status=failed");
		_ = output.Should().Contain("IOException");
		_ = output.Should().NotContain(@"C:\Users\Alice");
		_ = output.Should().NotContain("secret");
	}

	[Fact]
	public async Task RunAsync_CancelledBeforeStart_ReturnsSafeModeFailedAndPerformsNothing()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions);
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);
		using CancellationTokenSource cts = new();
		cts.Cancel();

		int exitCode = await runner.RunAsync(SafeModeRequest.All, cts.Token);

		_ = exitCode.Should().Be(ExitCodes.SafeModeFailed);
		_ = actions.Performed.Should().BeEmpty();
		_ = provider.Described.Should().BeEmpty();
	}

	[Fact]
	public async Task RunAsync_BareSafeMode_StillReportsTheStableEntryContract()
	{
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, new FixedProvider(new RecordingActions()));

		(string output, int exitCode) = await RunCapturedWithOutput(runner, SafeModeRequest.All);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = output.Should().Contain("[safe-mode] entry=accepted");
		_ = output.Should().Contain("[safe-mode] routing=user-requested");
		_ = output.Should().NotContain("config-corruption");
		_ = output.Should().Contain("[safe-mode] fail-safe=enabled");
		_ = output.Should().Contain("[safe-mode] summary=actions-performed");
	}

	// The routing marker states the entry route the CALLER took; the runner never
	// infers it. Only the config-corruption fallback request may claim corruption.
	[Fact]
	public async Task RunAsync_ConfigCorruptionFallback_ReportsCorruptionRecoveryRouting()
	{
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, new FixedProvider(new RecordingActions()));

		(string output, int exitCode) = await RunCapturedWithOutput(runner, SafeModeRequest.ConfigCorruptionFallback);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = output.Should().Contain("[safe-mode] routing=config-corruption-recovery");
		_ = output.Should().NotContain("routing=user-requested");
	}

	[Fact]
	public async Task RunAsync_SelectedAction_ReportsUserRequestedRouting()
	{
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, new FixedProvider(new RecordingActions()));

		(string output, _) = await RunCapturedWithOutput(runner, SafeModeRequest.Only(SafeModeActionIds.DiagnosticsExport));

		_ = output.Should().Contain("[safe-mode] routing=user-requested");
		_ = output.Should().NotContain("config-corruption");
	}

	[Fact]
	public async Task RunAsync_WithSelector_ForwardsThePortableDataArgument()
	{
		RecordingActions actions = new();
		FixedProvider provider = new(actions);
		SafeModeRunner runner = new(NullLogger<SafeModeRunner>.Instance, provider);

		int exitCode = await runner.RunAsync(
			SafeModeRequest.Only(SafeModeActionIds.DiagnosticsExport),
			@"D:\BroChoosData",
			CancellationToken.None);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = provider.LastPortableDataArgument.Should().Be(@"D:\BroChoosData");
	}

	// The default composition must be HONEST: with no acting side wired, every
	// action reports unavailable rather than claiming a recovery that cannot run.
	[Fact]
	public async Task UnavailableActions_ReportEveryActionUnavailable()
	{
		SafeModeRunner runner = new(
			NullLogger<SafeModeRunner>.Instance,
			new UnavailableSafeModeRecoveryActionsProvider("the platform recovery implementation is not composed"));

		(string output, int exitCode) = await RunCapturedWithOutput(runner, SafeModeRequest.All);

		_ = exitCode.Should().Be(ExitCodes.SafeModeOk);
		_ = output.Should().NotContain("status=available");
		_ = output.Should().NotContain("status=completed");
		_ = output.Should().Contain("status=unavailable reason=the platform recovery implementation is not composed");
	}

	private static async Task<int> RunCaptured(SafeModeRunner runner, SafeModeRequest request)
	{
		(string _, int exitCode) = await RunCapturedWithOutput(runner, request);
		return exitCode;
	}

	private static async Task<(string Output, int ExitCode)> RunCapturedWithOutput(SafeModeRunner runner, SafeModeRequest request)
	{
		TextWriter originalOut = Console.Out;
		StringWriter output = new();

		try
		{
			Console.SetOut(output);
			int exitCode = await runner.RunAsync(request, CancellationToken.None);
			return (output.ToString(), exitCode);
		}
		finally
		{
			Console.SetOut(originalOut);
		}
	}

	// The provider split is what keeps a bare run from provisioning: Describe is
	// answered without resolving a storage root, and Create is counted so a test can
	// prove it was never reached.
	private sealed class FixedProvider(ISafeModeRecoveryActions actions) : ISafeModeRecoveryActionsProvider
	{
		private readonly List<string> _described = [];

		public Func<string, SafeModeActionOutcome>? DescribeHandler { get; set; }

		public int CreateCalls { get; private set; }

		public string? LastPortableDataArgument { get; private set; }

		public IReadOnlyList<string> Described => _described;

		public SafeModeActionOutcome Describe(string actionId)
		{
			_described.Add(actionId);
			return DescribeHandler?.Invoke(actionId)
				?? new SafeModeActionOutcome(actionId, SafeModeActionStatus.Ready, "ready: recorded");
		}

		public ISafeModeRecoveryActions Create(string? portableDataArgument)
		{
			CreateCalls++;
			LastPortableDataArgument = portableDataArgument;
			return actions;
		}
	}

	private sealed class RecordingActions : ISafeModeRecoveryActions
	{
		private readonly List<string> _performed = [];

		public Func<string, SafeModeActionOutcome>? PerformHandler { get; set; }

		public IReadOnlyList<string> Performed => _performed;

		public SafeModeActionOutcome Perform(string actionId)
		{
			_performed.Add(actionId);
			return PerformHandler?.Invoke(actionId)
				?? new SafeModeActionOutcome(actionId, SafeModeActionStatus.Completed, "recorded");
		}
	}
}
