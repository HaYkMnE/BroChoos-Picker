using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

// Which step of a repair an event describes. Machine-readable, because D-v3.5-489
// keeps every user-facing surface in 0B, so no wording is invented here.
public enum ConfigRepairStep
{
	// The decision was carried out with no byte written, moved or replaced.
	CompletedWithoutWriting,

	// The original was moved into backups\corrupted. Owner decision 1 route (b):
	// this event is the "and that is logged" half of the licence to write.
	OriginalPreserved,

	// The original could NOT be preserved. Owner decision 2: fails closed, no retry.
	QuarantineFailed,

	// A validated backup was promoted over the destination by copying bytes.
	BackupPromoted,

	// Defaults were written to the destination.
	DefaultsPersisted,

	// The write itself failed after the original was already preserved. Fail open
	// (owner decision 10): the session continues on in-memory defaults.
	WriteFailed,

	// A candidate backup was refused and the next-newest was tried. Owner decision
	// 8: this iteration consumes the repair attempt budget.
	BackupCandidateRefused,

	// The attempt budget refused a further attempt. This is the ONLY place on the
	// repair path where elapsed time is consulted at all — which is not the same as
	// the D-v3.5-493 cap being enforced, because the check happens BETWEEN attempts
	// and never measures the attempt it authorised. See ConfigRepairExecutor's header
	// for the mechanism and for the two residual gaps it does not close.
	AttemptBudgetDenied
}

// ONE redacted, session-only repair event (D-v3.5-075 / D-v3.5-097).
//
// FileName IS A BARE FILE NAME, NEVER A PATH, and that is the whole point of the
// type. In installed mode a config path embeds the Windows ACCOUNT NAME, so a full
// path in a log is a privacy leak; the write side already redacts its own messages to
// the file name for exactly this reason. Paths travel as data on BackupCandidate,
// never through here.
//
// FailureType IS A TYPE NAME, NEVER A MESSAGE. A framework exception's own message
// embeds the full path (DirectoryNotFoundException does), and an InnerException chain
// carries it too, so neither ToString() nor InnerException.Message may ever be
// rendered. A type name says what went wrong without saying where.
public sealed record ConfigRepairEvent(
	string FileName,
	ConfigRepairStep Step,
	ConfigLoadAction Action,
	ConfigLoadReason Reason,
	string? FailureType);

// Where repair events go. A seam, so the 0B logging slice can replace the sink
// without the executor learning anything about rendering.
public interface IConfigRepairLog
{
	// MUST NOT THROW. A sink that fails must not turn a successful repair into a
	// failed one, so the executor calls this without a guard on the strength of this
	// declaration.
	void Record(ConfigRepairEvent entry);
}

// The default sink: SESSION-ONLY, in memory, never persisted.
//
// Not persisted for the same reason RepairAttemptBudget is not persisted: the files
// whose read can fail are the config files themselves, so a durable repair log would
// need its own durable write on the very path that is already failing. D-v3.5-489
// puts the real logging surface in 0B.
public sealed class InMemoryConfigRepairLog : IConfigRepairLog
{
	private readonly List<ConfigRepairEvent> _entries = [];

	// Snapshot, so a reader cannot mutate the log and cannot observe it tearing
	// while another thread records.
	public IReadOnlyList<ConfigRepairEvent> Entries
	{
		get
		{
			lock (_entries)
			{
				return [.. _entries];
			}
		}
	}

	public void Record(ConfigRepairEvent entry)
	{
		ArgumentNullException.ThrowIfNull(entry);

		lock (_entries)
		{
			_entries.Add(entry);
		}
	}
}

// What the executor did to one config file.
public sealed record ConfigRepairOutcome(
	ConfigLoadAction Action,
	ConfigLoadReason Reason,
	bool OriginalPreserved,
	bool BackupPromoted,
	bool DefaultsPersisted,
	// True when the destination now holds a document this build may read. False means
	// the session runs on in-memory defaults — which is D-v3.5-083 fail-open, not a
	// crash (owner decision 10).
	bool DestinationUsable,
	// True when this file must not be written for the rest of the session: a hard
	// stop (D-066 / D-070) or a failed quarantine (owner decision 2).
	bool ReadOnlyForSession);

