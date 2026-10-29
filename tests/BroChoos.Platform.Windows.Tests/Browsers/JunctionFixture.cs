using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BroChoos.Platform.Windows.Tests.Browsers;

// Creates an NTFS directory junction (mount-point reparse point) without a shell, a
// privilege, or Developer Mode — the same FSCTL_SET_REPARSE_POINT technique
// SystemStoragePathGateIntegrationTests uses. Returns null on success, else a short
// diagnostic so the caller can skip honestly instead of failing on a volume that refuses
// reparse points (FAT, some network or virtual volumes).
internal static class JunctionFixture
{
	private const uint GenericWrite = 0x40000000;
	private const uint FileFlagBackupSemantics = 0x02000000;
	private const uint FileFlagOpenReparsePoint = 0x00200000;
	private const uint FsctlSetReparsePoint = 0x000900A4;
	private const uint IoReparseTagMountPoint = 0xA0000003;

	internal static string? TryCreate(string link, string target)
	{
		string canonicalTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
		string substituteName = @"\??\" + canonicalTarget;
		byte[] substitute = Encoding.Unicode.GetBytes(substituteName);
		byte[] print = Encoding.Unicode.GetBytes(canonicalTarget);
		const int headerSize = 16;
		const int pathBufferHeaderSize = 8;
		int pathBufferLength = substitute.Length + sizeof(char) + print.Length + sizeof(char);
		byte[] buffer = new byte[headerSize + pathBufferLength];
		BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), IoReparseTagMountPoint);
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)(pathBufferHeaderSize + pathBufferLength)));
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substitute.Length));
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substitute.Length + sizeof(char))));
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)print.Length));
		substitute.CopyTo(buffer, headerSize);
		print.CopyTo(buffer, headerSize + substitute.Length + sizeof(char));

		_ = Directory.CreateDirectory(link);
		using SafeFileHandle handle = CreateFile(
			link,
			GenericWrite,
			FileShare.None,
			IntPtr.Zero,
			FileMode.Open,
			FileFlagBackupSemantics | FileFlagOpenReparsePoint,
			IntPtr.Zero);
		if (handle.IsInvalid)
		{
			int error = Marshal.GetLastWin32Error();
			Directory.Delete(link, recursive: false);
			return $"CreateFile native code {error}";
		}

		if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, checked((uint)buffer.Length), IntPtr.Zero, 0, out _, IntPtr.Zero))
		{
			int error = Marshal.GetLastWin32Error();
			handle.Dispose();
			Directory.Delete(link, recursive: false);
			return $"FSCTL_SET_REPARSE_POINT native code {error}";
		}

		return null;
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateFile(
		string fileName,
		uint desiredAccess,
		FileShare shareMode,
		IntPtr securityAttributes,
		FileMode creationDisposition,
		uint flagsAndAttributes,
		IntPtr templateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeviceIoControl(
		SafeFileHandle device,
		uint ioControlCode,
		byte[] inputBuffer,
		uint inputBufferSize,
		IntPtr outputBuffer,
		uint outputBufferSize,
		out uint bytesReturned,
		IntPtr overlapped);
}
