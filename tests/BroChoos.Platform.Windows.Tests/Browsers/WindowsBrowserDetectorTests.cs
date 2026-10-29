using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

public sealed class WindowsBrowserDetectorTests
{
	private const string EdgeX86 = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
	private const string EdgeX64 = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
	private const string ChromeX64 = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
	private const string ChromeLocal = @"C:\Users\dev\AppData\Local\Google\Chrome\Application\chrome.exe";

	[Fact]
	public void Detect_KnownPathsPresent_ReturnsEdgeBeforeChrome()
	{
		WindowsBrowserDetector detector = CreateDetector(new FakeEnvironmentProbe([EdgeX86, ChromeX64]));

		IReadOnlyList<BrowserCandidate> result = detector.Detect();

		_ = result.Should().HaveCount(2);
		_ = result[0].BrowserId.Should().Be("edge-stable");
		_ = result[0].ExePath.Should().Be(EdgeX86);
		_ = result[0].DetectionSource.Should().Be("known-path");
		_ = result[1].BrowserId.Should().Be("chrome-stable");
	}

	[Fact]
	public void Detect_NoBrowsersInstalled_ReturnsEmpty()
	{
		WindowsBrowserDetector detector = CreateDetector(new FakeEnvironmentProbe([]));

		_ = detector.Detect().Should().BeEmpty();
	}

	[Fact]
	public void Detect_OnlyChromeInstalled_ReturnsChromeOnly()
	{
		WindowsBrowserDetector detector = CreateDetector(new FakeEnvironmentProbe([ChromeLocal]));

		IReadOnlyList<BrowserCandidate> result = detector.Detect();

		_ = result.Should().ContainSingle();
		_ = result[0].BrowserId.Should().Be("chrome-stable");
		_ = result[0].ExePath.Should().Be(ChromeLocal);
	}

	[Fact]
	public void Detect_SameExeInBothKnownPathAndRegistry_IsReturnedOnce()
	{
		FakeEnvironmentProbe environment = new([EdgeX86]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Microsoft Edge", $"\"{EdgeX86}\" --single-argument %1", "registry-startmenuinternet")]);

		IReadOnlyList<BrowserCandidate> result = CreateDetector(environment, registry).Detect();

		_ = result.Should().ContainSingle();
		_ = result[0].DetectionSource.Should().Be("known-path");
	}

	[Fact]
	public void Detect_RegistryOnlyInstall_IsReportedWithRegistrySource()
	{
		// The exe exists on disk but at a path outside the known-path table.
		const string oddPath = @"C:\Portable\Chrome\chrome.exe";
		FakeEnvironmentProbe environment = new([oddPath]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Google Chrome", $"\"{oddPath}\" -- %1", "registry-startmenuinternet")]);

		IReadOnlyList<BrowserCandidate> result = CreateDetector(environment, registry).Detect();

		_ = result.Should().ContainSingle();
		_ = result[0].BrowserId.Should().Be("chrome-stable");
		_ = result[0].ExePath.Should().Be(oddPath);
		_ = result[0].DetectionSource.Should().Be("registry-startmenuinternet");
	}

	[Fact]
	public void Detect_RegistryFindsChromeAndKnownPathFindsEdge_KeepsEdgeFirst()
	{
		const string oddChrome = @"C:\Portable\Chrome\chrome.exe";
		FakeEnvironmentProbe environment = new([EdgeX64, oddChrome]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Google Chrome", $"\"{oddChrome}\" %1", "registry-registeredapplications")]);

		IReadOnlyList<BrowserCandidate> result = CreateDetector(environment, registry).Detect();

		_ = result.Should().HaveCount(2);
		_ = result[0].BrowserId.Should().Be("edge-stable");
		_ = result[1].BrowserId.Should().Be("chrome-stable");
	}

	[Fact]
	public void Detect_RegistryEntryForUnsupportedBrowser_IsIgnored()
	{
		const string opera = @"C:\Program Files\Opera\opera.exe";
		FakeEnvironmentProbe environment = new([opera]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Opera", $"\"{opera}\" --single-argument %1", "registry-startmenuinternet")]);

		_ = CreateDetector(environment, registry).Detect().Should().BeEmpty();
	}

	[Fact]
	public void Detect_RegistryEntryPointingAtMissingFile_IsIgnored()
	{
		FakeEnvironmentProbe environment = new([]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Microsoft Edge", $"\"{EdgeX86}\" %1", "registry-startmenuinternet")]);

		_ = CreateDetector(environment, registry).Detect().Should().BeEmpty();
	}

