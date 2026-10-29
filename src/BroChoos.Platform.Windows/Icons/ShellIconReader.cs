using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BroChoos.Platform.Windows.Browsers;

namespace BroChoos.Platform.Windows.Icons;

// Raw program-icon pixels: top-down BGRA, 4 bytes per pixel, stride = Width * 4.
// IsPremultiplied is MEASURED per icon (see IconAlpha), not assumed.
public sealed class IconPixels(int width, int height, ReadOnlyMemory<byte> bgra, bool isPremultiplied)
{
	public int Width { get; } = width;

	public int Height { get; } = height;

	public ReadOnlyMemory<byte> Bgra { get; } = bgra;

	public bool IsPremultiplied { get; } = isPremultiplied;
}

// The seam the App-side loader consumes. Both members may touch the file system, so callers
// run them off the UI thread. Neither member logs, and neither puts the path in an exception
// it lets escape: redaction is the caller's job, and a path never reaches a log from here.
public interface IProgramIconSource
{
	// A cache key (normalized path + last-write time + pixel size), or null when the path is not
	// a readable local executable - in which case no icon will ever be produced for it.
	string? TryGetCacheKey(string exePath, int sizePx);

	// The icon at exactly sizePx x sizePx device pixels, or null when it cannot be read.
	Task<IconPixels?> ReadAsync(string exePath, int sizePx, CancellationToken cancellationToken = default);
}

// IShellItemImageFactory.GetImage(SIIGBF_ICONONLY) on ONE dedicated STA worker thread: shell
// image extraction is apartment-sensitive, and one thread keeps the shell's per-thread state
// in one place. The thread is a background thread, so it never keeps the process alive.
public sealed class ShellIconReader : IProgramIconSource
{
	private const int MaxSizePx = 256;

	// A shell extraction that has not answered in this time is treated as hung: its caller gets
	// null (the letter plate stays), and while it is still hung later requests are skipped at once
	// instead of queueing behind it. The STA thread cannot be aborted, so it is left to finish.
	// The budget is counted FROM THE START OF THAT REQUEST'S EXTRACTION, not from the enqueue, so
	// a request queued behind a slow-but-alive extraction is not timed out for waiting its turn.
	internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

	private readonly Func<string, int, IconPixels?> _extract;
	private readonly TimeSpan _timeout;

	// Stopwatch timestamp of the extraction in progress; 0 while the worker is idle.
	private long _busySince;

	private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

	public ShellIconReader()
		: this(Extract, DefaultTimeout)
	{
	}

	// Test seam: a stand-in extractor and a short timeout.
	internal ShellIconReader(Func<string, int, IconPixels?> extract, TimeSpan timeout)
	{
		_extract = extract;
		_timeout = timeout;
		Thread worker = new(Run)
		{
			Name = "BroChoos-Icon-STA",
			IsBackground = true,
		};

		worker.SetApartmentState(ApartmentState.STA);
		worker.Start();
	}

	public string? TryGetCacheKey(string exePath, int sizePx)
	{
		string? normalized = PathSafety.NormalizeLocalExecutablePath(exePath);
		if (normalized is null || sizePx <= 0 || sizePx > MaxSizePx || !File.Exists(normalized))
		{
			return null;
		}

		long ticks = File.GetLastWriteTimeUtc(normalized).Ticks;
		return string.Create(CultureInfo.InvariantCulture, $"{normalized.ToUpperInvariant()}|{ticks}|{sizePx}");
	}

	public Task<IconPixels?> ReadAsync(string exePath, int sizePx, CancellationToken cancellationToken = default)
	{
		string? normalized = PathSafety.NormalizeLocalExecutablePath(exePath);
		if (normalized is null || sizePx <= 0 || sizePx > MaxSizePx || IsWorkerHung())
		{
			return Task.FromResult<IconPixels?>(null);
		}

		WorkItem item = new(normalized, sizePx, cancellationToken);
		return _queue.Writer.TryWrite(item) ? WithTimeoutAsync(item, cancellationToken) : Task.FromResult<IconPixels?>(null);
	}

