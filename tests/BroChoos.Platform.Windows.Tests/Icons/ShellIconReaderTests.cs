using BroChoos.Platform.Windows.Icons;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BroChoos.Platform.Windows.Tests.Icons;

public sealed class ShellIconReaderTests(ITestOutputHelper output)
{
	private readonly ITestOutputHelper _output = output;

	[Fact]
	public void AllZeroAlpha_IsMadeOpaque()
	{
		byte[] bgra = [10, 20, 30, 0, 40, 50, 60, 0];

		_ = IconAlpha.FixAllZeroAlpha(bgra).Should().BeTrue();
		_ = bgra.Should().Equal(10, 20, 30, 255, 40, 50, 60, 255);
	}

	[Fact]
	public void RealAlpha_IsLeftAlone()
	{
		byte[] bgra = [10, 20, 30, 0, 40, 50, 60, 128];

		_ = IconAlpha.FixAllZeroAlpha(bgra).Should().BeFalse();
		_ = bgra.Should().Equal(10, 20, 30, 0, 40, 50, 60, 128);
	}

	[Fact]
	public void FullyTransparentBitmap_StaysTransparent()
	{
		byte[] bgra = new byte[16];

		_ = IconAlpha.FixAllZeroAlpha(bgra).Should().BeFalse("an empty bitmap must not become a black square");
		_ = bgra.Should().OnlyContain(b => b == 0);
	}

	// REVIEW LOW 1: any exception from extraction is one missing icon; the worker keeps serving.
	[Fact]
	public async Task AnyExtractionException_GivesNull_AndTheWorkerSurvives()
	{
		int calls = 0;
		ShellIconReader reader = new(
			(_, size) => ++calls == 1 ? throw new KeyNotFoundException() : new IconPixels(size, size, new byte[size * size * 4], true),
			TimeSpan.FromSeconds(5));
		string exe = Environment.ProcessPath!;

		_ = (await reader.ReadAsync(exe, 16)).Should().BeNull();
		_ = (await reader.ReadAsync(exe, 16)).Should().NotBeNull("the worker thread is still alive");
	}

	// REVIEW LOW 1: a hung extraction times out to null for its caller, and while it is still hung
	// later callers are answered null at once instead of queueing behind it.
	[Fact]
	public async Task HungExtraction_TimesOut_AndLaterRequestsSkipWhileItIsHung()
	{
		TimeSpan timeout = TimeSpan.FromMilliseconds(500);
		using ManualResetEventSlim release = new(false);
		long extractionStarted = 0;
		ShellIconReader reader = new(
			(path, size) =>
			{
				_ = Interlocked.CompareExchange(ref extractionStarted, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
				bool released = release.Wait(TimeSpan.FromSeconds(10));
				return released ? new IconPixels(size, size, new byte[size * size * 4], true) : null;
			},
			timeout);
		string exe = Environment.ProcessPath!;

		_ = (await reader.ReadAsync(exe, 16)).Should().BeNull("the hung request times out");

		// Already gated: the extractor blocks on `release`, which is only set below, so the first
		// request's timeout and the hang are fixed by construction, not raced (checked for the L-2
		// pattern; nothing here depends on a Sleep finishing in time). The caller's budget runs from
		// the moment its continuation observes Started, and "hung" is measured by the worker's own
		// clock from the same start, so wait until the worker is past the hung threshold by that
		// clock: the next request is then deterministically a skip and not a queued wait.
		TimeSpan sinceStart = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref extractionStarted));
		TimeSpan untilHung = timeout - sinceStart + TimeSpan.FromMilliseconds(100);
		if (untilHung > TimeSpan.Zero)
		{
			await Task.Delay(untilHung);
		}

		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		_ = (await reader.ReadAsync(exe, 16)).Should().BeNull();
		_ = watch.Elapsed.Should().BeLessThan(timeout / 2, "a request while the worker is hung is skipped at once, not left to time out");

		// The worker clears its busy mark only after the extractor returns, so poll instead of
		// guessing a delay: the hang has ended once a request is served again.
		release.Set();
		IconPixels? served = null;
		System.Diagnostics.Stopwatch recovery = System.Diagnostics.Stopwatch.StartNew();
		while (served is null && recovery.Elapsed < TimeSpan.FromSeconds(10))
		{
			served = await reader.ReadAsync(exe, 16);
			if (served is null)
			{
				await Task.Delay(20);
			}
		}

