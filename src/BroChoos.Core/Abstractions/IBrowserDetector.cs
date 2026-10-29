namespace BroChoos.Core.Abstractions;

// D-181 priority for this milestone: known install paths, then registry
// application registration (manual tiers are a later milestone). Implementations
// return candidates in a deterministic priority order.
public interface IBrowserDetector
{
	IReadOnlyList<BrowserCandidate> Detect();
}
