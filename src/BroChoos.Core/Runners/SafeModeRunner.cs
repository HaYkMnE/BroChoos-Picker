using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BroChoos.Core.Runners;

// W8.2: REAL Safe Mode recovery, replacing the placeholder that printed six
// `status=available` lines and executed nothing.
//
// WHAT WAS WRONG, stated so the fix is verifiable rather than described: the
// previous runner held a private string[] of six action ids, printed each as
// `status=available`, and returned SafeModeOk. There were no handlers, no file
// or registry operations, and no consumers of the ids — the surface advertised
// six recoveries, none of which worked. That is an interface-truthfulness defect
// (HIGH): a user entering Safe Mode precisely because their configuration is
// broken was told six routes were available and none of them existed.
//
// WHAT THIS RUNNER NOW PROMISES, and nothing more:
//   * It performs the requested action(s) through an injected
//     ISafeModeRecoveryActions, and reports each action's OBSERVED outcome.
//   * It never prints `available` for an action. The status vocabulary is
//     Ready / Completed / Failed / Unavailable / NotRequested, and Unavailable
//     carries a user-readable reason. Ready is the DESCRIBE-time answer for a route
//     this build can perform; it is reported as performed only when Completed.
//   * A requested action that genuinely cannot be performed does not make the
//     run lie: it reports Unavailable, and if a selector asked for it explicitly
//     the exit code is SafeModeFailed, because the thing the user asked for did
//     not happen.
//   * A run with a selector returns SafeModeFailed when that action fails or is
//     unavailable; the full-contract run returns SafeModeFailed only when an
//     attempted action FAILED (an Unavailable integration is still a truthful,
//     successful report of the contract).
//
// SAFE MODE ITSELF STILL TOUCHES NOTHING BY DEFAULT. The runner performs actions
// only when a selector names one. The bare `--safe-mode` run reports the contract
// and executes no recovery action, which is what keeps Safe Mode a non-mutating
// entry point (the actions themselves are wired by the platform composition and
// are invoked one at a time).