		_ = served.Should().NotBeNull("once the hang ends the worker serves again");
	}

	// REVIEW C3: the budget starts when a request's extraction STARTS. Two requests, each taking
	// 70 % of the budget, both succeed: the second waited ~0.7 budget in the queue but its own
	// extraction stayed inside the budget. Counted from the enqueue, the second would have needed
	// ~1.4 budgets and timed out, which is the regression this pins.
	//
	// CI FLAKE (run 41083690719, test job, attempt 1): the old form slept 280 ms inside the
	// extractor against a 400 ms budget. On a loaded 2-vCPU runner the 120 ms margin was eaten by
	// Sleep overshoot plus the continuation that starts the per-request WaitAsync, so either the
	// first request's own budget or the worker's "hung" check (busy > budget) fired.
	//
	// NOW GATED ON EVENTS. Each extraction signals that it started and then blocks until it is
	// released, so the order (first runs, second queues behind it, second runs) is fixed by
	// construction, and each extraction is "slow but alive" for exactly the hold the test chooses.
	// The ratio is kept (hold = 70 % of the budget), and the budget is the product's own 5 s
	// default, so the safety margin per extraction is 1.5 s instead of 120 ms.
	//
	// N-1: THE HOLD IS RELEASED OFF THE THREAD POOL, AT A MONOTONIC DEADLINE. The extractor stamps
	// the Stopwatch time at which it started, on the worker thread, before it signals. A dedicated
	// thread (not a pool worker) waits for that signal and releases the extraction at exactly
	// start + hold by that stamp, so a late pool continuation can no longer stretch the hold into
	// the budget's margin.
	//
	// L-1: THE RELEASER THREAD NEVER OUTLIVES THE SEMAPHORES. If an assertion or an await fails,
	// the method leaves early while the releaser may still be waiting on `started` or holding, and
	// the `using` declarations would then dispose the semaphores under it; an exception escaping a
	// raw Thread crashes the test host. So the body runs in try/finally: the finally cancels `stop`
	// (which ends every wait and hold in the releaser and the extractor's wait on `release`), joins
	// the releaser with a bound, and only then do the semaphores go out of scope. The releaser also
	// catches ObjectDisposedException and OperationCanceledException itself, so even a releaser
	// that is still alive after the bounded join cannot throw out of its thread.
	[Fact]
	public async Task QueuedBehindSlowButAliveWork_IsNotTimedOut()
	{
		TimeSpan budget = ShellIconReader.DefaultTimeout;
		TimeSpan hold = budget * 0.7;
		using CancellationTokenSource stop = new();
		using SemaphoreSlim started = new(0);
		using SemaphoreSlim release = new(0);
		long[] startedAt = new long[2];
		int extractions = 0;
		CancellationToken stopToken = stop.Token;
		ShellIconReader reader = new(
			(path, size) =>
			{
				_ = path;
				int index = Interlocked.Increment(ref extractions) - 1;
				if (index < startedAt.Length)
				{
					Volatile.Write(ref startedAt[index], System.Diagnostics.Stopwatch.GetTimestamp());
				}

				_ = started.Release();

				// A cancelled or disposed wait throws, and the reader turns any extractor exception
				// into a null icon, so a stopped test only ends the extraction early.
				bool released = release.Wait(TimeSpan.FromSeconds(30), stopToken);
				return released ? new IconPixels(size, size, new byte[size * size * 4], true) : null;
			},
			budget);
		string exe = Environment.ProcessPath!;

		bool[] holderSawStart = new bool[2];
		TimeSpan[] heldFor = new TimeSpan[2];
		Thread holder = new(() =>
		{
			try
			{
				for (int i = 0; i < holderSawStart.Length; i++)
				{
					holderSawStart[i] = started.Wait(TimeSpan.FromSeconds(30), stopToken);
					if (!holderSawStart[i])
					{
						return;
					}

					long since = Volatile.Read(ref startedAt[i]);
					HoldUntil(since, hold, stopToken);
					heldFor[i] = System.Diagnostics.Stopwatch.GetElapsedTime(since);
					_ = release.Release();
				}
			}
			catch (Exception stopped) when (stopped is OperationCanceledException or ObjectDisposedException)
			{
				// The test has already failed or ended; nothing may escape this raw thread.
			}
		})
		{
			IsBackground = true,
			Name = "ShellIconReaderTests hold releaser",
		};

		bool holderStarted = false;
		try
		{
			System.Diagnostics.Stopwatch sinceSecondEnqueued = System.Diagnostics.Stopwatch.StartNew();
			Task<IconPixels?> first = reader.ReadAsync(exe, 16);
			Task<IconPixels?> second = reader.ReadAsync(exe, 16);
			holder.Start();
			holderStarted = true;

			_ = (await first).Should().NotBeNull();
			IconPixels? secondResult = await second;
			TimeSpan secondTotal = sinceSecondEnqueued.Elapsed;
			_ = holder.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the releaser thread ends after the second hold");

			_ = holderSawStart.Should().AllSatisfy(saw => saw.Should().BeTrue("the worker must start each queued extraction"));
			_output.WriteLine($"HOLD_PROBE hold={hold.TotalMilliseconds}ms heldFor=[{heldFor[0].TotalMilliseconds:F0}, {heldFor[1].TotalMilliseconds:F0}]ms secondTotal={secondTotal.TotalMilliseconds:F0}ms budget={budget.TotalMilliseconds}ms");
			_ = secondResult.Should().NotBeNull("waiting in the queue does not count against its own budget");

			// Guards against a vacuous pass: the second request really did take longer than one
			// budget from its enqueue, so a budget counted from the enqueue would have failed it.
			_ = secondTotal.Should().BeGreaterThan(budget, "the second request waited a full hold in the queue and then ran for another, ~1.4 budgets in all");
		}
		finally
		{
			// Stop the releaser BEFORE the semaphores are disposed at the end of this method.
			await stop.CancelAsync();
			if (holderStarted)
			{
				_ = holder.Join(TimeSpan.FromSeconds(10));
			}
		}
	}

	// Blocks the calling (dedicated) thread until `hold` has passed since the Stopwatch timestamp
	// `since`. Sleeps in short steps and re-reads the monotonic clock, so an oversleep is bounded by
	// one step plus OS scheduling, not by the thread pool. Throws OperationCanceledException once
	// `stop` is cancelled.
	private static void HoldUntil(long since, TimeSpan hold, CancellationToken stop)
	{
		while (true)
		{
			stop.ThrowIfCancellationRequested();
			TimeSpan left = hold - System.Diagnostics.Stopwatch.GetElapsedTime(since);
			if (left <= TimeSpan.Zero)
			{
				return;
			}

			_ = stop.WaitHandle.WaitOne(left > TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20) : left);
		}
	}

	[Fact]
	public void PremultipliedCheck_FailsOnAnyChannelAboveItsAlpha()
	{
		_ = IconAlpha.IsConsistentWithPremultiplied([10, 20, 30, 40, 0, 0, 0, 0]).Should().BeTrue();
		_ = IconAlpha.IsConsistentWithPremultiplied([10, 200, 30, 40]).Should().BeFalse();
	}

	[Fact]
	public async Task MissingOrRelativePath_GivesNoKeyAndNoIcon()
	{
		ShellIconReader reader = new();

		_ = reader.TryGetCacheKey(@"C:\Definitely\Not\Here\browser.exe", 48).Should().BeNull();
		_ = reader.TryGetCacheKey("msedge.exe", 48).Should().BeNull();
		_ = (await reader.ReadAsync("msedge.exe", 48)).Should().BeNull();
	}

	// LIVE PROBE on a real installed browser (skipped when none is installed). It pins the exact
	// pixel size and records whether the shell bitmap is premultiplied - the open question from
	// the icon research - so the loader's AlphaFormat choice rests on a measurement.
	[SkippableTheory]
	[InlineData(48)]
	[InlineData(72)]
	public async Task RealBrowserIcon_IsReadAtTheExactPixelSize(int sizePx)
	{
		string? exe = FindInstalledBrowser();
		Skip.If(exe is null, "no msedge.exe, chrome.exe or firefox.exe under Program Files");

		ShellIconReader reader = new();
		string? key = reader.TryGetCacheKey(exe!, sizePx);
		IconPixels? pixels = await reader.ReadAsync(exe!, sizePx);

		_ = key.Should().NotBeNull();
		_ = pixels.Should().NotBeNull();
		_ = pixels!.Width.Should().Be(sizePx);
		_ = pixels.Height.Should().Be(sizePx);

		byte[] data = pixels.Bgra.ToArray();
		int transparent = 0;
		int partial = 0;
		for (int i = 3; i < data.Length; i += 4)
		{
			transparent += data[i] == 0 ? 1 : 0;
			partial += data[i] is > 0 and < 255 ? 1 : 0;
		}

		int aboveAlpha = IconAlpha.CountChannelAboveAlpha(data);
		_output.WriteLine($"ICON_PROBE browser={Path.GetFileName(exe)} size={sizePx} premultiplied={pixels.IsPremultiplied} channelAboveAlpha={aboveAlpha} transparent={transparent} partial={partial} total={data.Length / 4}");

		_ = transparent.Should().BeGreaterThan(0, "a browser icon has transparent corners, so alpha was kept");
		_ = (transparent + partial).Should().BeLessThan(data.Length / 4, "the icon is not blank");
	}

	private static string? FindInstalledBrowser()
	{
		string[] roots =
		[
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
		];
		string[] relative =
		[
			@"Microsoft\Edge\Application\msedge.exe",
			@"Google\Chrome\Application\chrome.exe",
			@"Mozilla Firefox\firefox.exe",
		];

		return roots.Where(root => !string.IsNullOrEmpty(root))
			.SelectMany(root => relative.Select(path => Path.Combine(root, path)))
			.FirstOrDefault(File.Exists);
	}
}
