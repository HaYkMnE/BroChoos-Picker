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
	// NOW GATED ON EVENTS. Each extraction signals that it started and then blocks until the test
	// releases it, so the order (first runs, second queues behind it, second runs) is fixed by
	// construction, and each extraction is "slow but alive" for exactly the hold the test chooses.
	// The ratio is kept (hold = 70 % of the budget), and the budget is the product's own 5 s
	// default, so the safety margin per extraction is 1.5 s instead of 120 ms.
	[Fact]
	public async Task QueuedBehindSlowButAliveWork_IsNotTimedOut()
	{
		TimeSpan budget = ShellIconReader.DefaultTimeout;
		TimeSpan hold = budget * 0.7;
		using SemaphoreSlim started = new(0);
		using SemaphoreSlim release = new(0);
		ShellIconReader reader = new(
			(path, size) =>
			{
				_ = path;
				_ = started.Release();
				bool released = release.Wait(TimeSpan.FromSeconds(30));
				return released ? new IconPixels(size, size, new byte[size * size * 4], true) : null;
			},
			budget);
		string exe = Environment.ProcessPath!;

		System.Diagnostics.Stopwatch sinceSecondEnqueued = System.Diagnostics.Stopwatch.StartNew();
		Task<IconPixels?> first = reader.ReadAsync(exe, 16);
		Task<IconPixels?> second = reader.ReadAsync(exe, 16);

		await HoldOneExtractionAsync(started, release, hold);
		_ = (await first).Should().NotBeNull();

		await HoldOneExtractionAsync(started, release, hold);
		IconPixels? secondResult = await second;
		TimeSpan secondTotal = sinceSecondEnqueued.Elapsed;

		_ = secondResult.Should().NotBeNull("waiting in the queue does not count against its own budget");

		// Guards against a vacuous pass: the second request really did take longer than one
		// budget from its enqueue, so a budget counted from the enqueue would have failed it.
		_ = secondTotal.Should().BeGreaterThan(budget, "the second request waited a full hold in the queue and then ran for another, ~1.4 budgets in all");
	}

	// Waits (bounded) for the next extraction to start, keeps it running for `hold`, then lets it
	// finish. The hold is a test-side delay; the extractor itself never sleeps.
	private static async Task HoldOneExtractionAsync(SemaphoreSlim started, SemaphoreSlim release, TimeSpan hold)
	{
		_ = (await started.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue("the worker must start the next queued extraction");
		await Task.Delay(hold);
		_ = release.Release();
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