// EXECUTES a ConfigLoadDecision. This is the ACTING half of the D-063 / D-085 config
// read path: ConfigFileObserver describes, ConfigLoadPlanner decides, and nothing in
// either of them touches a byte. Everything that moves, copies or writes is here.
//
// THE ORDERING INVARIANT (owner decision 1) IS STRUCTURAL, NOT DOCUMENTED. State it
// as the one rule it is:
//
//   NO BYTE IS WRITTEN, MOVED OR REPLACED UNTIL EITHER THE ON-DISK ENVELOPE HAS BEEN
//   READ AND PROVED NOT-NEWER, OR THE ORIGINAL IS PRESERVED IN backups\corrupted AND
//   THAT IS LOGGED.
//
// THE ENFORCEMENT IS ONE COMPILE-TIME BARRIER PLUS ONE STRUCTURAL PROPERTY. They are
// named apart because they are not the same claim and they do not fail together:
//
//   THE BARRIER: every ConfigLoadDecision factory is internal to BroChoos.Core, so this
//     assembly cannot mint a decision of ANY kind. That is the whole of the compile-time
//     protection — there is no second compile-time layer behind it.
//   THE PROPERTY: no decision built anywhere else can reach the acting code. Perform is
//     private, and Execute calls ConfigLoadPlanner.Plan ITSELF instead of accepting a
//     decision, so this type's public surface takes no ConfigLoadDecision and returns
//     none — a forged or stale one has no route to a write. This is what actually
//     protects the ordering invariant.
//
//     WHAT IS MACHINE-CHECKED AND WHAT WAS CHECKED BY HAND ARE NOT THE SAME SIZE, and an
//     earlier revision of this sentence claimed a fact pinned the whole property when it
//     pins one half. The fact in ConfigRepairExecutorTests pins the SIGNATURE-SHAPE half:
//     no externally visible member of this type mentions a ConfigLoadDecision anywhere in
//     its signature, in any composition. The other half was closed by reading this file,
//     and is recorded because it is now checked rather than assumed — the visible surface
//     is the constructor below and Execute<TDocument>, whose TDocument : ConfigDocument
//     constraint a ConfigLoadDecision cannot satisfy (it is a sealed class and derives
//     from nothing that would let it); no field or property of decision type exists at ANY
//     accessibility, every one being a local or a private parameter; this file performs no
//     reflection, its only GetType() being on an exception for a log name; and no visible
//     member hands back a wrapper holding a decision.
//
//     THE HAND-CHECKED HALF DECAYS SILENTLY, which is the whole reason for writing it
//     down. A future `public void Something(object)` would accept a boxed decision and the
//     fact would stay green through it: object does not mention ConfigLoadDecision, and a
//     signature-shape fact reads signatures rather than what flows through them. For the
//     wrapper limitation, see "WHAT THIS FACT PROVES, AND WHAT IT CANNOT" in
//     ConfigRepairExecutorTests. Whoever adds such a member owns re-closing this by hand,
//     because nothing will report it.
//
//     INFLUENCE OVER THE INPUTS IS NOT A ROUTE IN, and the nearest thing to a
//     counterexample is named here so the property is not read as more than it is.
//     ConfigFileObserver is public sealed over IStorageReadFileSystem, so an external
//     caller can supply a hostile filesystem and thereby shape what gets OBSERVED. Only the
//     DECISION is minted by ConfigLoadPlanner.Plan; Execute passes the observation directly
//     to Perform. So such a caller gets a legitimately planned decision for the facts it
//     presented, never an object it built.
//     Influence over the inputs, not a decision handed in.
//
// THE BARRIER IS CHECKED, NOT ASSUMED — and it was checked because it is worthless if Core
// shares its internals with this assembly. Core's ONLY grant is
// [assembly: InternalsVisibleTo("BroChoos.Core.Tests")] (src/BroChoos.Core/AssemblyInfo.cs,
// its single attribute). No .csproj in the tree declares an InternalsVisibleTo item and
// neither does Directory.Build.props, so there is no second grant anywhere and
// BroChoos.Platform.Windows is NOT granted.
//
// THE CAPABILITY TOKENS ARE NOT AN INDEPENDENT SECOND LAYER, AND THE COUNTERFACTUAL THAT
// SAID THEY WERE WAS MEASURED FALSE. An earlier revision of this comment recorded that if
// Core ever granted InternalsVisibleTo to this assembly, the MUTATING factories would stay
// unmintable, because each demands a PersistClearance, RepairClearance or
// BackupPromotionClearance whose constructor is private. An audit built a sandbox assembly
// holding exactly this assembly's privilege — referencing the built BroChoos.Core.dll, no
// project reference, not named BroChoos.Core.Tests — and minted all three genuine tokens,
// then produced every mutating decision from them. The constructors are indeed private,
// but TryClear is PUBLIC on all three (PersistClearance.cs, RepairClearance.cs,
// BackupPromotionClearance.cs) and every ConfigFileObservation factory is public static, so
// the inputs a token demands are publicly constructible too. The tokens therefore die WITH
// the internal factories, not after them. Do not narrow TryClear to make the old paragraph
// true: that is editing code to rescue a comment, the tokens are not what holds the
// invariant, and anyone able to write the forging code could call File.Move directly.
//
// So the executor cannot decide to write — it can only carry out a
// decision the planner already licensed. On the quarantine-first route the executor
// does not write from the original decision at all: it quarantines, RE-PLANS from the
// advanced observation, and writes only if the planner licenses it a second time.
// A quarantine that fails therefore cannot be followed by a write, because
// PersistClearance refuses to mint for a Failed quarantine.
//
// THE D-v3.5-493 DEADLINE, AND ITS HONEST LIMIT — READ THIS BEFORE STRENGTHENING THE
// CLAIM. D-v3.5-493 caps picker-open at 2000 ms and RepairAttemptBudget is a
// PRE-ATTEMPT GATE (3 attempts, ~1500 ms), so an attempt beginning at 1499 ms was
// previously unbounded and the cap was enforced NOWHERE. What this type adds is
// exactly this and no more:
//
//   THE DEADLINE IS CHECKED BETWEEN ATTEMPTS, AND EACH ATTEMPT IS BOUNDED ONLY BY THE
//   TIMEOUTS ITS CALLEES ACCEPT.
//
// That is a real bound rather than a hard cap, and the difference is deliberate.
// SYNCHRONOUS .NET FILE I/O IGNORES CancellationToken: File.Move, File.Copy,
// File.Replace and a synchronous FileStream.Write cannot be cancelled mid-call, so
// "per-attempt timeout with cancellation" is not implementable and is not claimed.
// What makes the bound sound anyway is MEASURED rather than assumed:
//
//   THE DOMINANT COST IS PARAMETERIZABLE. AtomicJsonFileStore's write-lock wait is
//     the only unbounded wait on the path, its default is 10 s — several times the
//     whole repair budget — and it is a CONSTRUCTOR ARGUMENT. Measured against a held
//     mutex, elapsed time tracks the value passed: ~10.2 s for 10 s, ~415 ms for
//     400 ms, ~126 ms for 120 ms. So this type derives the timeout from the budget
//     that is LEFT, and the dominant violator becomes bounded without cancelling any
//     synchronous call. A timed-out wait publishes nothing (measured: the destination
//     did not exist afterwards), which is what makes shortening it safe.
//   THE ACTING CALLS DO NOT WAIT ON CONTENTION — THEY REFUSE, and the REFUSAL is the
//     load-bearing property, not any duration. Measured against a FileShare.None-held
//     file, every one of File.Move, File.Copy (either direction), File.Replace,
//     File.Delete and File.OpenRead fails with IOException rather than blocking on the
//     foreign handle. That is what makes an uncancellable call a bounded one: it
//     returns or throws instead of parking until the holder lets go.
//   AN UNCONTENDED REPAIR IS ORDERS OF MAGNITUDE INSIDE THE BUDGET. Medians: File.Move
//     (quarantine) 0.4 ms, File.Copy overwrite (promotion) 0.8 ms, and a full store
//     write over an existing destination — Replace plus backup mint plus prune plus
//     read-back — 6.5 ms, worst sample 19.6 ms against a 1500 ms budget.
//
// REPRODUCING THOSE FIGURES matters more than the figures, which are one machine on
// one run: hold the mutex whose name AtomicJsonFileStore.BuildMutexName derives from
// the destination path, on a SEPARATE THREAD (Mutex is thread-affine and recursive, so
// holding it on the calling thread measures nothing), then write through a store
// constructed with the timeout under test. A wait that does not block means the mutex
// name was reproduced wrongly, so a blocked write is itself the evidence that the
// right kernel object was taken.
//
// THE RESIDUAL GAP, RECORDED RATHER THAN DESIGNED AROUND — AND IT IS WIDER THAN A
// FUTURE-CALLEE WARNING. Two distinct holes, the first of them live TODAY:
//
//   1. THE POST-GATE COST OF THE LAST ATTEMPT IS UNCOUNTED, so the 2000 ms cap is
//      still not bounded by this type. RepairAttemptBudget.Evaluate is a PRE-ATTEMPT
//      GATE: ChooseBackup calls it, it authorises an attempt, and then it measures
//      NOTHING about what that attempt costs. So attempt 3 may legitimately begin at
//      1499 ms against the 1500 ms default budget, and everything after the gate is
//      outside every bound in this file:
//        * ObserveNthNewestBackup — a directory enumeration over backups\ plus a
//          candidate read admitting up to ConfigFileObserver's 8 MiB MaxReadableBytes
//          ceiling. Neither is timeout-bounded, and the ceiling is sized to never
//          reject a legitimate file rather than to bound a read's duration.
//        * Perform's quarantine — CreateDirectory then Move, before any budget is
//          consulted.
//        * Only THEN does PersistDefaults derive a write-lock timeout, and a spent
//          budget floors it at MinimumWriteLockTimeout (50 ms) rather than zero.
//      True worst case is therefore 1499 ms + an unbounded attempt-3 read +
//      provisioning + move + 50 ms + the store's own internals — which on a slow or
//      network volume EXCEEDS the locked D-v3.5-493 cap of 2000 ms. Do not read the
//      "checked between attempts" bound above as a latency guarantee; it is exactly
//      and only what it says.
//
//      THE FAILURE DIRECTION IS SAFE FOR BYTES AND UNSAFE FOR THE DEADLINE. Nothing
//      above writes early, skips a preservation or re-orders the invariant when this
//      overruns — a licensed write stays licensed and the original stays preserved.
//      What breaks is a timing decision: the picker opens slowly. That is why this is
//      recorded as a residual instead of being patched here.
//
//      NOT FIXABLE IN THIS SLICE, and the reasons are mechanical rather than a matter
//      of effort. Synchronous .NET file I/O ignores CancellationToken (see above), so
//      the enumeration and the read cannot be cut short mid-call; and the 8 MiB
//      ceiling belongs to ConfigFileObserver, which this slice must not modify. A
//      documented gap beats an invented bound — the bound that IS claimed above was
//      arrived at by measuring a parameterizable timeout, and there is no equivalent
//      measurement to make here.
//
//   2. A callee that neither accepts a timeout nor fails fast would overrun even
//      inside a counted attempt, and nothing here could stop it. No such callee is on
//      this path today. That is a property of the MEASURED path, not a guarantee about
//      arbitrary future ones — a new blocking call added below is a new hole, and it
//      will not announce itself.
public sealed class ConfigRepairExecutor
{
	// The quarantine subdirectory (owner decision 7). Quarantined originals go
	// STRICTLY here and nowhere else.
	//
	// THIS IS WHAT KEEPS QUARANTINE OUT OF THE PROMOTABLE-CANDIDATE LIST, and the
	// isolation rests on the read seam's NON-RECURSIVE enumeration rather than on
	// naming: ListBackupCandidates enumerates backups\ flatly, so a file one level
	// down cannot be listed however it is named. The distinct extension below is the
	// SECOND, independent reason — owner decision 7 also requires that a quarantined
	// name never collide with a backup name.
	private const string QuarantineDirectoryName = "corrupted";

