using BroChoos.Core.Ingress;

namespace BroChoos.Core.Routing;

public sealed record TrackingCleanupOptions(
	bool Enabled = false,
	bool PreserveAffiliateParameters = true,
	int MaxInputLength = ActivationRequest.MaxUrlLength,
	int MaxQueryLength = 4096);

public sealed record TrackingCleanupResult(
	string OriginalUrl,
	string EffectiveUrl,
	bool Changed,
	bool IsBypassed,
	string ReasonCode,
	string? AppliedPackId,
	IReadOnlyList<string> RemovedQueryParameterKeys)
{
	public bool CanUseOriginalForThisLaunch => Changed;
}

public static class TrackingCleanupAuditPolicy
{
	public const string CleanupEventsFileName = "cleanup_events.jsonl";

	public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(90);

	public const int MaxEntries = 2000;
}

public static class TrackingCleanupEngine
{
	private static readonly HashSet<string> GlobalTrackingQueryKeys =
	[
		"utm_source",
		"utm_medium",
		"utm_campaign",
		"utm_term",
		"utm_content",
		"utm_id",
		"utm_name",
		"gclid",
		"fbclid",
		"msclkid",
		"dclid",
		"yclid",
		"gbraid",
		"wbraid",
		"mc_cid",
		"mc_eid",
	];

	private static readonly HashSet<string> HardProtectedQueryKeys =
	[
		"code",
		"state",
		"id_token",
		"access_token",
		"refresh_token",
		"token",
		"session",
		"sessionid",
		"signature",
		"sig",
		"expires",
		"x-amz-signature",
		"x-amz-security-token",
		"x-amz-credential",
		"x-amz-date",
	];

	private static readonly HashSet<string> AffiliateOrReferralKeys =
	[
		"ref",
		"ref_",
		"referrer",
		"referer",
		"affiliate",
		"affiliate_id",
		"aff_id",
		"affid",
		"partner",
		"partnerid",
		"tag",
	];

	private static readonly string[] SsoBypassHostSuffixes =
	[
		"accounts.google.com",
		"login.microsoftonline.com",
		"auth0.com",
		"okta.com",
		"appleid.apple.com",
	];

	private static readonly string[] SsoBypassPathHints =
	[
		"/oauth",
		"/oauth2",
		"/signin-oidc",
		"/sso",
		"/login/callback",
	];

	private static readonly IReadOnlyList<DomainPack> DomainPacks =
	[
		new DomainPack("meta-click-ids", ["facebook.com", "instagram.com"], new HashSet<string>(StringComparer.Ordinal) { "fbclid" }),
		new DomainPack("microsoft-click-ids", ["bing.com"], new HashSet<string>(StringComparer.Ordinal) { "msclkid" }),
	];

	public static IReadOnlyList<string> GetShippedDomainPackIds()
	{
		return [.. DomainPacks.Select(pack => pack.PackId)];
	}

	public static TrackingCleanupResult Cleanup(string url, TrackingCleanupOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(url);

		TrackingCleanupOptions effectiveOptions = options ?? new TrackingCleanupOptions();
		if (!effectiveOptions.Enabled)
		{
			return Unchanged(url, "cleanup.disabled");
		}

		if (url.Length > effectiveOptions.MaxInputLength)
		{
			return Unchanged(url, "cleanup.over-limit");
		}

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			return Unchanged(url, "cleanup.malformed");
		}

		if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
		{
			return Unchanged(url, "cleanup.non-http");
		}

		if (ShouldBypassSsoOAuth(uri))
		{
			return new TrackingCleanupResult(
				url,
				url,
				Changed: false,
				IsBypassed: true,
				ReasonCode: "cleanup.sso-bypass",
				AppliedPackId: null,
				RemovedQueryParameterKeys: []);
		}

		int fragmentStart = url.IndexOf('#', StringComparison.Ordinal);
		string fragment = fragmentStart >= 0 ? url[fragmentStart..] : string.Empty;
		string withoutFragment = fragmentStart >= 0 ? url[..fragmentStart] : url;

		int queryStart = withoutFragment.IndexOf('?', StringComparison.Ordinal);
		if (queryStart < 0)
		{
			return Unchanged(url, "cleanup.no-query");
		}

		string prefix = withoutFragment[..queryStart];
		string rawQuery = withoutFragment[(queryStart + 1)..];

		if (rawQuery.Length == 0)
		{
			return Unchanged(url, "cleanup.no-query");
		}

		if (rawQuery.Length > effectiveOptions.MaxQueryLength)
		{
			return Unchanged(url, "cleanup.over-limit");
		}

		string[] segments = rawQuery.Split('&');
		List<string> keptSegments = [];
		List<string> removedKeys = [];
		string? appliedPack = null;

