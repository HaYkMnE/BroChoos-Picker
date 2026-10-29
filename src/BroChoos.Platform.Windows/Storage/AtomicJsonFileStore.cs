using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BroChoos.Core.Storage;

namespace BroChoos.Platform.Windows.Storage;

// D-063 atomic write. A config document is serialized to memory, re-parsed to
// prove the string is well-formed, then staged to a unique same-directory temp
// file and swapped into place (File.Replace / File.Move) only AFTER the bytes are
// flushed to disk. Readers therefore never observe a half-written destination.
//
// D-063 step 9 write-failure recovery ("on failure: preserve old file, delete
// temp only if safe, report recoverable error") is implemented here, in three
// parts:
//
// (A) CLASSIFICATION. Every WRITE failure that escapes Write carries
//     Data["destinationModified"] — false when the destination is provably
//     untouched (serialize, validate, temp write, lock acquisition, or the
//     Replace/Move call itself throwing), true when the swap already committed
//     and only the verification failed. The two states demand different caller
//     behavior, so the caller must be able to tell them apart without parsing
//     prose. Data["path"] carries the full path for a redaction-aware sink;
//     messages the store composes itself name the FILE ONLY (D-075 / D-097).
//
//     ARGUMENT VALIDATION IS DELIBERATELY NOT CLASSIFIED, and the guarantee
//     above must not be read as covering it. The ArgumentNullException.ThrowIfNull
//     guards sit OUTSIDE the classified region, so a null destinationPath or a
//     null document escapes with an EMPTY Data collection. That is intentional: a
//     null argument is a CALLER BUG caught before any work is attempted, not a
//     write outcome. There is no destination state to report and nothing for a
//     caller to recover from — it has to fix its own call site.
//     Framework exceptions rethrown as-is keep their own message, which for
//     DirectoryNotFoundException does embed the full path — that is pre-existing
//     framework behavior, and rewriting it would destroy the documented
//     DirectoryNotFoundException contract for a missing destination directory.
//
// (B) POST-PUBLISH VERIFICATION FAILURE. The swap has committed, so "preserve the
//     old file" no longer means "do nothing". A destination that cannot be OPENED
//     is treated as transient (antivirus, a foreign handle): retried, then
//     reported UNVERIFIED without reverting, because reverting a write that is
//     almost certainly good would itself be data loss. A destination that cannot
//     be PARSED is genuinely bad content, which is where D-085's "recover
//     interrupted atomic write" applies: the immediate backup minted by this very
//     write is copied back, and the error reports whether that restore happened.
//
// (C) STALE ORPHAN TEMP SWEEP (D-085 "delete stale .tmp"). A process killed
//     between the temp write and the swap leaves a temp that its own finally can
//     never reach, because the process is gone. Every write opportunistically
//     removes this destination's own long-stale orphans.
//
// KNOWN PHASE-0 RESIDUAL LIMITS of the D-076 write lock. Both are accepted, not
// bugs to be fixed here; recorded so nobody mistakes the lock for more than it is.
//
// 1. The lock is COOPERATIVE. It binds only writers that go through this class.
//    No exclusive FileShare lock is ever taken on the DESTINATION file, so an
//    external editor, an installer, a repair tool or any foreign process can
//    write the destination completely unserialized against us. This is compliant
//    with D-076 — its allowed-approaches list puts "named mutex per file" first
//    and does not require a FileStream lock — so the implementation deliberately
//    stays as is. A destination lock would also fight File.Replace, which needs
//    to swap the very file the lock would be held on.
//
// 2. "Local\" is PER-SESSION (see BuildMutexName for why Local wins over Global).
//    Two sessions of the SAME account therefore do not serialize against each
//    other — the realistic trigger is an RDP connect that disconnects the console
//    session without killing its processes, leaving two live sessions owning one
//    %LocalAppData%. The worst case is the pre-existing torn-publish window that
//    D-063 step 9 recovery is slated to cover; it is not made worse by this lock.
/// <summary>The backups directory must be on the SAME VOLUME as the
/// destination files: <see cref="File.Replace(string, string, string?)"/>
/// requires source, destination and backup to share a filesystem volume. This
/// is always true in the StorageRootResolver layout (Backups sits beside the
/// data files under the same storage root), but a caller passing a
/// cross-volume backup path would get a failure at replace time.
/// <para>A null writeLockTimeout takes <c>DefaultWriteLockTimeout</c>; callers
/// (and tests) may shorten it, but it is always bounded — see D-076
/// below.</para>
/// <para>A null observer takes <see cref="NullAtomicWriteObserver"/>, so production
/// callers pay nothing and behave exactly as they did before the seam existed. The
/// parameter is OPTIONAL and LAST so every existing call site compiles
/// unchanged.</para></summary>
public sealed class AtomicJsonFileStore(
	string? backupsDirectory = null,
	TimeSpan? writeLockTimeout = null,
	IAtomicWriteObserver? observer = null)
{
	internal static readonly AtomicWriteStage[] ProtectedCommitStages =
	[
		AtomicWriteStage.LockAcquired,
		AtomicWriteStage.BeforePublish,
		AtomicWriteStage.AfterPublish,
		AtomicWriteStage.AfterPrune,
		AtomicWriteStage.AfterVerify,
	];

	internal static bool IsProtectedCommitStage(AtomicWriteStage stage)
	{
		return stage is AtomicWriteStage.LockAcquired
			or AtomicWriteStage.BeforePublish
			or AtomicWriteStage.AfterPublish
			or AtomicWriteStage.AfterPrune
			or AtomicWriteStage.AfterVerify;
	}

	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		Converters = { new JsonStringEnumConverter() },
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
	};

	// D-063 step 8 retention: keep at most this many timestamped backups per
	// document, plus the age cap below. Prune deletes are best-effort — a locked
	// or undeletable backup must never turn an already-successful replace into a
	// reported failure.
	//
	// 10 / 30 days chosen by owner decision — D-063 step 8 fixes no numbers
	// (nearest analog D-074 "last 10").
	private const int BackupRetentionCount = 10;

	private static readonly TimeSpan BackupMaxAge = TimeSpan.FromDays(30);

	// D-063 step 9 / D-085 "delete stale .tmp": how old an orphan temp must be
	// before a later write is allowed to remove it.
	//
	// 24 hours is an OWNER DECISION — D-063 step 9 fixes no number. It mirrors
	// locked D-073's temp policy ("delete files older than 24 hours"), the nearest
	// locked analog. An analog rather than a direct rule: D-073 governs the temp\
	// directory, while these temps live BESIDE the destination because File.Replace
	// requires source, destination and backup to share one volume.
	//
	// The threshold must stay far above any real publish, and it is precisely the
	// thing being LEANED ON to keep the sweep off a live peer writer's in-flight
	// temp. It cannot lean on share protection instead: the staging stream is opened
	// FileShare.None, but that protection LAPSES THE MOMENT the stream is closed,
	// and Write closes it BEFORE acquiring the write lock (probe-verified). A peer
	// parked on the mutex therefore leaves a fully staged, freely deletable temp on
	// disk for the whole of its wait. What actually protects it is the ratio: a temp
	// must be 24 HOURS old to be eligible for sweeping, while a mutex wait is
	// bounded at DefaultWriteLockTimeout — 10 seconds by default.
	private static readonly TimeSpan StaleTempMaxAge = TimeSpan.FromHours(24);

	// D-063 step 9 (B): post-publish read-back retry budget. A destination that
	// cannot be OPENED immediately after the swap is usually a transient sharing
	// violation (antivirus or an indexer touching the file we just published, a
	// foreign handle), not corruption. Small and bounded because these attempts run
	// inside the write lock.
	private const int PostPublishReadAttempts = 3;

	private static readonly TimeSpan PostPublishReadRetryDelay = TimeSpan.FromMilliseconds(50);

	// D-063 step 9 (A): the machine-checkable classification keys. Prose in a
	// message cannot be branched on; these can.
	private const string DestinationModifiedKey = "destinationModified";

	private const string RestoredKey = "restored";

	private const string PathKey = "path";

	// Owner decision: D-076 mandates the per-file write mutex but fixes NO
	// acquisition timeout. An unbounded wait is not acceptable here — a config
	// write sits on the picker's interactive path, so a stuck peer holding the
	// lock must degrade to a reported failure, never to a hang. 10 seconds is far
	// longer than a real publish (a few filesystem calls on a small file) yet
	// short enough to stay well inside a user's patience.
	private static readonly TimeSpan DefaultWriteLockTimeout = TimeSpan.FromSeconds(10);

	private readonly string? _backupsDirectory = string.IsNullOrWhiteSpace(backupsDirectory) ? null : backupsDirectory;

	private readonly TimeSpan _writeLockTimeout = writeLockTimeout ?? DefaultWriteLockTimeout;

	// D-v3.5-493 kill-test seam. NEVER null: a missing observer collapses to the no-op
	// default in the constructor, so every call site on the write path can call it
	// unconditionally with no null check and no try/catch.
	private readonly IAtomicWriteObserver _observer = observer ?? NullAtomicWriteObserver.Instance;

	// D-076 Phase 0: the name of the per-file config-write mutex.
	//
	// D-076 spells this "BroChoos_ConfigWrite_<fileNameHash>", but hashing the BARE
	// file name would be wrong. Per D-072 an installed root and a portable root can
	// each hold their own app.json, and those are two independent files — a write to
	// one must not block a write to the other. Hashing the FULL normalized path is
	// the faithful per-file reading of the decision.
	//
	// The path is upper-cased before hashing because NTFS is case-insensitive:
	// "app.json" and "App.json" name ONE physical file, so they must map to ONE
	// mutex. Without the fold, two writers spelling the path differently would take
	// two different mutexes and the TOCTOU window this lock exists to close would
	// stay open. The fold is best-effort normalization, not a proof of identity:
	// GetFullPath resolves relative segments AND 8.3 short names (probe-verified:
	// "C:\PROGRA~3\BroChoos\data\app.json" normalizes to
	// "C:\PROGRAMDATA\BROCHOOS\DATA\APP.JSON" after the fold), but it does NOT
	// resolve junctions or symlinks, so two exotic spellings of one file could
	// still miss each other. Callers pass resolver-produced paths, which are
	// spelled consistently.
	//
	// ToUpperInvariant is also not NTFS's own uppercase table; the two diverge only
	// for exotic Unicode, and every caller hands us one resolver-produced spelling,
	// so the gap is not reachable today.
	//
	// "Local\" scopes the mutex to the current session. This is a deliberate choice
	// over "Global\", and NOT because Global needs a privilege we lack — creating a
	// "Global\" mutex succeeds on a standard, non-elevated token (probe-verified).
	// The real reasons to stay Local:
	//
	// (i) Under "Global\" a process running as a DIFFERENT account could squat the
	//     name and make every config write fail after the timeout — a cross-account
	//     denial of service on a name that is trivially derivable from a path.
	//     Under "Local\" a squatter must already be inside this user's own session,
	//     where it gains nothing it could not do directly.
	//
	// (ii) A path-derived name already avoids cross-user collisions for per-user
	//      roots — each %LocalAppData% differs, so "Global\" buys nothing there.
	//      In the one case where it WOULD help (a shared installed root written by
	//      two accounts), the kernel object created by user A carries A's default
	//      DACL, so user B's `new Mutex(...)` can throw UnauthorizedAccessException
	//      — turning a coordination gap into a hard write failure.
	//
	// Conclusion: keep "Local\". If cross-session coordination is ever genuinely
	// required, it needs "Global\" PLUS an explicit DACL on the mutex, which is
	// beyond Phase 0. The residual per-session gap is recorded on the class.
	internal static string BuildMutexName(string destinationPath)
	{
		ArgumentNullException.ThrowIfNull(destinationPath);

		string normalized = Path.GetFullPath(destinationPath).ToUpperInvariant();
		byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

		// 16 of the 32 digest bytes => 32 hex chars, keeping the whole name far
		// inside the kernel object name limit. Truncation is not a security
		// boundary here: a collision would only over-serialize two unrelated
		// files, never corrupt one — and at 128 bits it is moot anyway.
		return $@"Local\BroChoos_ConfigWrite_{Convert.ToHexString(digest.AsSpan(0, 16))}";
	}

	/// <summary>The destination directory must already exist (the storage root
	/// resolver pre-provisions it); a missing directory surfaces as a clean
	/// <see cref="DirectoryNotFoundException"/> before any bytes are staged.</summary>
	public void Write(string destinationPath, ConfigDocument document)
	{
		ArgumentNullException.ThrowIfNull(destinationPath);
		ArgumentNullException.ThrowIfNull(document);

		// D-063 step 9 (A): the single fact that classifies every failure below.
		// Flipped ONLY once File.Replace / File.Move has returned, so it is false for
		// every failure that leaves the destination untouched — including
		// Replace/Move itself throwing, which publishes nothing.
		bool published = false;

		try
		{
			// Serialize with the RUNTIME type. Serialize<ConfigDocument> would bind to
			// the base record's surface and silently drop BrowsersDocument.Browsers.
			string json = JsonSerializer.Serialize(document, document.GetType(), Options);

			// 2. The serialized string must deserialize back without throwing.
			_ = JsonSerializer.Deserialize(json, document.GetType(), Options);

			// D-v3.5-493 kill-test seam. NOT wrapped in try/catch, here or at any of the
			// stages below, and that is DELIBERATE — do not "harden" it:
			//  - IAtomicWriteObserver's contract forbids throwing, so a throwing observer
			//    is a broken implementation, not a condition the write path must survive;
			//  - the production default (NullAtomicWriteObserver) has an empty body and
			//    cannot throw, so the swallow would protect nothing in production;
			//  - a throwing TEST observer must be VISIBLE. Swallowed, it would silently
			//    disarm the kill-test harness this seam exists for, and the harness would
			//    report success while never actually reaching its checkpoints.
			// An observer that throws therefore escapes Write like any other failure, and
			// picks up the normal D-063 step 9 (A) classification on the way out.
			_observer.OnStage(AtomicWriteStage.Validated);

			byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);

			// D-063 step 9 (C) / D-085 "delete stale .tmp". Runs here, BEFORE this
			// attempt's temp exists and OUTSIDE the write lock — see SweepStaleTemps
			// for why both matter.
			SweepStaleTemps(destinationPath);

			// 3. Stage to a unique temp file in the SAME directory as the destination,
			// so the final swap never crosses filesystem boundaries.
			string tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

			try
			{
				// 4. Write-through + flush-to-disk before the swap.
				using (FileStream stream = new(
					tempPath,
					new FileStreamOptions
					{
						Mode = FileMode.CreateNew,
						Access = FileAccess.Write,
						Share = FileShare.None,
						Options = FileOptions.WriteThrough,
					}))
				{
					stream.Write(bytes, 0, bytes.Length);
					stream.Flush(flushToDisk: true);
				}

				// The staged bytes are durable AND the stream is closed (the using block
				// above has disposed it). The destination is still untouched.
				_observer.OnStage(AtomicWriteStage.TempFlushed);

				// D-076: the publish is the critical section. Two writers racing here
				// is a real TOCTOU hole — File.Exists and the File.Replace/File.Move
				// that follows it are not one atomic step, so without the lock a peer
				// can create the destination in the gap and turn our File.Move into a
				// spurious failure, or the two publishes can cross and leave the
				// backups/retention state inconsistent. The lock spans the existence
				// check, the swap, the read-back verification and the prune.
				//
				// Serialization, validation and the temp write stay OUTSIDE the lock:
				// the temp name is unique per attempt, so there is nothing to contend
				// for, and keeping them out holds the lock for a handful of filesystem
				// calls instead of a serialize.
				using Mutex writeLock = new(initiallyOwned: false, BuildMutexName(destinationPath));

				bool acquired = false;
				try
				{
					try
					{
						acquired = writeLock.WaitOne(_writeLockTimeout);
					}
					catch (AbandonedMutexException)
					{
						// The previous holder died without releasing (crash, kill). The
						// wait SUCCEEDED and this thread now owns the mutex, so proceed —
						// and release it in the finally like any other acquisition.
						// BroChoos is a single-user desktop app with no elevation, so the
						// abandoned holder was another instance of this same code; its
						// temp file is uniquely named and is swept by SweepStaleTemps
						// once it goes stale, and any half-done publish it left behind is
						// what the recovery below covers. Treating this as a failure
						// instead would wedge every future write behind a dead process.
						acquired = true;
					}

					if (!acquired)
					{
						// Recoverable, and shaped like the store's other write failures
						// (IOException) so existing callers already handle it. Nothing
						// has been published at this point: the destination is untouched
						// and the outer finally removes the temp file. See
						// DefaultWriteLockTimeout for why a bounded wait is an owner
						// decision.
						//
						// The message carries only the FILE NAME, never the full path.
						// In installed mode the full path embeds the Windows account
						// name, and D-075 / D-097 forbid full user file paths in default
						// logs. An exception message is not itself a log, but any caller
						// doing logger.Error(ex.Message) would breach that rule silently.
						// The full path rides in Data["path"] instead, where a
						// redaction-aware diagnostic sink can still reach it.
						IOException timedOut = new(FormattableString.Invariant(
							$"Timed out after {_writeLockTimeout} waiting for the per-file config-write lock on '{Path.GetFileName(destinationPath)}'; the destination was not modified."));
						timedOut.Data[PathKey] = destinationPath;
						throw timedOut;
					}

					// AFTER the !acquired guard, never merely after WaitOne returns. A
					// timed-out wait acquired NOTHING, so announcing LockAcquired there
					// would report a lock this thread does not hold — and a checkpoint
					// harness would park on a stage that logically never happened while
					// the write was already failing. The AbandonedMutexException path
					// DOES reach here, correctly: that wait succeeded and this thread
					// owns the mutex.
					_observer.OnStage(AtomicWriteStage.LockAcquired);

					// 5/6. Swap into place. D-063 step 5: when the destination already
					// exists, hand File.Replace an immediate backup path so the previous
					// revision survives in the backups directory; step 8 then prunes that
					// directory while the new revision is already safely in place.
					//
					// The backup path is captured because D-063 step 9 recovery needs the
					// exact file THIS write minted. Picking "the newest file in the
					// backups directory" instead would be a race: a concurrent writer's
					// mint could win, and recovery would restore a revision that is not
					// the one this write displaced.
					string? backupPath = null;

					if (File.Exists(destinationPath))
					{
						backupPath = BuildBackupPath(destinationPath);

						// Last point before the destination changes, announced with the
						// backup path already computed. Hoisting it above the existence
						// check would work only if the backup path were computed
						// conditionally on the same predicate; the per-branch shape is
						// kept instead so each path's whole publish sequence reads
						// contiguously, and so no shared "replacing" flag has to be
						// carried across three blocks.
						_observer.OnStage(AtomicWriteStage.BeforePublish);

						File.Replace(tempPath, destinationPath, destinationBackupFileName: backupPath);

						// Past this line the destination HAS been replaced. Set before
						// anything that can throw, so classification can never claim an
						// untouched destination after a committed swap.
						published = true;

						// AFTER `published = true`, deliberately. An observer that throws
						// here has to be classified as destinationModified: true, because
						// the swap HAS committed — that is the whole point of a kill
						// checkpoint at this position.
						_observer.OnStage(AtomicWriteStage.AfterPublish);

						PruneBackups(destinationPath);

						// Retention ran. Reached ONLY on this branch, because PruneBackups
						// is only called here — see the enum member's docs.
						_observer.OnStage(AtomicWriteStage.AfterPrune);
					}
					else
					{
						_observer.OnStage(AtomicWriteStage.BeforePublish);

						File.Move(tempPath, destinationPath);
						published = true;

						_observer.OnStage(AtomicWriteStage.AfterPublish);
					}

					// 7 + D-063 step 9 (B). Verification is where a committed publish can
					// still turn out bad, so recovery lives with it.
					VerifyPublished(destinationPath, document.GetType(), backupPath);

					// Still inside the write lock, by design: the harness needs a
					// checkpoint where the publish is complete AND verified but the lock
					// has not yet been handed to the next writer.
					_observer.OnStage(AtomicWriteStage.AfterVerify);
				}
				finally
				{
					// Guarded by `acquired` so a timed-out wait never releases a mutex
					// this thread does not own (that would throw ApplicationException and
					// mask the real failure).
					if (acquired)
					{
						writeLock.ReleaseMutex();
					}
				}
			}
			finally
			{
				// D-063 step 9 "delete temp only if safe". Safe is exactly this: the
				// temp is THIS attempt's uniquely named file, and after a successful
				// swap it no longer exists (Replace/Move consumed it), so a surviving
				// temp here can only be an attempt that never published. A failed
				// delete must never mask the original error.
				if (File.Exists(tempPath))
				{
					try
					{
						File.Delete(tempPath);
					}
					catch (IOException)
					{
					}
					catch (UnauthorizedAccessException)
					{
					}
				}
			}
		}
		catch (Exception failure)
		{
			// D-063 step 9 (A). Every failure leaving Write carries the one fact a
			// caller cannot otherwise recover: whether the destination was touched.
			// `throw;` (not `throw failure;`) preserves the original stack trace, and
			// the exception TYPE is left alone so the DirectoryNotFoundException and
			// lock-timeout IOException contracts other tests pin still hold.
			//
			// The stack-trace half of that claim is PINNED, not merely asserted in
			// prose: Write_WhenAFailureEscapes_RethrowKeepsTheFrameThatActuallyFailed
			// fails if this becomes `throw failure;`, in Debug and in Release alike
			// (verified 3 runs each — the VerifyPublished frame survives optimization).
			Classify(failure, destinationPath, published);
			throw;
		}
	}

	internal static void WriteRawUtf8(
		string destinationPath,
		byte[] bytes,
		ExpectedDocumentGeneration expectedGeneration,
		TimeSpan writeLockTimeout)
	{
		ArgumentNullException.ThrowIfNull(destinationPath);
		ArgumentNullException.ThrowIfNull(bytes);

		bool published = false;
		try
		{
			string tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

			try
			{
				using (FileStream stream = new(
					tempPath,
					new FileStreamOptions
					{
						Mode = FileMode.CreateNew,
						Access = FileAccess.Write,
						Share = FileShare.None,
						Options = FileOptions.WriteThrough,
					}))
				{
					stream.Write(bytes, 0, bytes.Length);
					stream.Flush(flushToDisk: true);
				}

				using Mutex writeLock = new(initiallyOwned: false, BuildMutexName(destinationPath));
				bool acquired = false;
				try
				{
					try
					{
						acquired = writeLock.WaitOne(writeLockTimeout);
					}
					catch (AbandonedMutexException)
					{
						acquired = true;
					}

					if (!acquired)
					{
						IOException timedOut = new(FormattableString.Invariant(
							$"Timed out after {writeLockTimeout} waiting for the per-file config-write lock on '{Path.GetFileName(destinationPath)}'; the destination was not modified."));
						timedOut.Data[PathKey] = destinationPath;
						throw timedOut;
					}

					long? observedGeneration = TryReadGeneration(destinationPath);
					if (!GenerationMatchesExpected(observedGeneration, expectedGeneration))
					{
						throw new IOException("Write conflict: expected generation no longer matches the current destination generation.");
					}

					if (File.Exists(destinationPath))
					{
						File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
					}
					else
					{
						File.Move(tempPath, destinationPath);
					}

					published = true;
				}
				finally
				{
					if (acquired)
					{
						writeLock.ReleaseMutex();
					}
				}
			}
			finally
			{
				if (File.Exists(tempPath))
				{
					try
					{
						File.Delete(tempPath);
					}
					catch (IOException)
					{
					}
					catch (UnauthorizedAccessException)
					{
					}
				}
			}
		}
		catch (Exception failure)
		{
			Classify(failure, destinationPath, published);
			throw;
		}
	}

	private static long? TryReadGeneration(string destinationPath)
	{
		if (!File.Exists(destinationPath))
		{
			return null;
		}

		try
		{
			using JsonDocument json = JsonDocument.Parse(File.ReadAllText(destinationPath));
			if (json.RootElement.TryGetProperty("generation", out JsonElement generation)
				&& generation.ValueKind == JsonValueKind.Number
				&& generation.TryGetInt64(out long value))
			{
				return value;
			}
		}
		catch (JsonException)
		{
		}
		catch (IOException)
		{
		}

		return 0;
	}

	private static bool GenerationMatchesExpected(long? observedGeneration, ExpectedDocumentGeneration expectedGeneration)
	{
		return !expectedGeneration.ExpectsDocument
			? observedGeneration is null
			: observedGeneration is not null && observedGeneration.Value == expectedGeneration.Value;
	}

	// Stamps the machine-checkable classification onto an escaping failure.
	//
	// Wrapped, because this runs while an exception is already in flight and its ONLY
	// job is to add diagnostics: an IDictionary that refuses the write (a read-only or
	// custom Data implementation) must not replace a real write failure with a
	// bookkeeping one. Losing a key is a diagnostic gap; losing the original exception
	// is a bug.
	private static void Classify(Exception failure, string destinationPath, bool published)
	{
		try
		{
			// VerifyPublished already stamped its own richer classification. Data
			// assignment is idempotent and `published` agrees with what it set, so the
			// two can never disagree.
			failure.Data[DestinationModifiedKey] = published;
			failure.Data[PathKey] = destinationPath;

			if (!failure.Data.Contains(RestoredKey))
			{
				// A pre-publish failure restores nothing, because nothing was lost.
				failure.Data[RestoredKey] = false;
			}
		}
		catch (NotSupportedException)
		{
		}
		catch (ArgumentException)
		{
		}
	}

	// D-063 step 9 (B): step 7 verification, plus what to do when it fails AFTER the
	// swap has already committed. The two failure causes are not interchangeable.
	//
	// CANNOT OPEN (IOException / UnauthorizedAccessException) is usually transient —
	// antivirus, a search indexer, or another handle briefly on the file we just
	// published. Retried a few times, then reported as UNVERIFIED. Deliberately NOT
	// reverted: the bytes were flushed to disk before the swap, so the published
	// content is almost certainly correct, and reverting on nothing more than "could
	// not open it just now" would silently discard a good write — a data-loss bug
	// dressed up as recovery.
	//
	// CANNOT PARSE (JsonException) is different in kind: the bytes on disk are not
	// the document we wrote. Retrying cannot change that, so there is one attempt and
	// this is where D-085's "recover interrupted atomic write" applies — the backup
	// this very write minted holds the previous revision, so restoring it returns the
	// destination to a known-good state.
	//
	// INTERNAL for direct testing, like BuildMutexName above, and for the same kind of
	// reason: this branch is not reachable through Write from outside. The bytes on
	// disk are the very string step 2 already validated, so a destination that failed
	// to parse can only come from a foreign process mutating it inside the critical
	// section — which a test could only reproduce as a race. Driving the recovery
	// directly pins its behavior deterministically without adding an observer or
	// callback seam to the write path (that seam is a later slice, deliberately not
	// introduced here).
	internal static void VerifyPublished(string destinationPath, Type documentType, string? backupPath)
	{
		Exception? unopenable = null;

		for (int attempt = 1; attempt <= PostPublishReadAttempts; attempt++)
		{
			string content;
			try
			{
				content = File.ReadAllText(destinationPath);
			}
			catch (Exception cannotOpen) when (cannotOpen is IOException or UnauthorizedAccessException)
			{
				unopenable = cannotOpen;

				// Suppresses the sleep after the final attempt. Deliberately NOT pinned: the
				// retry-budget test asserts a LOWER time bound, so it cannot catch a mutation
				// that ADDS waiting, and pinning it needs a load-sensitive UPPER bound that
				// would make the suite flaky. Owner decision: accept the gap — a regression
				// costs one wasted 50ms sleep in the write lock on an already-failing path,
				// latency only, no correctness impact.
				if (attempt < PostPublishReadAttempts)
				{
					Thread.Sleep(PostPublishReadRetryDelay);
				}

				continue;
			}

			try
			{
				_ = JsonSerializer.Deserialize(content, documentType, Options);

				// Verified: the destination holds a parseable document. Normal return.
				return;
			}
			catch (JsonException unparseable)
			{
				bool restored = TryRestore(destinationPath, backupPath, documentType);

				throw Unverified(
					destinationPath,
					restored
						? "the destination did not parse back after the swap and was restored from the backup this write minted"
						: "the destination did not parse back after the swap and no backup from this write was available to restore it",
					unparseable,
					restored);
			}
		}

		throw Unverified(
			destinationPath,
			FormattableString.Invariant(
				$"the destination could not be opened for read-back verification in {PostPublishReadAttempts} attempts, so its content is unverified; it was deliberately NOT reverted, because a transient sharing violation is the likely cause and reverting would discard a good write"),
			unopenable!,
			restored: false);
	}

	// Recoverable and shaped like the store's other write failures (IOException) so
	// existing callers already handle it, with the original cause kept as the inner
	// exception. The message names the FILE ONLY — never the directory — because a
	// caller doing logger.Error(ex.Message) must not leak the Windows account name
	// embedded in an installed-mode path (D-075 / D-097). The full path rides in
	// Data["path"] for a redaction-aware sink.
	private static IOException Unverified(string destinationPath, string reason, Exception cause, bool restored)
	{
		IOException failure = new(
			FormattableString.Invariant(
				$"The config write to '{Path.GetFileName(destinationPath)}' published, but {reason}. The destination WAS modified."),
			cause);

		failure.Data[DestinationModifiedKey] = true;
		failure.Data[RestoredKey] = restored;
		failure.Data[PathKey] = destinationPath;

		return failure;
	}

	// Best-effort revert to the revision this write displaced. It must NEVER throw in
	// place of the verification error that triggered it — losing the real diagnosis to
	// a secondary failure would leave the caller with no idea what happened — so the
	// outcome is returned as a bool and reported on the error instead.
	//
	// Restore must use one immutable capture and the same stage->flush->CAS->replace
	// publish protocol; direct path-copy publication is forbidden.
	private static bool TryRestore(string destinationPath, string? backupPath, Type documentType)
	{
		// No backup exists on the first-create branch (there was no previous revision
		// to preserve) or when no backups directory is configured at all. Reporting
		// that honestly beats inventing a recovery.
		if (backupPath is null)
		{
			return false;
		}

		try
		{
			if (!File.Exists(backupPath) || !File.Exists(destinationPath))
			{
				return false;
			}

			byte[] captured = File.ReadAllBytes(backupPath);

			// Validate the immutable capture before any publish attempt.
			_ = JsonSerializer.Deserialize(captured, documentType, Options);

			WriteRawUtf8(
				destinationPath,
				captured,
				ExpectedDocumentGeneration.Match(0),
				DefaultWriteLockTimeout);

			return true;
		}
		catch (JsonException)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	// D-063 step 9 (C) / D-085 "delete stale .tmp". A process killed between the temp
	// write and the swap leaves an orphan temp that its own finally can never reach,
	// because the process is gone. Only a LATER write can clean it up.
	//
	// Runs OUTSIDE the write lock, before this attempt's temp is even named. Two
	// reasons: it touches nothing the critical section protects (only files at least
	// StaleTempMaxAge old, never the destination and never this attempt's temp), and
	// the D-076 lock is deliberately held for a handful of filesystem calls — adding a
	// directory enumeration plus a stat per candidate inside it would lengthen the
	// section every writer contends for, to no benefit.
	//
	// Two filters keep it safe, and only two:
	//  - the name filter admits ONLY this destination's own temp pattern
	//    "<destination file name>.<something>.tmp" in the destination's directory;
	//  - the age filter admits only files older than StaleTempMaxAge. This is the
	//    SOLE REAL PROTECTION for a temp that belongs to a LIVE peer writer.
	//
	// Do NOT credit FileShare.None here. The staging stream is opened with it, but
	// that protection LAPSES AT STREAM CLOSE, and Write closes the staged temp
	// BEFORE it acquires the write lock (probe-verified), so in the window that
	// actually matters — a peer parked on the mutex with its temp fully staged — the
	// temp is freely deletable and a delete would NOT fail. The behaviour is still
	// safe, for one reason only: eligibility needs 24 hours of age, while a mutex
	// wait is bounded at DefaultWriteLockTimeout (10 seconds by default). The two
	// differ by four orders of magnitude, which is what makes the sweep unable to
	// reach a temp any live writer could still be waiting to publish.
	//
	// Fully OPPORTUNISTIC, like the retention prune: the whole sweep is wrapped so a
	// vanished directory or an undeletable file can NEVER fail a config write.
	private static void SweepStaleTemps(string destinationPath)
	{
		try
		{
			string? directory = Path.GetDirectoryName(destinationPath);
			string fileName = Path.GetFileName(destinationPath);

			if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
			{
				return;
			}

			DateTime cutoff = DateTime.UtcNow - StaleTempMaxAge;
			string prefix = fileName + ".";

			// Enumerated unfiltered and matched ordinally in code, exactly like
			// PruneBackups. A Directory.EnumerateFiles search PATTERN would be the
			// obvious shortcut, but an explicit ordinal test states precisely what is
			// eligible, in terms this code fully controls and independent of the
			// volume's 8.3 short-name configuration — which is machine state no
			// caller here can see or rely on.
			foreach (string candidate in Directory.EnumerateFiles(directory))
			{
				string name = Path.GetFileName(candidate);

				// The length test demands a non-empty middle segment, so the guid-bearing
				// "<name>.<guid>.tmp" matches while a bare "<name>..tmp" does not.
				if (!name.StartsWith(prefix, StringComparison.Ordinal)
					|| !name.EndsWith(".tmp", StringComparison.Ordinal)
					|| name.Length <= prefix.Length + ".tmp".Length)
				{
					continue;
				}

				if (File.GetLastWriteTimeUtc(candidate) < cutoff)
				{
					DeleteQuietly(candidate);
				}
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	// DESIGN DECISION (recorded so the next reader knows this is planned-and-deferred,
	// not overlooked): the backup/retention region below — BuildBackupPath,
	// PruneBackups, BackupTimestamp and the BackupRetentionCount / BackupMaxAge
	// constants — is a coherent candidate for extraction into its own
	// backup/retention type. It is deliberately NOT extracted yet. This store stays
	// ONE unit for now, and the extraction happens WHEN the load-side
	// corruption-recovery slice arrives: that slice has to read and quarantine
	// backups, which gives the extracted type a SECOND real caller. Extracting today
	// would produce a type with exactly one caller and move code without reducing
	// coupling.
	//
	// When it is extracted, the CAPTURED-BACKUP-PATH CONTRACT must stay EXPLICIT in
	// the new type's surface: recovery has to restore the backup THIS write captured,
	// never "the newest file in the backups directory". A concurrent writer's mint can
	// be newer, and restoring it would put back a revision this write never displaced.
	// That race is already pinned by a test (the decoy in
	// PostPublishParseFailure_WithBackup_RestoresPreviousRevisionAndReportsIt), so an
	// extraction that quietly turns the captured path into a directory lookup fails
	// loudly — keep it that way.
	//
	// SweepStaleTemps is NOT part of this and is not to be extracted: ~45 lines, one
	// call site, no state shared with retention.
	private string? BuildBackupPath(string destinationPath)
	{
		if (_backupsDirectory is null)
		{
			return null;
		}

		string fileName = Path.GetFileName(destinationPath);
		return Path.Combine(
			_backupsDirectory,
			$"{fileName}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak");
	}

	private void PruneBackups(string destinationPath)
	{
		if (_backupsDirectory is null)
		{
			return;
		}

		string fileName = Path.GetFileName(destinationPath);
		if (string.IsNullOrEmpty(fileName))
		{
			return;
		}

		// Retention is OPPORTUNISTIC. It runs AFTER File.Replace has already
		// committed, so any failure here must never surface from Write and must
		// never skip the post-replace read-back verification. A concurrently
		// deleted backup (or a vanished backups directory) throws IOException /
		// UnauthorizedAccessException from enumeration or stat — swallow it and
		// move on; retention catches up on the next write.
		try
		{
			// D-063 step 8: retention applies ONLY to this document's backups,
			// matched by an explicit "fileName." boundary so no future doc name
			// that merely prefixes another ("app" vs "appx") gets lumped in.
			// Ordered oldest-first so the newest always survive. Each file's
			// timestamp is read ONCE into a snapshot to shrink the TOCTOU
			// window between enumeration and deletion.
			//
			// Age and ordering come from the timestamp BuildBackupPath mints into
			// the FILENAME, never from filesystem creation time. File.Replace
			// (Win32 ReplaceFile) transfers the REPLACED destination's creation
			// time onto the backup file, so every backup of a document shares one
			// ctime — the destination's original creation time, not the moment the
			// backup was taken. Against a destination older than BackupMaxAge (the
			// steady state for a config written at install and still in use) that
			// marks EVERY backup stale, including the one this very write just
			// minted, and retention degenerates to "keep zero".
			List<(string File, DateTime CreatedUtc)> backups = [.. Directory
				.EnumerateFiles(_backupsDirectory)
				.Where(file => Path.GetFileName(file).StartsWith(fileName + ".", StringComparison.Ordinal))
				.Select(file => (File: file, CreatedUtc: BackupTimestamp(file)))
				.OrderBy(entry => entry.CreatedUtc)];

			DateTime cutoff = DateTime.UtcNow - BackupMaxAge;

			// Age cap first: drop anything older than BackupMaxAge.
			foreach ((string stale, DateTime _) in backups.Where(entry => entry.CreatedUtc < cutoff))
			{
				DeleteQuietly(stale);
			}

			// Then the count cap: keep only the newest BackupRetentionCount.
			List<(string File, DateTime CreatedUtc)> fresh = [.. backups.Where(entry => entry.CreatedUtc >= cutoff)];
			for (int i = 0; i < fresh.Count - BackupRetentionCount; i++)
			{
				DeleteQuietly(fresh[i].File);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	// Authoritative age/order key for a backup: the timestamp BuildBackupPath
	// mints into the file name. Filesystem creation time cannot be used —
	// File.Replace (Win32 ReplaceFile) transfers the replaced destination's
	// creation time onto the backup, so every backup of a document shares one
	// ctime that says nothing about when the backup was taken.
	//
	// Read POSITIONALLY. Every store-minted name is
	// "<document name>.<yyyyMMddHHmmssfff>.<guid:N>.bak", so the stamp is always
	// the THIRD segment from the end — true even when the document name itself
	// contains dots, because only the guid and "bak" ever follow the stamp. Any
	// other (foreign) file sitting in the backups directory carries no stamp at
	// that position and is aged by last-write time, which unlike ctime
	// ReplaceFile does not rewrite.
	private static DateTime BackupTimestamp(string path)
	{
		string[] segments = Path.GetFileName(path).Split('.');
		return segments.Length >= 3
			&& segments[^3].Length == 17
			&& DateTime.TryParseExact(
				segments[^3],
				"yyyyMMddHHmmssfff",
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
				out DateTime stamped)
			? stamped
			: File.GetLastWriteTimeUtc(path);
	}

	private static void DeleteQuietly(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
