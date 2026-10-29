using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

// PathSafety was previously exercised only THROUGH ShellCommandParser and
// WindowsBrowserDetector. That left the gate itself unpinned: a change to it could keep
// every indirect test green while altering what reaches ProcessStartInfo.FileName. These
// tests address the gate directly, one guarantee at a time.
public sealed class PathSafetyTests
{
	private const string LocalEdge = @"C:\Edge\msedge.exe";

	// A volume type that is not Network. The real C: reports Fixed (verified on this
	// machine), so the seam default and this constant agree.
	private static readonly FakeDriveTypeProbe FixedVolume = new(DriveType.Fixed);

	[Fact]
	public void NormalizeLocalExecutablePath_LocalDriveLetterPath_IsAccepted()
	{
		_ = PathSafety.NormalizeLocalExecutablePath(LocalEdge, FixedVolume).Should().Be(LocalEdge);
	}

	// Every shape that must NOT reach a launch. Verified against .NET 10 on Windows:
	// GetFullPath passes UNC and device paths through VERBATIM, so IsPathFullyQualified
	// accepts them and only the explicit drive-letter check refuses them. The two
	// relative forms fail the fully-qualified check first — without that ordering,
	// GetFullPath would resolve them against the CURRENT DIRECTORY and hand back a
	// drive-rooted path that sails through.
	[Theory]
	[InlineData(@"\\srv\s\msedge.exe")]            // UNC share
	[InlineData(@"\\?\C:\Edge\msedge.exe")]        // Win32 device path, extended-length
	[InlineData(@"\\.\pipe\x.exe")]                // Win32 device path, device namespace
	[InlineData(@"\\?\UNC\srv\s\msedge.exe")]      // UNC through the device namespace
	[InlineData("msedge.exe")]                     // relative: would resolve to CWD
	[InlineData(@"C:msedge.exe")]                  // drive-RELATIVE: not the same as C:\
	[InlineData(@"..\..\msedge.exe")]              // relative with traversal
	public void NormalizeLocalExecutablePath_NonLocalOrRelativePath_IsRejected(string candidate)
	{
		_ = PathSafety.NormalizeLocalExecutablePath(candidate, FixedVolume).Should().BeNull();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void NormalizeLocalExecutablePath_EmptyInput_IsRejected(string? candidate)
	{
		_ = PathSafety.NormalizeLocalExecutablePath(candidate, FixedVolume).Should().BeNull();
	}

	// (A1) The drive-letter check is purely LEXICAL, so `net use Z: \\host\share` used to
	// produce an accepted path whose comment promised a local target. The volume type is
	// now consulted through the seam, which is why this can be tested without asking the
	// machine running the suite to hold a real SMB mapping.
	[Fact]
	public void NormalizeLocalExecutablePath_DriveLetterMappedToNetwork_IsRejected()
	{
		FakeDriveTypeProbe networkDrive = new(DriveType.Network);

		_ = PathSafety.NormalizeLocalExecutablePath(@"Z:\Edge\msedge.exe", networkDrive).Should().BeNull();
	}

	// The volume root is what gets classified, in the "C:\" form DriveInfo expects —
	// asserted so a refactor cannot start passing the full file path instead.
	[Fact]
	public void NormalizeLocalExecutablePath_ClassifiesTheVolumeRoot_NotTheFullPath()
	{
		FakeDriveTypeProbe probe = new(DriveType.Fixed);

		_ = PathSafety.NormalizeLocalExecutablePath(@"Z:\Edge\deep\msedge.exe", probe).Should().NotBeNull();
		_ = probe.QueriedRoots.Should().ContainSingle().And.Contain(@"Z:\");
	}

	// Non-network volume types stay acceptable: a browser on a USB stick or a mounted
	// image is still a local target. Only Network is refused.
	//
	// DriveType.Fixed is deliberately absent: LocalDriveLetterPath_IsAccepted above
	// already covers it through FixedVolume, and duplicating it here only inflated the
	// case count without exercising another branch.
	[Theory]
	[InlineData(DriveType.Removable)]
	[InlineData(DriveType.CDRom)]
	[InlineData(DriveType.Ram)]
	public void NormalizeLocalExecutablePath_NonNetworkVolumeType_IsAccepted(DriveType driveType)
	{
		_ = PathSafety.NormalizeLocalExecutablePath(LocalEdge, new FakeDriveTypeProbe(driveType)).Should().Be(LocalEdge);
	}

	// FAIL-CLOSED on an unclassifiable volume. NoRootDirectory is what an unmapped
	// letter really reports (verified: `new DriveInfo("Q").DriveType` returned
	// NoRootDirectory on this machine), and Unknown is the OS declining to say.
	[Theory]
	[InlineData(DriveType.Network)]
	[InlineData(DriveType.NoRootDirectory)]
	[InlineData(DriveType.Unknown)]
	public void NormalizeLocalExecutablePath_UnclassifiableVolumeType_IsRejected(DriveType driveType)
	{
		_ = PathSafety.NormalizeLocalExecutablePath(LocalEdge, new FakeDriveTypeProbe(driveType)).Should().BeNull();
	}

	// A probe that throws tells us nothing about the volume, so the candidate is refused
	// rather than assumed local. Not throwing matters as much as the null: the gate sits
	// under Detect(), whose D-308/D-309 contract is exit 4, never exit 1. A throw here
	// fails the test outright, so one invocation asserts both halves.
	//
	// DriveNotFoundException is NOT listed: it derives from IOException, so it enters the
	// gate through the same catch branch that IOException already covers. Keeping both
	// would add a case without adding a path through the code.
	public static TheoryData<Exception> DriveProbeFailures =>
	[
		new ArgumentException("malformed root"),
		new IOException("volume is not responding"),
		new UnauthorizedAccessException("denied"),
		new System.Security.SecurityException("blocked"),
		new InvalidOperationException("unmodelled probe failure"),
	];

	[Theory]
	[MemberData(nameof(DriveProbeFailures))]
	public void NormalizeLocalExecutablePath_DriveProbeThrows_IsRejectedWithoutThrowing(Exception exception)
	{
		string? actual = PathSafety.NormalizeLocalExecutablePath(LocalEdge, new ThrowingDriveTypeProbe(exception));

		_ = actual.Should().BeNull();
	}

	// The one failure that must still escape: the process is no longer sane and quietly
	// downgrading it to "no candidate" would be a lie.
	[Fact]
	public void NormalizeLocalExecutablePath_DriveProbeThrowsOutOfMemory_IsNotCaught()
	{
		// CA2201 (do not raise reserved exception types) is suppressed for this line only:
		// the assertion IS that this exact reserved type propagates. The instance never
		// leaves the fake probe.
#pragma warning disable CA2201
		ThrowingDriveTypeProbe probe = new(new OutOfMemoryException());
#pragma warning restore CA2201

		Func<string?> act = () => PathSafety.NormalizeLocalExecutablePath(LocalEdge, probe);

		_ = act.Should().Throw<OutOfMemoryException>();
	}

	// A malformed path must degrade to "no candidate", not throw. The volume is never
	// even reached: GetFullPath fails first. A [Fact] because there is only one input
	// worth naming here — an embedded null byte, which is what GetFullPath refuses.
	// An escaping throw fails the test on its own, so one invocation covers both halves.
	[Fact]
	public void NormalizeLocalExecutablePath_MalformedPath_IsRejectedWithoutThrowing()
	{
		string? actual = PathSafety.NormalizeLocalExecutablePath("C:\\Edge\\ms\0edge.exe", FixedVolume);

		_ = actual.Should().BeNull();
	}

	[Fact]
	public void NormalizeLocalExecutablePath_ExtremelyLongPath_IsRejectedWithoutThrowing()
	{
		string huge = @"C:\" + new string('a', 40_000) + @"\msedge.exe";

		string? actual = PathSafety.NormalizeLocalExecutablePath(huge, FixedVolume);

		_ = actual.Should().BeNull();
	}

	// Normalization is what makes the result usable as a dedupe key: two spellings of
	// one install must collapse to one string.
	[Theory]
	[InlineData(@"C:\Edge\..\Edge\msedge.exe")]
	[InlineData(@"C:\Edge\.\msedge.exe")]
	[InlineData("C:/Edge/msedge.exe")]
	[InlineData(@"C:\\Edge\\msedge.exe")]
	public void NormalizeLocalExecutablePath_EquivalentSpellings_CollapseToOneCanonicalPath(string candidate)
	{
		_ = PathSafety.NormalizeLocalExecutablePath(candidate, FixedVolume).Should().Be(LocalEdge);
	}

	// DOCUMENTED BEHAVIOUR, not a guarantee: an 8.3 short name is expanded by
	// Path.GetFullPath against the LIVE FILESYSTEM, so the gate's output depends on
	// machine state here. Verified on this machine: "C:\PROGRA~1\..." came back as
	// "C:\Program Files\...". The assertion deliberately avoids naming that folder,
	// because the expansion target differs by locale and by whether 8.3 generation is
	// enabled on the volume (fsutil 8dot3name). What MUST hold either way is the gate's
	// actual contract: the result is still drive-rooted, so an 8.3 name cannot be used to
	// smuggle a UNC or device path past the check.
	[Fact]
	public void NormalizeLocalExecutablePath_ShortName83Path_StaysDriveRooted()
	{
		const string shortNamePath = @"C:\PROGRA~1\Microsoft\Edge\Application\msedge.exe";

		string? actual = PathSafety.NormalizeLocalExecutablePath(shortNamePath, FixedVolume);

		_ = actual.Should().NotBeNull();
		_ = actual.Should().StartWith(@"C:\");
		// Never a UNC or device path, whichever way expansion went.
		_ = actual.Should().NotStartWith(@"\\");

		if (Directory.Exists(@"C:\PROGRA~1"))
		{
			// 8.3 names ARE resolvable on this volume, so expansion really happened and
			// the tilde is gone. The expanded folder name itself is not asserted.
			_ = actual.Should().NotContain("PROGRA~1");
		}

		// When 8.3 is disabled the path is returned unexpanded and still drive-rooted;
		// the assertions above hold in both worlds, so nothing is skipped outright.
	}

	// The seam default must be the real volume table, not a permissive stub: the
	// no-probe overload is what production code calls.
	[Fact]
	public void NormalizeLocalExecutablePath_DefaultOverload_UsesTheLiveVolumeTable()
	{
		// The system drive of the machine running the suite is by definition present and
		// non-network, so this must be accepted through the default seam.
		string systemDriveExe = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Edge", "msedge.exe");

		_ = PathSafety.NormalizeLocalExecutablePath(systemDriveExe).Should().Be(systemDriveExe);
		// And a UNC path is still refused without any probe involved.
		_ = PathSafety.NormalizeLocalExecutablePath(@"\\srv\s\msedge.exe").Should().BeNull();
	}

	// The rejection REASON is what the detector turns into a diagnostic line. It carries
	// no security decision (only a non-null path authorises a launch), but a wrong reason
	// would send a reader hunting the wrong gate, so each one is pinned to its cause.
	[Fact]
	public void TryNormalize_AcceptedPath_ReportsNone()
	{
		string? actual = PathSafety.TryNormalize(LocalEdge, FixedVolume, out PathRejectionReason reason);

		_ = actual.Should().Be(LocalEdge);
		_ = reason.Should().Be(PathRejectionReason.None);
	}

	// Expectations are spelled as the LOG TOKEN rather than the enum value: the enum is
	// internal, so a public [Theory] parameter cannot name it (CS0051), and the token is
	// the form that actually reaches a log line anyway.
	[Theory]
	[InlineData(null, "empty")]
	[InlineData("", "empty")]
	[InlineData("   ", "empty")]
	[InlineData(@"\\srv\s\msedge.exe", "not-drive-rooted")]
	[InlineData(@"\\?\C:\Edge\msedge.exe", "not-drive-rooted")]
	[InlineData(@"\\.\pipe\x.exe", "not-drive-rooted")]
	[InlineData("msedge.exe", "not-drive-rooted")]
	[InlineData(@"C:msedge.exe", "not-drive-rooted")]
	[InlineData("C:\\Edge\\ms\0edge.exe", "malformed-path")]
	public void TryNormalize_RejectedPath_ReportsTheGateThatRefusedIt(string? candidate, string expectedToken)
	{
		string? actual = PathSafety.TryNormalize(candidate, FixedVolume, out PathRejectionReason reason);

		_ = actual.Should().BeNull();
		_ = reason.ToToken().Should().Be(expectedToken);
	}

	// Network is reported separately from the merely unclassifiable letters: a mapped or
	// roamed install is an ordinary machine state, while NoRootDirectory/Unknown mean the
	// OS declined to classify the volume at all.
	[Theory]
	[InlineData(DriveType.Network, "network-volume")]
	[InlineData(DriveType.NoRootDirectory, "unclassifiable-volume")]
	[InlineData(DriveType.Unknown, "unclassifiable-volume")]
	public void TryNormalize_RefusedVolume_ReportsTheVolumeReason(DriveType driveType, string expectedToken)
	{
		string? actual = PathSafety.TryNormalize(LocalEdge, new FakeDriveTypeProbe(driveType), out PathRejectionReason reason);

		_ = actual.Should().BeNull();
		_ = reason.ToToken().Should().Be(expectedToken);
	}

	[Fact]
	public void TryNormalize_DriveProbeThrows_ReportsProbeFailed()
	{
		string? actual = PathSafety.TryNormalize(
			LocalEdge,
			new ThrowingDriveTypeProbe(new IOException("volume is not responding")),
			out PathRejectionReason reason);

		_ = actual.Should().BeNull();
		_ = reason.Should().Be(PathRejectionReason.ProbeFailed);
	}

	// Every reason must have a log-safe token. Asserted over the whole enum so adding a
	// value without a token fails here rather than printing "unknown" in a diagnostic.
	[Fact]
	public void ToToken_EveryReason_HasADistinctNonPlaceholderToken()
	{
		PathRejectionReason[] all = Enum.GetValues<PathRejectionReason>();

		string[] tokens = [.. all.Select(r => r.ToToken())];

		_ = tokens.Should().OnlyHaveUniqueItems();
		_ = tokens.Should().NotContain("unknown");
		// Tokens are used in log lines and must stay machine-greppable.
		_ = tokens.Should().AllSatisfy(t => t.Should().MatchRegex("^[a-z-]+$"));
	}

	// (F5) The gate is consulted once per candidate and one Detect() run gates 5 known
	// paths plus every registry registration, nearly all on the same volume. The cache
	// collapses that to one probe per volume root.
	[Fact]
	public void MemoizingDriveTypeProbe_SameRootProbedRepeatedly_HitsTheInnerProbeOnce()
	{
		FakeDriveTypeProbe inner = new(DriveType.Fixed);
		MemoizingDriveTypeProbe probe = new(inner);

		for (int i = 0; i < 20; i++)
		{
			_ = probe.GetDriveType(@"C:\").Should().Be(DriveType.Fixed);
		}

		_ = inner.QueriedRoots.Should().ContainSingle();
	}

	// Volume roots are case-insensitive on Windows, so "c:\" must not cost a second probe.
	[Fact]
	public void MemoizingDriveTypeProbe_RootDifferingOnlyByCase_IsTreatedAsOneVolume()
	{
		FakeDriveTypeProbe inner = new(DriveType.Fixed);
		MemoizingDriveTypeProbe probe = new(inner);

		_ = probe.GetDriveType(@"C:\");
		_ = probe.GetDriveType(@"c:\");

		_ = inner.QueriedRoots.Should().ContainSingle();
	}

	// Caching must not merge volumes: a second letter is a different answer.
	[Fact]
	public void MemoizingDriveTypeProbe_DifferentRoots_AreProbedSeparately()
	{
		FakeDriveTypeProbe inner = new(DriveType.Fixed);
		MemoizingDriveTypeProbe probe = new(inner);

		_ = probe.GetDriveType(@"C:\");
		_ = probe.GetDriveType(@"D:\");

		_ = inner.QueriedRoots.Should().Equal(@"C:\", @"D:\");
	}

	// FAIL-CLOSED SURVIVES THE CACHE. A failure is never remembered as an answer: the
	// exception propagates every time, so ClassifyVolume keeps converting it to
	// ProbeFailed instead of a sick volume being cached as some accepted DriveType.
	[Fact]
	public void MemoizingDriveTypeProbe_ThrowingInnerProbe_IsNotCachedAndKeepsFailing()
	{
		CountingThrowingDriveTypeProbe inner = new(new IOException("volume is not responding"));
		MemoizingDriveTypeProbe probe = new(inner);

		for (int i = 0; i < 3; i++)
		{
			Func<DriveType> act = () => probe.GetDriveType(@"C:\");
			_ = act.Should().Throw<IOException>();
		}

		_ = inner.CallCount.Should().Be(3);

		// And the gate as a whole still refuses the candidate, cache or no cache.
		_ = PathSafety.TryNormalize(LocalEdge, probe, out PathRejectionReason reason).Should().BeNull();
		_ = reason.Should().Be(PathRejectionReason.ProbeFailed);
	}

	private sealed class FakeDriveTypeProbe(DriveType driveType) : IDriveTypeProbe
	{
		private readonly DriveType _driveType = driveType;

		public List<string> QueriedRoots { get; } = [];

		public DriveType GetDriveType(string root)
		{
			QueriedRoots.Add(root);
			return _driveType;
		}
	}

	private sealed class CountingThrowingDriveTypeProbe(Exception exception) : IDriveTypeProbe
	{
		private readonly Exception _exception = exception;

		public int CallCount { get; private set; }

		public DriveType GetDriveType(string root)
		{
			CallCount++;
			throw _exception;
		}
	}

	private sealed class ThrowingDriveTypeProbe(Exception exception) : IDriveTypeProbe
	{
		private readonly Exception _exception = exception;

		public DriveType GetDriveType(string root)
		{
			throw _exception;
		}
	}
}
