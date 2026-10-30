using System.Collections.Concurrent;
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
	// The whole collection takes well under a minute locally. ci.yml gives the test job 30 minutes
	// and test-debug 40 (timeout-minutes), so a hung collection is reported here as a collection
	// failure whose message names the avalonia-ui collection, before either job is cancelled.
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
	public void TheAvaloniaDispatcher_DeniesAccessToEveryOtherThread()
	{
		_ = Avalonia.Threading.Dispatcher.UIThread.CheckAccess().Should().BeFalse(
			"the dispatcher must be the real Win32 one bound to the avalonia-ui thread; a dispatcher created before setup grants access to every thread");
	}
}
