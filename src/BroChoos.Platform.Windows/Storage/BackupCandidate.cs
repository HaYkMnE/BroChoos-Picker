using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

// Where a candidate's ordering key came from. Reported rather than hidden, because
// the three cases carry very different confidence and the pure layer is entitled to
// see which one it is looking at.
public enum BackupStampSource
{
	// The authoritative key: the timestamp AtomicJsonFileStore minted into the file
	// name. Filesystem creation time cannot be used in its place — File.Replace
	// (Win32 ReplaceFile) transfers the REPLACED destination's creation time onto the
	// backup, so every backup of one document shares a ctime that says nothing about
	// when it was taken.
	MintedFileName,

	// The file name carries no parseable stamp at the expected position (a foreign
	// file, or a name that only looks stamped). Aged by last-write-time, which
	// ReplaceFile does not rewrite.
	LastWriteTime,

	// The file was gone when its timestamp was read. File.GetLastWriteTimeUtc does
	// not throw for a missing path — it returns 1601-01-01T00:00:00Z — so without
	// this member a vanished candidate would masquerade as the oldest real backup.
	// It still sorts oldest (there is nothing better to sort it by), but it is
	// LABELLED, so no caller can read that 1601 as an observation.
	Vanished,

	// The file IS there, but its timestamp could not be read — the stat itself
	// failed (a denied ACL, or an I/O error). Kept STRICTLY DISTINCT from Vanished,
	// and that distinction is the whole point of this member.
	//
	// Vanished means "gone, do not come back for it": a caller walking the candidate
	// list is entitled to move on to the next-newest. This state means the opposite
	// — the candidate EXISTS and may well become readable, so describing it as
	// Vanished would invite a caller to skip a recoverable backup. It is the same
	// class of error as collapsing "locked" into "corrupt", one level down.
	//
	// Like Vanished it carries the missing-file sentinel as its ordering stamp (there
	// is nothing better to sort it by) and therefore sorts oldest, deterministically
	// by the ordinal name key against any other sentinel-stamped candidate.
	StampUnavailable
}

// One file in the backups directory that MIGHT be a promotable backup of a given
// document, together with the key it is ordered by. Purely descriptive: nothing here
// asserts the file is a valid backup, and nothing here chooses it.
//
// FullPath is carried as DATA so the next slice can open the file. It is never
// composed into a message: D-075 / D-097 forbid full user file paths in logs,
// because in installed mode the path embeds the Windows account name. Name is a
// Core BackupName, which structurally cannot hold a directory, a drive or a
// traversal segment.
public sealed record BackupCandidate
{
	// GUARDED BY CONSTRUCTION, not by circumstance - which is the whole reason these two lines
	// exist, because no caller can reach them with a null today.
	//
	// The listing skips an entry whose name is null or empty before it ever gets here
	// (ConfigFileObserver.ListBackupCandidates), and MEASURED on .NET 10,
	// Path.GetFileName((string?)null) returns null - so a seam yielding a null entry is dropped
	// at that guard and never reaches this constructor. That makes "no null can arrive" a
	// consequence of a guard in ANOTHER type, one refactor away from being untrue.
	//
	// BackupCandidateObservation, two types down in this same file, already validates its own
	// argument for exactly this reason. This brings the pair into line. The cost is two lines;
	// the alternative is an invariant that holds only as long as nobody edits the caller.
	internal BackupCandidate(
		BackupName name,
		string fullPath,
		DateTime orderingStampUtc,
		BackupStampSource stampSource)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(fullPath);

		Name = name;
		FullPath = fullPath;
		OrderingStampUtc = orderingStampUtc;
		StampSource = stampSource;
	}

	public BackupName Name { get; }

	public string FullPath { get; }

	// The sort key. Meaningful as an OBSERVED time only when StampSource is
	// MintedFileName or LastWriteTime; when StampSource is Vanished or
	// StampUnavailable this is the missing-file sentinel and must not be read as a
	// time — in the StampUnavailable case no timestamp was obtained at all.
	public DateTime OrderingStampUtc { get; }

	public BackupStampSource StampSource { get; }

	// Convenience for the common guard. Kept as a computed property so there is one
	// definition of "vanished" and it cannot drift from StampSource.
	public bool Vanished => StampSource == BackupStampSource.Vanished;
}

