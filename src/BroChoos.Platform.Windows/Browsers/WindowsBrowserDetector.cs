using BroChoos.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BroChoos.Platform.Windows.Browsers;

// D-181 detection layering, in strict priority order:
//   1. known install paths (v13 §14)
//   2. HKLM/HKCU Software\Clients\StartMenuInternet registrations
//   3. RegisteredApplications, resolved through the same client keys
// Scope is deliberately Edge + Chrome only; every other registered browser is
// ignored in this milestone. Edge is ordered before Chrome so the first candidate
// is deterministic: the console path takes the first candidate, the Picker shows cards in this order, and there is no ranking rules engine yet.
public sealed class WindowsBrowserDetector(
	ILogger<WindowsBrowserDetector> logger,
	IEnvironmentProbe environment,
	IRegistryBrowserSource registrySource) : IBrowserDetector
{
#if DEBUG
	// DEBUG-ONLY escape hatch used by tools/smoke-launch.ps1 to exercise the
	// "no browser detected" path on a machine that does have browsers. It is
	// compiled out of Release builds on purpose: shipping it would let ANY parent
	// process silently disable browser detection by setting one variable, which is
	// a denial-of-service switch on the product's core function.
	internal const string ForceNoBrowsersVariable = "BROCHOOS_TEST_NO_BROWSERS";
#endif

	internal const string EdgeId = "edge-stable";
	internal const string EdgeBetaId = "edge-beta";
	internal const string EdgeDevId = "edge-dev";
	internal const string EdgeCanaryId = "edge-canary";
	internal const string ChromeId = "chrome-stable";
	internal const string ChromeBetaId = "chrome-beta";
	internal const string ChromeDevId = "chrome-dev";
	internal const string ChromeCanaryId = "chrome-canary";
	internal const string BraveId = "brave-stable";
	internal const string BraveBetaId = "brave-beta";
	internal const string BraveNightlyId = "brave-nightly";
	internal const string FirefoxId = "firefox-stable";
	internal const string FirefoxDevId = "firefox-developer";
	internal const string FirefoxNightlyId = "firefox-nightly";
	internal const string SourceKnownPath = "known-path";

	// Windows Firefox profile root. Verified on this machine: %AppData%\Mozilla\Firefox\
	// holds a single profiles.ini that both the installer and the runtime consult, and the
	// pre-existing registry tier has always resolved firefox.exe's profiles from this same
	// path (see EnumerateRegistryCandidates). Stable, Developer Edition and Nightly share
	// this ONE profiles.ini in the mainstream same-machine case, so one template is the
	// correct DEFAULT rather than a per-channel invention.
	//
	// Caveat (do not over-read): Mozilla documents that the three channels share the profile
	// by default, but Nightly is known to create a SEPARATE profile root in some
	// configurations and Firefox 67+ uses per-install installs.ini to let one profiles.ini
	// back multiple installs. If a build ever forks the profile root per channel, this
	// constant is the single point to revise — the shared-template test is the tripwire.
	internal const string FirefoxProfilesIniTemplate = @"%AppData%\Mozilla\Firefox\profiles.ini";

	private readonly ILogger<WindowsBrowserDetector> _logger = logger;
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IRegistryBrowserSource _registrySource = registrySource;
	private readonly ChromiumProfileReader _profileReader = new();
	private readonly FirefoxProfileReader _firefoxProfileReader = new();

	public IReadOnlyList<BrowserCandidate> Detect()
	{
#if DEBUG
		if (_environment.GetEnvironmentVariable(ForceNoBrowsersVariable) == "1")
		{
			_logger.LogWarning("Browser detection suppressed by the DEBUG-only override; treating the machine as having no browsers");
			return [];
		}
#endif

		List<BrowserCandidate> ordered = [];
		// Case-insensitive: Windows paths are case-insensitive, and the same install
		// legitimately appears in several D-181 tiers.
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		// One cache per detection run. The 5 known-path templates and every registry
		// registration almost always sit on the same volume, so without this the volume
		// table was queried repeatedly (roughly once per candidate) per Detect() for one
		// answer. Scoped to this call on purpose: DriveType is live machine state and a
		// static cache would pin a stale answer for the life of the process. Fail-closed
		// is unaffected — a throwing probe is not cached.
		MemoizingDriveTypeProbe driveTypeProbe = new(PathSafety.SystemVolumeProbe);

		// Edge before Chrome, known paths before registry: see the class remark.
		foreach (KnownBrowserPath known in EnumerateKnownPaths())
		{
			// ResolvePath splices in %ProgramFiles%/%LocalAppData%, which come from an
			// inheritable environment a hostile parent controls. The result therefore
			// passes the SAME gate as registry-derived paths before it is probed, so a
			// forged \\evil\share can never outrank a real local install.
			string? candidatePath = ResolvePath(known.PathTemplate);
			string? resolved = PathSafety.TryNormalize(candidatePath, driveTypeProbe, out PathRejectionReason reason);

			if (resolved is null)
			{
				LogRejectedCandidate(candidatePath, reason, SourceKnownPath);
				continue;
			}

			if (_environment.FileExists(resolved) && seenPaths.Add(resolved))
			{
				// Dispatch on browser KIND, not on one generic builder. Firefox keeps its
				// profiles in a profiles.ini while every Chromium vendor keeps them under
				// "Local State", and the two readers speak different formats: the generic
				// path produces ZERO profiles for a Gecko browser by construction, because
				// GetLocalStateTemplate has no Firefox arm (its default arm returns null).
				// Firefox is therefore handed to the SAME reader the registry tier uses
				// (EnumerateRegistryCandidates), so a known-path Firefox — the common case —
				// reports its profiles too instead of an empty, permanently-unresolved list.
				ordered.Add(IsFirefoxId(known.BrowserId)
					? BuildFirefoxCandidate(
						known.BrowserId,
						known.DisplayName,
						resolved,
						known.Channel,
						SourceKnownPath,
						FirefoxProfilesIniTemplate)
					: BuildCandidate(
						known.BrowserId,
						known.DisplayName,
						resolved,
						known.Channel,
						SourceKnownPath,
						GetLocalStateTemplate(known.BrowserId)));
			}
		}

		foreach (BrowserCandidate candidate in EnumerateRegistryCandidates(driveTypeProbe))
		{
			if (seenPaths.Add(candidate.ExePath))
			{
				ordered.Add(candidate);
			}
		}

		List<BrowserCandidate> result = ApplyVendorOrder(ordered);

		if (_logger.IsEnabled(LogLevel.Information))
		{
			_logger.LogInformation("Browser detection finished with {CandidateCount} candidate(s)", result.Count);
		}

		return result;
	}

	// Registry tiers can surface Chrome before Edge; re-apply the vendor order so the
	// overall list stays deterministic regardless of which tier found what.
	//
	// This step is a SORT, NOT A FILTER. Anything that is neither Edge nor Chrome keeps
	// its discovery position AFTER the ordered pair. Dropping it here would silently
	// discard a candidate an earlier tier deliberately admitted, and the loss would be
	// invisible in both the result and the logged count — so a future tier that learns
	// to recognise Firefox would appear to work while its output vanished at the exit.
	// Scope for 0A-2 is still Edge + Chrome; that scope is enforced where candidates are
	// ADMITTED (EnumerateRegistryCandidates / the known-path table), which is the one
	// place a reader looks for it.
	internal static List<BrowserCandidate> ApplyVendorOrder(IEnumerable<BrowserCandidate> candidates)
	{
		List<BrowserCandidate> source = [.. candidates];

		return
		[
			.. source.Where(c => c.BrowserId.StartsWith("edge-", StringComparison.Ordinal)),
			.. source.Where(c => c.BrowserId.StartsWith("chrome-", StringComparison.Ordinal)),
			.. source.Where(c => c.BrowserId.StartsWith("brave-", StringComparison.Ordinal)),
			.. source.Where(c => c.BrowserId.StartsWith("firefox-", StringComparison.Ordinal)),
			.. source.Where(c => !c.BrowserId.StartsWith("edge-", StringComparison.Ordinal)
				&& !c.BrowserId.StartsWith("chrome-", StringComparison.Ordinal)
				&& !c.BrowserId.StartsWith("brave-", StringComparison.Ordinal)
				&& !c.BrowserId.StartsWith("firefox-", StringComparison.Ordinal)),
		];
	}

	// v13 §14 known paths. Edge lists x86 first because that is where the shipped
	// Edge install actually lives on 64-bit Windows.
	private static IEnumerable<KnownBrowserPath> EnumerateKnownPaths()
	{
		yield return new KnownBrowserPath(EdgeId, "Microsoft Edge", "stable", @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe");
		yield return new KnownBrowserPath(EdgeId, "Microsoft Edge", "stable", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe");
		yield return new KnownBrowserPath(EdgeBetaId, "Microsoft Edge Beta", "beta", @"%ProgramFiles(x86)%\Microsoft\Edge Beta\Application\msedge.exe");
		yield return new KnownBrowserPath(EdgeBetaId, "Microsoft Edge Beta", "beta", @"%ProgramFiles%\Microsoft\Edge Beta\Application\msedge.exe");
		yield return new KnownBrowserPath(EdgeDevId, "Microsoft Edge Dev", "dev", @"%ProgramFiles(x86)%\Microsoft\Edge Dev\Application\msedge.exe");
		yield return new KnownBrowserPath(EdgeDevId, "Microsoft Edge Dev", "dev", @"%ProgramFiles%\Microsoft\Edge Dev\Application\msedge.exe");
		yield return new KnownBrowserPath(EdgeCanaryId, "Microsoft Edge Canary", "canary", @"%LocalAppData%\Microsoft\Edge SxS\Application\msedge.exe");
		yield return new KnownBrowserPath(ChromeId, "Google Chrome", "stable", @"%ProgramFiles%\Google\Chrome\Application\chrome.exe");
		yield return new KnownBrowserPath(ChromeId, "Google Chrome", "stable", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe");
		yield return new KnownBrowserPath(ChromeId, "Google Chrome", "stable", @"%LocalAppData%\Google\Chrome\Application\chrome.exe");
		yield return new KnownBrowserPath(ChromeBetaId, "Google Chrome Beta", "beta", @"%ProgramFiles%\Google\Chrome Beta\Application\chrome.exe");
		yield return new KnownBrowserPath(ChromeBetaId, "Google Chrome Beta", "beta", @"%ProgramFiles(x86)%\Google\Chrome Beta\Application\chrome.exe");
		yield return new KnownBrowserPath(ChromeDevId, "Google Chrome Dev", "dev", @"%ProgramFiles%\Google\Chrome Dev\Application\chrome.exe");
		yield return new KnownBrowserPath(ChromeDevId, "Google Chrome Dev", "dev", @"%ProgramFiles(x86)%\Google\Chrome Dev\Application\chrome.exe");
		yield return new KnownBrowserPath(ChromeCanaryId, "Google Chrome Canary", "canary", @"%LocalAppData%\Google\Chrome SxS\Application\chrome.exe");
		yield return new KnownBrowserPath(BraveId, "Brave", "stable", @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe");
		yield return new KnownBrowserPath(BraveId, "Brave", "stable", @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe");
		yield return new KnownBrowserPath(BraveId, "Brave", "stable", @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe");
		yield return new KnownBrowserPath(BraveBetaId, "Brave Beta", "beta", @"%ProgramFiles%\BraveSoftware\Brave-Browser-Beta\Application\brave.exe");
		yield return new KnownBrowserPath(BraveBetaId, "Brave Beta", "beta", @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser-Beta\Application\brave.exe");
		yield return new KnownBrowserPath(BraveBetaId, "Brave Beta", "beta", @"%LocalAppData%\BraveSoftware\Brave-Browser-Beta\Application\brave.exe");
		yield return new KnownBrowserPath(BraveNightlyId, "Brave Nightly", "nightly", @"%ProgramFiles%\BraveSoftware\Brave-Browser-Nightly\Application\brave.exe");
		yield return new KnownBrowserPath(BraveNightlyId, "Brave Nightly", "nightly", @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser-Nightly\Application\brave.exe");
		yield return new KnownBrowserPath(BraveNightlyId, "Brave Nightly", "nightly", @"%LocalAppData%\BraveSoftware\Brave-Browser-Nightly\Application\brave.exe");
		yield return new KnownBrowserPath(FirefoxId, "Mozilla Firefox", "stable", @"%ProgramFiles%\Mozilla Firefox\firefox.exe");
		yield return new KnownBrowserPath(FirefoxId, "Mozilla Firefox", "stable", @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe");
		yield return new KnownBrowserPath(FirefoxDevId, "Firefox Developer Edition", "developer", @"%ProgramFiles%\Firefox Developer Edition\firefox.exe");
		yield return new KnownBrowserPath(FirefoxDevId, "Firefox Developer Edition", "developer", @"%ProgramFiles(x86)%\Firefox Developer Edition\firefox.exe");
		yield return new KnownBrowserPath(FirefoxNightlyId, "Firefox Nightly", "nightly", @"%ProgramFiles%\Firefox Nightly\firefox.exe");
		yield return new KnownBrowserPath(FirefoxNightlyId, "Firefox Nightly", "nightly", @"%ProgramFiles(x86)%\Firefox Nightly\firefox.exe");
	}

	// The registry source is best-effort by contract, but "best-effort" must be
	// enforced at the boundary rather than trusted: a failing tier degrades detection
	// to the known-path candidates instead of taking Detect() down with it (D-308/309).
	private IReadOnlyList<RegistryBrowserRegistration> ReadRegistrationsSafely()
	{
		try
		{
			return _registrySource.GetRegistrations();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
		{
			_logger.LogWarning(ex, "The registry browser source failed; continuing with known-path candidates only");
			return [];
		}
	}

	// A candidate refused by the path gate leaves a Debug trail. Without it, a SYSTEMIC
	// rejection — a forged %ProgramFiles%, an install roamed onto SMB, a volume that
	// stopped answering — was indistinguishable from a machine with no browser at all:
	// both produced exit 4 with nothing logged. The reason token names which gate
	// refused, so the two cases can finally be told apart from a log.
	//
	// PRIVACY (PRIVACY.md): the FULL path is never logged. Only the trailing segment and
	// a fixed reason token go out, so a log cannot be used to reconstruct where a user
	// keeps their software or what their volume layout looks like.
	private void LogRejectedCandidate(string? candidate, PathRejectionReason reason, string tier)
	{
		// An unset %ProgramFiles%/%LocalAppData% is an ordinary machine state, not an
		// anomaly, and logging it for every template would be pure noise.
		if (reason == PathRejectionReason.Empty)
		{
			return;
		}

		if (!_logger.IsEnabled(LogLevel.Debug))
		{
			return;
		}

		_logger.LogDebug(
			"Rejected a {Tier} browser candidate: file {RedactedFileName}, reason {Reason}",
			tier,
			RedactToFileName(candidate),
			reason.ToToken());
	}

	// Reduces an untrusted candidate to its trailing segment for logging. Done LEXICALLY
	// on purpose: Path.GetFileName would be fine today, but this value is attacker-shaped
	// (null bytes, 40k characters, no separator at all) and a redactor on the diagnostic
	// path must not be able to throw or to emit an unbounded line.
	private static string RedactToFileName(string? candidate)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return "(none)";
		}

		int lastSeparator = candidate.LastIndexOfAny(['\\', '/']);
		string name = lastSeparator >= 0 ? candidate[(lastSeparator + 1)..] : candidate;

		// The registry tier passes a whole "shell\open\command" value, whose trailing
		// segment still carries the closing quote and the argument list
		// (msedge.exe" --single-argument %1). Cutting at the first quote or space reduces
		// it to the bare file name, so vendor arguments never reach the log either.
		int argumentBoundary = name.IndexOfAny(['"', ' ']);
		if (argumentBoundary >= 0)
		{
			name = name[..argumentBoundary];
		}

		// Control characters would corrupt the log line; length is capped so a hostile
		// 40 000-character segment cannot flood the sink.
		const int maxLoggedLength = 64;
		string trimmed = name.Length > maxLoggedLength ? name[..maxLoggedLength] + "..." : name;

		return trimmed.Length == 0
			? "(none)"
			: string.Concat(trimmed.Select(c => char.IsControl(c) ? '?' : c));
	}

	private IEnumerable<BrowserCandidate> EnumerateRegistryCandidates(IDriveTypeProbe driveTypeProbe)
	{
		foreach (RegistryBrowserRegistration registration in ReadRegistrationsSafely())
		{
			string? exePath = ShellCommandParser.TryExtractExecutablePath(
				registration.ShellOpenCommand,
				driveTypeProbe,
				out PathRejectionReason reason);

			if (exePath is null)
			{
				LogRejectedCandidate(registration.ShellOpenCommand, reason, registration.DetectionSource);
				continue;
			}

			if (!_environment.FileExists(exePath))
			{
				continue;
			}

			// Identity comes from the executable name, never from the vendor-controlled
			// display name: the key name is untrusted input from the registry (D-310).
			string fileName = Path.GetFileName(exePath);

			if (fileName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))
			{
				yield return BuildCandidate(
					EdgeId,
					"Microsoft Edge",
					exePath,
					"stable",
					registration.DetectionSource,
					@"%LocalAppData%\Microsoft\Edge\User Data\Local State");
			}
			else if (fileName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase))
			{
				yield return BuildCandidate(
					ChromeId,
					"Google Chrome",
					exePath,
					"stable",
					registration.DetectionSource,
					@"%LocalAppData%\Google\Chrome\User Data\Local State");
			}
			else if (fileName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase))
			{
				yield return BuildCandidate(
					BraveId,
					"Brave",
					exePath,
					"stable",
					registration.DetectionSource,
					@"%LocalAppData%\BraveSoftware\Brave-Browser\User Data\Local State");
			}
			else if (fileName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
			{
				yield return BuildFirefoxCandidate(
					FirefoxId,
					"Mozilla Firefox",
					exePath,
					"stable",
					registration.DetectionSource,
					FirefoxProfilesIniTemplate);
			}
		}
	}

	private BrowserCandidate BuildCandidate(
		string browserId,
		string displayName,
		string exePath,
		string channel,
		string detectionSource,
		string? localStateTemplate)
	{
		ChromiumProfileReadResult profileResult = localStateTemplate is null
			? new ChromiumProfileReadResult([], true)
			: _profileReader.TryRead(ResolvePath(localStateTemplate) ?? string.Empty);

		return new BrowserCandidate(browserId, displayName, exePath, channel, detectionSource)
		{
			ProfileIds = profileResult.ProfileIds,
			ProfileNames = profileResult.ProfileNames,
			HasUnresolvedProfiles = profileResult.HasUnresolvedProfiles,
			IsCustomBrowser = detectionSource.Equals("manual", StringComparison.OrdinalIgnoreCase),
			IsPortableBrowser = detectionSource.Equals("portable", StringComparison.OrdinalIgnoreCase),
		};
	}

	private BrowserCandidate BuildFirefoxCandidate(
		string browserId,
		string displayName,
		string exePath,
		string channel,
		string detectionSource,
		string profilesIniTemplate)
	{
		FirefoxProfileReadResult profileResult = _firefoxProfileReader.TryRead(ResolvePath(profilesIniTemplate) ?? string.Empty);

		return new BrowserCandidate(browserId, displayName, exePath, channel, detectionSource)
		{
			ProfileIds = profileResult.ProfileIds,
			// Firefox profile ids ARE the profiles.ini Name= values, i.e. the visible names.
			ProfileNames = [.. profileResult.ProfileIds.Select(Core.Privacy.ProfileNameSanitizer.Sanitize)],
			HasUnresolvedProfiles = profileResult.HasUnresolvedProfiles,
			IsCustomBrowser = detectionSource.Equals("manual", StringComparison.OrdinalIgnoreCase),
			IsPortableBrowser = detectionSource.Equals("portable", StringComparison.OrdinalIgnoreCase),
		};
	}

	// Firefox is the one supported vendor whose profiles are NOT described by a Chromium
	// "Local State" file, so its candidates must be built by BuildFirefoxCandidate in every
	// tier. Kept as a predicate rather than three inline || comparisons so the known-path
	// loop and any future tier cannot drift apart on which ids belong to the Gecko family.
	internal static bool IsFirefoxId(string browserId)
	{
		return browserId is FirefoxId or FirefoxDevId or FirefoxNightlyId;
	}

	private static string? GetLocalStateTemplate(string browserId)
	{
		return browserId switch
		{
			// Each channel is a SEPARATE install with its own directory (see EnumerateKnownPaths),
			// so each has its own user-data root under %LocalAppData%: Beta and Dev must not read
			// the STABLE browser's "Local State", or their profile lists are silently the stable
			// browser's (code review finding). Canary keeps its own "SxS" root and is unchanged.
			EdgeId => @"%LocalAppData%\Microsoft\Edge\User Data\Local State",
			EdgeBetaId => @"%LocalAppData%\Microsoft\Edge Beta\User Data\Local State",
			EdgeDevId => @"%LocalAppData%\Microsoft\Edge Dev\User Data\Local State",
			EdgeCanaryId => @"%LocalAppData%\Microsoft\Edge SxS\User Data\Local State",
			ChromeId => @"%LocalAppData%\Google\Chrome\User Data\Local State",
			ChromeBetaId => @"%LocalAppData%\Google\Chrome Beta\User Data\Local State",
			ChromeDevId => @"%LocalAppData%\Google\Chrome Dev\User Data\Local State",
			ChromeCanaryId => @"%LocalAppData%\Google\Chrome SxS\User Data\Local State",
			BraveId => @"%LocalAppData%\BraveSoftware\Brave-Browser\User Data\Local State",
			BraveBetaId => @"%LocalAppData%\BraveSoftware\Brave-Browser-Beta\User Data\Local State",
			BraveNightlyId => @"%LocalAppData%\BraveSoftware\Brave-Browser-Nightly\User Data\Local State",
			_ => null,
		};
	}

	// Expands the %VAR% segments used by the known-path table. A missing variable
	// yields null so the entry is skipped rather than probed as a literal path.
	private string? ResolvePath(string template)
	{
		int start = template.IndexOf('%', StringComparison.Ordinal);
		if (start != 0)
		{
			return template;
		}

		int end = template.IndexOf('%', start + 1);
		if (end < 0)
		{
			return null;
		}

		string variableName = template[(start + 1)..end];
		string? value = _environment.GetEnvironmentVariable(variableName);

		return string.IsNullOrWhiteSpace(value) ? null : value + template[(end + 1)..];
	}

	private sealed record KnownBrowserPath(string BrowserId, string DisplayName, string Channel, string PathTemplate);
}
