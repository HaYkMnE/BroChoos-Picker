using System.Globalization;

namespace BroChoos.Platform.Windows.Storage;

// Reads the mint timestamp back out of a backup file name.
//
// DELIBERATE, TEMPORARY DUPLICATION — READ THIS BEFORE "DRYING IT UP".
// AtomicJsonFileStore.BuildBackupPath mints
// "<document name>.<yyyyMMddHHmmssfff>.<guid:N>.bak", and its PRIVATE
// BackupTimestamp reads that stamp back with exactly the rules re-implemented
// below. The read path needs the same rules, and the write-side method is private
// on a kill-process-verified file that this slice must not touch. Copying the
// rules into an internal helper here is the smaller risk than editing that file.
//
// MIGRATION, named so it is not left to drift: the write side moves onto a shared
// helper IF AND WHEN a SECOND REAL CALLER actually appears — that is the extraction
// the write side already schedules for itself (see the DESIGN DECISION comment above
// BuildBackupPath: "the extraction happens WHEN the load-side corruption-recovery
// slice arrives"). At that point BackupTimestamp collapses into this helper and this
// comment goes away.
//
// AN EARLIER REVISION NAMED THE TRIGGER WRONGLY, and the correction is recorded here
// rather than silently dropped. It predicted that the next slice — the one that
// restores and quarantines backups — WOULD be that second caller. That slice has now
// landed as ConfigRepairExecutor, and the prediction failed BY PREMISE rather than by
// schedule: the executor delegates all backup ORDERING to
// ConfigFileObserver.ObserveNthNewestBackup and never references this helper at all.
// The only consumer today is still ConfigFileObserver (three call sites: the stamp
// parse, and two comparisons against MissingFileTimestampSentinel), so the caller
// count has not moved and neither has the case for extracting.
//
// So the trigger is a SECOND REAL CALLER, counted — not "the next slice", and not any
// named slice. Arriving at the load path was never what created the second caller; a
// second caller is. The write side's own comment still names the load-side slice as
// the trigger and is now stale in the same way; correcting it needs a touch on that
// kill-process-verified file and is scheduled separately.
//
// The duplication is not held together by prose. BackupFileNameStampTests DRIVES A
// REAL AtomicJsonFileStore.Write, reads the .bak name that actually appears on
// disk, and asserts this parser extracts the stamp from it. If the mint format ever
// changes, that test fails — which is the whole point of pinning it against the
// real writer instead of against a hand-written literal.
internal static class BackupFileNameStamp
{
	// The mint format. Fixed-width, which is what makes the 17-character length
	// test below a meaningful pre-filter rather than a guess.
	private const string StampFormat = "yyyyMMddHHmmssfff";

	private const int StampLength = 17;

	// What File.GetLastWriteTimeUtc returns for a path that is NOT THERE. It does
	// not throw — it reports the Win32 FILETIME epoch, DateTime.FromFileTimeUtc(0).
	//
	// This is a TRAP, not a curiosity: a backup deleted between enumeration and stat
	// would silently acquire the oldest-possible timestamp and sort as the oldest
	// real candidate. Ordering code must detect the sentinel and report the candidate
	// as VANISHED instead of pretending it observed a 1601 timestamp.
	//
	// Pinned executably by
	// BackupFileNameStampTests.MissingFileTimestampSentinel_MatchesFrameworkBehaviour.
	internal static readonly DateTime MissingFileTimestampSentinel =
		new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

	// Reads the stamp POSITIONALLY: it is always the THIRD dot-separated segment
	// from the END, because only "<guid:N>" and "bak" ever follow it. Positional
	// reading is what keeps a document name that itself contains dots
	// ("my.config.json") parseable, where a "second segment" rule would fail.
	//
	// Every guard below is load-bearing:
	//  - Length >= 3 so [^3] cannot walk off the front;
	//  - the segment being exactly 17 characters, so "app.json.<17>.bak" (which
	//    LOOKS stamped) is rejected: with the guid segment missing, [^3] lands on
	//    "json";
	//  - TryParseExact with the invariant culture, so a machine locale cannot change
	//    which names parse;
	//  - AssumeUniversal | AdjustToUniversal, because the stamp is minted from an
	//    ambient UTC clock reading and the parsed value must come back as UTC, not
	//    as local time.
	//
	// Returns false for anything else — a foreign file in the backups directory, a
	// truncated stamp, or 17 characters that are not a real date. Callers fall back
	// to last-write-time, which unlike creation time File.Replace does not rewrite.
	internal static bool TryParse(string fileName, out DateTime stampUtc)
	{
		ArgumentNullException.ThrowIfNull(fileName);

		string[] segments = fileName.Split('.');
		if (segments.Length >= 3
			&& segments[^3].Length == StampLength
			&& DateTime.TryParseExact(
				segments[^3],
				StampFormat,
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
				out stampUtc))
		{
			return true;
		}

		stampUtc = default;
		return false;
	}
}
