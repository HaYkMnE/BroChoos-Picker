using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("BroChoos.App.Tests.AvaloniaUiTestFramework", "BroChoos.App.Tests")]

namespace BroChoos.App.Tests;

// ONE DEDICATED, TEST-OWNED THREAD FOR EVERYTHING IN THE avalonia-ui COLLECTION.
//
// The Avalonia dispatcher is bound to whichever thread first runs SetupWithoutStarting, and
// Dispatcher.CheckAccess() is simply "is the current thread that thread". Before this change
// that thread was an ordinary thread-pool worker: xUnit ran the avalonia-ui collection on the
// pool, so the "UI thread" went back to the pool between tests and anything else could run on
// it, including the render timer (System.Threading.Timer, pool callbacks), which then passed
// CheckAccess() and entered ServerCompositor.Render -> Dispatcher.DisableProcessing() on the
// UI dispatcher. MEASURED: the failing probe ran on a ".NET TP Worker" thread that held
// dispatcher access, and Dispatcher.RunJobs threw "Cannot perform this operation while
// dispatcher processing is suspended" (Avalonia 11.3.18 Dispatcher.Queue.cs, the
// DisabledProcessingCount > 0 guard). Which tests failed changed from run to run.
//
// The fix keeps the scheduling the repository already relies on and changes only the thread:
// the whole avalonia-ui collection (constructors, [CultureIsolated] Before/After and every
// test body) runs on one long-lived background thread that nothing else uses, so Avalonia is
// set up on it and only test code can ever hold dispatcher access. The other collections still
// run in parallel with it on the pool, exactly as before. The thread stays MTA, which is what
// the measured-good pool threads were.
public sealed class AvaloniaUiTestFramework(IMessageSink messageSink) : XunitTestFramework(messageSink)
{
	protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
	{
		return new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
	}

