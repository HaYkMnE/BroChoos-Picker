using System.Globalization;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// Pins BackupFileNameStamp against the write side it deliberately duplicates.
//
// WHY A MINT-THEN-PARSE TEST AND NOT A TABLE OF LITERALS. The contract being
// duplicated ("the stamp is the third-from-last dot-separated segment, 17 chars,
// yyyyMMddHHmmssfff, invariant, UTC") lives in a PRIVATE method on
// AtomicJsonFileStore, a kill-process-verified file this slice must not modify. A
// table of hand-written names would pin only this parser against ITSELF: change the
// writer's format and every literal still passes. So the first test DRIVES A REAL
// AtomicJsonFileStore.Write, reads the .bak name that actually appeared on disk, and
// asserts this parser recovers the real mint time from it. That turns an unpinnable
// prose contract into an executable one WITHOUT touching the verified file.
//
// The table below it covers the fallback EDGES, which a real write cannot produce.
public sealed class BackupFileNameStampTests
{
	// A valid 32-character hex guid segment, so the table cases differ from a real
	// minted name only in the way each case is meant to.
	private const string GuidSegment = "0123456789abcdef0123456789abcdef";

	// THE PIN. If AtomicJsonFileStore's mint format ever changes shape, this fails.
	[Fact]
	public void TryParse_AgainstARealMintedBackupName_RecoversTheWritersOwnStamp()
	{
		(string dataDir, string backupsDir, string root) = NewStorageLayout();
		try
		{
			string path = Path.Combine(dataDir, "app.json");
			AtomicJsonFileStore store = new(backupsDir);

			// TimeProvider.System, never the ambient static UTC clock: the write side
			// reaches for that directly in three places and it is a known wart, not a
			// pattern to copy into new code.
			DateTime before = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(-2);

			// First write creates (no previous revision, so no backup); the second
			// replaces and therefore mints one.
			store.Write(path, new AppDocument());
			store.Write(path, new AppDocument());

			DateTime after = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(2);

			string backupName = Path.GetFileName(
				Directory.GetFiles(backupsDir).Should().ContainSingle(
					"replacing an existing destination mints exactly one backup").Which);

			// The positional structure the parser depends on, asserted against the real
			// name rather than assumed: "<document name>.<17>.<guid:N>.bak".
			string[] segments = backupName.Split('.');
			_ = backupName.Should().StartWith("app.json.");
			_ = segments[^1].Should().Be("bak");
			_ = segments[^2].Length.Should().Be(32, "the guid segment is Guid's \"N\" format");
			_ = segments[^3].Length.Should().Be(17, "the stamp is yyyyMMddHHmmssfff");

			_ = BackupFileNameStamp.TryParse(backupName, out DateTime stamp).Should().BeTrue(
				"the read path must be able to parse the very names the write path mints");

			// NOT circular: the bound is an INDEPENDENT clock reading taken around the
			// write, so this asserts the parsed value really is the mint instant, not
			// merely that the same segment re-parses the same way.
			_ = stamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
			_ = stamp.Kind.Should().Be(
				DateTimeKind.Utc,
				"AdjustToUniversal must return a UTC DateTime, or comparisons against other UTC stamps silently shift by the machine's offset");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void TryParse_CanonicalMintedName_ReturnsTheStampAsUtc()
	{
		_ = BackupFileNameStamp.TryParse($"app.json.20260830120000123.{GuidSegment}.bak", out DateTime stamp)
			.Should().BeTrue();

		_ = stamp.Should().Be(new DateTime(2026, 8, 30, 12, 0, 0, 123, DateTimeKind.Utc));
		_ = stamp.Kind.Should().Be(DateTimeKind.Utc);
	}

	// The dotted-document-name case is exactly why the stamp is read POSITIONALLY
	// from the END. A "second segment" rule would land on "config" here and fail.
	[Fact]
	public void TryParse_DottedDocumentName_StillReadsTheStampPositionally()
	{
		_ = BackupFileNameStamp.TryParse($"my.config.json.20260830120000123.{GuidSegment}.bak", out DateTime stamp)
			.Should().BeTrue("only the guid and \"bak\" ever follow the stamp, whatever the document is called");

		_ = stamp.Should().Be(new DateTime(2026, 8, 30, 12, 0, 0, 123, DateTimeKind.Utc));
	}

	// The fallback edges. Every one of these must report "no stamp" so the caller
	// ages the file by last-write-time instead of trusting a misread name.
	[Theory]
	// 16 characters: one digit short of the format. Rejected by the length test
	// before TryParseExact ever sees it.
	[InlineData("app.json.2026083012000000." + GuidSegment + ".bak")]
	// 18 characters: one too many.
	[InlineData("app.json.202608301200000000." + GuidSegment + ".bak")]
	// Exactly 17 characters, but not a date.
	[InlineData("app.json.notatimestamp1234." + GuidSegment + ".bak")]
	// Impossible month (13) and impossible day (32): 17 digits that TryParseExact
	// still refuses. This is why the length test alone is not enough.
	[InlineData("app.json.20261330120000000." + GuidSegment + ".bak")]
	[InlineData("app.json.20260832120000000." + GuidSegment + ".bak")]
	// LOOKS stamped, is not: with the guid segment missing, the third-from-last
	// segment is "json".
	[InlineData("app.json.20260830120000123.bak")]
	// A foreign file that happens to sit in the backups directory.
	[InlineData("app.json.legacy.bak")]
	// Too few segments to index [^3] at all.
	[InlineData("notes.txt")]
	[InlineData("stamped")]
	public void TryParse_NamesThatOnlyLookStamped_ReportNoStamp(string fileName)
	{
		_ = BackupFileNameStamp.TryParse(fileName, out DateTime stamp).Should().BeFalse();
		_ = stamp.Should().Be(default, "a failed parse must not leave a half-believable value behind");
	}

	// Pins the framework behaviour the ordering code has to defend against, so the
	// sentinel constant can never drift from what the framework actually returns.
	//
	// File.GetLastWriteTimeUtc on a MISSING path does not throw — it returns the
	// Win32 FILETIME epoch. A candidate that vanishes between enumeration and stat
	// would therefore sort as the oldest real backup unless the sentinel is detected.
	[Fact]
	public void MissingFileTimestampSentinel_MatchesFrameworkBehaviourForAnAbsentFile()
	{
		string dir = NewTempDirectory();
		try
		{
			string missing = Path.Combine(dir, "not-there.bak");
			_ = File.Exists(missing).Should().BeFalse();

			// Called directly, NOT through Should().NotThrow(): the whole hazard is that
			// this does not throw, so if the framework ever started throwing, this test
			// should fail loudly rather than quietly assert an expectation about throwing.
			DateTime observed = File.GetLastWriteTimeUtc(missing);

			_ = observed.Should().Be(
				BackupFileNameStamp.MissingFileTimestampSentinel,
				"the sentinel constant must equal what the framework really returns, or vanished candidates go undetected");
			_ = observed.Kind.Should().Be(DateTimeKind.Utc);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// A null file name is a caller bug, not an observation — the one thing this
	// helper is allowed to throw for.
	[Fact]
	public void TryParse_NullFileName_ThrowsArgumentNull()
	{
		Action parse = () => _ = BackupFileNameStamp.TryParse(null!, out _);

		_ = parse.Should().Throw<ArgumentNullException>();
	}

	// Guards the table's own premise: the segment lengths the InlineData rows claim
	// really are 16, 17 and 18. A miscounted literal would make a row pass for the
	// wrong reason.
	[Fact]
	public void TableCases_HaveTheSegmentLengthsTheyClaim()
	{
		_ = "2026083012000000".Length.Should().Be(16);
		_ = "20260830120000123".Length.Should().Be(17);
		_ = "202608301200000000".Length.Should().Be(18);
		_ = "notatimestamp1234".Length.Should().Be(17);
		_ = "20261330120000000".Length.Should().Be(17);
		_ = "20260832120000000".Length.Should().Be(17);
		_ = DateTime.TryParseExact(
			"20261330120000000",
			"yyyyMMddHHmmssfff",
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
			out _).Should().BeFalse("month 13 is not a date, however many digits it has");
	}

	private static string NewTempDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "BroChoos.BackupFileNameStamp", Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(path);
		return path;
	}

	// Mirrors AtomicJsonFileStoreTests' layout: a data dir for the document and a
	// pre-provisioned backups dir (the store never creates the backups dir itself).
	private static (string DataDir, string BackupsDir, string Root) NewStorageLayout()
	{
		string root = Path.Combine(Path.GetTempPath(), "BroChoos.BackupFileNameStamp", Guid.NewGuid().ToString("N"));
		string dataDir = Path.Combine(root, "data");
		string backupsDir = Path.Combine(root, "backups");
		_ = Directory.CreateDirectory(dataDir);
		_ = Directory.CreateDirectory(backupsDir);
		return (dataDir, backupsDir, root);
	}
}