	// The quarantine name shape. Deliberately NOT ".bak": a backup minted by the
	// write side is "<file>.<stamp>.<guid:N>.bak", so a different final segment makes
	// a collision impossible by construction rather than by luck. The stamp and guid
	// are kept so two quarantines of the same file in one session cannot collide with
	// each other either — File.Move refuses an existing destination, and a collision
	// would therefore fail a repair that had nothing wrong with it.
	private const string QuarantineExtension = ".corrupted";

	// This executor keeps a local 50 ms floor because it owns the remaining-budget
	// derivation passed to the write-lock wait. The detailed TimeSpan/Mutex.WaitOne
	// hazard rationale is owned by
	// src/BroChoos.Platform.Windows/Storage/BrowserCatalogPersistenceService.cs:
	// BrowserCatalogPersistenceService.MinimumWriteLockTimeout.
	private static readonly TimeSpan MinimumWriteLockTimeout = TimeSpan.FromMilliseconds(50);

	private static readonly TimeSpan PromotionWriteLockTimeout = TimeSpan.FromSeconds(5);

	private readonly ConfigFileObserver _observer;

	private readonly IStorageRepairFileSystem _repairFileSystem;

	private readonly IStorageFileSystem _provisioning;

	private readonly IConfigDocumentWriter _writer;