	private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
		: XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
	{
		protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
		{
			using AssemblyRunner runner = new(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
			_ = await runner.RunAsync();
		}
	}

	private sealed class AssemblyRunner(
		ITestAssembly testAssembly,
		IEnumerable<IXunitTestCase> testCases,
		IMessageSink diagnosticMessageSink,
		IMessageSink executionMessageSink,
		ITestFrameworkExecutionOptions executionOptions)
		: XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
	{
		// Avalonia must be set up BEFORE any test can read Dispatcher.UIThread. The first read
		// caches the dispatcher for the process, and a read before setup caches one backed by
		// NullDispatcherImpl, whose CurrentThreadIsLoopThread is always true (Avalonia 11.3.18
		// IDispatcherImpl.cs). Production code reached by console-capture tests reads it
		// (PickerSession.PostToUi defaults to Dispatcher.UIThread.Post). Once that happens, every
		// thread passes CheckAccess, the render timer's pool callbacks enter
		// ServerCompositor.Render -> DisableProcessing next to a running test, and that test's
		// RunJobs fails with "dispatcher processing is suspended". Whether a pre-setup read
		// happened depended on scheduling, which is why a different subset failed in each run.
		protected override async Task<RunSummary> RunTestCollectionsAsync(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource)
		{
			// A failed or timed-out setup is recorded, not thrown: an exception here would escape the
			// async void RunTestCases and take the test host down. The guard test asserts that setup
			// ran AND succeeded, and every UI test already asserts PickerXamlProbe.Run().Failure.
			try
			{
				PickerXamlProbe.ProbeResult setup = await AvaloniaUiThread.RunAsync(() => Task.FromResult(PickerXamlProbe.Run()), AvaloniaUiThread.SetupTimeout);
				AvaloniaUiThread.SetupFailure = setup.Failure;
				AvaloniaUiThread.SetupRan = true;
			}
			catch (Exception failure)
			{
				AvaloniaUiThread.SetupFailure = failure.ToString();
			}

			RunSummary summary = await base.RunTestCollectionsAsync(messageBus, cancellationTokenSource);

			// Late items: anything posted to the UI thread after the avalonia-ui collection finished
			// (for example by a pool-thread test) still fails the run, at assembly level. After a
			// collection timeout the thread is known to be hung, so it is not flushed again.
			foreach (Exception failure in await CollectCleanupFailuresAsync("assembly end", flush: !AvaloniaUiThread.Hung))
			{
				_ = messageBus.QueueMessage(new TestAssemblyCleanupFailure(TestCases, TestAssembly, failure));
				summary.Failed++;
			}

			return summary;
		}

		protected override async Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
		{
			// The console-capture collection runs on the pool as before, but through runners that
			// bound EACH test (BoundedMethodRunner below), so a test that hangs while holding the
			// CultureGate is reported by name instead of stalling the collection without limit.
			if (string.Equals(testCollection.CollectionDefinition?.Name, typeof(ConsoleCaptureCollection).FullName, StringComparison.Ordinal))
			{
				return await new BoundedCollectionRunner(testCollection, testCases, DiagnosticMessageSink, messageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator), cancellationTokenSource).RunAsync();
			}

			if (!string.Equals(testCollection.CollectionDefinition?.Name, typeof(AvaloniaUiCollection).FullName, StringComparison.Ordinal))
			{
				return await base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource);
			}

			CutoffMessageBus collectionBus = new(messageBus);
			RunSummary summary;
			try
			{
				summary = await AvaloniaUiThread.RunAsync(() => base.RunTestCollectionAsync(collectionBus, testCollection, testCases, cancellationTokenSource), AvaloniaUiThread.CollectionTimeout);
			}
			catch (TimeoutException timeout)
			{
				// Reported as a collection failure instead of thrown: a throw here would escape the
				// async void RunTestCases and crash the test host without naming anything.
				//
				// The bus is cut off first, so the hung collection, which keeps running on the UI
				// thread, can never post a message after this point, in particular not after
				// TestAssemblyFinished. Its later messages are dropped, not refused: a false return
				// would make xUnit cancel the shared token and stop the other collections too.
				collectionBus.Close();
				AvaloniaUiThread.Hung = true;
				TimeoutException named = new(
					$"The avalonia-ui collection did not finish within {AvaloniaUiThread.CollectionTimeout.TotalMinutes} minutes on its dedicated test thread; the thread is hung (CultureGate holder: {CultureGate.Holder ?? "none"}).",
					timeout);
				_ = messageBus.QueueMessage(new TestCollectionCleanupFailure(testCases, testCollection, named));

				// The hung run never returns its own summary, so the counts are rebuilt from the
				// results it did report before the cutoff, plus one failure for the timeout. The
				// test that hung reported no result and is not counted on its own.
				return new RunSummary
				{
					Total = collectionBus.Total + 1,
					Failed = collectionBus.Failed + 1,
					Skipped = collectionBus.Skipped,
				};
			}

			// The pump records an exception that escaped a work item (an async void rethrow, a
			// throwing Post callback) instead of dying with it. Items posted by the last test are
			// flushed first, then any recorded exception, or a flush that timed out, fails the
			// collection, whatever the order in which the guard test ran.
			//
			// A cleanup failure is sent to VSTest as an adapter ERROR message, and it turns the run
			// (and the dotnet test exit code) into a failure only because the VSTest setting
			// RunConfiguration.TreatTestAdapterErrorsAsWarnings keeps its default, false. The guard
			// TestRunConfigurationTests in BroChoos.Architecture.Tests fails if any runsettings,
			// build file or workflow in the repository turns it on.
			foreach (Exception failure in await CollectCleanupFailuresAsync("the end of the avalonia-ui collection", flush: true))
			{
				_ = messageBus.QueueMessage(new TestCollectionCleanupFailure(testCases, testCollection, failure));
				summary.Failed++;
			}

			return summary;
		}

		private static async Task<List<Exception>> CollectCleanupFailuresAsync(string point, bool flush)
		{
			List<Exception> failures = [];
			if (flush && await AvaloniaUiThread.FlushAsync(point) is { } flushTimeout)
			{
				failures.Add(flushTimeout);
			}

			if (TakeEscapedFailures() is { } escaped)
			{
				failures.Add(escaped);
			}

			return failures;
		}

		// Forwards the avalonia-ui collection's messages to the assembly bus until Close, and counts
		// the test results that went through, so a timed-out collection still reports real counts.
		// After Close every message is dropped and true is returned: a false return would make
		// xUnit cancel the shared token and stop the collections that are not hung.
		private sealed class CutoffMessageBus(IMessageBus inner) : IMessageBus
		{
			private readonly Lock gate = new();
			private bool closed;

			internal int Total { get; private set; }

			internal int Failed { get; private set; }

			internal int Skipped { get; private set; }

			public bool QueueMessage(IMessageSinkMessage message)
			{
				lock (gate)
				{
					if (closed)
					{
						return true;
					}

					switch (message)
					{
						case ITestFailed:
							Total++;
							Failed++;
							break;
						case ITestSkipped:
							Total++;
							Skipped++;
							break;
						case ITestPassed:
							Total++;
							break;
						default:
							break;
					}

					return inner.QueueMessage(message);
				}
			}

