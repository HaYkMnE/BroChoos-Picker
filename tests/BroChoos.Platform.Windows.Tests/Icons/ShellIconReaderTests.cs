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

		// The caller's timeout runs from ENQUEUE, but "hung" is measured from when the worker STARTED
		// the extraction, which is a little later. Wait until the worker is past the hung threshold by
		// its own clock, so the next request is deterministically a skip and not a queued wait.
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
	// extraction stayed inside the budget.
	[Fact]
	public async Task QueuedBehindSlowButAliveWork_IsNotTimedOut()
	{
		TimeSpan budget = TimeSpan.FromMilliseconds(400);
		ShellIconReader reader = new(
			(_, size) =>
			{
				Thread.Sleep(280);
				return new IconPixels(size, size, new byte[size * size * 4], true);
			},
			budget);
		string exe = Environment.ProcessPath!;

		Task<IconPixels?> first = reader.ReadAsync(exe, 16);
		Task<IconPixels?> second = reader.ReadAsync(exe, 16);

		_ = (await first).Should().NotBeNull();
		_ = (await second).Should().NotBeNull("waiting in the queue does not count against its own budget");
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
