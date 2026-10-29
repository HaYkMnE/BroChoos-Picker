using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BroChoos.Platform.Windows.Icons;

// ON-DISK ICON CACHE (owner decision). Wraps the shell reader behind the same seam the App
// loader already uses, so a cached icon reaches the tile without a shell call and the loader
// does not change.
//
//   Where   <data root>\cache\icons\ - the same root every other BroChoos file uses
//           (%LocalAppData%\BroChoos installed, <exe>\BroChoosConfig portable), so portable
//           mode keeps its icons beside its other data.
//   Name    SHA-256 of the cache key (normalized path + last-write time + pixel size), hex. The
//           file name never contains the path, and a reinstalled or updated browser gets a new
//           key, so a stale icon is never served (the old file ages out by the cap).
//   Format  "BCIC" magic, version 1, width, height, premultiplied flag, then width*height*4 BGRA.
//           Anything else - a short file, a wrong magic or version, an odd size - is ignored and
//           replaced by a fresh shell read.
//   Write   temp file in the same folder, then File.Move(overwrite) - a reader never sees half a
//           file. Write failures are swallowed: the cache is an optimisation, never a requirement.
//   Cap     MaxFiles files and MaxBytes bytes; the least recently written files are removed first.
//
// Never logs. Runs only on the loader's background thread (BrowserIconLoader.LoadAsync is a
// Task.Run), never on the UI thread.
public sealed class IconDiskCache(IProgramIconSource inner, string directory) : IProgramIconSource
{
	internal const int MaxFiles = 64;
	internal const long MaxBytes = 8L * 1024 * 1024;

	private const uint Magic = 0x43494342; // "BCIC" little-endian
	private const ushort Version = 1;
	private const int HeaderBytes = 4 + 2 + 2 + 2 + 2;
	private const string Extension = ".icon";

	// The largest valid file: a 256 x 256 icon. Anything bigger is not read into memory at all.
	internal const long MaxFileBytes = HeaderBytes + (256L * 256 * 4);

	// A .tmp file younger than this may belong to a write in progress (another picker run).
	private static readonly TimeSpan StrayTempAge = TimeSpan.FromMinutes(1);

	private readonly IProgramIconSource _inner = inner;
	private readonly string _directory = directory;

	public static IconDiskCache ForDataRoot(IProgramIconSource inner, string dataRoot)
	{
		return new IconDiskCache(inner, Path.Combine(dataRoot, "cache", "icons"));
	}

	public string? TryGetCacheKey(string exePath, int sizePx)
	{
		return _inner.TryGetCacheKey(exePath, sizePx);
	}

	public async Task<IconPixels?> ReadAsync(string exePath, int sizePx, CancellationToken cancellationToken = default)
	{
		string? key = SafeKey(exePath, sizePx);
		if (key is not null && TryRead(FileFor(key), sizePx) is { } cached)
		{
			return cached;
		}

		IconPixels? fresh = await _inner.ReadAsync(exePath, sizePx, cancellationToken).ConfigureAwait(false);
		if (fresh is not null && key is not null)
		{
			TryWrite(FileFor(key), fresh);
		}

		return fresh;
	}

	// The file name for a key: hex SHA-256, so it never carries the path.
	internal static string FileNameFor(string key)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + Extension;
	}

	internal static byte[] Encode(IconPixels pixels)
	{
		byte[] data = new byte[HeaderBytes + pixels.Bgra.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), Version);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)pixels.Width);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)pixels.Height);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), pixels.IsPremultiplied ? (ushort)1 : (ushort)0);
		pixels.Bgra.Span.CopyTo(data.AsSpan(HeaderBytes));
		return data;
	}

	internal static IconPixels? Decode(ReadOnlySpan<byte> data, int expectedSizePx)
	{
		if (data.Length < HeaderBytes
			|| BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
			|| BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != Version)
		{
			return null;
		}

		int width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
		int height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
		ushort premultiplied = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
		return width != expectedSizePx || height != expectedSizePx || premultiplied > 1
			|| data.Length != HeaderBytes + (width * height * 4)
			? null
			: new IconPixels(width, height, data[HeaderBytes..].ToArray(), premultiplied == 1);
	}

	private string? SafeKey(string exePath, int sizePx)
	{
		try
		{
			return _inner.TryGetCacheKey(exePath, sizePx);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private string FileFor(string key)
	{
		return Path.Combine(_directory, FileNameFor(key));
	}

	private static IconPixels? TryRead(string file, int sizePx)
	{
		try
		{
			FileInfo info = new(file);
			return info.Exists && info.Length >= HeaderBytes && info.Length <= MaxFileBytes
				? Decode(File.ReadAllBytes(file), sizePx)
				: null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private void TryWrite(string file, IconPixels pixels)
	{
		string temp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
		try
		{
			_ = Directory.CreateDirectory(_directory);
			File.WriteAllBytes(temp, Encode(pixels));
			File.Move(temp, file, overwrite: true);
			Trim();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			TryDelete(temp);
		}
	}

	// Removes the least recently written cache files until both caps hold. Temp files left by a
	// crash are removed too, but only once they are over a minute old: a younger one may be
	// another run's write that is about to be moved into place.
	private void Trim()
	{
		DirectoryInfo dir = new(_directory);
		DateTime strayBefore = DateTime.UtcNow - StrayTempAge;
		foreach (FileInfo stray in dir.EnumerateFiles("*.tmp").Where(f => f.LastWriteTimeUtc < strayBefore))
		{
			TryDelete(stray.FullName);
		}

		List<FileInfo> files = [.. dir.EnumerateFiles("*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc)];
		long total = 0;
		for (int i = 0; i < files.Count; i++)
		{
			total += files[i].Length;
			if (i >= MaxFiles || total > MaxBytes)
			{
				TryDelete(files[i].FullName);
			}
		}
	}

	private static void TryDelete(string file)
	{
		try
		{
			File.Delete(file);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Left for the next trim.
		}
	}
}