			// The lock makes the cutoff exact: once Close returns, no message of this collection is
			// in flight to the assembly bus or can reach it later.
			internal void Close()
			{
				lock (gate)
				{
					closed = true;
				}
			}

			// The inner bus belongs to the assembly runner, which disposes it.
			public void Dispose()
			{
			}
		}

		// THE PER-TEST BOUND FOR THE console-capture COLLECTION (L-1). CultureGate bounds how long a
		// WAITER waits, but nothing bounded the HOLDER: a console-capture test that hung while
		// holding the gate stalled its collection until the CI job was cancelled. These three
		// runners are xUnit's own (XunitTestCollectionRunner -> XunitTestClassRunner ->
		// XunitTestMethodRunner) with one change at the bottom: each test case may HOLD the gate
		// for at most ConsoleCaptureTestBound.Timeout (waiting for it is not counted). When that
		// expires, the hung test is reported as FAILED BY NAME with the results it already had,
		// its later messages are dropped (TestCaseCutoffBus), the gate is poisoned in its name so
		// later gated tests fail at once while it still holds it, and the collection moves on.
		// Scheduling is unchanged: the
		// collection still runs on the pool, one test at a time, in parallel with avalonia-ui.
		private sealed class BoundedCollectionRunner(
			ITestCollection collection,
			IEnumerable<IXunitTestCase> collectionTestCases,
			IMessageSink diagnostics,
			IMessageBus bus,
			ITestCaseOrderer orderer,
			ExceptionAggregator collectionAggregator,
			CancellationTokenSource cancellation)
			: XunitTestCollectionRunner(collection, collectionTestCases, diagnostics, bus, orderer, collectionAggregator, cancellation)
		{
			protected override Task<RunSummary> RunTestClassAsync(ITestClass testClass, IReflectionTypeInfo @class, IEnumerable<IXunitTestCase> testCases)
			{
				return new BoundedClassRunner(testClass, @class, testCases, DiagnosticMessageSink, MessageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator), CancellationTokenSource, CollectionFixtureMappings).RunAsync();
			}
		}

		private sealed class BoundedClassRunner(
			ITestClass ownerClass,
			IReflectionTypeInfo ownerClassInfo,
			IEnumerable<IXunitTestCase> classTestCases,
			IMessageSink diagnostics,
			IMessageBus bus,
			ITestCaseOrderer orderer,
			ExceptionAggregator classAggregator,
			CancellationTokenSource cancellation,
			IDictionary<Type, object> collectionFixtures)
			: XunitTestClassRunner(ownerClass, ownerClassInfo, classTestCases, diagnostics, bus, orderer, classAggregator, cancellation, collectionFixtures)
		{
			protected override Task<RunSummary> RunTestMethodAsync(ITestMethod testMethod, IReflectionMethodInfo method, IEnumerable<IXunitTestCase> testCases, object[] constructorArguments)
			{
				return new BoundedMethodRunner(testMethod, Class, method, testCases, DiagnosticMessageSink, MessageBus, new ExceptionAggregator(Aggregator), CancellationTokenSource, constructorArguments).RunAsync();
			}
		}

		private sealed class BoundedMethodRunner(
			ITestMethod ownerMethod,
			IReflectionTypeInfo ownerClassInfo,
			IReflectionMethodInfo ownerMethodInfo,
			IEnumerable<IXunitTestCase> methodTestCases,
			IMessageSink diagnostics,
			IMessageBus bus,
			ExceptionAggregator methodAggregator,
			CancellationTokenSource cancellation,
			object[] classConstructorArguments)
			: XunitTestMethodRunner(ownerMethod, ownerClassInfo, ownerMethodInfo, methodTestCases, diagnostics, bus, methodAggregator, cancellation, classConstructorArguments)
		{
			protected override async Task<RunSummary> RunTestCaseAsync(IXunitTestCase testCase)
			{
				// The base runs the case as testCase.RunAsync(..., MessageBus, ...), reading MessageBus
				// synchronously at the call. Swapping in the per-case cutoff bus around that call is
				// therefore exact, and safe because a method runner runs its cases one after another.
				// Active and Clock flow (AsyncLocal) into the test: the guard test reads Active, and
				// [CultureIsolated] marks on Clock when this case holds the gate.
				TestCaseCutoffBus caseBus = new(MessageBus);
				IMessageBus original = MessageBus;
				Stopwatch elapsed = Stopwatch.StartNew();
				ConsoleCaptureTestBound.CaseClock clock = new();
				ConsoleCaptureTestBound.Active = true;
				ConsoleCaptureTestBound.Clock = clock;
				Task<RunSummary> run;
				try
				{
					MessageBus = caseBus;
					run = base.RunTestCaseAsync(testCase);
				}
				finally
				{
					MessageBus = original;
				}

				// N-2: THE BOUND COUNTS ONLY TIME SPENT HOLDING THE GATE. Time a case spends waiting
				// for the gate is already bounded by CultureGate.Timeout (that waiter then fails with
				// the holder's name), so it is not charged here. The case is hung when it has held the
				// gate continuously for longer than Timeout. A backstop (OverallTimeout) covers a
				// case that hangs outside the gate (constructor, Dispose) and is reported as such.
				while (true)
				{
					using CancellationTokenSource tick = new();
					Task delay = Task.Delay(ConsoleCaptureTestBound.PollInterval, tick.Token);
					Task first = await Task.WhenAny(run, delay).ConfigureAwait(false);
					await tick.CancelAsync().ConfigureAwait(false);
					if (first == run)
					{
						return await run.ConfigureAwait(false);
					}

					(TimeSpan HeldFor, string? Name, long Epoch) held = clock.Snapshot();
					bool heldTooLong = held.HeldFor > ConsoleCaptureTestBound.Timeout;
					bool ranTooLong = elapsed.Elapsed > ConsoleCaptureTestBound.OverallTimeout;
					if (!heldTooLong && !ranTooLong)
					{
						continue;
					}

					if (caseBus.Close())
					{
						// The case reported TestCaseFinished in the instant before the cutoff, so it is
						// not hung: its own result stands.
						return await run.ConfigureAwait(false);
					}

					return ReportHung(testCase, caseBus, original, heldTooLong ? held : null, (decimal)elapsed.Elapsed.TotalSeconds);
				}
			}

			// `heldTooLong` is the clock snapshot that crossed the bound, or null when the case ran past
			// the backstop instead; its epoch is the acquisition that was measured (N-1).
			private static RunSummary ReportHung(IXunitTestCase testCase, TestCaseCutoffBus caseBus, IMessageBus target, (TimeSpan HeldFor, string? Name, long Epoch)? heldTooLong, decimal seconds)
			{
				string name = testCase.DisplayName;
				string? gateHolder = CultureGate.Holder;
				string text;
				if (heldTooLong is { Name: { } selfName } self)
				{
					// Poisoned only while the measured acquisition (its epoch) still holds the gate,
					// atomically with that check (L-1), so a later row with the same name or another
					// test that took the gate since is never named.
					bool poisoned = CultureGate.State.TryPoisonWhileHeldBy(self.Epoch, selfName, out _);
					text = $"The console-capture test '{name}' held the CultureGate for more than {ConsoleCaptureTestBound.Timeout.TotalMinutes} minutes; it is hung while holding it. It keeps running in the background and its later output is discarded.{(poisoned ? " Every later CultureGate waiter fails at once while it still holds the gate." : string.Empty)}";
				}
				else
				{
					text = $"The console-capture test '{name}' did not finish within {ConsoleCaptureTestBound.OverallTimeout.TotalMinutes} minutes and was NOT holding the CultureGate at that point (current holder: {gateHolder ?? "none"}); it hung outside the gated section (constructor, Dispose, or waiting for the gate). Its later output is discarded.";
				}

				TimeoutException hung = new(text);

				// N-1: whatever the hung case already reported stays, and its counts are kept; the
				// rest of its message sequence is completed here so the runner sees a balanced case
				// with its real results plus one failure for the hang.
				if (!caseBus.SawCaseStarting)
				{
					_ = target.QueueMessage(new TestCaseStarting(testCase));
				}

				ITest? open = caseBus.OpenTest;
				if (open is not null && caseBus.OpenTestHasResult)
				{
					// The open test already has its own result: close it, and report the hang as a
					// separate test so no test gets two results.
					_ = target.QueueMessage(new TestFinished(open, 0, string.Empty));
					open = null;
				}

				// N-3: a separate hang test gets its own display name, so TRX does not merge it with
				// a test of the same name that already finished.
				ITest test = open ?? new XunitTest(testCase, name + " [hang]");
				if (open is null)
				{
					_ = target.QueueMessage(new TestStarting(test));
				}

				_ = target.QueueMessage(new TestFailed(test, seconds, string.Empty, hung));
				_ = target.QueueMessage(new TestFinished(test, seconds, string.Empty));

				int total = caseBus.Total + 1;
				int failed = caseBus.Failed + 1;
				_ = target.QueueMessage(new TestCaseFinished(testCase, seconds, total, failed, caseBus.Skipped));
				return new RunSummary { Total = total, Failed = failed, Skipped = caseBus.Skipped, Time = seconds };
			}
		}

		// Forwards one test case's messages until Close and remembers how far the case got, so a
		// hung case can be completed with exactly the messages it has not sent. It counts results
		// the way CutoffMessageBus does (N-1), so a hung Theory keeps the counts of the rows that
		// already ran. After Close every message is dropped and true is returned (a false return
		// would cancel the whole run).
		private sealed class TestCaseCutoffBus(IMessageBus inner) : IMessageBus
		{
			private readonly Lock gate = new();
			private bool closed;
			private bool finished;

			internal bool SawCaseStarting { get; private set; }

			// The test that has started and not finished yet, and whether it already has a result.
			internal ITest? OpenTest { get; private set; }

			internal bool OpenTestHasResult { get; private set; }

			internal int Total { get; private set; }

			internal int Failed { get; private set; }

			internal int Skipped { get; private set; }

			public bool QueueMessage(IMessageSinkMessage message)
			{
				lock (gate)
				{
					if (closed)
					{
						return true;
					}

					switch (message)
					{
						case ITestCaseStarting:
							SawCaseStarting = true;
							break;
						case ITestStarting starting:
							OpenTest = starting.Test;
							OpenTestHasResult = false;
							break;
						case ITestFailed:
							Total++;
							Failed++;
							OpenTestHasResult = true;
							break;
						case ITestSkipped:
							Total++;
							Skipped++;
							OpenTestHasResult = true;
							break;
						case ITestPassed:
							Total++;
							OpenTestHasResult = true;
							break;
						case ITestFinished:
							OpenTest = null;
							OpenTestHasResult = false;
							break;
						case ITestCaseFinished:
							finished = true;
							break;
						default:
							break;
					}

					return inner.QueueMessage(message);
				}
			}

			// Returns whether the case had already finished; either way nothing passes afterwards.
			internal bool Close()
			{
				lock (gate)
				{
					closed = true;
					return finished;
				}
			}

			// The inner bus belongs to the collection runner.
			public void Dispose()
			{
			}
		}

		private static InvalidOperationException? TakeEscapedFailures()
		{
			List<string> texts = [];
			while (AvaloniaUiThread.EscapedFailures.TryDequeue(out string? text))
			{
				texts.Add(text);
			}

			return texts.Count == 0
				? null
				: new InvalidOperationException(
					$"{texts.Count} exception(s) escaped a work item on the avalonia-ui test thread:{Environment.NewLine}{string.Join(Environment.NewLine, texts)}");
		}
	}
}