	private readonly TimeProvider _timeProvider;

	private readonly IConfigRepairLog _log;

	// TEST SEAM: invoked on the calling thread immediately before the per-file lock wait, so a
	// test can prove that everything preceding the wait has already happened. Null in
	// production.
	internal Action? BeforeLockWait { get; init; }

	// The provisioning seam is the EXISTING IStorageFileSystem, not a second
	// definition of CreateDirectory: backups\corrupted is not pre-provisioned (owner
	// decision 3's reason for quarantine being able to fail at all), and
	// StorageRootResolver already owns directory creation through that interface.
	public ConfigRepairExecutor(
		ConfigFileObserver observer,
		IStorageRepairFileSystem repairFileSystem,
		IStorageFileSystem provisioning,
		IConfigDocumentWriter writer,
		TimeProvider timeProvider,
		IConfigRepairLog log)
	{
		ArgumentNullException.ThrowIfNull(observer);
		ArgumentNullException.ThrowIfNull(repairFileSystem);
		ArgumentNullException.ThrowIfNull(provisioning);
		ArgumentNullException.ThrowIfNull(writer);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(log);

		_observer = observer;
		_repairFileSystem = repairFileSystem;
		_provisioning = provisioning;
		_writer = writer;
		_timeProvider = timeProvider;
		_log = log;
	}

	// Loads and, if necessary, repairs ONE config file.
	//
	// ONE FILE, BY CONTRACT (D-071). There is deliberately no trio-wide entry point
	// and no trio-wide verdict: D-071 makes atomicity per file and explicitly refuses
	// a transaction across the JSON trio, so app.json, settings.json and browsers.json
	// each get their own call and their own outcome. "app.json good, browsers.json
	// repaired" is a LEGITIMATE result, not a partial failure to be reconciled.
	//
	// FAILS OPEN (owner decision 10 / D-v3.5-083). Every failure below is converted
	// into an outcome; nothing escapes to crash startup. The one exception is a CALLER
	// BUG — a null argument — which is rejected before any work, exactly as the read
	// side rejects one, because it is fixable at the call site and has touched nothing.
	public ConfigRepairOutcome Execute<TDocument>(
		string destinationPath,
		string backupsDirectory,
		TDocument defaults)
		where TDocument : ConfigDocument
	{
		return Execute(destinationPath, backupsDirectory, defaults, quarantineOversized: false);
	}