		foreach (string segment in segments)
		{
			if (segment.Length == 0)
			{
				keptSegments.Add(segment);
				continue;
			}

			if (HasInvalidPercentEncoding(segment))
			{
				return Unchanged(url, "cleanup.malformed");
			}

			int equalsIndex = segment.IndexOf('=');
			string keyRaw = equalsIndex >= 0 ? segment[..equalsIndex] : segment;
			if (keyRaw.Length == 0)
			{
				keptSegments.Add(segment);
				continue;
			}

			if (!TryDecodeQueryKey(keyRaw, out string key))
			{
				return Unchanged(url, "cleanup.malformed");
			}

			string keyLower = key.ToLowerInvariant();

			if (IsHardProtected(keyLower))
			{
				keptSegments.Add(segment);
				continue;
			}

			if (effectiveOptions.PreserveAffiliateParameters && AffiliateOrReferralKeys.Contains(keyLower))
			{
				keptSegments.Add(segment);
				continue;
			}

			if (ShouldRemoveFromDomainPack(uri.Host, keyLower, out string? matchedPack))
			{
				appliedPack ??= matchedPack;
				removedKeys.Add(keyLower);
				continue;
			}

			if (GlobalTrackingQueryKeys.Contains(keyLower))
			{
				appliedPack ??= "global-known-tracking";
				removedKeys.Add(keyLower);
				continue;
			}

			keptSegments.Add(segment);
		}

		if (removedKeys.Count == 0)
		{
			return Unchanged(url, "cleanup.no-change");
		}

		string rebuiltQuery = string.Join("&", keptSegments);
		string effectiveUrl = rebuiltQuery.Length == 0
			? prefix + fragment
			: prefix + "?" + rebuiltQuery + fragment;

		return new TrackingCleanupResult(
			url,
			effectiveUrl,
			Changed: true,
			IsBypassed: false,
			ReasonCode: "cleanup.changed",
			AppliedPackId: appliedPack,
			RemovedQueryParameterKeys: removedKeys);
	}

	private static TrackingCleanupResult Unchanged(string url, string reasonCode)
	{
		return new TrackingCleanupResult(
			url,
			url,
			Changed: false,
			IsBypassed: false,
			ReasonCode: reasonCode,
			AppliedPackId: null,
			RemovedQueryParameterKeys: []);
	}

	private static bool TryDecodeQueryKey(string rawKey, out string key)
	{
		try
		{
			key = Uri.UnescapeDataString(rawKey.Replace("+", "%20", StringComparison.Ordinal));
			return true;
		}
		catch (UriFormatException)
		{
			key = string.Empty;
			return false;
		}
	}

	private static bool HasInvalidPercentEncoding(string value)
	{
		for (int i = 0; i < value.Length; i++)
		{
			if (value[i] != '%')
			{
				continue;
			}

			if (i + 2 >= value.Length)
			{
				return true;
			}

			if (!Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
			{
				return true;
			}

			i += 2;
		}

		return false;
	}

	private static bool IsHardProtected(string keyLower)
	{
		return HardProtectedQueryKeys.Contains(keyLower)
			|| keyLower.StartsWith("x-amz-", StringComparison.Ordinal)
			|| keyLower.StartsWith("oauth_", StringComparison.Ordinal);
	}

	// A host suffix must match a whole host-label boundary, never a mere string suffix:
	// "okta.com" admits "okta.com" and "acme.okta.com" but must NOT admit "evilokta.com" or
	// "notokta.com". A lookalike that merely ends with the configured string would otherwise
	// inherit a FREE SSO/OAuth bypass and keep its tracked parameters uncleaned — the dangerous
	// direction, since the exemption is what suppresses cleanup.
	//
	// A leading dot in the configured suffix is normalized away: ".okta.com" and "okta.com"
	// denote the SAME host suffix, so both spellings behave identically. A suffix that is empty
	// (or only dots) matches nothing. Callers already lower-case the host, and configured
	// suffixes are lower-case, so the comparison stays ordinal.
	private static bool MatchesHostLabelSuffix(string host, string suffix)
	{
		string normalized = suffix.TrimStart('.');
		return normalized.Length > 0
			&& (string.Equals(host, normalized, StringComparison.Ordinal)
				|| host.EndsWith("." + normalized, StringComparison.Ordinal));
	}

	private static bool ShouldBypassSsoOAuth(Uri uri)
	{
		string host = uri.Host.ToLowerInvariant();
		if (SsoBypassHostSuffixes.Any(suffix => MatchesHostLabelSuffix(host, suffix)))
		{
			return true;
		}

		string path = uri.AbsolutePath.ToLowerInvariant();
		return SsoBypassPathHints.Any(hint => path.Contains(hint, StringComparison.Ordinal));
	}

	private static bool ShouldRemoveFromDomainPack(string host, string keyLower, out string? matchedPack)
	{
		string hostLower = host.ToLowerInvariant();
		foreach (DomainPack pack in DomainPacks)
		{
			if (pack.HostSuffixes.Any(suffix => MatchesHostLabelSuffix(hostLower, suffix))
				&& pack.Keys.Contains(keyLower))
			{
				matchedPack = pack.PackId;
				return true;
			}
		}

		matchedPack = null;
		return false;
	}

	private sealed record DomainPack(string PackId, IReadOnlyList<string> HostSuffixes, IReadOnlySet<string> Keys);
}