internal static class AvaloniaUiThread
{
	// The whole collection takes well under a minute locally. A hung collection is reported here
	// as a collection failure naming the avalonia-ui collection, 15 minutes after the collection
	// STARTED. ci.yml gives the test job 30 minutes and test-debug 40 (timeout-minutes), counted
	// from the start of the JOB. In run 41051054418 the tests started 2 min 30 s (test) and
	// 1 min 47 s (test-debug) into the job, after checkout, restore and build, and other test
	// assemblies run before or alongside this one. So the report reaches the log before the job
	// is cancelled only if this collection starts less than 15 minutes (test) or 25 minutes
	// (test-debug) after the job did; nothing here guarantees that, so it is not a promise.
	// What bounds a hang in a gated test: the gate HOLDER is bounded per test in console-capture
	// (ConsoleCaptureTestBound, 5 minutes) and per collection here (CollectionTimeout); a WAITER
	// gives up after CultureGate.Timeout (10 minutes); and once either bound fires, every later
	// gated test fails at once (CultureGate.PoisonedBy) instead of waiting another 10 minutes.
	internal static readonly TimeSpan CollectionTimeout = TimeSpan.FromMinutes(15);

	// Setup plus the first PickerWindow takes about a second locally.
	internal static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(2);

	// A Send from a foreign thread waits at most this long for the UI thread to START it. The UI
	// thread can legitimately block for up to CultureGate.Timeout inside a UI test's
	// [CultureIsolated] Before, so the Send budget is that plus a margin; only a thread that is
	// really hung runs past it.
	internal static readonly TimeSpan SendTimeout = CultureGate.Timeout + TimeSpan.FromMinutes(2);