	// quarantineOversized: an explicit, caller-requested extra route for a file over the
	// observer's read ceiling. The planner can never repair such a file (it is OpenFailed:
	// unread, so nothing is known about it), which would leave it in place forever. With this
	// flag the file is MOVED, unread, into backups\corrupted - a move destroys nothing, so owner
	// decision 1's route (b) "original preserved first" holds - and the now-absent destination
	// then takes the ordinary absent-file plan (defaults). Only Safe Mode's repair-settings
	// passes true; the default keeps every existing caller's behaviour.
	//
	// THE PER-FILE WRITE LOCK IS HELD FOR THE WHOLE CALL, from the observation to the last
	// write, so no other BroChoos writer (the picker's history record, a rules save) can land
	// between "observed broken" and "replaced" - or between the quarantine move and the write
	// that follows it. The inner writes take the same named mutex again on this thread, which a
	// Windows mutex allows (it is re-entrant for its owning thread). A lock that cannot be taken
	// in time touches nothing.
	public ConfigRepairOutcome Execute<TDocument>(
		string destinationPath,
		string backupsDirectory,
		TDocument defaults,
		bool quarantineOversized)
		where TDocument : ConfigDocument
	{
		ArgumentNullException.ThrowIfNull(destinationPath);
		ArgumentNullException.ThrowIfNull(backupsDirectory);
		ArgumentNullException.ThrowIfNull(defaults);

		string fileName = Path.GetFileName(destinationPath);
		Mutex? writeLock = null;
		bool lockHeld = false;

		try
		{
			writeLock = new Mutex(initiallyOwned: false, AtomicJsonFileStore.BuildMutexName(destinationPath));
			try
			{
				// THE LOCK WAIT HAS ITS OWN FIXED CAP and is deliberately NOT charged to the
				// repair budget. Starting the budget before this wait (a review suggestion)
				// was tried and rejected: the budget is a pre-attempt gate for the repair
				// WORK, so a lock that was won late (>= the 1.5 s budget) made ChooseBackup
				// see DeniedTimeBudgetExhausted and fall back to quarantine + defaults even
				// though a compatible backup existed, and a contended lock gave up after
				// ~1.5 s instead of this cap. The budget therefore starts at acquisition.
				BeforeLockWait?.Invoke();
				lockHeld = writeLock.WaitOne(PromotionWriteLockTimeout);
			}
			catch (AbandonedMutexException)
			{
				lockHeld = true;
			}

			if (!lockHeld)
			{
				Record(fileName, ConfigRepairStep.WriteFailed, ConfigLoadAction.UseInMemoryDefaults,
					ConfigLoadReason.OpenFailedDefaultsInMemoryOnly, failure: null);
				return new ConfigRepairOutcome(
					ConfigLoadAction.UseInMemoryDefaults,
					ConfigLoadReason.OpenFailedDefaultsInMemoryOnly,
					OriginalPreserved: false,
					BackupPromoted: false,
					DefaultsPersisted: false,
					DestinationUsable: false,
					ReadOnlyForSession: false);
			}

			RepairAttemptBudget budget = RepairAttemptBudget.Start(_timeProvider);

			ConfigFileObservation observation = _observer.Observe<TDocument>(destinationPath);
			ConfigLoadDecision decision = ConfigLoadPlanner.Plan(observation);

			if (quarantineOversized
				&& observation.Access == ConfigFileAccess.OpenFailed
				&& _observer.ExceedsReadCeiling(destinationPath))
			{
				return RepairOversized(destinationPath, backupsDirectory, fileName, defaults, decision, budget);
			}

			// Owner decision 8: the observer only DESCRIBES the Nth-newest candidate,
			// so walking the list is this type's loop, and it consumes the budget.
			// Reached only on the repair route — an absent, unreadable, sound or
			// hard-stopped file has no promotion to consider.
			if (decision.QuarantineFirst)
			{
				(decision, observation, budget) =
					ChooseBackup<TDocument>(observation, decision, backupsDirectory, fileName, budget);
			}

			return Perform(decision, observation, destinationPath, backupsDirectory, fileName, defaults, budget);
		}
		catch (Exception failure) when (failure is not ArgumentNullException)
		{
			return FailOpen(fileName, failure);
		}
		finally
		{
			if (lockHeld)
			{
				writeLock!.ReleaseMutex();
			}

			writeLock?.Dispose();
		}
	}

	// Read-only: the picker's verdict on one file (see ConfigFileObserver.ObserveReadOutcome).
	// Exposed here so Safe Mode judges a repair by the same rule the picker's notice uses.
	public ConfigReadOutcome ObserveReadOutcome<TDocument>(string destinationPath)
		where TDocument : ConfigDocument
	{
		ArgumentNullException.ThrowIfNull(destinationPath);
		return _observer.ObserveReadOutcome<TDocument>(destinationPath).Outcome;
	}

	// THE OVERSIZED ROUTE. Called with the per-file lock held. The file is MOVED, unread, into
	// backups\corrupted (Quarantine, the same move the corrupt route uses - nothing is read,
	// copied or deleted), and only then is the destination observed AGAIN. It is now absent, so
	// the planner itself plans the next step (create defaults, with Core's own PersistClearance
	// for an absent file) and the ordinary Perform carries it out. No decision is minted here:
	// the planner stays the only thing that licenses a write. A failed move leaves the file in
	// place and writes nothing; a backup is NOT promoted on this route, because the planner only
	// walks backups for a file whose bytes it read and found broken.
	private ConfigRepairOutcome RepairOversized<TDocument>(
		string destinationPath,
		string backupsDirectory,
		string fileName,
		TDocument defaults,
		ConfigLoadDecision current,
		RepairAttemptBudget budget)
		where TDocument : ConfigDocument
	{
		QuarantineAttempt moved = Quarantine(destinationPath, backupsDirectory, fileName, current);
		if (moved != QuarantineAttempt.Succeeded)
		{
			return new ConfigRepairOutcome(
				ConfigLoadAction.QuarantineFailedReadOnly,
				current.Reason,
				OriginalPreserved: false,
				BackupPromoted: false,
				DefaultsPersisted: false,
				DestinationUsable: false,
				ReadOnlyForSession: true);
		}

		ConfigFileObservation afterMove = _observer.Observe<TDocument>(destinationPath);
		ConfigLoadDecision next = ConfigLoadPlanner.Plan(afterMove);
		ConfigRepairOutcome outcome = Perform(next, afterMove, destinationPath, backupsDirectory, fileName, defaults, budget);

		// The original was preserved by the move above, which Perform could not see.
		return outcome with { OriginalPreserved = true };
	}

