namespace BroChoos.Core.Routing;

// W7.2: the ONE place a run's URL becomes the evaluator's RoutingTarget. Kept pure and in
// Core so rule evaluation and its inputs are testable without a UI, a window or a process.
//
// ONLY HTTP/HTTPS IS ADMITTED, and that is a contract rather than a narrowing: the picker
// only ever routes those two schemes (the CLI/URL validator already refuses the rest), and
// a rule must never be matched against a file:, javascript: or custom-scheme string that
// happens to reach here. An unparseable or non-HTTP(S) URL yields null, which the caller
// must treat as "no target, no suggestion" — never as an empty target that could match a
// catch-all. There is no catch-all match type in Phase 0, so "no target" is simply "no
// rules evaluated" and every card falls to the visible AllChoices reason.
public static class RoutingTargetFactory
{
	public static RoutingTarget? FromUrl(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			return null;
		}

		if (!(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
		{
			return null;
		}

		// Host is lower-cased ONCE here because HostEquals/HostSuffix compare it
		// OrdinalIgnoreCase and a rules author should not have to guess which spelling the
		// picker happened to carry. Path keeps its case because PathPrefix/PathContains are
		// Ordinal by design. Query is left exactly as it appeared: QueryContains is a
		// deliberate literal substring test and re-encoding it would change what it means.
		string host = uri.Host.Trim().ToLowerInvariant();
		string path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
		string? query = string.IsNullOrEmpty(uri.Query) ? null : uri.Query.TrimStart('?');

		return new RoutingTarget(url, host, path, query);
	}
}