	// The end-of-collection flush waits at most this long for items already queued.
	private static readonly TimeSpan FlushTimeout = TimeSpan.FromMinutes(2);

	// The life of one Send item. Exactly one CompareExchange wins each transition:
	// Pending -> Running (the pump starts it) or Pending -> Abandoned (the caller timed out first);
	// Running -> Finished (it completed and hands its outcome to the caller) or
	// Running -> Orphaned (the caller timed out while it ran; its outcome goes to EscapedFailures).
	private const int Pending = 0;
	private const int Running = 1;
	private const int Abandoned = 2;
	private const int Finished = 3;
	private const int Orphaned = 4;

	private static readonly BlockingCollection<Action> Work = [];

	// The Avalonia setup failure text, or null. Only meaningful once SetupRan is true: both are
	// set before any collection runs, and a setup that threw or timed out leaves SetupRan false.
	internal static string? SetupFailure { get; set; }

	internal static bool SetupRan { get; set; }

	// Set when the avalonia-ui collection timed out: the thread is hung, so it is not flushed again.
	internal static bool Hung { get; set; }

	// Exceptions that escaped a work item, and failures of Send callbacks that finished after their
	// caller had timed out. The pump keeps running. The framework drains this queue at the end of
	// the avalonia-ui collection and again at assembly end and reports each batch as a cleanup
	// failure, which fails the run. The guard test NoWorkItem_EscapedOnTheDedicatedThread is only
	// an earlier signal for items recorded before it runs.
	internal static ConcurrentQueue<string> EscapedFailures { get; } = [];

