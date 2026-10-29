using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BroChoos.Platform.Windows.Icons;
using Microsoft.Extensions.Logging;

namespace BroChoos.App.Icons;

// App side of the browser icons (docs/design/PICKER-LAYOUT-SPEC.md). Lives OUTSIDE Picker/ on
// purpose: the picker's first-frame path is forbidden any file-system API (ForbiddenApiTests),
// and the cache key needs the program file's last-write time.
//
// NEVER BLOCKS THE CALLER: LoadAsync hands everything (the key, the shell read, the bitmap) to
// the thread pool and the STA icon worker, so the window shows its letter plates at once and
// swaps an icon in whenever it arrives.
//
// NEVER LOGS THE PATH: the messages are constant and no exception object is attached, because
// a shell or IO exception message can contain the executable path.
//
// Cached per (normalized path, last-write time, pixel size): one read per program per run,
// shared by concurrent requests, and a reinstalled browser gets a fresh key. A failed read is
// cached too (null), so a broken icon is not retried for every tile.
internal sealed class BrowserIconLoader(
	IProgramIconSource source,
	ILogger<BrowserIconLoader> logger,
	Func<IconPixels, IImage?>? toImage = null)
{
	internal const double IconDip = 48;

	private readonly IProgramIconSource _source = source;
	private readonly ILogger<BrowserIconLoader> _logger = logger;
	private readonly Func<IconPixels, IImage?> _toImage = toImage ?? ToBitmap;
	private readonly ConcurrentDictionary<string, Lazy<Task<IImage?>>> _cache = new(StringComparer.Ordinal);

	// 48 logical px at the monitor's scale: 48 / 60 / 72 device px at 100 / 125 / 150 %.
	internal static int PixelSizeFor(double scale)
	{
		double safe = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
		return Math.Clamp((int)Math.Round(IconDip * safe), 16, 256);
	}

	public Task<IImage?> LoadAsync(string exePath, int sizePx)
	{
		return Task.Run(() => LoadCoreAsync(exePath, sizePx));
	}

	private async Task<IImage?> LoadCoreAsync(string exePath, int sizePx)
	{
		string? key;
		try
		{
			key = _source.TryGetCacheKey(exePath, sizePx);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			_logger.LogDebug("Browser icon key could not be read; the letter plate stays");
			return null;
		}

		if (key is null)
		{
			_logger.LogDebug("Browser program file is not readable; the letter plate stays");
			return null;
		}

		Lazy<Task<IImage?>> entry = _cache.GetOrAdd(key, _ => new Lazy<Task<IImage?>>(() => ReadAndConvertAsync(exePath, sizePx)));
		return await entry.Value.ConfigureAwait(false);
	}

	private async Task<IImage?> ReadAndConvertAsync(string exePath, int sizePx)
	{
		try
		{
			IconPixels? pixels = await _source.ReadAsync(exePath, sizePx).ConfigureAwait(false);
			if (pixels is null)
			{
				_logger.LogDebug("Browser icon could not be read; the letter plate stays");
				return null;
			}

			return _toImage(pixels);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_logger.LogDebug("Browser icon could not be converted; the letter plate stays");
			return null;
		}
	}

	// Copies the BGRA rows into an Avalonia bitmap. The alpha format is the one the reader
	// MEASURED for this icon, so a straight-alpha icon is never drawn as premultiplied.
	internal static IImage? ToBitmap(IconPixels pixels)
	{
		if (pixels.Width <= 0 || pixels.Height <= 0 || pixels.Bgra.Length < pixels.Width * pixels.Height * 4)
		{
			return null;
		}

		byte[] data = pixels.Bgra.ToArray();
		WriteableBitmap bitmap = new(
			new PixelSize(pixels.Width, pixels.Height),
			new Vector(96, 96),
			PixelFormat.Bgra8888,
			pixels.IsPremultiplied ? AlphaFormat.Premul : AlphaFormat.Unpremul);

		using (ILockedFramebuffer frame = bitmap.Lock())
		{
			int rowBytes = pixels.Width * 4;
			for (int y = 0; y < pixels.Height; y++)
			{
				Marshal.Copy(data, y * rowBytes, frame.Address + (y * frame.RowBytes), rowBytes);
			}
		}

		return bitmap;
	}
}
