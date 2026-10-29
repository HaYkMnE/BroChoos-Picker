using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using BroChoos.Platform.Windows.SingleInstance;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.SingleInstance;

// F-2b: the OS-level peer process-image anchor, exercised against REAL process handles.
//
// The pipe server must learn the CONNECTING process's image from the kernel
// (GetNamedPipeClientProcessId -> OpenProcess -> QueryFullProcessImageName), never from
// the peer's own frame. These tests use live handles only: this process, a spawned child
// whose image differs, and deliberately invalid/closed handles. Nothing here mocks the
// OS, so a regression in the P/Invoke chain fails these tests rather than passing them.
public sealed class PeerProcessImageVerifierTests
{
	private const uint ProcessQueryLimitedInformation = 0x1000;

	// The strongest form of the positive control: a REAL connected pipe whose client end
	// is this same process. It exercises the entire kernel path, not just the comparison.
	[Fact]
	public async Task VerifyClientProcessImage_ConnectedPipe_AcceptsWhenThePeerIsThisSameImage()
	{
		string pipeName = BuildUniquePipeName();
		using NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		using NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

		Task waiting = server.WaitForConnectionAsync();
		await client.ConnectAsync(5000);
		await waiting;

		IpcPeerAuthenticationResult result = PeerProcessImageVerifier.VerifyClientProcessImage(server.SafePipeHandle, Environment.ProcessPath!);

		_ = result.Success.Should().BeTrue("both ends of this pipe are the test process itself");
		_ = result.Failure.Should().BeNull();
	}

