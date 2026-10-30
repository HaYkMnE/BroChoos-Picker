using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BroChoos.Platform.Windows.Tests.TestSupport;

// BEST-EFFORT CLEANUP OF WHAT TESTS LEAVE IN %TEMP%. Shared by LINK with BroChoos.App.Tests and
// BroChoos.Security.Tests (see their .csproj), so the three test projects use one copy.
//
// Nothing here throws for a cleanup failure: by the time it runs the test has already proved
// its point, and a locked file or a process that is already gone must not turn a pass into a
// failure.
//
// SCOPE GUARD. Every entry point first checks that its target, after full-path and long-path
// normalization, lies under %TEMP%\brochoos-*. Anything else is ignored, so a wrong argument
// (a repository path, an evidence folder, a system image) can never be deleted or killed. A
// target that no longer exists may not normalize to its long form and is then ignored too,
// which is harmless: there is nothing left to clean.
internal static class TestTempCleanup
{
	private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(200);

	// The empty-parent delete is retried this many times, this far apart (500 ms at most).
	private const int EmptyParentAttempts = 5;
	private static readonly TimeSpan EmptyParentRetryDelay = TimeSpan.FromMilliseconds(100);

	private static readonly string OwnTempPrefix =
		Path.TrimEndingDirectorySeparator(LongPath(Path.GetTempPath())) + Path.DirectorySeparatorChar + "brochoos-";

	private static bool IsOwnTempPath(string path)
	{
		return LongPath(path).StartsWith(OwnTempPrefix, StringComparison.OrdinalIgnoreCase);
	}

	// Kills any process still running from THIS stub image, then waits briefly for it to go.
	//
	// IDENTITY, NOT A PID. GetProcessesByName only returns pids, and a pid can be reused once
	// its process has exited. Reading SafeHandle opens and STORES a handle in the Process
	// object, and Windows does not reuse a pid while any handle to that process is open, so
	// from that point on the pid names the same process. The image check then runs against
	// that pinned process (MainModule.FileName must equal the stub's full path, ignoring case),
	// and Kill uses the stored handle. A process that exits first, or whose modules cannot be
	// read, throws from SafeHandle or MainModule and is skipped.
	internal static void KillSurvivors(string executablePath)
	{
		if (!IsOwnTempPath(executablePath))
		{
			return;
		}

		string expected = LongPath(executablePath);
		foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
		{
			try
			{
				_ = process.SafeHandle;
				string? image = process.MainModule?.FileName;
				if (image is not null && string.Equals(LongPath(image), expected, StringComparison.OrdinalIgnoreCase))
				{
					process.Kill(entireProcessTree: true);
					_ = process.WaitForExit(5000);
				}
			}
			catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
			{
				// AggregateException: the tree kill reports descendants it could not end this way.
			}
			finally
			{
				process.Dispose();
			}
		}
	}

	// Recursive delete with ONE short retry: a child killed just before can hold its image
	// file for a moment after it is gone from the process list.
	internal static void DeleteDirectory(string path)
	{
		if (!IsOwnTempPath(path))
		{
			return;
		}

		for (int attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}

				return;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				if (attempt == 0)
				{
					Thread.Sleep(DeleteRetryDelay);
				}
			}
		}
	}

	// Removes a fixed parent folder such as %TEMP%\brochoos-w73-tests once the last per-test
	// child is gone. The non-recursive delete IS the emptiness check: it fails on a folder that
	// still has entries, so a parent another test is using is never removed. Each such parent
	// belongs to a single test class, and xUnit runs the tests of one class one at a time, so no
	// test of the same class can be creating a child at that moment.
	//
	// RETRIED, because the first r13 validation left all five fixed parents behind, empty a few
	// seconds later. The likeliest cause [not proven] is a child folder whose delete was still
	// pending: Windows keeps a deleted entry visible while another process (an antivirus scan, an
	// indexer) still has a handle open, and the parent reads as non-empty until that handle closes.
	// A short bounded retry (5 tries, 100 ms apart) covers that window; a folder that is gone, or
	// still really in use after it, is left alone.
	internal static void DeleteDirectoryIfEmpty(string path)
	{
		if (!IsOwnTempPath(path))
		{
			return;
		}

		for (int attempt = 1; attempt <= EmptyParentAttempts; attempt++)
		{
			try
			{
				if (!Directory.Exists(path))
				{
					return;
				}

				Directory.Delete(path, recursive: false);
				return;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Not empty yet, or in use: retry, then give up quietly (best-effort cleanup).
				if (attempt < EmptyParentAttempts)
				{
					Thread.Sleep(EmptyParentRetryDelay);
				}
			}
		}
	}

	internal static void DeleteFile(string path)
	{
		if (!IsOwnTempPath(path))
		{
			return;
		}

		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	// %TEMP% can be an 8.3 short path (GitHub runners use C:\Users\RUNNER~1\...), while the
	// loader reports a module by its long path, so both sides are expanded before comparing.
	private static string LongPath(string path)
	{
		string full = Path.GetFullPath(path);
		char[] buffer = new char[1024];
		uint length = GetLongPathName(full, buffer, (uint)buffer.Length);
		return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : full;
	}

	[DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static extern uint GetLongPathName(string shortPath, [Out] char[] longPath, uint bufferLength);
}