	private ConfigRepairOutcome FailOpen(string fileName, Exception failure)
	{
		// FAIL OPEN, AND THE BREADTH IS THE POINT. A repair runs on the picker's
		// interactive path, so no filesystem state and no broken collaborator may
		// take the app down: the user's link must still open (D-v3.5-083). The
		// session continues on in-memory defaults, and the destination is reported
		// unusable rather than guessed about.
		//
		// The exception object is NOT rendered — only its TYPE reaches the log, for
		// the D-075 / D-097 reason on ConfigRepairEvent.
		//
		// WRAPPED, UNLIKE EVERY OTHER Record CALL SITE, and for the reason
		// AtomicJsonFileStore.Classify is wrapped: this one runs while an exception
		// is ALREADY IN FLIGHT and its only job is to add diagnostics. IConfigRepairLog
		// declares that implementations must not throw, so every other call site trusts
		// that declaration — but here a sink that broke it would replace the fail-open
		// outcome with a crash, which is precisely what owner decision 10 forbids.
		// Losing a log entry is a diagnostic gap; losing the fail-open guarantee is the
		// bug this whole method exists to prevent.
		try
		{
			Record(fileName, ConfigRepairStep.WriteFailed, ConfigLoadAction.UseInMemoryDefaults,
				ConfigLoadReason.OpenFailedDefaultsInMemoryOnly, failure);
		}
		catch (Exception sinkFailure) when (sinkFailure is not ArgumentNullException)
		{
		}

		return new ConfigRepairOutcome(
			ConfigLoadAction.UseInMemoryDefaults,
			ConfigLoadReason.OpenFailedDefaultsInMemoryOnly,
			OriginalPreserved: false,
			BackupPromoted: false,
			DefaultsPersisted: false,
			DestinationUsable: false,
			ReadOnlyForSession: true);
	}

	// Walks the candidate list newest-first, stopping at the first candidate the
	// PLANNER accepts. Returns the decision to carry out.
	//
	// THE ITERATION IS THE RETRY LOOP D-089 BOUNDS, which is why the budget is spent
	// here rather than only around the write: each candidate costs a read of a file
	// that may be locked or absent, and an unbounded walk of a large backups directory
	// can exceed the measured D-v3.5-493 picker-open target. The elapsed budget is only
	// a pre-attempt gate; it does not enforce or cap picker-open latency after an attempt begins.
	//
	// A REFUSAL IS NOT AN ERROR. The incoming decision is already the correct
	// fallback — quarantine-then-create-defaults — so exhausting the candidates simply
	// keeps it, and the refused backup FILES ARE NEVER TOUCHED (owner decision 3).
	// Promotability is not re-decided here: Plan is asked again with the candidate
	// attached, so BackupPromotionClearance stays the only thing that admits a
	// promotion.
	private (ConfigLoadDecision Decision, ConfigFileObservation Observation, RepairAttemptBudget Budget) ChooseBackup<TDocument>(
		ConfigFileObservation observation,
		ConfigLoadDecision fallback,
		string backupsDirectory,
		string fileName,
		RepairAttemptBudget budget)
		where TDocument : ConfigDocument
	{
		for (int index = 0; ; index++)
		{
			RepairAttemptPolicy policy = budget.Evaluate();
			if (policy != RepairAttemptPolicy.Permitted)
			{
				// D-089 / D-v3.5-493. Keeping the fallback is the safe direction: the
				// original is still preserved before anything is written.
				Record(fileName, ConfigRepairStep.AttemptBudgetDenied, fallback.Action, fallback.Reason, failure: null);
				return (fallback, observation, budget);
			}

			budget = budget.RecordAttempt();

			BackupCandidateObservation? candidate =
				_observer.ObserveNthNewestBackup<TDocument>(backupsDirectory, fileName, index);

			// Past the end of the list. Not a failure — most repairs have no promotable
			// backup at all, which is why the fallback exists.
			if (candidate is null)
			{
				return (fallback, observation, budget);
			}

			// Observation is non-null EXACTLY when the bytes were obtained, so a locked
			// or vanished candidate attaches null and the planner reads "nothing to
			// restore from" — correct, and it keeps a candidate that could not be READ
			// from being described as one that was rejected.
			ConfigFileObservation withCandidate = observation.WithBackup(candidate.Observation);
			ConfigLoadDecision trial = ConfigLoadPlanner.Plan(withCandidate);

			// A hard stop is TERMINAL and must not be walked past. A newer-version or
			// illegal-envelope backup is not "try the next one": D-066 forbids writing
			// next to data a newer version owns, so defaults are not written either.
			if (trial.Action == ConfigLoadAction.HardStopReadOnly)
			{
				return (trial, withCandidate, budget);
			}

			if (trial.BackupToPromote is not null)
			{
				return (trial, withCandidate, budget);
			}

			Record(fileName, ConfigRepairStep.BackupCandidateRefused, fallback.Action, fallback.Reason, failure: null);
		}
	}

