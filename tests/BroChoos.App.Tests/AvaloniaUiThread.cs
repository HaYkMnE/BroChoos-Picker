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
			_ = await AvaloniaUiThread.RunAsync(() => Task.FromResult(PickerXamlProbe.Run()), AvaloniaUiThread.SetupTimeout);
			return await base.RunTestCollectionsAsync(messageBus, cancellationTokenSource);
		}

		protected override Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
		{
			return string.Equals(testCollection.CollectionDefinition?.Name, typeof(AvaloniaUiCollection).FullName, StringComparison.Ordinal)
				? AvaloniaUiThread.RunAsync(() => base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource), AvaloniaUiThread.CollectionTimeout)
				: base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource);
		}
	}
}

internal static class AvaloniaUiThread
{
	// The whole collection takes well under a minute locally; the CI test job allows 30.
	internal static readonly TimeSpan CollectionTimeout = TimeSpan.FromMinutes(15);

	// Setup plus the first PickerWindow takes about a second locally.
	internal static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(2);

	private static readonly BlockingCollection<Action> Work = [];

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
			item();
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

			using ManualResetEventSlim sent = new();
			Exception? failure = null;
			Work.Add(() =>
			{
				try
				{
					d(state);
				}
				catch (Exception thrown)
				{
					failure = thrown;
				}
				finally
				{
					sent.Set();
				}
			});
			sent.Wait();
			if (failure is not null)
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
