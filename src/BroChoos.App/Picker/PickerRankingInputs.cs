using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;

namespace BroChoos.App.Picker;

// W7.2: the ranking inputs a picker run actually has. Kept as a small explicit record so
// the view model states which signals it ranks on, rather than reaching into the session
// for them — the unit tests drive ranking with no session at all.
//
// NONE IS THE HONEST DEFAULT. With no rules document, an Unknown source and no session
// history, every candidate keeps detector order and carries the visible AllChoices reason.
// That is the same behaviour the picker had before ranking existed, which is what makes
// the change additive rather than a behavioural swap.
internal sealed record PickerRankingInputs(
	RoutingDecision Decision,
	SourceConfidence SourceConfidence,
	string? ProbablePreferredBrowserId,
	IReadOnlySet<string>? RecentSessionBrowserIds)
{
	public static PickerRankingInputs None { get; } = new(
		new RoutingDecision(0, null),
		SourceConfidence.Unknown,
		ProbablePreferredBrowserId: null,
		RecentSessionBrowserIds: null);
}