	private ConfigRepairOutcome Perform<TDocument>(
		ConfigLoadDecision decision,
		ConfigFileObservation observation,
		string destinationPath,
		string backupsDirectory,
		string fileName,
		TDocument defaults,
		RepairAttemptBudget budget)
		where TDocument : ConfigDocument
	{
		// THE NON-MUTATING OUTCOMES WRITE NO FILE BYTES, and the gate rather than the
		// exit is what enforces it: MutatesDisk is asked once, here, so a future arm
		// cannot quietly acquire a write by being added below.
		//
		// TWO EXITS, NOT ONE — say it, because the shorter claim is false. A failed
		// quarantine returns QuarantineFailedReadOnly from the SECOND exit inside the
		// quarantine branch, not from this one, and it gets there only after Quarantine
		// has already called _provisioning.CreateDirectory, which can succeed while the
		// Move that follows fails. So an outcome reporting MutatesDisk == false can leave
		// an empty backups\corrupted directory behind. "No file bytes" is the honest
		// scope: nothing of the user's is written, moved or replaced, which is what owner
		// decision 1 constrains — a created directory holds no bytes of theirs.
		if (!decision.MutatesDisk)
		{
			Record(fileName, ConfigRepairStep.CompletedWithoutWriting, decision.Action, decision.Reason, failure: null);

			return new ConfigRepairOutcome(
				decision.Action,
				decision.Reason,
				OriginalPreserved: false,
				BackupPromoted: false,
				DefaultsPersisted: false,
				DestinationUsable: decision.Action == ConfigLoadAction.UseLoadedDocument,
				ReadOnlyForSession: decision.Action
					is ConfigLoadAction.HardStopReadOnly
					or ConfigLoadAction.QuarantineFailedReadOnly);
		}

		bool preserved = false;

		if (decision.QuarantineFirst)
		{
			QuarantineAttempt attempt = Quarantine(destinationPath, backupsDirectory, fileName, decision);

			// RE-PLANNED, NOT BRANCHED ON. Handing the advanced observation back to the
			// planner is what makes the ordering invariant structural: on a Failed
			// quarantine PersistClearance refuses to mint, so the planner returns
			// QuarantineFailedReadOnly and there is no code path from here to a write.
			// A local `if (failed) return` would enforce the same thing by convention,
			// which is exactly what this slice is built not to rely on.
			ConfigFileObservation advanced = observation.WithQuarantine(attempt);
			ConfigLoadDecision second = ConfigLoadPlanner.Plan(advanced);

			if (!second.MutatesDisk)
			{
				// Owner decision 2: fails closed to read-only and NEVER retries. The
				// original is still in place and unpreserved, so nothing may be written
				// over it — not even defaults.
				return new ConfigRepairOutcome(
					second.Action,
					second.Reason,
					OriginalPreserved: false,
					BackupPromoted: false,
					DefaultsPersisted: false,
					DestinationUsable: false,
					ReadOnlyForSession: true);
			}

			preserved = true;
			decision = second;
		}

		// Reachable only with a decision the planner licensed: either the original is
		// preserved (above) or the file was absent, which is the only other route
		// PersistClearance mints for.
		return decision.Action == ConfigLoadAction.RestoreFromBackup
			? Promote<TDocument>(decision, destinationPath, backupsDirectory, fileName, preserved)
			: PersistDefaults(decision, destinationPath, fileName, defaults, preserved, budget);
	}