	private bool IsWorkerHung()
	{
		long since = Interlocked.Read(ref _busySince);
		return since != 0 && Stopwatch.GetElapsedTime(since) > _timeout;
	}

	// Waits for the item's own extraction to START (no budget: queued behind live work is fine,
	// but a hung worker ends the wait, since the item will then never be reached in time), then
	// gives that extraction the per-request budget.
	private async Task<IconPixels?> WithTimeoutAsync(WorkItem item, CancellationToken cancellationToken)
	{
		try
		{
			while (!item.Started.Task.IsCompleted)
			{
				// The per-pass delay is cancelled as soon as the item starts, so no timer is left
				// running for up to a full budget after every queued request.
				using CancellationTokenSource pass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				Task delay = Task.Delay(_timeout, pass.Token);
				Task first = await Task.WhenAny(item.Started.Task, delay).ConfigureAwait(false);
				await pass.CancelAsync().ConfigureAwait(false);
				if (first != item.Started.Task && IsWorkerHung())
				{
					return null;
				}

				cancellationToken.ThrowIfCancellationRequested();
			}

			return await item.Completion.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			return null;
		}
	}

	private void Run()
	{
		ChannelReader<WorkItem> reader = _queue.Reader;
		while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
		{
			while (reader.TryRead(out WorkItem? item))
			{
				if (item.CancellationToken.IsCancellationRequested)
				{
					_ = item.Completion.TrySetCanceled(item.CancellationToken);
					continue;
				}

				IconPixels? result;
				_ = Interlocked.Exchange(ref _busySince, Stopwatch.GetTimestamp());
				_ = item.Started.TrySetResult();
				try
				{
					result = _extract(item.Path, item.SizePx);
				}
				catch (Exception ex) when (ex is not OutOfMemoryException)
				{
					// ANY failure is one missing icon, never a dead worker or process. Swallowed WITHOUT
					// the exception: a shell exception message can carry the path.
					result = null;
				}
				finally
				{
					_ = Interlocked.Exchange(ref _busySince, 0);
				}

				_ = item.Completion.TrySetResult(result);
			}
		}
	}

	private static IconPixels? Extract(string path, int sizePx)
	{
		Guid iid = typeof(IShellItemImageFactory).GUID;
		int hr = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory? factory);
		if (hr < 0 || factory is null)
		{
			return null;
		}

		IntPtr bitmap = IntPtr.Zero;
		try
		{
			hr = factory.GetImage(new NativeMethods.NativeSize(sizePx, sizePx), NativeMethods.SiigbfIconOnly, out bitmap);
			return hr < 0 || bitmap == IntPtr.Zero ? null : CopyBitmap(bitmap);
		}
		finally
		{
			if (bitmap != IntPtr.Zero)
			{
				_ = NativeMethods.DeleteObject(bitmap);
			}

			_ = Marshal.ReleaseComObject(factory);
		}
	}

	private static IconPixels? CopyBitmap(IntPtr bitmap)
	{
		if (NativeMethods.GetObject(bitmap, Marshal.SizeOf<NativeMethods.BitmapObject>(), out NativeMethods.BitmapObject info) == 0)
		{
			return null;
		}

		int width = info.bmWidth;
		int height = Math.Abs(info.bmHeight);
		if (width <= 0 || height <= 0 || width > MaxSizePx || height > MaxSizePx)
		{
			return null;
		}

		NativeMethods.BitmapInfoHeader header = new()
		{
			biSize = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
			biWidth = width,
			biHeight = -height,
			biPlanes = 1,
			biBitCount = 32,
			biCompression = NativeMethods.BiRgb,
		};

		byte[] bgra = new byte[width * height * 4];
		IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
		try
		{
			int lines = NativeMethods.GetDIBits(screen, bitmap, 0, (uint)height, bgra, ref header, NativeMethods.DibRgbColors);
			if (lines != height)
			{
				return null;
			}
		}
		finally
		{
			_ = NativeMethods.ReleaseDC(IntPtr.Zero, screen);
		}

		_ = IconAlpha.FixAllZeroAlpha(bgra);
		return new IconPixels(width, height, bgra, IconAlpha.IsConsistentWithPremultiplied(bgra));
	}

	private sealed class WorkItem(string path, int sizePx, CancellationToken cancellationToken)
	{
		public string Path { get; } = path;

		public int SizePx { get; } = sizePx;

		public CancellationToken CancellationToken { get; } = cancellationToken;

		public TaskCompletionSource<IconPixels?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		// Set when the worker begins this item's extraction; the per-request budget starts here.
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}

