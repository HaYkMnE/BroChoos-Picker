using System.Collections.Concurrent;
using Avalonia.Media;
using BroChoos.App.Icons;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Routing;
using BroChoos.Platform.Windows.Icons;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.App.Tests;

// Phase 2 of the picker layout spec: program icons are loaded in the background, cached, never
// block the caller, fall back to the letter plate, and never put the executable path in a log.
public sealed class BrowserIconLoaderTests
{
	private const string SecretPath = @"C:\Users\SecretUser\AppData\Local\Chromium\chrome.exe";

	[Fact]
	public async Task SameProgramAndSize_IsReadOnce_AndADifferentSizeIsReadAgain()
	{
		FakeSource source = new();
		BrowserIconLoader loader = new(source, new RecordingLogger(), _ => new FakeImage());

		IImage? first = await loader.LoadAsync(SecretPath, 48);
		IImage? second = await loader.LoadAsync(SecretPath, 48);
		IImage? larger = await loader.LoadAsync(SecretPath, 72);

		_ = first.Should().NotBeNull();
		_ = second.Should().BeSameAs(first, "the cache hands back the same bitmap");
		_ = larger.Should().NotBeNull().And.NotBeSameAs(first);
		_ = source.Reads.Should().Be(2, "one read per (program, last-write, size) key");
	}

	[Fact]
	public async Task ConcurrentRequestsForOneKey_ShareOneRead()
	{
		FakeSource source = new() { Gate = new TaskCompletionSource() };
		BrowserIconLoader loader = new(source, new RecordingLogger(), _ => new FakeImage());

		Task<IImage?>[] loads = [.. Enumerable.Range(0, 5).Select(_ => loader.LoadAsync(SecretPath, 48))];
		await Task.Delay(50);
		source.Gate.SetResult();
		IImage?[] results = await Task.WhenAll(loads);

		_ = results.Distinct().Should().ContainSingle();
		_ = source.Reads.Should().Be(1);
	}

	[Fact]
	public async Task UnreadableIcon_FallsBackToNull_AndIsNotRetried()
	{
		FakeSource source = new() { ReturnNull = true };
		BrowserIconLoader loader = new(source, new RecordingLogger(), _ => new FakeImage());

		_ = (await loader.LoadAsync(SecretPath, 48)).Should().BeNull();
		_ = (await loader.LoadAsync(SecretPath, 48)).Should().BeNull();
		_ = source.Reads.Should().Be(1, "a failed read is cached so every tile does not retry it");
	}

	[Fact]
	public async Task ThrowingSourceOrConverter_FallsBackToNull()
	{
		BrowserIconLoader throwingRead = new(new FakeSource { Throw = true }, new RecordingLogger(), _ => new FakeImage());
		BrowserIconLoader throwingConvert = new(new FakeSource(), new RecordingLogger(), _ => throw new InvalidOperationException(SecretPath));
		BrowserIconLoader throwingKey = new(new FakeSource { ThrowOnKey = true }, new RecordingLogger(), _ => new FakeImage());

		_ = (await throwingRead.LoadAsync(SecretPath, 48)).Should().BeNull();
		_ = (await throwingConvert.LoadAsync(SecretPath, 48)).Should().BeNull();
		_ = (await throwingKey.LoadAsync(SecretPath, 48)).Should().BeNull();
	}

	[Fact]
	public async Task NoCacheKey_MeansNoRead()
	{
		FakeSource source = new() { NoKey = true };
		BrowserIconLoader loader = new(source, new RecordingLogger(), _ => new FakeImage());

		_ = (await loader.LoadAsync(SecretPath, 48)).Should().BeNull();
		_ = source.Reads.Should().Be(0);
	}

	[Fact]
	public async Task NoFailurePath_WritesTheExecutablePathIntoTheLog()
	{
		RecordingLogger logger = new();
		BrowserIconLoader[] loaders =
		[
			new(new FakeSource { ReturnNull = true }, logger, _ => new FakeImage()),
			new(new FakeSource { Throw = true }, logger, _ => new FakeImage()),
			new(new FakeSource { ThrowOnKey = true }, logger, _ => new FakeImage()),
			new(new FakeSource { NoKey = true }, logger, _ => new FakeImage()),
			new(new FakeSource(), logger, _ => throw new InvalidOperationException(SecretPath)),
		];

		foreach (BrowserIconLoader loader in loaders)
		{
			_ = await loader.LoadAsync(SecretPath, 48);
		}

		_ = logger.Entries.Should().NotBeEmpty("each failure leaves a constant diagnostic line");
		_ = logger.Entries.Should().OnlyContain(entry =>
			!entry.Message.Contains("SecretUser", StringComparison.OrdinalIgnoreCase)
			&& !entry.Message.Contains("chrome.exe", StringComparison.OrdinalIgnoreCase)
			&& !entry.HadException);
	}

