using BroChoos.App.Logging;
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
		catch (IOException)
		{
			// Best-effort temp cleanup.
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Flush_OnAStarvedThreadPool_StillCompletesWithinTheOriginalBudget()
	{
		ProblemLogWriter writer = new(new ProblemLogOptions(_logPath)) { BeforeAppend = static () => Thread.Sleep(300) };
		ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
		using ManualResetEventSlim release = new(false);
		using ManualResetEventSlim probe = new(false);
		int blockers = ThreadPool.ThreadCount + 256;
		using CountdownEvent exited = new(blockers);
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
			}

			// The starvation is real: a work item queued behind the blockers does not run.
			_ = ThreadPool.UnsafeQueueUserWorkItem(static done => done.Set(), probe, preferLocal: false);
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
			_ = exited.Wait(TimeSpan.FromSeconds(60));
			_ = probe.Wait(TimeSpan.FromSeconds(60));
		}

		_ = File.ReadAllLines(_logPath).Should().HaveCount(2).And.Contain(l => l.Contains("\"event\":\"first\"", StringComparison.Ordinal)).And.Contain(l => l.Contains("\"event\":\"second\"", StringComparison.Ordinal));
	}
}