#if DEBUG
	// The override is compiled out of Release builds (F5), so this test only exists
	// where the production code does.
	//
	// THIS TEST IS NO LONGER THE WHOLE DEBUG/RELEASE DELTA, and that invariant's death is
	// recorded here because this is where a reader comes looking when the counts disagree.
	// "The difference is exactly one" held from 0A-2 to 0A-4. At 0A-5 it became TWELVE, by
	// design, and a run reporting twelve is HEALTHY rather than broken:
	//
	//   1 in BroChoos.Platform.Windows.Tests — this test, Detect_TestOnlyOverride_ForcesEmptyDetection,
	//     excluded by the #if DEBUG below because the production override it exercises is.
	//   11 in BroChoos.App.Tests — the whole Scaffold0A5 test folder, excluded by a
	//     configuration-scoped Compile Remove in BroChoos.App.Tests.csproj because the types
	//     they exercise are removed from BroChoos.App in the same configurations. Six facts in
	//     EventCheckpointObserverTests (OnStage_ArmedStage_SignalsReachedThenBlocksUntilProceedIsSet,
	//     OnStage_UnarmedStage_ReturnsImmediatelyAndNeverParks,
	//     OnStage_ProceedNeverSet_ExpiresAndReturnsInsteadOfHangingForever,
	//     OnStage_OnParkExpiredThrows_DoesNotEscape, Constructor_NonPositiveParkTimeout_IsRejected,
	//     EventNames_MatchTheLiteralFormatThePowerShellParentRebuilds) and five in
	//     AtomicWriteKillChildTriggerTests (IsTriggered_TriggerFirstAndEnvironmentSet_IsRecognised,
	//     IsTriggered_TriggerPresentButNotFirst_IsRefused,
	//     IsTriggered_EnvironmentVariableAbsent_IsRefused,
	//     IsTriggered_EnvironmentVariableSetToSomethingElse_IsRefused,
	//     IsTriggered_NoArguments_IsRefusedWithoutThrowing).
	//   0 in BroChoos.Core.Tests, which is configuration-independent.
	//
	// THE 0A-3 SCAFFOLD GATE ADDS NOTHING TO THIS NUMBER, which is worth stating because it
	// landed in the same change set and looks like it should. Nothing under tests\ or tools\
	// names Scaffold0A3, AvaloniaProbeRunner or the trigger --internal-0a3-avalonia-probe, so
	// excluding those sources from non-Debug builds removes ZERO tests.
	//
	// WHAT STILL MAKES A COUNT MISMATCH A DEFECT. Equal counts in the two configurations mean a
	// gate stopped working and DEBUG-only code — this override, or the 0A-5 kill child that
	// performs a real config write from its arguments — is compiling into a shipping build. A
	// delta OTHER than twelve means a gate moved without this comment being updated. Either way
	// the number is the symptom, not the diagnosis: identify which named test above is missing.
	// See RUN_DEV.md.
	[Fact]
	public void Detect_TestOnlyOverride_ForcesEmptyDetection()
	{
		FakeEnvironmentProbe environment = new([EdgeX86, ChromeX64]);
		environment.Variables["BROCHOOS_TEST_NO_BROWSERS"] = "1";

		_ = CreateDetector(environment).Detect().Should().BeEmpty();
	}