	private static readonly Thread UiThread = Start();

	internal static bool IsCurrent => ReferenceEquals(Thread.CurrentThread, UiThread);

	// Runs work on the dedicated thread. Every continuation of the returned task is posted back
	// to that thread, so async code started there stays there. A fault or cancellation is handed
	// back to the caller as-is, and a run that does not finish in time fails with TimeoutException.
	internal static async Task<T> RunAsync<T>(Func<Task<T>> work, TimeSpan timeout)
	{
		TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Work.Add(() =>
		{
			Task<T> task;
			try
			{
				task = work();
			}
			catch (Exception failure)
			{
				_ = done.TrySetException(failure);
				return;
			}

			_ = task.ContinueWith(
				finished => _ = finished.IsCanceled
					? done.TrySetCanceled()
					: finished.Exception is { } fault
						? done.TrySetException(fault.InnerExceptions)
						: done.TrySetResult(finished.Result),
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		});

		return await done.Task.WaitAsync(timeout).ConfigureAwait(false);
	}

	// Runs every item queued so far (FIFO), so callbacks posted by the last test have executed
	// before EscapedFailures is read. A hung thread is not waited on past FlushTimeout: the
	// timeout is returned, and the caller reports it as a cleanup failure, since the items that
	// did not run may hide exactly the failures the flush exists to catch.
	internal static async Task<TimeoutException?> FlushAsync(string point)
	{
		try
		{
			_ = await RunAsync(() => Task.FromResult(true), FlushTimeout).ConfigureAwait(false);
			return null;
		}
		catch (TimeoutException timeout)
		{
			return new TimeoutException(
				$"The avalonia-ui test thread did not drain its queue within {FlushTimeout.TotalMinutes} minutes at {point}; it is busy or hung (CultureGate holder: {CultureGate.Holder ?? "none"}), so work items still queued there were not checked for escaped exceptions.",
				timeout);
		}
	}

	private static Thread Start()
	{
		Thread thread = new(Pump) { IsBackground = true, Name = "BroChoos avalonia-ui test thread" };
		thread.Start();
		return thread;
	}

	// The pump context is put back around every item. Avalonia installs its own
	// AvaloniaSynchronizationContext on the thread that sets it up, and this thread never runs
	// an Avalonia message loop, so an xUnit continuation captured under that context would be
	// queued on the dispatcher and never run (MEASURED: the collection hung until the timeout).
	private static void Pump()
	{
		PumpContext context = new();
		foreach (Action item in Work.GetConsumingEnumerable())
		{
			SynchronizationContext.SetSynchronizationContext(context);
			try
			{
				item();
			}
			catch (Exception escaped)
			{
				// Losing this thread would hang every later UI test, so the failure is kept and
				// written to stderr instead of ending the pump.
				string text = escaped.ToString();
				EscapedFailures.Enqueue(text);
				Console.Error.WriteLine("[avalonia-ui test thread] work item threw: " + text);
			}

			SynchronizationContext.SetSynchronizationContext(context);
		}
	}

	private sealed class PumpContext : SynchronizationContext
	{
		public override void Post(SendOrPostCallback d, object? state)
		{
			Work.Add(() => d(state));
		}

		public override void Send(SendOrPostCallback d, object? state)
		{
			if (IsCurrent)
			{
				d(state);
				return;
			}

			// No disposable wait handle: a timed-out item may still be dequeued later, and it must
			// then neither run the callback nor touch anything the caller has already dropped. The
			// claim (see Pending..Orphaned) decides each outcome exactly once.
			TaskCompletionSource<Exception?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
			int claim = Pending;
			Work.Add(() =>
			{
				if (Interlocked.CompareExchange(ref claim, Running, Pending) != Pending)
				{
					return;
				}

				Exception? thrown = null;
				try
				{
					d(state);
				}
				catch (Exception failure)
				{
					thrown = failure;
				}

				if (Interlocked.CompareExchange(ref claim, Finished, Running) == Running)
				{
					_ = done.TrySetResult(thrown);
				}
				else if (thrown is not null)
				{
					// Orphaned: the caller already gave up, so nobody would see this failure. It is
					// recorded instead, and the framework fails the run with it.
					string text = "A callback sent to the avalonia-ui test thread failed after its caller had timed out: " + thrown;
					EscapedFailures.Enqueue(text);
					Console.Error.WriteLine("[avalonia-ui test thread] " + text);
				}
			});
			if (!done.Task.Wait(SendTimeout))
			{
				int seen = Interlocked.CompareExchange(ref claim, Abandoned, Pending);
				if (seen == Pending)
				{
					throw new TimeoutException(
						$"A callback sent to the avalonia-ui test thread did not start within {SendTimeout.TotalMinutes} minutes, which is longer than any legitimate wait there (CultureGate {CultureGate.Timeout.TotalMinutes} minutes plus a margin); the thread is hung in another work item (CultureGate holder: {CultureGate.Holder ?? "none"}). The callback was abandoned and will not run.");
				}

				if (seen == Running && Interlocked.CompareExchange(ref claim, Orphaned, Running) == Running)
				{
					throw new TimeoutException(
						$"A callback sent to the avalonia-ui test thread was still running {SendTimeout.TotalMinutes} minutes after it was sent; it may have started late or be hung itself. It keeps running, and if it throws, the failure is recorded and fails the run.");
				}

				// Otherwise the callback finished in the window between the timed-out wait and the
				// claim check. Its outcome is set right after the claim, so it is handled normally.
			}

			if (done.Task.GetAwaiter().GetResult() is { } sentFailure)
			{
				throw new InvalidOperationException("A callback sent to the avalonia-ui test thread failed.", sentFailure);
			}
		}

		public override SynchronizationContext CreateCopy()
		{
			return this;
		}
	}
}

// The per-test bound applied to the console-capture collection by AvaloniaUiTestFramework
// (BoundedMethodRunner). The slowest console-capture test takes about 10 s under the CI-load
// simulation (the 8 s toast lifetime plus scheduling); 5 minutes of HOLDING the gate is far
// above that, and below CultureGate.Timeout, so a hung holder is reported by name before a
// waiter gives up on it. Time spent waiting for the gate is not counted (N-2); the backstop
// OverallTimeout covers a case that hangs outside the gated section.
internal static class ConsoleCaptureTestBound
{
	internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