	[Fact]
	public async Task LoadAsync_ReturnsAtOnce_EvenWhenTheFileSystemIsSlow()
	{
		using ManualResetEventSlim release = new(false);
		FakeSource source = new() { KeyBlocker = release };
		BrowserIconLoader loader = new(source, new RecordingLogger(), _ => new FakeImage());

		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
		Task<IImage?> load = loader.LoadAsync(SecretPath, 48);
		watch.Stop();

		_ = watch.ElapsedMilliseconds.Should().BeLessThan(500, "the first frame must never wait on an icon");
		_ = load.IsCompleted.Should().BeFalse();

		release.Set();
		IImage? result = await load.WaitAsync(TimeSpan.FromSeconds(5));
		_ = result.Should().NotBeNull();
	}

	[Theory]
	[InlineData(1.0, 48)]
	[InlineData(1.25, 60)]
	[InlineData(1.5, 72)]
	[InlineData(2.0, 96)]
	[InlineData(double.NaN, 48)]
	[InlineData(0, 48)]
	public void PixelSize_IsFortyEightLogicalPixelsAtTheMonitorScale(double scale, int expected)
	{
		_ = BrowserIconLoader.PixelSizeFor(scale).Should().Be(expected);
	}

	[Fact]
	public void ChoiceCard_StartsWithTheLetterPlate_AndRaisesHasIconWhenTheIconArrives()
	{
		ChoiceCardViewModel card = new(
			new BrowserCandidate("chrome-stable", "Google Chrome", SecretPath, "stable", "known-path"),
			1,
			1,
			RankingReasonCode.AllChoices,
			null);
		List<string?> raised = [];
		card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		_ = card.HasIcon.Should().BeFalse();
		_ = card.Initial.Should().Be("G");

		card.Icon = new FakeImage();

		_ = card.HasIcon.Should().BeTrue();
		_ = raised.Should().Contain([nameof(ChoiceCardViewModel.Icon), nameof(ChoiceCardViewModel.HasIcon)]);
	}

	private sealed class FakeSource : IProgramIconSource
	{
		private int _reads;

		public int Reads => _reads;

		public bool ReturnNull { get; init; }

		public bool Throw { get; init; }

		public bool ThrowOnKey { get; init; }

		public bool NoKey { get; init; }

		public TaskCompletionSource? Gate { get; init; }

		public ManualResetEventSlim? KeyBlocker { get; init; }

		public string? TryGetCacheKey(string exePath, int sizePx)
		{
			_ = KeyBlocker?.Wait(TimeSpan.FromSeconds(10));
			return ThrowOnKey
				? throw new IOException(exePath)
				: NoKey ? null : $"{exePath}|1|{sizePx}";
		}

		public async Task<IconPixels?> ReadAsync(string exePath, int sizePx, CancellationToken cancellationToken = default)
		{
			_ = Interlocked.Increment(ref _reads);
			if (Gate is not null)
			{
				await Gate.Task.ConfigureAwait(false);
			}

			return Throw
				? throw new InvalidOperationException(exePath)
				: ReturnNull ? null : new IconPixels(sizePx, sizePx, new byte[sizePx * sizePx * 4], true);
		}
	}

	private sealed class FakeImage : IImage
	{
		public Avalonia.Size Size => new(48, 48);

		public void Draw(DrawingContext context, Avalonia.Rect sourceRect, Avalonia.Rect destRect)
		{
		}
	}

	private sealed record LogEntry(string Message, bool HadException);

	private sealed class RecordingLogger : ILogger<BrowserIconLoader>
	{
		private readonly ConcurrentQueue<LogEntry> _entries = new();

		public IReadOnlyCollection<LogEntry> Entries => _entries;

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			_entries.Enqueue(new LogEntry(formatter(state, exception), exception is not null));
		}
	}
}
