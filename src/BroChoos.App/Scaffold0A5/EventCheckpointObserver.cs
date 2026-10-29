// DEBUG-ONLY, for the same two-gate reason as AtomicWriteKillChild (see that file, and the
// Compile Remove in BroChoos.App.csproj). This half parks a write on named events and is
// harmless on its own - it performs no I/O and decides nothing - but it exists ONLY to serve
// the kill child, and leaving it in a shipped build would leave a type whose entire purpose is
// to stop a config write mid-flight sitting in production code with no caller. Gated with its
// partner so the scaffold is one unit that either compiles or does not.
#if DEBUG
using BroChoos.Platform.Windows.Storage;

namespace BroChoos.App.Scaffold0A5;

/// <summary>
/// 0A-5 SCAFFOLD - THROWAWAY, AND DEBUG-ONLY. The <see cref="IAtomicWriteObserver"/> half of the
/// D-v3.5-493 kill-process harness: it lets a PARENT process stop this process at an exact
/// point in <c>AtomicJsonFileStore.Write</c>, inspect the destination while it is provably
/// still parked there, and only then kill it.
/// <para>Goes away with the scaffold. Nothing in production references it, and no
/// production type had to change to enable it - the seam it plugs into shipped in the
/// write path already.</para>
/// </summary>
///
/// WHY EVENTS AND NOT A SLEEP. A sleep would make the harness a race: the parent would have
/// to guess how long the child needs to reach a stage, and a slow machine would turn a real
/// assertion into a flaky one. A named event pair is exact - the parent learns the child IS
/// at the stage, and the child does not leave it until told to.
///
/// WHY THE WAIT IS BOUNDED. IAtomicWriteObserver's contract permits a test observer to park
/// at a stage, but ONLY "under a bounded timeout it enforces itself" - and from LockAcquired
/// onward the park is inside the per-file write mutex, so an unbounded park would wedge every
/// other writer of that file. The harness also has a failure mode of its own: if the PARENT
/// dies (crash, Ctrl+C, a harness bug) nothing will ever set `proceed`, and an unbounded wait
/// would leave a BroChoos process parked on a mutex forever, on a developer's machine, with
/// no owner. The timeout converts that into a self-limiting non-zero exit.
///
/// WHY THE NAMES CARRY A RUN ID. Kernel event names are machine-wide within the session, so
/// a stale `proceed` left set by an earlier run would be consumed instantly by the next one:
/// the child would sail past the checkpoint, the parent would kill it somewhere else
/// entirely, and the assertions would still pass - a kill test passing for the wrong reason,
/// which is worse than one that fails. Every run mints a fresh GUID, so no name from a
/// previous run is reachable.
internal sealed class EventCheckpointObserver : IAtomicWriteObserver, IDisposable
{
	// ManualReset on purpose. An AutoReset event would be consumed by the first waiter, so
	// the parent could not re-read "was this stage reached?" after the fact, and a signal
	// that arrived before the parent got round to waiting would be lost. A latch is what is
	// wanted here: once reached, permanently reached.
	private const EventResetMode Latch = EventResetMode.ManualReset;

	private readonly HashSet<AtomicWriteStage> _armed;

	private readonly TimeSpan _parkTimeout;

	private readonly Action<AtomicWriteStage> _onParkExpired;

	private readonly Action<string> _trace;

	// Both handles per armed stage are created UP FRONT, in the constructor, not lazily when
	// the stage is reached. The parent creates the same names before it starts this process,
	// so by the time Write runs, both sides hold an open handle to one kernel object and
	// neither can miss the other's signal.
	// new() rather than a collection expression: Dictionary is not reliably constructible from
	// `[]` across C# versions, and this file must compile without surprises.
	private readonly Dictionary<AtomicWriteStage, (EventWaitHandle Reached, EventWaitHandle Proceed)> _gates = [];

	// UNSYNCHRONIZED, and that is sufficient here rather than an oversight. IAtomicWriteObserver
	// states that no thread-safety is provided, so one observer CAN in general be notified from
	// several concurrent writes - but this observer's only host is the kill child, which performs
	// exactly ONE write on one thread and then exits. There is no second writer to race with, so
	// a lock would add cost and a false suggestion that concurrent use is supported. If this is
	// ever reused for a multi-write host, this list needs a lock (or a concurrent collection).
	private readonly List<AtomicWriteStage> _expired = [];

	/// <param name="runId">Scopes every event name to this run. Must be non-empty and must
	/// match what the parent used.</param>
	/// <param name="armedStages">The stages to park at. A stage that is NOT in this set costs
	/// one hash lookup and returns, so the write runs at full speed everywhere else.</param>
	/// <param name="parkTimeout">The self-enforced bound on a single park.</param>
	/// <param name="onParkExpired">Invoked when a park expires without the parent releasing
	/// it. The child mode passes an action that prints a marker and terminates the process;
	/// tests pass a recorder. It MUST NOT throw - see OnStage.</param>
	/// <param name="trace">Marker sink, so the parent can see the park in the child's stdout
	/// as well as through the event.</param>
	public EventCheckpointObserver(
		string runId,
		IEnumerable<AtomicWriteStage> armedStages,
		TimeSpan parkTimeout,
		Action<AtomicWriteStage> onParkExpired,
		Action<string>? trace = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runId);
		ArgumentNullException.ThrowIfNull(armedStages);
		ArgumentNullException.ThrowIfNull(onParkExpired);