// The selector contract, parsed by CliParser and carried here. Kept in this file
// rather than in Cli because it is the runner's input shape and Cli must stay
// free of recovery semantics.
public sealed class SafeModeRunner(
	ILogger<SafeModeRunner> logger,
	ISafeModeRecoveryActionsProvider recoveryActionsProvider)
{
	private static readonly EventId SafeModeRouteEvent = new(7001, "safe-mode.route");

	private readonly ILogger<SafeModeRunner> _logger = logger;
	private readonly ISafeModeRecoveryActionsProvider _recoveryActionsProvider = recoveryActionsProvider;

	// Non-selected entry point preserved for callers that only need the contract
	// report. It performs no action and reports the user-requested route; the
	// config-corruption fallback passes SafeModeRequest.ConfigCorruptionFallback.
	public Task<int> RunAsync(CancellationToken cancellationToken)
	{
		return RunAsync(SafeModeRequest.All, cancellationToken);
	}

	public Task<int> RunAsync(SafeModeRequest request, CancellationToken cancellationToken)
	{
		return RunAsync(request, portableDataArgument: null, cancellationToken);
	}

	public Task<int> RunAsync(SafeModeRequest request, string? portableDataArgument, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (cancellationToken.IsCancellationRequested)
		{
			Console.WriteLine("[safe-mode] entry=rejected reason=cancelled-before-contract");
			_logger.LogWarning("Safe mode start was canceled before recovery contract was shown");
			return Task.FromResult(ExitCodes.SafeModeFailed);
		}

		Console.WriteLine("[safe-mode] entry=accepted");
		// The route is the caller's fact, carried on the request (a closed enum, so
		// no user-supplied value can reach this line).
		Console.WriteLine($"[safe-mode] routing={FormatRoute(request.Route)}");
		Console.WriteLine("[safe-mode] fail-safe=enabled");

		// Context step for the local problem log: closed-enum route + action id only. An
		// unknown selector is never echoed (it is reported as "unknown").
		if (_logger.IsEnabled(LogLevel.Information))
		{
			string actionForLog = request.ActionId is null ? "all"
				: SafeModeActionIds.IsKnown(request.ActionId) ? request.ActionId
				: string.IsNullOrWhiteSpace(request.ActionId) ? "invalid"
				: "unknown";
			_logger.LogInformation(SafeModeRouteEvent, "Safe mode route {Route} action {ActionId}", FormatRoute(request.Route), actionForLog);
		}

		bool selected = request.ActionId is not null;
		if (selected && !SafeModeActionIds.IsKnown(request.ActionId!))
		{
			// A bad selector is a command-line error, not a recovery run: say so without
			// echoing the value (the selector could be anything typed). A missing or blank
			// value (the parser's invalid-selector state) is reported precisely as such,
			// apart from a well-formed id this build does not know.
			bool missingValue = string.IsNullOrWhiteSpace(request.ActionId);
			Console.WriteLine(missingValue
				? "[safe-mode] selector=rejected reason=invalid-selector"
				: "[safe-mode] selector=rejected reason=unknown-action");
			if (missingValue)
			{
				_logger.LogWarning("Safe mode action selector was missing, blank or option-like (value suppressed)");
			}
			else
			{
				_logger.LogWarning("Safe mode action selector was not recognized (value suppressed)");
			}

			return Task.FromResult(ExitCodes.SafeModeFailed);
		}

		ISafeModeRecoveryActions? actions = selected ? ResolveActions(portableDataArgument) : null;

		List<SafeModeActionOutcome> outcomes = [];
		foreach (string actionId in SafeModeActionIds.All)
		{
			if (selected && !string.Equals(actionId, request.ActionId, StringComparison.Ordinal))
			{
				outcomes.Add(new SafeModeActionOutcome(actionId, SafeModeActionStatus.NotRequested, "not selected in this run"));
				continue;
			}

			outcomes.Add(selected ? Perform(actions!, actionId) : Describe(actionId));
		}

		SafeModeRecoveryReport report = new(outcomes);

		foreach (SafeModeActionOutcome outcome in report.Outcomes)
		{
			Console.WriteLine($"[safe-mode] action={outcome.ActionId} status={Format(outcome.Status)} reason={outcome.Reason}");
		}

		int completed = report.Outcomes.Count(o => o.Status == SafeModeActionStatus.Completed);
		int ready = report.Outcomes.Count(o => o.Status == SafeModeActionStatus.Ready);
		int failed = report.Outcomes.Count(o => o.Status == SafeModeActionStatus.Failed);
		int unavailable = report.Outcomes.Count(o => o.Status == SafeModeActionStatus.Unavailable);
		Console.WriteLine($"[safe-mode] summary=actions-performed completed={completed} failed={failed} unavailable={unavailable} ready={ready}");

		// A selected action that did not happen is a failure of the user's explicit
		// request, so the exit code reflects it. A full-contract run only fails when
		// an attempted action failed; Ready (offered, not run) and Unavailable (an
		// integration this build does not own) are truthful reports of the contract,
		// not failures of it.
		bool exitFailure = selected
			? report.Outcomes.First(o => string.Equals(o.ActionId, request.ActionId, StringComparison.Ordinal)).Status
				is not SafeModeActionStatus.Completed
			: report.AnyFailed;

		if (_logger.IsEnabled(LogLevel.Information))
		{
			_logger.LogInformation(
				"Safe mode recovery completed: {Completed} completed, {Failed} failed, {Unavailable} unavailable, {Ready} ready",
				completed,
				failed,
				unavailable,
				ready);
		}

		return Task.FromResult(exitFailure ? ExitCodes.SafeModeFailed : ExitCodes.SafeModeOk);
	}

	private SafeModeActionOutcome Describe(string actionId)
	{
		try
		{
			return ValidateDescribe(actionId, _recoveryActionsProvider.Describe(actionId));
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(failure, "Safe mode action {ActionId} could not be described", actionId);
			return new SafeModeActionOutcome(actionId, SafeModeActionStatus.Unavailable, $"the recovery route could not be inspected ({failure.GetType().Name}; details suppressed)");
		}
	}

	private SafeModeActionOutcome Perform(ISafeModeRecoveryActions actions, string actionId)
	{
		long started = Stopwatch.GetTimestamp();
		try
		{
			SafeModeActionOutcome outcome = actions.Perform(actionId);
			SafeModeActionOutcome validated = Validate(actionId, outcome);

			if (_logger.IsEnabled(LogLevel.Debug))
			{
				_logger.LogDebug("Safe mode action {ActionId} finished as {Status} in {ElapsedMs} ms", actionId, validated.Status, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
			}

			return validated;
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			// A recovery surface must never crash while recovering. The TYPE is
			// reported, never the message: an I/O failure message embeds a full path
			// in installed mode, which carries the Windows account name (D-075/D-097).
			_logger.LogWarning(failure, "Safe mode action {ActionId} threw", actionId);
			return new SafeModeActionOutcome(actionId, SafeModeActionStatus.Failed, $"recovery action failed ({failure.GetType().Name}; details suppressed)");
		}
	}

	// A seam that answers a different question, or declines without a reason, is a
	// defect rather than a result: reporting it as Completed would reinstate exactly
	// the untruthful surface this runner exists to remove.
	private static SafeModeActionOutcome Validate(string actionId, SafeModeActionOutcome? outcome)
	{
		return outcome is null || !string.Equals(outcome.ActionId, actionId, StringComparison.Ordinal)
			? new SafeModeActionOutcome(actionId, SafeModeActionStatus.Failed, "recovery action returned no outcome for the requested action")
			: outcome.Status == SafeModeActionStatus.NotRequested
				? new SafeModeActionOutcome(actionId, SafeModeActionStatus.Failed, "recovery action declined without a reason")
				: outcome;
	}

	// THE RUNNER DOES NOT TRUST A DESCRIBE-TIME Completed, and this is hardening
	// rather than redundancy. Before this method existed, Validate passed a
	// provider-supplied Completed straight through the describe path, so the only
	// thing preventing a bare --safe-mode from printing `status=completed` was the
	// PRODUCTION provider's own restraint (SafeModeRecoveryActionsFactory.Ready).
	// That is a property of one implementation, not of the runner: an independent
	// probe with a provider that answers Completed from Describe made a bare run
	// print six `status=completed` lines and exit SafeModeOk for six routes it had
	// performed none of. A Describe is defined here as read-only — it performs no
	// work and touches nothing — so a Completed answer is a claim the describe path
	// cannot have observed. It is rejected as Failed with a clear reason, which
	// keeps the contract readable and makes the run's exit code reflect that the
	// describe half could not be trusted.
	//
	// THE SELECTED/PERFORM PATH IS DELIBERATELY UNCHANGED: Validate is still what
	// runs there, so a real Completed from Perform still yields SafeModeOk. The
	// hardening is scoped to the describe path because that is where the effect
	// claim is unobservable by construction.
	private static SafeModeActionOutcome ValidateDescribe(string actionId, SafeModeActionOutcome? outcome)
	{
		SafeModeActionOutcome validated = Validate(actionId, outcome);

		return validated.Status == SafeModeActionStatus.Completed
			? new SafeModeActionOutcome(
				actionId,
				SafeModeActionStatus.Failed,
				"the recovery route reported a completed action from a read-only description, which cannot observe an effect; the report was rejected")
			: validated;
	}

	private static string FormatRoute(SafeModeEntryRoute route)
	{
		return route switch
		{
			SafeModeEntryRoute.ConfigCorruptionFallback => "config-corruption-recovery",
			SafeModeEntryRoute.UserRequested => "user-requested",
			_ => "user-requested",
		};
	}

	private static string Format(SafeModeActionStatus status)
	{
		return status switch
		{
			SafeModeActionStatus.Ready => "ready",
			SafeModeActionStatus.Completed => "completed",
			SafeModeActionStatus.Failed => "failed",
			SafeModeActionStatus.Unavailable => "unavailable",
			SafeModeActionStatus.NotRequested => "not-requested",
			_ => "not-requested",
		};
	}

	// Resolution happens per run, not at construction, because composing the
	// platform actions resolves a storage root and that IS provisioning — it must
	// not happen before the parsed --portable-data value is known. A provider that
	// cannot compose an acting side is reported honestly: every action becomes
	// Unavailable, and the run still publishes the contract.
	private ISafeModeRecoveryActions ResolveActions(string? portableDataArgument)
	{
		try
		{
			return _recoveryActionsProvider.Create(portableDataArgument);
		}
		catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(failure, "Safe mode recovery actions could not be composed");
			return new UnavailableSafeModeRecoveryActions(
				$"the recovery environment could not be prepared ({failure.GetType().Name}; details suppressed)");
		}
	}
}