	// Owner decision 1 route (b): preserve the original BEFORE anything is written.
	//
	// A MOVE, NOT A COPY, AND NOT A DELETE. Moving takes the corrupt original out of
	// the way while destroying nothing, which is what makes the quarantine safe even
	// for content that later turns out to have been newer than this build.
	private QuarantineAttempt Quarantine(
		string destinationPath,
		string backupsDirectory,
		string fileName,
		ConfigLoadDecision decision)
	{
		try
		{
			string quarantineDirectory = Path.Combine(backupsDirectory, QuarantineDirectoryName);

			// Not pre-provisioned by StorageRootResolver (it creates only Root, backups,
			// logs and temp), so it is created here — and creating it is one of the ways
			// a quarantine can fail, which is why owner decision 2 exists.
			_provisioning.CreateDirectory(quarantineDirectory);

			string quarantinePath = Path.Combine(
				quarantineDirectory,
				string.Concat(
					fileName,
					".",
					_timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture),
					".",
					Guid.NewGuid().ToString("N"),
					QuarantineExtension));

			_repairFileSystem.Move(destinationPath, quarantinePath);

			// The "and that is logged" half of owner decision 1's route (b), recorded
			// BEFORE any write is attempted so the log cannot show a write preceding
			// the preservation that licensed it.
			Record(fileName, ConfigRepairStep.OriginalPreserved, decision.Action, decision.Reason, failure: null);
			return QuarantineAttempt.Succeeded;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// The target directory is not pre-provisioned and the disk can be full, so
			// this is an ordinary state rather than a defect. Reported, never rethrown:
			// the caller re-plans and fails closed.
			Record(fileName, ConfigRepairStep.QuarantineFailed, decision.Action, decision.Reason, failure);
			return QuarantineAttempt.Failed;
		}
	}

	// D-085 "restore corrupted JSON from backup", by a BYTE-LEVEL COPY.
	//
	// THE BYTE COPY IS MANDATORY, INCLUDING FOR CompletelyCompatible, and it is a
	// requirement rather than an implementation preference. Core's
	// BackupPromotionClearance cannot enforce HOW an executor copies, so it refuses to
	// mint for anything but CompletelyCompatible and records the byte-copy obligation
	// as an executor precondition. A deserialize-then-reserialize promotion would run
	// with UnmappedMemberHandling.Skip and permanently DROP any member this build does
	// not map — a hand edit, a sibling tool's addition — which is a silent overwrite of
	// the user's bytes even where the envelope is exactly compatible.
	//
	// The name comes from the token, so the file promoted is the file that was
	// validated. It is a Core BackupName, which structurally cannot hold a directory,
	// a drive or a traversal segment, so combining it with the backups directory
	// cannot escape that directory.
	private ConfigRepairOutcome Promote<TDocument>(
		ConfigLoadDecision decision,
		string destinationPath,
		string backupsDirectory,
		string fileName,
		bool preserved)
		where TDocument : ConfigDocument
	{
		string sourcePath = Path.Combine(backupsDirectory, decision.BackupToPromote!.Value);

		try
		{
			// The observer refused to read anything past its ceiling when it SELECTED this
			// backup; one that grew past it since must not be read whole and promoted either.
			if (_observer.ExceedsReadCeiling(sourcePath))
			{
				Record(fileName, ConfigRepairStep.WriteFailed, decision.Action, decision.Reason, failure: null);
				return Degraded(decision, preserved);
			}

			byte[] captured = _repairFileSystem.ReadAllBytes(sourcePath);

			if (!TryValidateCapturedBackup<TDocument>(captured, decision.BackupToPromote!))
			{
				Record(fileName, ConfigRepairStep.WriteFailed, decision.Action, decision.Reason, failure: null);
				return Degraded(decision, preserved);
			}

			ExpectedDocumentGeneration expectedGeneration = ExpectedDocumentGeneration.Absent;
			_repairFileSystem.WriteBytesAtomically(destinationPath, captured, expectedGeneration, PromotionWriteLockTimeout);

			Record(fileName, ConfigRepairStep.BackupPromoted, decision.Action, decision.Reason, failure: null);

			return new ConfigRepairOutcome(
				decision.Action,
				decision.Reason,
				OriginalPreserved: preserved,
				BackupPromoted: true,
				DefaultsPersisted: false,
				DestinationUsable: true,
				ReadOnlyForSession: false);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// Fail open. The original is already preserved, so nothing of the user's is
			// lost; the session runs on in-memory defaults instead.
			Record(fileName, ConfigRepairStep.WriteFailed, decision.Action, decision.Reason, failure);

			return Degraded(decision, preserved);
		}
	}

	// Validates the CAPTURED bytes - the exact snapshot that is then staged and published - with
	// the observer's own classification (BOM handling, counting envelope scan, strict body
	// bind), so promotion cannot accept bytes the observer would reject.
	private static bool TryValidateCapturedBackup<TDocument>(byte[] capturedBytes, BackupName backupName)
		where TDocument : ConfigDocument
	{
		BackupObservation observation = ConfigFileObserver.ObserveCapturedBackup<TDocument>(backupName, capturedBytes);
		return observation.Envelope == EnvelopeParse.Parsed
			&& observation.Body == DocumentBodyParse.Parsed
			&& observation.Schema == SchemaCompatibility.CompletelyCompatible;
	}

	// D-085 "create missing default config on first run", and the post-quarantine
	// defaults write.
	private ConfigRepairOutcome PersistDefaults<TDocument>(
		ConfigLoadDecision decision,
		string destinationPath,
		string fileName,
		TDocument defaults,
		bool preserved,
		RepairAttemptBudget budget)
		where TDocument : ConfigDocument
	{
		try
		{
			// THE DEADLINE REACHES THE WRITE SIDE HERE, and this is the only place it
			// can: the write-lock wait is the one unbounded wait on the path and it is a
			// constructor argument on the store. See the type header for the measurement
			// and for what this does and does not bound.
			_writer.Write(destinationPath, defaults, RemainingWriteLockTimeout(budget));

			Record(fileName, ConfigRepairStep.DefaultsPersisted, decision.Action, decision.Reason, failure: null);

			return new ConfigRepairOutcome(
				decision.Action,
				decision.Reason,
				OriginalPreserved: preserved,
				BackupPromoted: false,
				DefaultsPersisted: true,
				DestinationUsable: true,
				ReadOnlyForSession: false);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// Fail open, and note what is NOT done: no retry. The store's own failure
			// classification already distinguishes a touched destination from an
			// untouched one, and retrying a write whose lock wait just expired would
			// spend the remaining budget on the same contention.
			Record(fileName, ConfigRepairStep.WriteFailed, decision.Action, decision.Reason, failure);

			return Degraded(decision, preserved);
		}
	}

	// The remaining budget, floored. Derived per attempt rather than fixed at
	// construction, which is the whole reason IConfigDocumentWriter takes it as a
	// parameter.
	private static TimeSpan RemainingWriteLockTimeout(RepairAttemptBudget budget)
	{
		TimeSpan remaining = budget.TimeBudget - budget.Elapsed;
		return remaining < MinimumWriteLockTimeout ? MinimumWriteLockTimeout : remaining;
	}

	// A licensed write that failed. The destination is not usable, but the file is NOT
	// marked read-only for the session: the failure was transient contention, not a
	// schema verdict, and locking the file out would outlive the condition.
	private static ConfigRepairOutcome Degraded(ConfigLoadDecision decision, bool preserved)
	{
		return new(
			decision.Action,
			decision.Reason,
			OriginalPreserved: preserved,
			BackupPromoted: false,
			DefaultsPersisted: false,
			DestinationUsable: false,
			ReadOnlyForSession: false);
	}

	// The single log call site shape, so no caller can accidentally pass a path or a
	// rendered exception. GetType().Name only — never ToString(), never
	// InnerException.Message (D-075 / D-097).
	private void Record(
		string fileName,
		ConfigRepairStep step,
		ConfigLoadAction action,
		ConfigLoadReason reason,
		Exception? failure)
	{
		_log.Record(new ConfigRepairEvent(fileName, step, action, reason, failure?.GetType().Name));
	}
}