	// Waiting for the gate (up to CultureGate.Timeout) plus holding it (Timeout) plus a margin.
	internal static readonly TimeSpan OverallTimeout = CultureGate.Timeout + Timeout + TimeSpan.FromMinutes(2);

	// How often the runner checks the clock while a case is running; it costs nothing for a case
	// that finishes first, because the wait ends with the case.
	internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

	private static readonly AsyncLocal<bool> ActiveFlag = new();

	private static readonly AsyncLocal<CaseClock?> ClockSlot = new();

	// True inside a test that runs under the bound; the guard test below fails if it is not.
	internal static bool Active
	{
		get => ActiveFlag.Value;
		set => ActiveFlag.Value = value;
	}

	// The running case's clock; null outside the console-capture collection.
	internal static CaseClock? Clock
	{
		get => ClockSlot.Value;
		set => ClockSlot.Value = value;
	}

	// Written by the test's [CultureIsolated] Before/After, read by the runner, under one lock.
	// A Theory takes and releases the gate once per row, so each acquisition restarts the clock.
	internal sealed class CaseClock
	{
		private readonly Lock _gate = new();

		// The values change together, so they are kept as one tuple and read and written under
		// _gate: Since is a Stopwatch timestamp of the current acquisition, 0 when not holding;
		// Epoch is that acquisition's CultureGateState epoch.
		private (long Since, string? Name, long Epoch) _state;

