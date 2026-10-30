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
			// (for example by a pool-thread test) still fails the run, at assembly level.
			await AvaloniaUiThread.FlushAsync();
			if (TakeEscapedFailures() is { } late)
			{
				_ = messageBus.QueueMessage(new TestAssemblyCleanupFailure(TestCases, TestAssembly, late));
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

			RunSummary summary;
			try
			{
				summary = await AvaloniaUiThread.RunAsync(() => base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource), AvaloniaUiThread.CollectionTimeout);
			}
			catch (TimeoutException timeout)
			{
				// Reported as a collection failure instead of thrown: a throw here would escape the
				// async void RunTestCases and crash the test host without naming anything.
				TimeoutException named = new(
					$"The avalonia-ui collection did not finish within {AvaloniaUiThread.CollectionTimeout.TotalMinutes} minutes on its dedicated test thread; the thread is hung (CultureGate holder: {CultureGate.Holder ?? "none"}).",
					timeout);
				_ = messageBus.QueueMessage(new TestCollectionCleanupFailure(testCases, testCollection, named));
				return new RunSummary { Total = 1, Failed = 1 };
			}

			// The pump records an exception that escaped a work item (an async void rethrow, a
			// throwing Post callback) instead of dying with it. Items posted by the last test are
			// flushed first, then any recorded exception fails the collection, whatever the order
			// in which the guard test ran.
			await AvaloniaUiThread.FlushAsync();
			if (TakeEscapedFailures() is { } escaped)
			{
				_ = messageBus.QueueMessage(new TestCollectionCleanupFailure(testCases, testCollection, escaped));
				summary.Failed++;
			}

			return summary;
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

	private const int Pending = 0;
	private const int Started = 1;
	private const int Abandoned = 2;

	private static readonly BlockingCollection<Action> Work = [];

	// The Avalonia setup failure text, or null. Only meaningful once SetupRan is true: both are
	// set before any collection runs, and a setup that threw or timed out leaves SetupRan false.
	internal static string? SetupFailure { get; set; }

	internal static bool SetupRan { get; set; }

	// Exceptions that escaped a work item. The pump keeps running; the guard test reports them.
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
	// before EscapedFailures is read. A hung thread is not waited on past FlushTimeout.
	internal static async Task FlushAsync()
	{
		try
		{
			_ = await RunAsync(() => Task.FromResult(true), FlushTimeout).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			Console.Error.WriteLine($"[avalonia-ui test thread] flush did not complete within {FlushTimeout.TotalMinutes} minutes; reading escaped failures without it.");
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
			// claim decides exactly once whether the item runs (Started) or is skipped (Abandoned).
			TaskCompletionSource<Exception?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
			int claim = Pending;
			Work.Add(() =>
			{
				if (Interlocked.CompareExchange(ref claim, Started, Pending) != Pending)
				{
					return;
				}

				try
				{
					d(state);
					_ = done.TrySetResult(null);
				}
				catch (Exception thrown)
				{
					_ = done.TrySetResult(thrown);
				}
			});
			if (!done.Task.Wait(SendTimeout))
			{
				throw Interlocked.CompareExchange(ref claim, Abandoned, Pending) == Pending
					? new TimeoutException(
						$"A callback sent to the avalonia-ui test thread did not start within {SendTimeout.TotalMinutes} minutes, which is longer than any legitimate wait there (CultureGate {CultureGate.Timeout.TotalMinutes} minutes plus a margin); the thread is hung in another work item (CultureGate holder: {CultureGate.Holder ?? "none"}). The callback was abandoned and will not run.")
					: new TimeoutException(
						$"A callback sent to the avalonia-ui test thread started but did not finish within {SendTimeout.TotalMinutes} minutes; the callback itself is hung.");
			}

			if (done.Task.Result is { } failure)
			{
				throw new InvalidOperationException("A callback sent to the avalonia-ui test thread failed.", failure);
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
