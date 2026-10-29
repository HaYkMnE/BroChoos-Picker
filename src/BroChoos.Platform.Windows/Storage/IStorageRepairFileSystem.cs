using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

// The ACTING seam for the config repair path: move one whole file, copy one whole
// file. Nothing else.
//
// A THIRD SIBLING, and the narrowness is the reason there are three rather than one.
// IStorageFileSystem exists so directory PROVISIONING can be injected, and its own
// doc comment rests on staying free of unrelated members; IStorageReadFileSystem's
// header rests its separateness on the same argument, and on a fake whose entire
// value is its narrowness. Widening either one to carry Move/Copy would falsify the
// reasoning both of them are built on, so the repair executor takes THIS seam for
// its two acting primitives and reuses IStorageFileSystem.CreateDirectory for the
// quarantine directory rather than declaring a second definition of provisioning.
//
// WHY A SEAM AT ALL, given the read path already has one: the read path's seam is
// safe because it CANNOT write. This one is the opposite — it exists precisely to
// make the acting side visible and recordable, so a test can assert the ORDER in
// which bytes move without a real disk deciding the answer. The ordering invariant
// is the property under test, and an in-memory recorder is the only way to observe
// order rather than outcome.
//
// BOTH MEMBERS THROW; NEITHER REPORTS. Unlike the read seam, a failure here is not
// an ordinary observation to be folded into a verdict — it is the executor's cue to
// fail closed, and the executor converts it at the one call site that knows what
// was at stake. Reporting a bool would lose which step failed.
public interface IStorageRepairFileSystem
{
	// Moves a whole file. The destination must NOT already exist; an existing
	// destination is a failure, never a silent overwrite, because this member's only
	// caller is the quarantine step and overwriting there would destroy the very
	// bytes the move exists to preserve.
	void Move(string sourcePath, string destinationPath);

	// Reads the whole file as immutable bytes from one open. This exists so repair
	// promotion can validate exactly the same captured bytes it later stages/publishes,
	// with no reopen/re-read of the backup path between validation and commit.
	byte[] ReadAllBytes(string sourcePath);

	// Publishes already-captured bytes through the same atomic write protocol used by
	// normal durable writes (stage -> Flush(true) -> protected commit/CAS -> replace).
	void WriteBytesAtomically(
		string destinationPath,
		byte[] bytes,
		ExpectedDocumentGeneration expectedGeneration,
		TimeSpan writeLockTimeout);
}

// The real filesystem behind IStorageRepairFileSystem. One framework call each, so
// there is no behaviour here for a test to pin that the framework does not already
// guarantee — and File.Copy is a byte copy, which is what makes it the faithful
// implementation of the member above.
public sealed class SystemStorageRepairFileSystem : IStorageRepairFileSystem
{
	// File.Move WITHOUT the overwrite overload, deliberately: the two-argument form
	// throws when the destination exists, which is the wanted behaviour for a
	// quarantine and cannot be turned into an overwrite by a parameter tweak.
	public void Move(string sourcePath, string destinationPath)
	{
		File.Move(sourcePath, destinationPath);
	}

	public byte[] ReadAllBytes(string sourcePath)
	{
		return File.ReadAllBytes(sourcePath);
	}

	public void WriteBytesAtomically(
		string destinationPath,
		byte[] bytes,
		ExpectedDocumentGeneration expectedGeneration,
		TimeSpan writeLockTimeout)
	{
		AtomicJsonFileStore.WriteRawUtf8(destinationPath, bytes, expectedGeneration, writeLockTimeout);
	}
}