	// The mismatch, proven through the full kernel path on a connected pipe: the peer is
	// this process, the expectation is a DIFFERENT image. Before F-2b nothing compared
	// these two values at all.
	[Fact]
	public async Task VerifyClientProcessImage_ConnectedPipe_RejectsWhenTheExpectedImageDiffers()
	{
		string pipeName = BuildUniquePipeName();
		using NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		using NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

		Task waiting = server.WaitForConnectionAsync();
		await client.ConnectAsync(5000);
		await waiting;

		IpcPeerAuthenticationResult result = PeerProcessImageVerifier.VerifyClientProcessImage(server.SafePipeHandle, ChildImagePath);

		_ = result.Success.Should().BeFalse();
		_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageMismatch);
		_ = result.Failure.RedactedReason.Should().Be("ipc.peer.image-mismatch");
	}

	[Fact]
	public void VerifyProcessHandleImage_RealHandleOnThisProcess_Accepts()
	{
		IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, (uint)Environment.ProcessId);
		_ = handle.Should().NotBe(IntPtr.Zero, "the OS must grant a query handle on our own process");

		try
		{
			IpcPeerAuthenticationResult result = PeerProcessImageVerifier.VerifyProcessHandleImage(handle, Environment.ProcessPath!);

			_ = result.Success.Should().BeTrue();
		}
		finally
		{
			_ = CloseHandle(handle);
		}
	}

	// (a) A real child process whose image DIFFERS from the expected one is rejected, in
	// both directions, so the mismatch cannot be an artifact of argument order.
	[Fact]
	public void VerifyProcessHandleImage_SpawnedChildWithADifferentImage_RejectsAsMismatch()
	{
		using ChildProcessGuard childGuard = new(StartChildWithDifferentImage());
		Process child = childGuard.Process;
		IntPtr childHandle = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, (uint)child.Id);
		IntPtr selfHandle = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, (uint)Environment.ProcessId);
		_ = childHandle.Should().NotBe(IntPtr.Zero, "the spawned child must be queryable");
		_ = selfHandle.Should().NotBe(IntPtr.Zero);

		try
		{
			// PRECONDITION: the kernel really reports the child's own image, and it really
			// is a different executable than this test host. Without this the mismatch
			// below could be satisfied by two copies of the same path.
			_ = PeerProcessImageVerifier.TryResolveProcessImagePath(childHandle, out string childImage).Should().BeTrue();
			_ = Path.GetFileName(childImage).Should().BeEquivalentTo(Path.GetFileName(ChildImagePath));
			_ = childImage.Should().NotBeEquivalentTo(Environment.ProcessPath!, "the child must be a different image for this test to mean anything");

			IpcPeerAuthenticationResult forward = PeerProcessImageVerifier.VerifyProcessHandleImage(childHandle, Environment.ProcessPath!);
			_ = forward.Success.Should().BeFalse("the child is not this executable");
			_ = forward.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageMismatch);

			IpcPeerAuthenticationResult reverse = PeerProcessImageVerifier.VerifyProcessHandleImage(selfHandle, ChildImagePath);
			_ = reverse.Success.Should().BeFalse("and the reverse direction must reject too");
			_ = reverse.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageMismatch);
		}
		finally
		{
			_ = CloseHandle(childHandle);
			_ = CloseHandle(selfHandle);
		}
	}

	// (b) FAIL-CLOSED, and the distinction is the whole point: an unusable handle must
	// produce UNAVAILABLE (we could not obtain the truth), never a pass and never a
	// fallback to anything the peer said.
	[Fact]
	public void VerifyProcessHandleImage_ZeroHandle_FailsClosedAsUnavailable()
	{
		IpcPeerAuthenticationResult result = PeerProcessImageVerifier.VerifyProcessHandleImage(IntPtr.Zero, Environment.ProcessPath!);

		_ = result.Success.Should().BeFalse("an unqueryable handle must never authenticate");
		_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageUnavailable);
		_ = result.Failure.RedactedReason.Should().Be("ipc.peer.image-unavailable");
	}

	[Fact]
	public void VerifyClientProcessImage_InvalidPipeHandle_FailsClosedWithoutThrowing()
	{
		using SafePipeHandle invalid = new(IntPtr.Zero, ownsHandle: false);

		IpcPeerAuthenticationResult result = PeerProcessImageVerifier.VerifyClientProcessImage(invalid, Environment.ProcessPath!);

		_ = result.Success.Should().BeFalse();
		_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageUnavailable);
		_ = result.Failure.RedactedReason.Should().Be("ipc.peer.image-handle-invalid");
	}

	[Fact]
	public void VerifyClientProcessImage_ClosedPipeHandle_FailsClosedWithoutThrowing()
	{
		string pipeName = BuildUniquePipeName();
		NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		SafePipeHandle handle = server.SafePipeHandle;
		server.Dispose();

		_ = handle.IsClosed.Should().BeTrue("disposing the stream must close the handle this test then probes");

		IpcPeerAuthenticationResult result = PeerProcessImageVerifier.VerifyClientProcessImage(handle, Environment.ProcessPath!);

		_ = result.Success.Should().BeFalse("a closed handle is a rejection, not a crash and not a pass");
		_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageUnavailable);
	}

	[Fact]
	public void VerifyImagePath_BlankInputs_FailClosed()
	{
		_ = PeerProcessImageVerifier.VerifyImagePath(string.Empty, Environment.ProcessPath!).Failure!.Code
			.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageUnavailable);
		_ = PeerProcessImageVerifier.VerifyImagePath(Environment.ProcessPath!, string.Empty).Failure!.Code
			.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageUnavailable);
		_ = PeerProcessImageVerifier.VerifyImagePath("   ", Environment.ProcessPath!).Failure!.Code
			.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageUnavailable);
	}

	// The comparison is canonical and case-insensitive: a different SPELLING of the same
	// file must pass, so the anchor does not depend on how the path happened to be written.
	[Fact]
	public void VerifyImagePath_DifferentSpellingOfTheSameFile_Accepts()
	{
		string expected = Path.Combine(Path.GetTempPath(), "BroChoos.PeerImage.Fixture.exe");
		File.WriteAllText(expected, "fixture");

		try
		{
			string dotted = Path.Combine(Path.GetTempPath(), "sub", "..", "BroChoos.PeerImage.Fixture.exe");
			string upper = Path.Combine(Path.GetTempPath(), "BROCHOOS.PEERIMAGE.FIXTURE.EXE");

			_ = PeerProcessImageVerifier.VerifyImagePath(dotted, expected).Success.Should().BeTrue("a '..' spelling of the same file is the same file");
			_ = PeerProcessImageVerifier.VerifyImagePath(upper, expected).Success.Should().BeTrue("Windows paths compare case-insensitively");
		}
		finally
		{
			File.Delete(expected);
		}
	}

	[Fact]
	public void VerifyImagePath_DifferentFileName_RejectsAsMismatch()
	{
		string first = Path.Combine(Path.GetTempPath(), "BroChoos.PeerImage.One.exe");
		string second = Path.Combine(Path.GetTempPath(), "BroChoos.PeerImage.Two.exe");
		File.WriteAllText(first, "fixture");
		File.WriteAllText(second, "fixture");

		try
		{
			IpcPeerAuthenticationResult result = PeerProcessImageVerifier.VerifyImagePath(first, second);

			_ = result.Success.Should().BeFalse();
			_ = result.Failure!.Code.Should().Be(IpcPeerAuthenticationErrorCode.PeerProcessImageMismatch);
		}
		finally
		{
			File.Delete(first);
			File.Delete(second);
		}
	}

	private static string ChildImagePath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

	// A real, long-enough-lived child whose image is NOT this test host's. cmd.exe is
	// present on every supported Windows build; ping keeps it alive without a console.
	private static Process StartChildWithDifferentImage()
	{
		ProcessStartInfo startInfo = new(ChildImagePath)
		{
			Arguments = "/c ping -n 60 127.0.0.1 > nul",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
		};

		Process child = Process.Start(startInfo)!;
		_ = SpinWait.SpinUntil(() => child.HasExited || child.Id > 0, TimeSpan.FromSeconds(5));
		_ = child.HasExited.Should().BeFalse("the child must still be running when its handle is opened");
		return child;
	}

	// Process.Dispose only releases the handle; it does not stop the child. cmd.exe spawns
	// ping, so the whole tree is killed first, tolerating a child that already exited.
	private sealed class ChildProcessGuard(Process process) : IDisposable
	{
		public Process Process { get; } = process;

		public void Dispose()
		{
			try
			{
				if (!Process.HasExited)
				{
					Process.Kill(entireProcessTree: true);
					_ = Process.WaitForExit(5000);
				}
			}
			catch (InvalidOperationException)
			{
				// Already exited and reaped between the check and the kill.
			}
			catch (System.ComponentModel.Win32Exception)
			{
				// Access denied or exiting: the process is going away on its own.
			}
			finally
			{
				Process.Dispose();
			}
		}
	}

	private static string BuildUniquePipeName()
	{
		return $"BroChoos.PeerImage.Test.{Guid.NewGuid():N}";
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
