using BroChoos.Platform.Windows.Icons;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Icons;

public sealed class IconDiskCacheTests : IDisposable
{
	private const string SecretPath = @"C:\Users\SecretUser\Chromium\chrome.exe";

	private readonly string _dir = Path.Combine(Path.GetTempPath(), "brochoos-icon-cache-" + Guid.NewGuid().ToString("N")[..12]);

	public void Dispose()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, recursive: true);
		}
	}

	[Fact]
	public async Task Miss_ReadsTheShellOnce_ThenHitsTheFile()
	{
		CountingSource shell = new();
		IconDiskCache cache = new(shell, _dir);

		IconPixels? first = await cache.ReadAsync(SecretPath, 4);
		IconPixels? second = await new IconDiskCache(shell, _dir).ReadAsync(SecretPath, 4);

		_ = shell.Reads.Should().Be(1, "the second, fresh cache instance is served from disk");
		_ = second.Should().NotBeNull();
		_ = second!.Bgra.ToArray().Should().Equal(first!.Bgra.ToArray());
		_ = second.IsPremultiplied.Should().Be(first.IsPremultiplied);
	}

	[Fact]
	public async Task StaleLastWriteTime_IsAMiss()
	{
		CountingSource shell = new();
		IconDiskCache cache = new(shell, _dir);
		_ = await cache.ReadAsync(SecretPath, 4);

		shell.Ticks++;
		_ = await cache.ReadAsync(SecretPath, 4);

		_ = shell.Reads.Should().Be(2, "an updated browser has a new key, so its old icon is not served");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(5)]
	[InlineData(12)]
	[InlineData(70)]
	public async Task CorruptOrTruncatedFile_IsIgnoredAndRewritten(int keepBytes)
	{
		CountingSource shell = new();
		IconDiskCache cache = new(shell, _dir);
		_ = await cache.ReadAsync(SecretPath, 4);
		string file = Directory.GetFiles(_dir).Single();
		byte[] full = File.ReadAllBytes(file);
		File.WriteAllBytes(file, full[..Math.Min(keepBytes, full.Length - 1)]);

		IconPixels? again = await cache.ReadAsync(SecretPath, 4);

		_ = again.Should().NotBeNull();
		_ = shell.Reads.Should().Be(2);
		_ = File.ReadAllBytes(file).Should().Equal(full, "the bad file was replaced");
	}

	[Fact]
	public void WrongMagicOrSize_DecodesToNull()
	{
		byte[] good = IconDiskCache.Encode(new IconPixels(4, 4, new byte[64], true));
		byte[] badMagic = [.. good];
		badMagic[0] ^= 0xFF;

		_ = IconDiskCache.Decode(good, 4).Should().NotBeNull();
		_ = IconDiskCache.Decode(badMagic, 4).Should().BeNull();
		_ = IconDiskCache.Decode(good, 8).Should().BeNull("a 4 px icon is not served for an 8 px request");
	}

	[Fact]
	public async Task OversizedFile_IsNotReadAndIsRewritten()
	{
		CountingSource shell = new();
		IconDiskCache cache = new(shell, _dir);
		_ = await cache.ReadAsync(SecretPath, 4);
		string file = Directory.GetFiles(_dir, "*.icon").Single();
		File.WriteAllBytes(file, new byte[IconDiskCache.MaxFileBytes + 1]);

		_ = (await cache.ReadAsync(SecretPath, 4)).Should().NotBeNull();
		_ = shell.Reads.Should().Be(2, "a file over the 256 px maximum is a miss");
		_ = new FileInfo(file).Length.Should().BeLessThan(IconDiskCache.MaxFileBytes);
	}

	[Fact]
	public async Task Trim_KeepsAFreshTempFile_AndRemovesAnOldOne()
	{
		_ = Directory.CreateDirectory(_dir);
		string fresh = Path.Combine(_dir, "a.icon.1.tmp");
		string old = Path.Combine(_dir, "b.icon.2.tmp");
		File.WriteAllBytes(fresh, [1]);
		File.WriteAllBytes(old, [1]);
		File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));

		_ = await new IconDiskCache(new CountingSource(), _dir).ReadAsync(SecretPath, 4);

		_ = File.Exists(fresh).Should().BeTrue("a temp file under a minute old may be another run's write");
		_ = File.Exists(old).Should().BeFalse();
	}

	[Fact]
	public async Task Cap_KeepsAtMostMaxFiles_RemovingTheOldest()
	{
		CountingSource shell = new();
		IconDiskCache cache = new(shell, _dir);
		// Each file gets a KNOWN, increasing write time in the past, set by its own name. (Picking
		// "the newest file" by time was the flake: files already moved forward were newer than the
		// one just written, so which file was evicted depended on timing.) A file written NOW is
		// always newer than every earlier one, so the cap must evict b0 first.
		DateTime start = DateTime.UtcNow.AddDays(-1);
		for (int i = 0; i < IconDiskCache.MaxFiles + 5; i++)
		{
			string exe = $@"C:\Browsers\b{i}.exe";
			_ = await cache.ReadAsync(exe, 4);
			string written = Path.Combine(_dir, IconDiskCache.FileNameFor(shell.KeyFor(exe, 4)));
			if (File.Exists(written))
			{
				File.SetLastWriteTimeUtc(written, start.AddMinutes(i));
			}
		}

		string[] left = Directory.GetFiles(_dir, "*.icon");
		_ = left.Should().HaveCount(IconDiskCache.MaxFiles);
		_ = left.Should().Contain(Path.Combine(_dir, IconDiskCache.FileNameFor(shell.KeyFor($@"C:\Browsers\b{IconDiskCache.MaxFiles + 4}.exe", 4))), "the newest stays");
		_ = left.Should().NotContain(Path.Combine(_dir, IconDiskCache.FileNameFor(shell.KeyFor(@"C:\Browsers\b0.exe", 4))), "the oldest went first");
		_ = Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
	}

	[Fact]
	public async Task FileNames_NeverContainThePath()
	{
		IconDiskCache cache = new(new CountingSource(), _dir);
		_ = await cache.ReadAsync(SecretPath, 4);

		string name = Path.GetFileName(Directory.GetFiles(_dir).Single());
		_ = name.Should().MatchRegex("^[0-9A-F]{64}\\.icon$");
		_ = name.Should().NotContainEquivalentOf("SecretUser").And.NotContainEquivalentOf("chrome");
	}

	[Fact]
	public void ForDataRoot_UsesTheCacheIconsFolderUnderTheDataRoot()
	{
		IconDiskCache cache = IconDiskCache.ForDataRoot(new CountingSource(), @"D:\Portable\BroChoosConfig");

		_ = cache.Should().NotBeNull();
		_ = typeof(IconDiskCache).GetField("_directory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
			.GetValue(cache).Should().Be(@"D:\Portable\BroChoosConfig\cache\icons");
	}

	private sealed class CountingSource : IProgramIconSource
	{
		public int Reads { get; private set; }

		public long Ticks { get; set; } = 1;

		public string KeyFor(string exePath, int sizePx)
		{
			return $"{exePath.ToUpperInvariant()}|{Ticks}|{sizePx}";
		}

		public string? TryGetCacheKey(string exePath, int sizePx)
		{
			return KeyFor(exePath, sizePx);
		}

		public Task<IconPixels?> ReadAsync(string exePath, int sizePx, CancellationToken cancellationToken = default)
		{
			Reads++;
			byte[] bgra = [.. Enumerable.Range(0, sizePx * sizePx * 4).Select(i => (byte)(i * 7))];
			return Task.FromResult<IconPixels?>(new IconPixels(sizePx, sizePx, bgra, false));
		}
	}
}