// What was observed about ONE candidate backup.
//
// WHY THIS TYPE EXISTS INSTEAD OF JUST A Core BackupObservation: BackupObservation's
// factories are Loaded / BodyUnparseable / EnvelopeUnparseable, and ALL THREE
// presuppose that the bytes were obtained. There is deliberately no
// BackupObservation for "the candidate was locked" or "the candidate vanished", so
// those two states cannot be expressed in Core — and Core is not to be modified.
// This platform-side wrapper reports them honestly instead: Access says whether the
// bytes were obtained, and Observation is non-null EXACTLY when they were.
//
// A caller that finds Observation null has learned something real ("there is a
// candidate file, but it could not be read"), which is not the same as Core's
// ConfigFileObservation.Backup being null ("no candidate was found at all").
public sealed record BackupCandidateObservation
{
	// PRIVATE, with named factories below — the invariant "Observation is non-null
	// EXACTLY when Access is Opened" is now UNWRITABLE, not merely documented.
	//
	// It used to be internal and unvalidated. External code could not reach it, but
	// this assembly can, and so can its test assembly through
	// InternalsVisibleTo("BroChoos.Platform.Windows.Tests") — so nothing stopped a
	// future maintainer writing `new BackupCandidateObservation(c, Opened, null)`
	// ("opened, but nothing was observed") or pairing a non-Opened access with a
	// verdict ("could not read it, and here is what it says"). Both are SELF-
	// CONTRADICTORY OBSERVATIONS, which is exactly the class of lie Core makes
	// unrepresentable with private constructors plus named factories throughout
	// (BackupObservation, ConfigFileObservation, RepairClearance). This type was the
	// one place on the read path that reintroduced the possibility; it no longer
	// does. The pairings — and the absence of any non-private constructor — are
	// pinned executably by ConfigFileObserverTests rather than left to the call
	// sites to honour.
	private BackupCandidateObservation(
		BackupCandidate candidate,
		ConfigFileAccess access,
		BackupObservation? observation)
	{
		Candidate = candidate;
		Access = access;
		Observation = observation;
	}

	public BackupCandidate Candidate { get; }

	// Absent    -> the candidate vanished between enumeration and open.
	// OpenFailed-> it exists but would not open (sharing violation, denied ACL).
	// Opened    -> bytes obtained; Observation carries the parse and schema verdict.
	public ConfigFileAccess Access { get; }

	// Non-null exactly when Access is Opened. Guaranteed by construction: only
	// Opened() can supply one, and it refuses null.
	public BackupObservation? Observation { get; }

	// The candidate vanished between enumeration and open. No verdict exists,
	// because no bytes were obtained.
	internal static BackupCandidateObservation Absent(BackupCandidate candidate)
	{
		return new(candidate, ConfigFileAccess.Absent, observation: null);
	}

	// The candidate is there but would not open (sharing violation, denied ACL).
	// Carries NO verdict, deliberately: a file that could not be read is not known
	// to be damaged, and describing it as damaged would later authorise a
	// destructive repair of undamaged data.
	internal static BackupCandidateObservation OpenFailed(BackupCandidate candidate)
	{
		return new(candidate, ConfigFileAccess.OpenFailed, observation: null);
	}

	// Bytes were obtained and parsed. The observation is REQUIRED — "opened, but
	// nothing observed" is not a state this type can express.
	internal static BackupCandidateObservation Opened(BackupCandidate candidate, BackupObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation);
		return new(candidate, ConfigFileAccess.Opened, observation);
	}
}
