using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BroChoos.Platform.Windows.SingleInstance;

// F-2b: THE REAL IPC AUTHENTICATION ANCHOR (OS-LEVEL PEER IDENTITY).
//
// WHY THIS TYPE EXISTS. Before it, peer identity was SELF-REPORTED: the connecting
// process wrote its own executable path and signer identity into the identity frame and
// the pipe server believed those strings (WindowsSingleInstanceGuard.TryParsePeerIdentityFrame),
// while the server's own expectation was a hardcoded constant. Any process running as the
// same user could claim to be the Dev Runner by writing the right text. The frame checks
// stay as defense-in-depth, but they are no longer the only check.
//
// WHAT THIS DOES. The pipe server asks the OPERATING SYSTEM who is on the other end:
//   GetNamedPipeClientProcessId      -> the client PID (kernel-reported, not claimed),
//   OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) -> a handle to that process,
//   QueryFullProcessImageName        -> the REAL image path the process was started from.
// That path is canonicalized (full path, symlink target resolved) and compared
// case-insensitively with the expected local executable. Mismatch -> the connection is
// rejected before the peer's frame is read.
//
// FAIL-CLOSED, AND NO FALLBACK. Every failure to OBTAIN the truth - invalid or closed
// handle, missing PID, OpenProcess refusal, query failure, uncanonicalizable path, blank
// input - is a rejection. There is deliberately no code path in this type that falls back
// to a frame-declared value, and the guard never substitutes one on failure.
//
// SCOPE NOTE (owner ruling). docs/decisions/owner-rulings/dev-runner-ipc-trust-anchor.json
// declares signerIdentityModel = "x509-chain". This type anchors the PROCESS IMAGE; it does
// not yet walk the image's Authenticode chain. That remains enforced at the frame level
// (ExecutableIdentityVerifier.VerifySignerIdentity) and is recorded as a remaining item
// rather than silently half-implemented here.
internal static class PeerProcessImageVerifier
{
	// PROCESS_QUERY_LIMITED_INFORMATION is the least privilege that still answers
	// QueryFullProcessImageName, and unlike QUERY_INFORMATION it is granted across
	// integrity levels - which matters because the guard must be able to check a peer
	// it does not otherwise have rights over.
	private const uint ProcessQueryLimitedInformation = 0x1000;

	// QueryFullProcessImageName takes the buffer size in CHARACTERS and fails with
	// ERROR_INSUFFICIENT_BUFFER when the path does not fit. That failure is reported as
	// unavailable (fail-closed); 4096 characters is far beyond any real image path.
	private const int MaxImagePathChars = 4096;

	// The server-side entry point: verify the process at the CLIENT end of a connected pipe.
	public static IpcPeerAuthenticationResult VerifyClientProcessImage(SafePipeHandle clientPipeHandle, string expectedExecutablePath)
	{
		ArgumentNullException.ThrowIfNull(clientPipeHandle);

		// Checked before the P/Invoke: marshalling a disposed SafeHandle throws
		// ObjectDisposedException, and a throw here would be a crash rather than a
		// rejection. A closed handle is exactly the fail-closed case.
		if (clientPipeHandle.IsInvalid || clientPipeHandle.IsClosed)
		{
			return Unavailable("ipc.peer.image-handle-invalid");
		}

		if (!GetNamedPipeClientProcessId(clientPipeHandle, out uint clientProcessId) || clientProcessId == 0)
		{
			return Unavailable("ipc.peer.image-pid-unavailable");
		}

		IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, clientProcessId);
		if (processHandle == IntPtr.Zero)
		{
			// OpenProcess does not throw; a zero handle means the OS refused (the process
			// exited between the PID lookup and the open, or access was denied).
			return Unavailable("ipc.peer.image-open-failed");
		}

		try
		{
			return VerifyProcessHandleImage(processHandle, expectedExecutablePath);
		}
		finally
		{
			// Closed on every path, including the early return inside the try.
			_ = CloseHandle(processHandle);
		}
	}

	// Verifies an already-open process handle. Split out so the identity core is testable
	// with a real handle (the current process, a spawned child) without a pipe.
	public static IpcPeerAuthenticationResult VerifyProcessHandleImage(IntPtr processHandle, string expectedExecutablePath)
	{
		return !TryResolveProcessImagePath(processHandle, out string imagePath)
			? Unavailable("ipc.peer.image-unavailable")
			: VerifyImagePath(imagePath, expectedExecutablePath);
	}

	// The kernel-reported image path for a process handle, or false when the OS will not
	// answer. No throw: a failed query is a rejection, not an exception.
	public static bool TryResolveProcessImagePath(IntPtr processHandle, out string imagePath)
	{
		imagePath = string.Empty;

		if (processHandle == IntPtr.Zero)
		{
			return false;
		}

		char[] buffer = new char[MaxImagePathChars];
		uint size = MaxImagePathChars;
		if (!QueryFullProcessImageName(processHandle, dwFlags: 0, buffer, ref size) || size == 0)
		{
			return false;
		}

		imagePath = new string(buffer, 0, (int)size);
		return imagePath.Length > 0;
	}

	// The comparison itself, exposed so the fail-closed rules on the INPUTS are testable
	// without a process: a blank value on either side is unavailability, never a pass.
	public static IpcPeerAuthenticationResult VerifyImagePath(string actualImagePath, string expectedExecutablePath)
	{
		if (string.IsNullOrWhiteSpace(actualImagePath) || string.IsNullOrWhiteSpace(expectedExecutablePath))
		{
			return Unavailable("ipc.peer.image-unavailable");
		}

		string actualCanonical;
		string expectedCanonical;
		try
		{
			// Same canonicalization the frame-level identity check uses: full path plus
			// symlink resolution, so a junction or a short path cannot disguise a
			// different image as the expected one.
			actualCanonical = ExecutableIdentityVerifier.ResolveCanonicalPath(actualImagePath);
			expectedCanonical = ExecutableIdentityVerifier.ResolveCanonicalPath(expectedExecutablePath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
		{
			return Unavailable("ipc.peer.image-unavailable");
		}

		return !string.Equals(actualCanonical, expectedCanonical, StringComparison.OrdinalIgnoreCase)
			? IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.PeerProcessImageMismatch, "ipc.peer.image-mismatch")
			: IpcPeerAuthenticationResult.Passed;
	}

	private static IpcPeerAuthenticationResult Unavailable(string reasonCode)
	{
		return IpcPeerAuthenticationResult.Fail(IpcPeerAuthenticationErrorCode.PeerProcessImageUnavailable, reasonCode);
	}

	// Classic DllImport, matching the repository's existing Win32 surface (the App's
	// NativeMethods documents the same choice): every signature here is blittable and the
	// call rate is one connection at a time, so the LibraryImport source generator would
	// add build machinery for no gain.
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint processId);

	[DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryFullProcessImageName(IntPtr process, uint dwFlags, [Out] char[] exeName, ref uint size);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
