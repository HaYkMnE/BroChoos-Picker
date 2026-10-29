using BroChoos.Core.Abstractions;

namespace BroChoos.Platform.Windows.Browsers;

// W6.3 (plan `Task W6.3`, spec 3.5/3.6, Gate 0B criterion 0B-1) launch matrix model.
//
// This is deliberately PLATFORM-NEUTRAL DATA plus a PURE builder: it decides WHICH
// families a machine can launch and records what happened. It never starts a
// process itself РІР‚вЂќ the live test wires real launchers in and feeds their typed
// results here, while unit tests feed synthetic ones. That split is what lets the
// "absent browser is NotAvailable" rule be proven without opening any browser.

// A declared launch family row: one vendor family at a fixed channel. The plan
// requires Chrome/Edge/Firefox/Brave to stay distinguishable rather than merged.
public sealed record LaunchFamilyTarget(
	string Family,
	string DisplayName,
	string Channel,
	string ExePath,
	string? Version);

// Availability is explicit machine state, not an inference from a launch attempt:
//   Available    -> the executable was found on this machine
//   NotAvailable -> the executable was NOT found; this row is never attempted and
//                   can never be reported as launched
public enum LaunchTargetAvailability
{
	Available,
	NotAvailable,
}

// The immutable request handed to the builder. Outcomes are keyed by family and are
// present only for families that were actually attempted.
public sealed record LaunchMatrixRequest(
	string Url,
	IReadOnlyDictionary<string, LaunchFamilyTarget> Targets,
	IReadOnlyDictionary<string, LaunchStatus>? Outcomes = null);

// One rendered matrix row.
//   Attempted == true  -> an outcome was recorded for a present family; Outcome is
//                         that recorded typed status.
//   Attempted == false -> either the family was absent (Availability NotAvailable,
//                         Outcome null), or it was present with no recorded outcome
//                         (Availability Available, Outcome Unknown РІР‚вЂќ a missing
//                         record never reads as a pass).
public sealed record LaunchMatrixRow(
	string Family,
	string DisplayName,
	string Channel,
	string? ExePath,
	string? Version,
	LaunchTargetAvailability Availability,
	bool Attempted,
	LaunchStatus? Outcome,
	string? AbsenceReason);

// The whole matrix. IsComplete mirrors the acceptance-gate reading: true only when
// every required family was Available (i.e. the machine could attempt all four).
// A machine missing a family yields IsComplete=false and a non-empty MissingFamilies
// so a partial run can never be laundered into a full 0B-1 pass.
public sealed record LaunchMatrix(
	IReadOnlyList<LaunchMatrixRow> Rows,
	IReadOnlyList<string> MissingFamilies,
	bool IsComplete,
	int AvailableCount,
	int NotAvailableCount,
	int AttemptedCount)
{
	public LaunchMatrixRow RowFor(string family)
	{
		return Rows.First(row => string.Equals(row.Family, family, StringComparison.Ordinal));
	}
}

public static class LaunchMatrixBuilder
{
	// The Gate 0B family set and the order the acceptance record lists them in.
	public static readonly IReadOnlyList<string> RequiredFamilies = ["chrome", "edge", "firefox", "brave"];

	public const string AbsenceReasonNotAvailable = "not-available-on-machine";

	public static LaunchMatrix Build(LaunchMatrixRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		IReadOnlyDictionary<string, LaunchStatus> outcomes =
			request.Outcomes ?? new Dictionary<string, LaunchStatus>(StringComparer.Ordinal);

		List<LaunchMatrixRow> rows = [];

		foreach (string family in RequiredFamilies)
		{
			bool present = request.Targets.TryGetValue(family, out LaunchFamilyTarget? target);

			if (!present || target is null)
			{
				rows.Add(new LaunchMatrixRow(
					family,
					family,
					"unknown",
					ExePath: null,
					Version: null,
					LaunchTargetAvailability.NotAvailable,
					Attempted: false,
					Outcome: null,
					AbsenceReasonNotAvailable));
				continue;
			}

			// Present, but the outcome map carries no record for it. The matrix must not
			// invent an attempt that never happened (Attempted stays false), yet it also
			// must never let a missing record read as a pass: the outcome is Unknown.
			// Only a recorded launch sets Attempted and the real typed status.
			bool attempted = outcomes.TryGetValue(family, out LaunchStatus recorded);
			LaunchStatus? outcome = attempted ? recorded : LaunchStatus.Unknown;

			rows.Add(new LaunchMatrixRow(
				family,
				target.DisplayName,
				target.Channel,
				target.ExePath,
				target.Version,
				LaunchTargetAvailability.Available,
				Attempted: attempted,
				Outcome: outcome,
				AbsenceReason: null));
		}

		string[] missing = [.. RequiredFamilies.Where(f => !request.Targets.ContainsKey(f))];

		return new LaunchMatrix(
			rows,
			missing,
			IsComplete: missing.Length == 0,
			AvailableCount: rows.Count(r => r.Availability == LaunchTargetAvailability.Available),
			NotAvailableCount: rows.Count(r => r.Availability == LaunchTargetAvailability.NotAvailable),
			AttemptedCount: rows.Count(r => r.Attempted));
	}
}
