using System.Runtime.InteropServices;

namespace BroChoos.App.Picker;

// Win32 surface for the picker shell, in one place and nothing else:
//  - cursor + monitor geometry (D-149 pre-show placement, spike D1 fix input),
//  - the bounded foreground ladder (SetForegroundWindow / AttachThreadInput,
//    verified by re-reading GetForegroundWindow),
//  - the losing-fallback taskbar flash,
//  - the effective DPI-awareness assertion (spike: trust the runtime query, not
//    the manifest). Raw contexts are compared with AreDpiAwarenessContextsEqual
//    only: == on the documented pseudo handles is a false negative on Windows
//    builds that return raw internal values (0x22 for PerMonitorV2).
//
// Classic DllImport on purpose: LibraryImport's source generator would force
// <AllowUnsafeBlocks> on the project for no benefit at this call rate, and every
// signature below is blittable. There is deliberately NO SystemParametersInfo
// import: SPI_SETFOREGROUNDLOCKTIMEOUT must never be read-and-restored or
// mutated by this product (locked spike lesson).
internal static class NativeMethods
{
	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetCursorPos(out Point lpPoint);

	[DllImport("user32.dll")]
	internal static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

	[DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

	[DllImport("shcore.dll")]
	internal static extern int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

	[DllImport("user32.dll")]
	internal static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool BringWindowToTop(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

	[DllImport("user32.dll")]
	internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("kernel32.dll")]
	internal static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool FlashWindowEx(ref FlashInfo pwfi);

	[DllImport("user32.dll")]
	internal static extern IntPtr GetThreadDpiAwarenessContext();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool AreDpiAwarenessContextsEqual(IntPtr dpiContextA, IntPtr dpiContextB);

	internal const uint MonitorDefaultToNearest = 2;

	internal const uint FlashWindowAll = 0x3;
	internal const uint FlashWindowTimerNoForeground = 0xC;

	internal enum MonitorDpiType
	{
		EffectiveDpi = 0,
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct Point
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct Rect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct MonitorInfo
	{
		public int cbSize;
		public Rect rcMonitor;
		public Rect rcWork;
		public uint dwFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct FlashInfo
	{
		public uint cbSize;
		public IntPtr hwnd;
		public uint dwFlags;
		public uint uCount;
		public uint dwTimeout;
	}

	internal static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
}
