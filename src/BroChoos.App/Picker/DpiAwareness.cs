namespace BroChoos.App.Picker;

// D-149 runtime assertion: the spike instruction is to keep the PerMonitorV2
// manifest and ASSERT the effective awareness at startup instead of trusting the
// file (the manifest is embedded by the build; this is the readback that proves
// it took effect on the real OS).
//
// The trap that produced the false "NOT PerMonitorV2" marker at 0A-3 smoke: the
// documented DPI_AWARENESS_CONTEXT pseudo handles (-1..-5) are input values for
// the setters. GetThreadDpiAwarenessContext on Windows 10 22H2 / Windows 11
// returns RAW internal context values (observed on the dev machine: 0x6010 for
// unaware, 0x22 for PerMonitorV2), so comparing the returned value to
// DpiAwarenessContextPerMonitorAwareV2 (IntPtr)(-4) with == is a false negative
// even for a correctly manifested process. AreDpiAwarenessContextsEqual is the
// supported comparison: it maps a raw context to its awareness class first.
internal static class DpiAwareness
{
	internal static bool IsPerMonitorV2(IntPtr context)
	{
		return NativeMethods.AreDpiAwarenessContextsEqual(context, NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
	}

	// The SESSION_START marker text: "PerMonitorV2" exactly when the calling
	// thread runs at the D-149 level, otherwise the raw context value plus an
	// explicit tag, so a regression cannot hide in the smoke log.
	internal static string DescribeCurrentThread()
	{
		IntPtr context = NativeMethods.GetThreadDpiAwarenessContext();
		return IsPerMonitorV2(context) ? "PerMonitorV2" : $"0x{context:X} (NOT PerMonitorV2)";
	}
}