		internal string? HolderName
		{
			get
			{
				lock (_gate)
				{
					return _state.Name;
				}
			}
		}

		internal bool IsHolding
		{
			get
			{
				lock (_gate)
				{
					return _state.Since != 0;
				}
			}
		}

		// N-1: how long the case has held the gate in its current acquisition (zero when not
		// holding), together with the acquisition it belongs to. The hold time and the acquisition it belongs to, read in ONE critical section, so
		// the runner's "held too long" decision and the epoch it poisons with always describe the
		// same acquisition (a release plus a new row in between cannot mix them).
		internal (TimeSpan HeldFor, string? Name, long Epoch) Snapshot()
		{
			lock (_gate)
			{
				TimeSpan held = _state.Since == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_state.Since);
				return (held, _state.Name, _state.Epoch);
			}
		}

		internal void MarkHolding(string holder, long epoch)
		{
			lock (_gate)
			{
				_state = (Stopwatch.GetTimestamp(), holder, epoch);
			}
		}

		internal void MarkNotHolding()
		{
			lock (_gate)
			{
				_state = (0, _state.Name, _state.Epoch);
			}
		}
	}
}

// GUARDS: the mechanism only holds if the framework above is the one xUnit runs and it routes
// exactly this collection to the dedicated thread.
[Collection(AvaloniaUiCollection.Name)]
public sealed class AvaloniaUiThreadGuardTests
{
	[Fact]
	public void TestsInTheAvaloniaUiCollection_RunOnTheDedicatedUiThread()
	{
		_ = AvaloniaUiThread.IsCurrent.Should().BeTrue("AvaloniaUiTestFramework must run the avalonia-ui collection on its dedicated thread");
	}

	[Fact]
	public void AvaloniaSetup_OnTheDedicatedThread_Succeeded()
	{
		_ = AvaloniaUiThread.SetupRan.Should().BeTrue("Avalonia setup must have run to completion on the avalonia-ui thread before any collection starts; failure: {0}", AvaloniaUiThread.SetupFailure ?? "none recorded");
		_ = AvaloniaUiThread.SetupFailure.Should().BeNull("Avalonia setup on the avalonia-ui thread must succeed before any UI test runs");
	}

	[Fact]
	public void NoWorkItem_EscapedOnTheDedicatedThread()
	{
		_ = AvaloniaUiThread.EscapedFailures.Should().BeEmpty("an exception that escaped a work item on the avalonia-ui thread was swallowed by the pump");
	}
}

[Collection(ConsoleCaptureCollection.Name)]
public sealed class AvaloniaUiThreadConsoleCaptureGuardTests
{
	[Fact]
	public void TestsOutsideTheAvaloniaUiCollection_NeverRunOnTheUiThread()
	{
		_ = AvaloniaUiThread.IsCurrent.Should().BeFalse("only the avalonia-ui collection may run on the thread that owns the Avalonia dispatcher");
	}

	[Fact]
	public void TestsInTheConsoleCaptureCollection_RunUnderThePerTestBound()
	{
		_ = ConsoleCaptureTestBound.Active.Should().BeTrue("AvaloniaUiTestFramework must run every console-capture test through BoundedMethodRunner, so a test that hangs while holding the CultureGate is reported by name");
		_ = ConsoleCaptureTestBound.Timeout.Should().BeLessThan(CultureGate.Timeout, "the holder must be reported before a waiter gives up on the gate");

		// N-2 pin: the bound counts from the moment the gate is HELD, so inside the test body the
		// clock must already read as holding, in this test's name.
		ConsoleCaptureTestBound.CaseClock? clock = ConsoleCaptureTestBound.Clock;
		_ = clock.Should().NotBeNull("the runner must hand every console-capture case a clock");
		_ = clock!.IsHolding.Should().BeTrue("[CultureIsolated] Before must start the bound when it acquires the gate");
		_ = clock.HolderName.Should().Be(CultureGate.Holder, "the clock and the gate must name the same holder");
	}

	[Fact]
	public void TheAvaloniaDispatcher_DeniesAccessToEveryOtherThread()
	{
		_ = Avalonia.Threading.Dispatcher.UIThread.CheckAccess().Should().BeFalse(
			"the dispatcher must be the real Win32 one bound to the avalonia-ui thread; a dispatcher created before setup grants access to every thread");
	}
}
