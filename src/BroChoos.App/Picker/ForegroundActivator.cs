using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BroChoos.App.Picker;

// Spike-proven activation ladder, hardened (spike checks 4/7 + item 10):
//
//   step 1: BringWindowToTop + SetForegroundWindow        (usually refused by
//           the foreground-lock rules, which any toolkit faces identically);
//   step 2: attach BOTH the picker's UI thread and this worker thread to the
//           incumbent foreground thread's input queue, retry briefly, detach
//           in finally;
//
// Success is never trusted to a return value: every step re-reads
// GetForegroundWindow(). SPI_SETFOREGROUNDLOCKTIMEOUT is never touched.
//
// Item 10 bound: the ladder runs OFF the UI thread and the CALLER caps the whole
// operation at ~2.5 s wall clock, so a hung incumbent foreground window (e.g. a
// frozen browser) can stall at most this worker - never the picker's UI thread.
// The attach segments stay inside the worker's own budget and are always
// detached in finally while the worker is alive.
internal static class ForegroundActivator
{
	// Outer cap awaited by the caller. Sized so the ladder's own 1.5 s budget
	// always wins (0A-3 review finding A4): the stopwatch only gates BETWEEN
	// retries, so the last iteration may sit inside one BLOCKED Win32 call while
	// the budget is already spent; the 1 s headroom absorbs that block (and the
	// finally detach) instead of letting the outer timeout pre-empt the ladder's
	// own verified Outcome with the coarser TimedOut=true verdict.
	internal static readonly TimeSpan HardCap = TimeSpan.FromSeconds(2.5);

	// Inner budget for the attached retry loop, leaving headroom inside HardCap.
	private static readonly TimeSpan AttachBudget = TimeSpan.FromMilliseconds(1500);

	internal readonly record struct Outcome(bool Verified, bool AttachNeeded, bool TimedOut, double Milliseconds, int FirstAttemptWin32Error);

	// Handle-zero short-circuit, checked by the CALLER before awaiting anything
	// (0A-3 review finding A5): with no platform handle every Win32 call in the
	// ladder is a no-op on handle 0 - the whole budget burns for nothing and the
	// losing fallback degenerates to FlashWindowEx(0), which flashes nothing.
	internal static bool IsAttemptable(IntPtr hwnd)
	{
		return hwnd != 0;
	}

	public static async Task<Outcome> ActivateAsync(IntPtr hwnd, uint uiThreadId, CancellationToken cancellationToken)
	{
		Task<Outcome> ladder = Task.Run(() => RunLadder(hwnd, uiThreadId), CancellationToken.None);

		try
		{
			return await ladder.WaitAsync(HardCap, cancellationToken);
		}
		catch (TimeoutException)
		{
			// The incumbent foreground queue is presumably hung; the UI thread kept
			// pumping. The window stays topmost-visible as the losing fallback.
			return new Outcome(false, AttachNeeded: true, TimedOut: true, HardCap.TotalMilliseconds, -1);
		}
	}

	private static Outcome RunLadder(IntPtr hwnd, uint uiThreadId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();

		_ = NativeMethods.BringWindowToTop(hwnd);
		_ = NativeMethods.SetForegroundWindow(hwnd);
		int firstError = Marshal.GetLastWin32Error();

		if (NativeMethods.GetForegroundWindow() == hwnd)
		{
			return new Outcome(true, AttachNeeded: false, TimedOut: false, stopwatch.Elapsed.TotalMilliseconds, firstError);
		}

		// Step 1 refused (expected: the OS enforces the foreground lock for any
		// toolkit): escalate by sharing the incumbent's input queue, then retry.
		IntPtr foreground = NativeMethods.GetForegroundWindow();
		uint foregroundThreadId = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
		uint workerThreadId = NativeMethods.GetCurrentThreadId();

		bool attachUi = false;
		bool attachWorker = false;

		try
		{
			if (foregroundThreadId is not 0 && foregroundThreadId != uiThreadId)
			{
				attachUi = NativeMethods.AttachThreadInput(uiThreadId, foregroundThreadId, true);
				attachWorker = NativeMethods.AttachThreadInput(workerThreadId, foregroundThreadId, true);
			}

			while (stopwatch.Elapsed < AttachBudget)
			{
				_ = NativeMethods.BringWindowToTop(hwnd);
				_ = NativeMethods.SetForegroundWindow(hwnd);

				if (NativeMethods.GetForegroundWindow() == hwnd)
				{
					return new Outcome(true, AttachNeeded: true, TimedOut: false, stopwatch.Elapsed.TotalMilliseconds, firstError);
				}

				Thread.Sleep(50);
			}

			return new Outcome(false, AttachNeeded: true, TimedOut: false, stopwatch.Elapsed.TotalMilliseconds, firstError);
		}
		finally
		{
			if (attachWorker)
			{
				_ = NativeMethods.AttachThreadInput(workerThreadId, foregroundThreadId, false);
			}

			if (attachUi)
			{
				_ = NativeMethods.AttachThreadInput(uiThreadId, foregroundThreadId, false);
			}
		}
	}

	// The losing fallback: with ShowInTaskbar=false the flash is mostly invisible,
	// but the topmost window stays open and readable regardless; the marker line
	// is what makes "foreground lost" observable in the smoke.
	internal static void FlashAsFallback(IntPtr hwnd)
	{
		NativeMethods.FlashInfo info = new()
		{
			cbSize = (uint)Marshal.SizeOf<NativeMethods.FlashInfo>(),
			hwnd = hwnd,
			dwFlags = NativeMethods.FlashWindowAll | NativeMethods.FlashWindowTimerNoForeground,
			uCount = 3,
			dwTimeout = 0,
		};

		_ = NativeMethods.FlashWindowEx(ref info);
	}
}
