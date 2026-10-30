using BroChoos.App.Logging;
using BroChoos.Platform.Windows.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.App.Tests;

// The test below starves the process-wide thread pool on purpose, so it must not run next to
// other tests: xUnit runs a DisableParallelization collection on its own, after the parallel ones.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolStarvationCollection
{
	public const string Name = "thread-pool-starvation";
}

// Regression for the CI flake of ProblemLogTests.Write_ReturnsWithoutWaitingForDisk (run
// 41027998726): the drain was started with Task.Run, so on a starved pool (a loaded 2-vCPU
// runner, the whole solution testing in parallel) it began seconds late and Flush(5 s) returned
// false with the work not even started. The drain must not depend on pool availability.
[Collection(ThreadPoolStarvationCollection.Name)]
public sealed class ProblemLogStarvationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "brochoos-problemlog-starvation-tests", Guid.NewGuid().ToString("N"));
	private readonly string _logPath;

	public ProblemLogStarvationTests()
	{
		_ = Directory.CreateDirectory(_root);
		_logPath = Path.Combine(_root, "logs", "problems.log");
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best-effort temp cleanup: a read-only or still-open file must not fail the test.
		}

		// The fixed parent goes once the last per-test folder is gone; best effort, never throws.
		TestTempCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(_root)!);

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Flush_OnAStarvedThreadPool_StillCompletesWithinTheOriginalBudget()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath)) { BeforeAppend = static () => Thread.Sleep(300) };
		ThreadPool.GetMinThreads(out int minWorkers, out int minIo);

		// Disposed by hand in the finally, and ONLY once every queued work item is known to have
		// finished with them: a blocked item still touches `release` and `exited`, and the probe
		// item touches `probe`, so disposing them under a still-running item would race it.
		ManualResetEventSlim release = new(false);
		ManualResetEventSlim probe = new(false);
		int blockers = ThreadPool.ThreadCount + 256;
		CountdownEvent exited = new(blockers);
		bool blockersExited = false;
		bool probeRan = false;

		// How many blockers, and whether the probe, were ACTUALLY queued. If queuing throws
		// midway, the finally waits only for those instead of for items that never existed.
		int queued = 0;
		bool probeQueued = false;
		try
		{
			// Min threads at the floor (the processor count), then more blocking work items than
			// the pool has threads: every worker, idle or newly injected, picks one up and blocks.
			_ = ThreadPool.SetMinThreads(Environment.ProcessorCount, minIo);
			for (int i = 0; i < blockers; i++)
			{
				_ = ThreadPool.UnsafeQueueUserWorkItem(
					static state =>
					{
						_ = state.Release.Wait(TimeSpan.FromSeconds(30));
						_ = state.Exited.Signal();
					},
					(Release: release, Exited: exited),
					preferLocal: false);
				queued++;
			}

			// The starvation is real: a work item queued behind the blockers does not run.
			_ = ThreadPool.UnsafeQueueUserWorkItem(static done => done.Set(), probe, preferLocal: false);
			probeQueued = true;
			_ = probe.Wait(TimeSpan.FromMilliseconds(300)).Should().BeFalse("the thread pool must be starved for this test to mean anything");

			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			_ = writer.Write(new ProblemLogEntry(LogLevel.Warning, "test", "first", null, []));
			_ = writer.Write(new ProblemLogEntry(LogLevel.Warning, "test", "second", null, []));
			watch.Stop();

			_ = watch.ElapsedMilliseconds.Should().BeLessThan(150);
			_ = writer.Flush(TimeSpan.FromSeconds(5)).Should().BeTrue("the drain must not wait for a thread-pool thread");
			_ = probe.IsSet.Should().BeFalse("the pool was still starved while the drain ran");
		}
		finally
		{
			// Every queued item must have run before the events are disposed.
			release.Set();
			_ = ThreadPool.SetMinThreads(minWorkers, minIo);
			if (queued < blockers)
			{
				// Count off the blockers that were never queued, so the wait covers only real ones.
				_ = exited.Signal(blockers - queued);
			}

			blockersExited = exited.Wait(TimeSpan.FromSeconds(60));
			probeRan = !probeQueued || probe.Wait(TimeSpan.FromSeconds(60));
			if (blockersExited && probeRan)
			{
				release.Dispose();
				probe.Dispose();
				exited.Dispose();
			}
		}

		// Asserted after the finally, so a failure inside the try is not masked by these.
		_ = blockersExited.Should().BeTrue("every blocking work item must finish once released, or the pool was left starved for the next test");
		_ = probeRan.Should().BeTrue("the probe work item must run once the pool recovers");
		_ = File.ReadAllLines(_logPath).Should().HaveCount(2).And.Contain(l => l.Contains("\"event\":\"first\"", StringComparison.Ordinal)).And.Contain(l => l.Contains("\"event\":\"second\"", StringComparison.Ordinal));
	}
}
