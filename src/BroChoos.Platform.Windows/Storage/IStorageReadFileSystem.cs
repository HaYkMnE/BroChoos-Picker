namespace BroChoos.Platform.Windows.Storage;

// The READ seam for the config load path: existence, open-for-read, non-recursive
// directory enumeration, last-write-time. Nothing else, and NOTHING THAT WRITES.
//
// A SIBLING of IStorageFileSystem, deliberately NOT an extension of it. Three
// reasons, each checked against the code rather than assumed:
//
//  1. IStorageFileSystem's own doc comment states its purpose: directory
//     PROVISIONING is injectable, and it was kept separate from the browser probes
//     precisely so unrelated read-only probes "do not grow disk-writing members".
//     Adding read members to it inverts that reasoning.
//  2. Its only fake exists to assert exactly WHICH directories get created
//     (StorageRootResolverTests). Widening the interface pollutes a fake whose
//     entire value is its narrowness — every read-path test would have to satisfy
//     a provisioning recorder it does not care about.
//  3. There is no registration coupling to inherit: nothing storage-related is
//     registered in ServiceCollectionExtensions today, so splitting the seams
//     costs no DI churn.
//
// The one-way property matters more than the ergonomics: a component that can only
// take this dependency CANNOT write a byte, so it cannot violate the read path's
// ordering invariant (observe -> decide -> preserve -> write). Do not add a
// CreateDirectory, a Delete, a Move or an open-for-write member here. If a caller
// needs to write, it takes the write seam explicitly and becomes visible as such.
public interface IStorageReadFileSystem
{
	// False when the file is absent OR its directory is absent. Never throws for
	// either — both are ordinary observations on the read path.
	bool FileExists(string path);

	// False when the directory is absent OR a parent of it is absent. Never throws
	// for either — both are ordinary observations on the read path, exactly as for
	// FileExists above.
	//
	// THIS DECLARATION IS LOAD-BEARING, not symmetry for its own sake. ConfigFileObserver
	// leaves three seam calls unguarded on purpose, and TWO of them are this member:
	// the directory test on Observe's not-found branch, and the absent-backups-directory
	// early return in ListBackupCandidates (which ObserveNthNewestBackup forwards to).
	// Its header rests that decision on the never-throws declaration of BOTH members,
	// so an implementation that throws here is a BROKEN SEAM and its exception is meant
	// to escape as a defect.
	bool DirectoryExists(string path);

	// Opens for READING ONLY. Throws IOException on a sharing violation and
	// UnauthorizedAccessException on a denied ACL; the caller converts both into a
	// ConfigFileAccess.OpenFailed observation rather than letting them escape.
	Stream OpenRead(string path);

	// NON-RECURSIVE by contract, not by accident. Quarantined originals will live in
	// backups\corrupted\, and recursion would pull them into the promotable-candidate
	// list (and, on the write side, into the retention prune). Callers rely on the
	// flat listing; an implementation that recurses breaks the read path's isolation
	// from quarantine.
	IEnumerable<string> EnumerateFiles(string directory);

	// Returns the framework's missing-file sentinel (1601-01-01T00:00:00Z) rather
	// than throwing when the path is gone — see BackupFileNameStamp for why that
	// sentinel must never be mistaken for a real timestamp.
	DateTime GetLastWriteTimeUtc(string path);

	// The file's size in bytes, so a caller can REFUSE to read a pathologically
	// large file instead of discovering its size by running out of memory.
	//
	// Unlike GetLastWriteTimeUtc this one THROWS rather than reporting a sentinel
	// length: FileNotFoundException or DirectoryNotFoundException when the path is
	// gone, and IOException or UnauthorizedAccessException if the metadata cannot be
	// read. That is deliberately the SAME vocabulary OpenRead throws in, so a caller
	// that already converts those into observations needs no new handling, and there
	// is no second sentinel value to be mistaken for a real length.
	//
	// STILL A READ. Asking how big a file is writes nothing, so this does not weaken
	// the one-way property above: the seam remains incapable of creating, modifying,
	// moving or deleting anything.
	long GetLength(string path);
}

// The real filesystem behind IStorageReadFileSystem. A thin pass-through: every
// method is one framework call, so there is no behaviour here for a test to pin
// that the framework does not already guarantee.
public sealed class SystemStorageReadFileSystem : IStorageReadFileSystem
{
	public bool FileExists(string path)
	{
		return File.Exists(path);
	}

	public bool DirectoryExists(string path)
	{
		return Directory.Exists(path);
	}

	public Stream OpenRead(string path)
	{
		return File.OpenRead(path);
	}

	// Directory.EnumerateFiles(directory) — the two-argument search-pattern and the
	// SearchOption overloads are deliberately not used, so recursion cannot be
	// introduced by a parameter change at a call site.
	public IEnumerable<string> EnumerateFiles(string directory)
	{
		return Directory.EnumerateFiles(directory);
	}

	public DateTime GetLastWriteTimeUtc(string path)
	{
		return File.GetLastWriteTimeUtc(path);
	}

	// FileInfo.Length, which throws for a path that is not there rather than
	// reporting a sentinel — see the interface for why that is the wanted behaviour.
	public long GetLength(string path)
	{
		return new FileInfo(path).Length;
	}
}