// Alpha rules for shell bitmaps.
internal static class IconAlpha
{
	// Some icons (old 24-bit ones) come back with every alpha byte 0 although they have colour,
	// which would draw nothing; such a bitmap has no transparency information, so it is made
	// opaque. A bitmap that is ALL zero (colour too) really is empty and stays transparent -
	// forcing it opaque would paint a black square.
	internal static bool FixAllZeroAlpha(Span<byte> bgra)
	{
		bool anyColour = false;
		for (int i = 0; i + 3 < bgra.Length; i += 4)
		{
			if (bgra[i + 3] != 0)
			{
				return false;
			}

			anyColour |= bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0;
		}

		if (!anyColour)
		{
			return false;
		}

		for (int i = 3; i < bgra.Length; i += 4)
		{
			bgra[i] = 255;
		}

		return true;
	}

	// Premultiplied data can never have a colour channel above its alpha. One such pixel proves
	// the data is straight (unpremultiplied) alpha; none is read as premultiplied.
	internal static bool IsConsistentWithPremultiplied(ReadOnlySpan<byte> bgra)
	{
		return CountChannelAboveAlpha(bgra) == 0;
	}

	internal static int CountChannelAboveAlpha(ReadOnlySpan<byte> bgra)
	{
		int count = 0;
		for (int i = 0; i + 3 < bgra.Length; i += 4)
		{
			byte a = bgra[i + 3];
			if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a)
			{
				count++;
			}
		}

		return count;
	}
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
	[PreserveSig]
	int GetImage(NativeMethods.NativeSize size, int flags, out IntPtr phbm);
}

// Classic DllImport, as in BroChoos.App.Picker.NativeMethods: every signature is blittable apart
// from the Unicode path string and the COM out-parameter, and unsafe blocks stay off.
internal static class NativeMethods
{
	internal const int SiigbfIconOnly = 0x4;
	internal const int BiRgb = 0;
	internal const uint DibRgbColors = 0;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	internal static extern int SHCreateItemFromParsingName(
		string pszPath,
		IntPtr pbc,
		ref Guid riid,
		[MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

	[DllImport("gdi32.dll", EntryPoint = "GetObjectW", ExactSpelling = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	internal static extern int GetObject(IntPtr h, int c, out BitmapObject pv);

	[DllImport("gdi32.dll", ExactSpelling = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BitmapInfoHeader lpbmi, uint usage);

	[DllImport("gdi32.dll", ExactSpelling = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool DeleteObject(IntPtr ho);

	[DllImport("user32.dll", ExactSpelling = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	internal static extern IntPtr GetDC(IntPtr hWnd);

	[DllImport("user32.dll", ExactSpelling = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

	[StructLayout(LayoutKind.Sequential)]
	internal readonly struct NativeSize(int cx, int cy)
	{
		public readonly int Cx = cx;
		public readonly int Cy = cy;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct BitmapObject
	{
		public int bmType;
		public int bmWidth;
		public int bmHeight;
		public int bmWidthBytes;
		public ushort bmPlanes;
		public ushort bmBitsPixel;
		public IntPtr bmBits;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct BitmapInfoHeader
	{
		public int biSize;
		public int biWidth;
		public int biHeight;
		public ushort biPlanes;
		public ushort biBitCount;
		public int biCompression;
		public int biSizeImage;
		public int biXPelsPerMeter;
		public int biYPelsPerMeter;
		public int biClrUsed;
		public int biClrImportant;
	}
}
