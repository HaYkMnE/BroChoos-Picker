namespace BroChoos.Platform.Windows.Storage;

// D-v3.5-493 requires "atomic writes verified by kill-process test". A harness has to
// kill a writer process at EXACT points in the publish sequence and then assert the
// destination is byte-for-byte either the complete OLD document or the complete NEW
// one — never partial. Reaching those points needs the write path to announce where it
// is, so this is the seam and NOTHING more: an interface call at each interesting
// boundary. The harness itself is a later slice.
//
// The write path stays free of kill/checkpoint machinery of any kind — no
// EventWaitHandle, no Thread.Sleep, no env-var checkpoint reads. All of that belongs
// in the harness's own implementation of this interface, where it can be thrown away
// without touching production code.
//
// DELIBERATELY PUBLIC — DO NOT "TIGHTEN" TO internal, and do not "fix" it with an
// InternalsVisibleTo grant either. The throwaway kill-test scaffold lives in the
// BroChoos.App assembly (it needs an executable to kill), so it must implement this
// interface from OUTSIDE BroChoos.Platform.Windows. Narrowing the enum or the interface
// to internal compiles fine here and breaks the harness in a different assembly —
// exactly the kind of change that looks like tidying.
//
// The alternative was checked rather than assumed: AssemblyInfo.cs grants internals to
// "BroChoos.Platform.Windows.Tests" and to NOTHING else, and no assembly in this
// solution grants internals to BroChoos.App (each src assembly grants only to its own
// test assembly). Adding a grant to BroChoos.App purely to satisfy a test-only need
// would be a PRODUCTION change for no runtime benefit, and it would widen this
// assembly's internals to the whole app forever. This assembly has no external
// consumers — it is referenced by BroChoos.App and by its own test project, and is not
// packaged — so `public` here is internal architecture, not a published API surface.
//
// NullAtomicWriteObserver stays internal on purpose: it is the production default and
// nobody outside needs to name it. The existing Tests grant is what lets the test
// project see it.
/// <summary>The points on the atomic write path a <see cref="IAtomicWriteObserver"/> is
/// notified at, in the order a REPLACING write reaches them.</summary>
public enum AtomicWriteStage
{
	/// <summary>The document serialized and the serialized string parsed back cleanly.
	/// Nothing has touched the filesystem yet.</summary>
	Validated,

	/// <summary>The staged temp file is fully written, flushed to disk and CLOSED. The
	/// bytes of the new revision are durable, but the destination is untouched.</summary>
	TempFlushed,

	/// <summary>The per-file write mutex is held. Also fires when the mutex was
	/// ABANDONED by a dead holder, because that wait succeeded too.</summary>
	LockAcquired,

	/// <summary>The last point before the destination changes: the existence check has
	/// run and, on a replacing write, the backup path is computed. Fires on both the
	/// replacing and the first-create path.</summary>
	BeforePublish,

	/// <summary>The swap has COMMITTED — the destination now holds the new revision.
	/// Fires on both the replacing and the first-create path.</summary>
	AfterPublish,

	/// <summary>Backup retention has run. Fires ONLY on a replacing write, because
	/// retention only runs where a backup was minted; a first-create write skips this
	/// stage entirely. A harness must not wait on it unconditionally.</summary>
	AfterPrune,

	/// <summary>The post-publish read-back verification succeeded. Still inside the
	/// write lock.</summary>
	AfterVerify,
}

/// <summary>Write-path progress notifications. Implementations MUST NOT throw. They MUST
/// NOT block INDEFINITELY: a test observer MAY park at a stage waiting for an external
/// signal, but only under a bounded timeout it enforces itself. Production implementations
/// must return promptly. The production default is a no-op.
/// <para>NO thread-safety is provided or implied. One AtomicJsonFileStore instance may be
/// written by several threads concurrently, and each notifies the SAME observer from its own
/// write, so an observer that accumulates state must synchronize that state itself.</para></summary>
public interface IAtomicWriteObserver
{
	/// <summary>Called as the write path reaches <paramref name="stage"/>. Stages arrive on
	/// the thread performing the write, synchronously, so whatever this method does is added
	/// directly to the write's latency — and from <see cref="AtomicWriteStage.LockAcquired"/>
	/// through <see cref="AtomicWriteStage.AfterVerify"/> it is added to the time the per-file
	/// write lock is HELD. A bounded wait here is legitimate for the kill-test harness; an
	/// unbounded one wedges every other writer of this file until its own acquisition times
	/// out.</summary>
	/// <param name="stage">The boundary just reached.</param>
	void OnStage(AtomicWriteStage stage);
}

/// <summary>The production default: observes nothing, costs nothing, cannot throw.
/// Substituted whenever no observer is supplied, so the write path never has to
/// null-check.</summary>
internal sealed class NullAtomicWriteObserver : IAtomicWriteObserver
{
	/// <summary>The single shared instance. Stateless, so one is enough and every store
	/// without an observer shares it.</summary>
	internal static readonly NullAtomicWriteObserver Instance = new();

	/// <summary>Does nothing, by design. This is what makes it safe for the write path to
	/// call the observer WITHOUT a try/catch — see the first call site in
	/// <see cref="AtomicJsonFileStore"/>.</summary>
	/// <param name="stage">Ignored.</param>
	public void OnStage(AtomicWriteStage stage)
	{
	}
}