#endif

	// (o) Rewritten so it cannot pass for the wrong reason. The old version only
	// asserted "empty", which would still hold if ResolvePath returned the unexpanded
	// literal "%LocalAppData%\Google\..." — an empty result would then prove nothing
	// about expansion. Here the unexpanded literal is registered as an EXISTING file,
	// so returning it would produce a candidate and fail the test.
	[Fact]
	public void Detect_MissingEnvironmentVariable_SkipsThatKnownPath()
	{
		const string unexpandedLiteral = @"%LocalAppData%\Google\Chrome\Application\chrome.exe";
		FakeEnvironmentProbe environment = new([ChromeLocal, unexpandedLiteral]);
		_ = environment.Variables.Remove("LocalAppData");

		IReadOnlyList<BrowserCandidate> result = CreateDetector(environment).Detect();

		// Chrome exists only under %LocalAppData%, which can no longer be expanded, so
		// the entry must be dropped rather than probed as a literal path.
		_ = result.Should().BeEmpty();
		_ = result.Should().NotContain(c => c.ExePath.Contains('%', StringComparison.Ordinal));
	}

	// (l) F3: %ProgramFiles% is inherited from the parent process, so a hostile parent
	// can point it at a share it controls. Such a candidate must be rejected outright,
	// not merely ranked lower.
	[Theory]
	[InlineData(@"\\evil\share")]
	[InlineData(@"\\10.0.0.5\c$")]
	[InlineData(@"\\?\C:\Program Files")]
	[InlineData(@"\\.\pipe")]
	public void Detect_KnownPathEnvironmentVariableIsNotLocal_RejectsThatCandidate(string hostileRoot)
	{
		FakeEnvironmentProbe environment = new([$@"{hostileRoot}\Microsoft\Edge\Application\msedge.exe"]);
		environment.Variables["ProgramFiles"] = hostileRoot;
		environment.Variables["ProgramFiles(x86)"] = hostileRoot;

		_ = CreateDetector(environment).Detect().Should().BeEmpty();
	}

	// A forged variable must not outrank a genuine local install either.
	[Fact]
	public void Detect_HostileEnvironmentVariable_DoesNotOutrankRealLocalBrowser()
	{
		const string hostileEdge = @"\\evil\share\Microsoft\Edge\Application\msedge.exe";
		FakeEnvironmentProbe environment = new([hostileEdge, ChromeX64]);
		environment.Variables["ProgramFiles(x86)"] = @"\\evil\share";

		IReadOnlyList<BrowserCandidate> result = CreateDetector(environment).Detect();

		_ = result.Should().ContainSingle();
		_ = result[0].BrowserId.Should().Be("chrome-stable");
		_ = result.Should().NotContain(c => c.ExePath.StartsWith(@"\\", StringComparison.Ordinal));
	}

	// (m) F10: a trailing slash on the variable used to produce a second, differently
	// spelled path for the same install, which the dedupe set could not collapse.
	[Theory]
	[InlineData(@"C:\Program Files\")]
	[InlineData(@"C:\Program Files\\")]
	[InlineData("C:/Program Files")]
	[InlineData(@"C:\Program Files\.")]
	public void Detect_KnownPathVariableIsNotCanonical_ReturnsExactlyOneEdge(string programFiles)
	{
		FakeEnvironmentProbe environment = new([EdgeX64]);
		environment.Variables["ProgramFiles"] = programFiles;
		// Remove the x86 entry so only the %ProgramFiles% template can match.
		_ = environment.Variables.Remove("ProgramFiles(x86)");

		IReadOnlyList<BrowserCandidate> result = CreateDetector(environment).Detect();

		_ = result.Should().ContainSingle();
		_ = result[0].BrowserId.Should().Be("edge-stable");
		_ = result[0].ExePath.Should().Be(EdgeX64);
	}

	// The same install reachable through two known-path templates stays one candidate.
	[Fact]
	public void Detect_SameEdgeReachableThroughTwoTemplates_IsReturnedOnce()
	{
		FakeEnvironmentProbe environment = new([EdgeX64]);
		// Both templates now resolve to the same directory, spelled differently.
		environment.Variables["ProgramFiles"] = @"C:\Program Files";
		environment.Variables["ProgramFiles(x86)"] = @"C:\Program Files\";

		_ = CreateDetector(environment).Detect().Should().ContainSingle();
	}

	// (n) Exercises the re-sort: the registry tier yields Chrome, the known-path tier
	// yields Edge, and Edge must still be index 0 because it is the launch target.
	[Fact]
	public void Detect_RegistryFindsEdgeWhileKnownPathFindsChrome_PutsEdgeFirst()
	{
		const string oddEdge = @"C:\Portable\Edge\msedge.exe";
		// Only Chrome is on a known path; Edge is registry-only, so it is appended last
		// before the re-sort runs.
		FakeEnvironmentProbe environment = new([ChromeX64, oddEdge]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Microsoft Edge", $"\"{oddEdge}\" %1", "registry-startmenuinternet")]);

		IReadOnlyList<BrowserCandidate> result = CreateDetector(environment, registry).Detect();

		_ = result.Should().HaveCount(2);
		_ = result[0].BrowserId.Should().Be("edge-stable");
		_ = result[0].ExePath.Should().Be(oddEdge);
		_ = result[0].DetectionSource.Should().Be("registry-startmenuinternet");
		_ = result[1].BrowserId.Should().Be("chrome-stable");
	}

	// (A9) Alternate data streams, hostile direction. ShellCommandParserTests documents
	// that PathSafety ACCEPTS an ADS path — it is drive-rooted and GetFullPath keeps the
	// ":stream" suffix verbatim (verified on .NET 10). So the identity check is the only
	// thing standing between "C:\Edge\host.txt:msedge.exe" and a launch, and an attacker
	// who can write a stream onto any file they own would otherwise forge an allowlisted
	// browser name. Path.GetFileName returns "host.txt:msedge.exe" / "x:chrome.exe" — the
	// WHOLE trailing segment, stream included — which matches neither allowlisted name.
	// The file is registered as EXISTING here so the test cannot pass for the trivial
	// reason that the path was not found.
	[Theory]
	[InlineData(@"C:\Edge\host.txt:msedge.exe")]
	[InlineData(@"C:\Edge\x:chrome.exe")]
	[InlineData(@"C:\Edge\msedge.exe:evil.exe")]
	[InlineData(@"C:\Edge\notes.txt:chrome.exe")]
	public void Detect_RegistryEntryUsesAlternateDataStreamToForgeBrowserName_IsRejected(string adsPath)
	{
		FakeEnvironmentProbe environment = new([adsPath]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Microsoft Edge", $"\"{adsPath}\" %1", "registry-startmenuinternet")]);

		_ = CreateDetector(environment, registry).Detect().Should().BeEmpty();
	}

	// The identity check reads the full trailing segment, which is what makes the ADS
	// forgery fail. Pinned separately so a future "strip everything after the last colon"
	// convenience cannot silently re-open the hole above.
	[Theory]
	[InlineData(@"C:\Edge\host.txt:msedge.exe", "host.txt:msedge.exe")]
	[InlineData(@"C:\Edge\x:chrome.exe", "x:chrome.exe")]
	public void PathGetFileName_OnAlternateDataStreamPath_KeepsTheStreamSuffix(string adsPath, string expectedFileName)
	{
		// The path survives the safety gate...
		_ = PathSafety.NormalizeLocalExecutablePath(adsPath).Should().Be(adsPath);
		// ...and is then refused on identity, not on shape. The exact-value assertion
		// already excludes both allowlisted names, so it is the only one needed.
		_ = Path.GetFileName(adsPath).Should().Be(expectedFileName);
	}

	// THE ADS DEFENCE RESTS ON RUNTIME BEHAVIOUR, SO THE RUNTIME IS INTERROGATED HERE.
	//
	// Everything above assumes two things about how .NET treats a path carrying an
	// alternate data stream, and NEITHER is guaranteed by any documented contract:
	//   * Path.GetFileName returns the WHOLE trailing segment, stream included, so
	//     "host.txt:msedge.exe" never collapses to the allowlisted "msedge.exe", and
	//   * Path.GetFullPath passes the ':' through verbatim instead of throwing.
	//
	// .NET Framework behaved differently (GetFileName stopped at the colon, returning
	// "msedge.exe", and GetFullPath threw NotSupportedException), so this is exactly the
	// kind of behaviour a runtime upgrade can move. If it moves, the detector's identity
	// check silently starts comparing a forged name against its allowlist and the ADS
	// hole reopens with every other test still green.
	//
	// OBSERVED on .NET 10.0.5 / Windows 10.0.26200 (values below are measured, not
	// assumed): GetFileName keeps the full segment and GetFullPath returns the input
	// unchanged. This test fails loudly the day that stops being true.
	[Theory]
	[InlineData(@"C:\Edge\msedge.exe:stream.exe", "msedge.exe:stream.exe")]
	[InlineData(@"C:\Edge\host.txt:msedge.exe", "host.txt:msedge.exe")]
	[InlineData(@"C:\Edge\x:chrome.exe", "x:chrome.exe")]
	public void RuntimePathApi_OnAlternateDataStreamPath_StillCannotProduceAnAllowlistedName(string adsPath, string expectedFileName)
	{
		// 1. The measured facts this defence depends on.
		_ = Path.GetFileName(adsPath).Should().Be(expectedFileName);
		// GetFullPath does NOT throw here (it did on .NET Framework) and changes nothing.
		_ = Path.GetFullPath(adsPath).Should().Be(adsPath);

		// 2. The security property that actually matters, asserted independently of the
		// exact strings above: whichever way the runtime spells the trailing segment, it
		// must not equal a name the detector would accept as Edge or Chrome. This is the
		// same comparison EnumerateRegistryCandidates performs, so if it ever succeeds
		// here the detector is exploitable.
		string identity = Path.GetFileName(Path.GetFullPath(adsPath));

		_ = identity.Should().NotBe("msedge.exe");
		_ = identity.Should().NotBe("chrome.exe");
		_ = identity.Should().NotBeEquivalentTo("msedge.exe");
		_ = identity.Should().NotBeEquivalentTo("chrome.exe");
	}

	// (A6) The ordering step is a SORT, not a filter. It used to keep only Edge and
	// Chrome, so a candidate from any future tier would vanish between detection and the
	// returned list — invisibly, including in the logged count. Scope is enforced where
	// candidates are ADMITTED; this pin makes sure it is not silently re-enforced here.
	[Fact]
	public void ApplyVendorOrder_UnknownBrowserId_SurvivesAfterTheOrderedPair()
	{
		BrowserCandidate firefox = new("firefox-stable", "Mozilla Firefox", @"C:\FF\firefox.exe", "stable", "registry-startmenuinternet");
		BrowserCandidate chrome = new("chrome-stable", "Google Chrome", ChromeX64, "stable", "known-path");
		BrowserCandidate edge = new("edge-stable", "Microsoft Edge", EdgeX64, "stable", "known-path");

		// Deliberately worst-case input order: the unknown id arrives FIRST.
		List<BrowserCandidate> result = WindowsBrowserDetector.ApplyVendorOrder([firefox, chrome, edge]);

		_ = result.Should().HaveCount(3);
		_ = result[0].BrowserId.Should().Be("edge-stable");
		_ = result[1].BrowserId.Should().Be("chrome-stable");
		_ = result[2].BrowserId.Should().Be("firefox-stable");
	}

	// Firefox is now a supported vendor family in W6.2 and therefore participates in
	// vendor ordering before unknown ids, which still keep discovery order among themselves.
	[Fact]
	public void ApplyVendorOrder_MultipleUnknownIds_KeepsFirefoxVendorGroupBeforeUnknowns()
	{
		BrowserCandidate opera = new("opera-stable", "Opera", @"C:\Opera\opera.exe", "stable", "registry-startmenuinternet");
		BrowserCandidate firefox = new("firefox-stable", "Mozilla Firefox", @"C:\FF\firefox.exe", "stable", "registry-startmenuinternet");
		BrowserCandidate edge = new("edge-stable", "Microsoft Edge", EdgeX64, "stable", "known-path");

		IReadOnlyList<BrowserCandidate> result = WindowsBrowserDetector.ApplyVendorOrder([opera, firefox, edge]);

		_ = result.Select(c => c.BrowserId).Should().Equal("edge-stable", "firefox-stable", "opera-stable");
	}

	// Nothing is invented either. Split from the two-vendor case below so a failure names
	// which one broke instead of pointing at a single test covering both.
	[Fact]
	public void ApplyVendorOrder_EmptyInput_ReturnsEmpty()
	{
		_ = WindowsBrowserDetector.ApplyVendorOrder([]).Should().BeEmpty();
	}

	[Fact]
	public void ApplyVendorOrder_TwoKnownVendors_IsUnchangedInCountAndOrder()
	{
		BrowserCandidate chrome = new("chrome-stable", "Google Chrome", ChromeX64, "stable", "known-path");
		BrowserCandidate edge = new("edge-stable", "Microsoft Edge", EdgeX64, "stable", "known-path");

		_ = WindowsBrowserDetector.ApplyVendorOrder([chrome, edge]).Select(c => c.BrowserId)
			.Should().Equal("edge-stable", "chrome-stable");
	}

	// The registry tier is subject to the same path gate as the known-path tier.
	[Fact]
	public void Detect_RegistryEntryOnUncPath_IsRejected()
	{
		const string uncEdge = @"\\evil\share\Edge\msedge.exe";
		FakeEnvironmentProbe environment = new([uncEdge]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Microsoft Edge", $"\"{uncEdge}\" %1", "registry-startmenuinternet")]);

		_ = CreateDetector(environment, registry).Detect().Should().BeEmpty();
	}

	// (F3) DIAGNOSTICS GAP. A candidate refused by the path gate used to leave no trace
	// at all, so a SYSTEMIC rejection — a forged %ProgramFiles%, an install roamed onto
	// SMB — reached the user as "no browser detected" and exit 4, indistinguishable in
	// the log from a machine that genuinely has no browser. The reason token is what
	// tells those two cases apart.
	[Fact]
	public void Detect_HostileEnvironmentVariable_LogsTheRejectionWithAReason()
	{
		const string hostileEdge = @"\\evil\share\Microsoft\Edge\Application\msedge.exe";
		FakeEnvironmentProbe environment = new([hostileEdge]);
		environment.Variables["ProgramFiles"] = @"\\evil\share";
		environment.Variables["ProgramFiles(x86)"] = @"\\evil\share";
		ListLogger<WindowsBrowserDetector> logger = new();

		_ = CreateDetector(environment, logger: logger).Detect().Should().BeEmpty();

		// The user-visible outcome is still exit-4-shaped, but now it is explained.
		_ = logger.Entries.Should().Contain(e => e.Contains("not-drive-rooted", StringComparison.Ordinal));
	}

	// The registry tier is gated by the same code and must be just as traceable.
	[Fact]
	public void Detect_RegistryEntryOnUncPath_LogsTheRejectionWithAReason()
	{
		const string uncEdge = @"\\evil\share\Edge\msedge.exe";
		FakeEnvironmentProbe environment = new([uncEdge]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Microsoft Edge", $"\"{uncEdge}\" %1", "registry-startmenuinternet")]);
		ListLogger<WindowsBrowserDetector> logger = new();

		_ = CreateDetector(environment, registry, logger).Detect().Should().BeEmpty();

		_ = logger.Entries.Should().Contain(e => e.Contains("not-drive-rooted", StringComparison.Ordinal));
	}

	// A drive letter mapped to SMB is the case that motivated the reason token: it is
	// refused on VOLUME TYPE, not on shape, and the two must not read the same in a log.
	[Fact]
	public void Detect_RegistryEntryOnNetworkMappedDrive_LogsTheVolumeReason()
	{
		// The gate's live volume table reports NoRootDirectory for an unmapped letter,
		// which is the unclassifiable branch — still a volume-level refusal, and still
		// distinct from the lexical one above.
		const string unmappedEdge = @"Q:\Edge\msedge.exe";
		FakeEnvironmentProbe environment = new([unmappedEdge]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Microsoft Edge", $"\"{unmappedEdge}\" %1", "registry-startmenuinternet")]);
		ListLogger<WindowsBrowserDetector> logger = new();

		_ = CreateDetector(environment, registry, logger).Detect().Should().BeEmpty();

		_ = logger.Entries.Should().Contain(e =>
			e.Contains("unclassifiable-volume", StringComparison.Ordinal)
			|| e.Contains("network-volume", StringComparison.Ordinal)
			|| e.Contains("probe-failed", StringComparison.Ordinal));
	}

	// PRIVACY (PRIVACY.md). The diagnostic exists to explain a refusal, NOT to record
	// where the user keeps their files. Only the trailing file name and the reason token
	// may appear; the directory chain must not. Asserted on a deliberately identifiable
	// path so a regression to "log the full path" fails here.
	[Fact]
	public void Detect_RejectedCandidate_NeverLogsTheFullPath()
	{
		const string secretive = @"\\fileserver-hr\payroll$\Confidential\Edge\msedge.exe";
		FakeEnvironmentProbe environment = new([secretive]);
		FakeRegistrySource registry = new([new RegistryBrowserRegistration("Microsoft Edge", $"\"{secretive}\" %1", "registry-startmenuinternet")]);
		ListLogger<WindowsBrowserDetector> logger = new();

		_ = CreateDetector(environment, registry, logger).Detect().Should().BeEmpty();

		string log = string.Join('\n', logger.Entries);

		// The refusal IS explained...
		_ = log.Should().Contain("not-drive-rooted");
		// ...without any of the parts that describe the user's machine.
		_ = log.Should().NotContain(secretive);
		_ = log.Should().NotContain("fileserver-hr");
		_ = log.Should().NotContain("payroll");
		_ = log.Should().NotContain("Confidential");
		// The bare file name is the only path fragment allowed through.
		_ = log.Should().Contain("msedge.exe");
	}

	// An unset %LocalAppData% is an ordinary machine state, not an anomaly, so it must
	// not produce a rejection line for every template that mentions it.
	[Fact]
	public void Detect_MissingEnvironmentVariable_DoesNotLogARejection()
	{
		FakeEnvironmentProbe environment = new([EdgeX86]);
		_ = environment.Variables.Remove("LocalAppData");
		ListLogger<WindowsBrowserDetector> logger = new();

		_ = CreateDetector(environment, logger: logger).Detect().Should().ContainSingle();

		_ = logger.Entries.Should().NotContain(e => e.Contains("empty", StringComparison.Ordinal));
	}

	// A clean machine stays quiet: nothing was refused, so nothing is explained.
	[Fact]
	public void Detect_AllCandidatesAccepted_LogsNoRejection()
	{
		FakeEnvironmentProbe environment = new([EdgeX86, ChromeX64]);
		ListLogger<WindowsBrowserDetector> logger = new();

		_ = CreateDetector(environment, logger: logger).Detect().Should().HaveCount(2);

		_ = logger.Entries.Should().NotContain(e => e.Contains("Rejected", StringComparison.Ordinal));
	}

	private static WindowsBrowserDetector CreateDetector(
		FakeEnvironmentProbe environment,
		IRegistryBrowserSource? registry = null,
		ILogger<WindowsBrowserDetector>? logger = null)
	{
		return new WindowsBrowserDetector(
			logger ?? NullLogger<WindowsBrowserDetector>.Instance,
			environment,
			registry ?? new FakeRegistrySource([]));
	}

	private sealed class FakeEnvironmentProbe(IEnumerable<string> existingFiles) : IEnvironmentProbe
	{
		private readonly HashSet<string> _existingFiles = new(existingFiles, StringComparer.OrdinalIgnoreCase);

		public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase)
		{
			["ProgramFiles"] = @"C:\Program Files",
			["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
			["LocalAppData"] = @"C:\Users\dev\AppData\Local",
		};

		public string? CurrentProcessPath => @"C:\Dev\BroChoos\BroChoos.exe";

		public string? GetEnvironmentVariable(string name)
		{
			return Variables.TryGetValue(name, out string? value) ? value : null;
		}

		public bool FileExists(string path)
		{
			return _existingFiles.Contains(path);
		}
	}

	// Captures every level, matching the ListLogger in BroChoos.Core.Tests. Debug is
	// enabled here because the rejection diagnostic is Debug-level: a logger that
	// reported IsEnabled(Debug)=false would make these tests pass vacuously.
	private sealed class ListLogger<T> : ILogger<T>
	{
		private readonly List<string> _entries = [];

		public IReadOnlyList<string> Entries => _entries;

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			_entries.Add(formatter(state, exception));
		}
	}

	private sealed class FakeRegistrySource(IReadOnlyList<RegistryBrowserRegistration> registrations) : IRegistryBrowserSource
	{
		private readonly IReadOnlyList<RegistryBrowserRegistration> _registrations = registrations;

		public IReadOnlyList<RegistryBrowserRegistration> GetRegistrations()
		{
			return _registrations;
		}
	}
}
