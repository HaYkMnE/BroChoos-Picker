using System.Runtime.InteropServices;
using BroChoos.App.Picker;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// The 0A-3 SESSION_START marker bug, pinned against the live OS: the documented
// DPI_AWARENESS_CONTEXT pseudo handles (-1..-5) are setter inputs;
// GetThreadDpiAwarenessContext returns RAW internal context values (on the dev
// machine 0x6010 = unaware, 0x22 = PerMonitorV2), so == against (IntPtr)(-4)
// reported "NOT PerMonitorV2" for a process whose manifest IS PerMonitorV2. The
// product check must go through AreDpiAwarenessContextsEqual. Each test swaps
// the CURRENT thread's context for real and restores it in finally, so the
// mapping is proven by the OS, not by an assumption about raw values.
//
// NOT #if DEBUG, deliberately (0A-3 review item 6): what these tests exercise is
// a pure Win32 swap/restore (SetThreadDpiAwarenessContext) plus product code that
// compiles into EVERY build (the SESSION_START readback runs in Release too), so
// gating them would leave Release's DPI assertion unverified. They run in both
// configurations and keep the Debug-Release count difference at EXACTLY ONE (the
// detector-override test) - verified green in Release on the 0A-3 nit-fix round.
public sealed class DpiAwarenessTests
{
	private static readonly IntPtr PerMonitorAwareV2PseudoHandle = new(-4);
	private static readonly IntPtr SystemAwarePseudoHandle = new(-2);

	[Fact]
	public void DescribeCurrentThread_PerMonitorV2Context_ReportsPerMonitorV2()
	{
		IntPtr previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2PseudoHandle);
		try
		{
			_ = DpiAwareness.IsPerMonitorV2(GetThreadDpiAwarenessContext()).Should().BeTrue();
			_ = DpiAwareness.DescribeCurrentThread().Should().Be("PerMonitorV2");
		}
		finally
		{
			_ = SetThreadDpiAwarenessContext(previous);
		}
	}

	[Fact]
	public void DescribeCurrentThread_NonPerMonitorV2Context_KeepsTheRegressionTag()
	{
		IntPtr previous = SetThreadDpiAwarenessContext(SystemAwarePseudoHandle);
		try
		{
			_ = DpiAwareness.IsPerMonitorV2(GetThreadDpiAwarenessContext()).Should().BeFalse();
			_ = DpiAwareness.DescribeCurrentThread().Should().EndWith("(NOT PerMonitorV2)");
		}
		finally
		{
			_ = SetThreadDpiAwarenessContext(previous);
		}
	}

	[DllImport("user32.dll", ExactSpelling = true)]
	private static extern IntPtr GetThreadDpiAwarenessContext();

	[DllImport("user32.dll", ExactSpelling = true)]
	private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