		if (parkTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(parkTimeout),
				parkTimeout,
				"The park timeout must be positive: the observer contract requires a bounded wait.");
		}

		_armed = [.. armedStages];
		_parkTimeout = parkTimeout;
		_onParkExpired = onParkExpired;
		_trace = trace ?? (static _ => { });

		// The run id is not kept as a field on purpose: it is needed only to MINT the names
		// here. Once both handles exist, the observer works through the handles, so holding the
		// id would invite a second name-building path that could drift from these two.
		foreach (AtomicWriteStage stage in _armed)
		{
			_gates[stage] = (
				new EventWaitHandle(initialState: false, Latch, ReachedEventName(runId, stage)),
				new EventWaitHandle(initialState: false, Latch, ProceedEventName(runId, stage)));
		}
	}

	/// <summary>Set by the child when it reaches an armed stage; the parent waits on this to
	/// learn the write is parked exactly there.</summary>
	/// <remarks>"Local\" matches the scope the write mutex uses (see
	/// <c>AtomicJsonFileStore.BuildMutexName</c>): parent and child are the same user in the
	/// same session, so a session-scoped name is sufficient and avoids the cross-account
	/// squatting surface a "Global\" name would open.</remarks>
	public static string ReachedEventName(string runId, AtomicWriteStage stage)
	{
		return $@"Local\BroChoos_KillTest_{runId}_{stage}_reached";
	}

	/// <summary>Set by the parent to release a parked child. In a KILL case the parent never
	/// sets it - the child is killed while parked - so the harness can prove the run died at
	/// the intended stage rather than somewhere downstream.</summary>
	public static string ProceedEventName(string runId, AtomicWriteStage stage)
	{
		return $@"Local\BroChoos_KillTest_{runId}_{stage}_proceed";
	}

	/// <summary>The stages whose park expired without being released. Non-empty means the
	/// parent never arrived, so any observation the harness reports about this run is
	/// worthless - the child ran on past the checkpoint.</summary>
	public IReadOnlyCollection<AtomicWriteStage> ExpiredStages => _expired;

	/// <summary>Signals <c>reached</c> for an armed stage, then blocks on <c>proceed</c> for at
	/// most the configured timeout. Unarmed stages return immediately.</summary>
	///
	/// DELIBERATELY DOES NOT THROW, on any path. The write path calls the observer WITHOUT a
	/// try/catch by design (see the first call site in AtomicJsonFileStore), and an exception
	/// escaping here would be classified as a write failure - so a HARNESS problem would be
	/// reported as a defect in the code under test. The two must stay distinguishable: a park
	/// that expires is reported through _onParkExpired and ExpiredStages, never as an
	/// exception. Only the CONSTRUCTOR validates arguments by throwing, which happens before
	/// any write starts.
	public void OnStage(AtomicWriteStage stage)
	{
		if (!_gates.TryGetValue(stage, out (EventWaitHandle Reached, EventWaitHandle Proceed) gate))
		{
			return;
		}

		// Announce BEFORE waiting, in this order. The parent's whole design rests on it: it
		// asserts against the destination while the child is still inside this call, so the
		// signal must mean "parked here now", not "was here a moment ago".
		_trace($"KILLCHILD_PARKED stage={stage}");
		_ = gate.Reached.Set();

		if (gate.Proceed.WaitOne(_parkTimeout))
		{
			_trace($"KILLCHILD_RELEASED stage={stage}");
			return;
		}

		_expired.Add(stage);
		_trace($"KILLCHILD_PARK_EXPIRED stage={stage} timeoutSeconds={_parkTimeout.TotalSeconds}");

		// CONTAINED, and this is the one place OnStage could previously have broken its own
		// no-throw contract. _onParkExpired is caller-supplied: the child mode passes an action
		// that prints a marker and exits, but a test (or a future caller) can pass anything. An
		// exception escaping here would leave OnStage via the write path, which calls the observer
		// with NO try/catch by explicit design - so AtomicJsonFileStore would classify a HARNESS
		// callback fault as a WRITE failure and the run would report a product defect that does
		// not exist. Diagnosing that from evidence would be brutal, because the reported symptom
		// names the store rather than the harness.
		//
		// Swallowed rather than rethrown for the same reason the contract exists: the write under
		// observation is not at fault and must be allowed to complete. The fault is still visible
		// - it goes to the marker sink, and ExpiredStages already recorded the expiry above.
		try
		{
			_onParkExpired(stage);
		}
		catch (Exception callbackFault)
		{
			// The trace sink is itself caller-supplied, so it gets the same treatment: reporting
			// a contained fault must not become a new escape route out of OnStage.
			try
			{
				_trace($"KILLCHILD_PARK_EXPIRED_CALLBACK_FAILED stage={stage} type={callbackFault.GetType().Name}");
			}
			catch (Exception)
			{
			}
		}
	}

	public void Dispose()
	{
		foreach ((EventWaitHandle reached, EventWaitHandle proceed) in _gates.Values)
		{
			reached.Dispose();
			proceed.Dispose();
		}

		_gates.Clear();
	}
}
#endif
