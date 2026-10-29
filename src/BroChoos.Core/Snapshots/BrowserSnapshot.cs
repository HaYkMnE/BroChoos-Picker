using BroChoos.Core.Abstractions;

namespace BroChoos.Core.Snapshots;

public sealed record BrowserSnapshot(
	IReadOnlyList<BrowserCandidate> Candidates,
	long Generation,
	DateTimeOffset CapturedAtUtc)
{
	public static BrowserSnapshot Empty { get; } = new([], 0, DateTimeOffset.UnixEpoch);

	public bool IsEmpty => Candidates.Count == 0;
}
