using System.Security.Principal;

namespace BroChoos.Platform.Windows.Tests.SingleInstance;

// The guard's per-user Global mutex is shared by every process on the machine, so another
// test run (or another test assembly) can legitimately own it. The real-mutex tests need to
// be the primary, and they must tell two situations apart:
//   * the mutex is demonstrably held by SOMEONE ELSE - the test cannot speak about its own
//     guard, so it skips with a specific reason;
//   * nobody holds it - the first acquirer MUST be Primary, and a Secondary/Failed role is a
//     real guard bug that has to fail the test, not be hidden behind a skip.
// The probe therefore runs BEFORE the guard is created and looks at whether the named mutex
// already exists. It never takes ownership, and it closes its handle immediately.
internal static class SharedGuardMutex
{
	internal const string SkipReason =
		"the per-user Global single-instance mutex already exists (held by another test run or assembly); this test needs to be the primary";

	// Must stay identical to WindowsSingleInstanceGuard's mutex name.
	internal static string Name
	{
		get
		{
			string sid = WindowsIdentity.GetCurrent().User?.Value ?? "anon";
			return $@"Global\BroChoos.SingleInstance.{sid}";
		}
	}

	internal static bool IsHeldElsewhere()
	{
		try
		{
			if (Mutex.TryOpenExisting(Name, out Mutex? existing))
			{
				existing.Dispose();
				return true;
			}

			return false;
		}
		catch (UnauthorizedAccessException)
		{
			// The mutex exists but this principal may not open it: it is certainly held by
			// someone else.
			return true;
		}
	}
}
