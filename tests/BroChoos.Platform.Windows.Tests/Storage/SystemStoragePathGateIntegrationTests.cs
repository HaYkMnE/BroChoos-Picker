using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

public sealed class SystemStoragePathGateIntegrationTests
{
	private const int ErrorInvalidFunction = 1;
	private const int ErrorNotSupported = 50;
	private const int ErrorAlreadyExists = 183;
	private const int ErrorPrivilegeNotHeld = 1314;
	private const uint FileFlagBackupSemantics = 0x02000000;
	private const uint FileFlagOpenReparsePoint = 0x00200000;
	private const uint FsctlSetReparsePoint = 0x000900A4;
	private const uint GenericWrite = 0x40000000;
	private const uint IoReparseTagMountPoint = 0xA0000003;
	private const int RootCreationAttempts = 8;
	private const string ForcedProbeEnvironmentVariable = "BROCHOOS_STORAGE_PATH_GATE_PROBE";
	private const string ForcedRootIdEnvironmentVariable = "BROCHOOS_STORAGE_PATH_GATE_ROOT_ID";
	private const string ForcedTempBaseEnvironmentVariable = "BROCHOOS_STORAGE_PATH_GATE_TEMP_BASE";
	private const string MetacharacterTempBasePrefix = "BroChoos SystemStoragePathGate SAFE&ver.(native)^.";
	private const string OwnershipSentinelName = ".brochoos-test-root-owner";

	[SkippableFact]
	public void ReadImmediateReparseTarget_TwoHopDirectoryLinks_ReturnsImmediateTarget()
	{
		if (!OperatingSystem.IsWindows())
		{
			Skip.If(true, "Requires Windows directory reparse-point semantics.");
		}

		ForcedProbe forcedProbe = GetForcedProbe();
		string tempRoot = GetTestTempBase();
		RootClaim? rootClaim = null;
		string realTarget = string.Empty;
		string linkB = string.Empty;
		string linkA = string.Empty;

		try
		{
			rootClaim = ClaimUniqueRoot(tempRoot);
			string root = rootClaim.Root;
			realTarget = Path.Combine(root, "RealTarget");
			linkB = Path.Combine(root, "LinkB");
			linkA = Path.Combine(root, "LinkA");
			_ = Directory.CreateDirectory(realTarget);
			DirectoryLinkKind linkKind = CreateDirectoryLinkOrSkip(linkB, realTarget, forcedProbe);
			Assert.False(
				forcedProbe == ForcedProbe.AssertionFailureAfterFirstLink,
				"Forced assertion failure after partial topology setup.");
			_ = CreateDirectoryLinkOrSkip(linkA, linkB, linkKind);

			SystemStoragePathGate gate = new();
			_ = gate.Inspect(linkA).Should().Be(StoragePathEntry.ReparsePoint);
			_ = gate.Inspect(linkB).Should().Be(StoragePathEntry.ReparsePoint);

			string? immediateFromARaw = gate.ReadImmediateReparseTarget(linkA);
			string? immediateFromBRaw = gate.ReadImmediateReparseTarget(linkB);
			_ = immediateFromARaw.Should().NotBeNull();
			_ = immediateFromBRaw.Should().NotBeNull();
			string immediateFromA = Canonicalize(immediateFromARaw!);
			string immediateFromB = Canonicalize(immediateFromBRaw!);
			string canonicalLinkB = Canonicalize(linkB);
			string canonicalRealTarget = Canonicalize(realTarget);

			_ = immediateFromA.Should().BeEquivalentTo(canonicalLinkB);
			_ = immediateFromA.Should().NotBeEquivalentTo(canonicalRealTarget);
			_ = immediateFromB.Should().BeEquivalentTo(canonicalRealTarget);
		}
		finally
		{
			if (rootClaim is not null)
			{
				CleanupClaimedRoot(rootClaim, linkA, linkB, realTarget);
			}
		}
	}

	[Fact]
	public void TryCreateJunction_MetacharacterPathAndPoisonedComSpec_DoesNotLaunchAShell()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		RootClaim? tempBaseClaim = null;
		RootClaim? rootClaim = null;
		string target = string.Empty;
		string link = string.Empty;
		string? originalComSpec = Environment.GetEnvironmentVariable("ComSpec");

		try
		{
			tempBaseClaim = ClaimUniqueRoot(Canonicalize(Path.GetTempPath()), MetacharacterTempBasePrefix);
			rootClaim = ClaimUniqueRoot(tempBaseClaim.Root);
			target = Path.Combine(rootClaim.Root, "Real Target");
			link = Path.Combine(rootClaim.Root, "Link SAFE&ver");
			_ = Directory.CreateDirectory(target);
			Environment.SetEnvironmentVariable(
				"ComSpec",
				Path.Combine(rootClaim.Root, "poisoned-command-processor.exe"));

			JunctionAttempt attempt = TryCreateJunction(link, target, ForcedProbe.None);

			_ = attempt.Outcome.Should().Be(JunctionOutcome.Created, attempt.Diagnostic);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ComSpec", originalComSpec);
			try
			{
				if (rootClaim is not null)
				{
					CleanupClaimedRoot(rootClaim, link, string.Empty, target);
				}
			}
			finally
			{
				if (tempBaseClaim is not null
					&& (rootClaim is null || !TryGetAttributes(rootClaim.Root, out _)))
				{
					CleanupClaimedRoot(tempBaseClaim, string.Empty, string.Empty, string.Empty);
				}
			}
		}
	}

	[Fact]
	public void ClaimUniqueRoot_PreExistingForcedRootWithForeignContent_FailsAndLeavesItUntouched()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		string tempBase = Path.Combine(
			Canonicalize(Path.GetTempPath()),
			$"BroChoos forced-root probe {Guid.NewGuid():N}");
		string forcedId = Guid.NewGuid().ToString("N");
		string root = Path.Combine(tempBase, $"BroChoos.SystemStoragePathGate.{forcedId}");
		string foreignSentinel = Path.Combine(root, OwnershipSentinelName);
		string foreignContent = Path.Combine(root, "foreign-content.txt");
		string? originalRootId = Environment.GetEnvironmentVariable(ForcedRootIdEnvironmentVariable);

		try
		{
			_ = Directory.CreateDirectory(root);
			File.WriteAllText(foreignSentinel, "foreign-sentinel", Encoding.UTF8);
			File.WriteAllText(foreignContent, "foreign-content", Encoding.UTF8);
			Environment.SetEnvironmentVariable(ForcedRootIdEnvironmentVariable, forcedId);

			Action claim = () => ClaimUniqueRoot(tempBase);

			_ = claim.Should().Throw<IOException>()
				.WithMessage("*already exists*");
			_ = File.ReadAllText(foreignSentinel, Encoding.UTF8).Should().Be("foreign-sentinel");
			_ = File.ReadAllText(foreignContent, Encoding.UTF8).Should().Be("foreign-content");
		}
		finally
		{
			Environment.SetEnvironmentVariable(ForcedRootIdEnvironmentVariable, originalRootId);
			File.Delete(foreignContent);
			File.Delete(foreignSentinel);
			DeleteOrdinaryDirectoryIfPresent(root);
			DeleteOrdinaryDirectoryIfPresent(tempBase);
		}
	}

	[Fact]
	public void CleanupClaimedRoot_MismatchedSentinel_LeavesClaimedTreeUntouched()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		RootClaim claim = ClaimUniqueRoot(Canonicalize(Path.GetTempPath()));
		string realTarget = Path.Combine(claim.Root, "RealTarget");

		try
		{
			_ = Directory.CreateDirectory(realTarget);
			File.WriteAllText(claim.SentinelPath, "foreign-sentinel", Encoding.ASCII);

			Action cleanup = () => CleanupClaimedRoot(claim, string.Empty, string.Empty, realTarget);

			_ = cleanup.Should().Throw<IOException>()
				.WithMessage("*sentinel is missing or mismatched*");
			_ = Directory.Exists(realTarget).Should().BeTrue();
			_ = File.ReadAllText(claim.SentinelPath, Encoding.ASCII).Should().Be("foreign-sentinel");
		}
		finally
		{
			DeleteOrdinaryDirectoryIfPresent(realTarget);
			File.Delete(claim.SentinelPath);
			DeleteOrdinaryDirectoryIfPresent(claim.Root);
		}
	}

	private static DirectoryLinkKind CreateDirectoryLinkOrSkip(
		string link,
		string target,
		ForcedProbe forcedProbe)
	{
		JunctionAttempt junction = TryCreateJunction(link, target, forcedProbe);
		if (junction.Outcome == JunctionOutcome.Created)
		{
			return DirectoryLinkKind.Junction;
		}

		if (junction.Outcome == JunctionOutcome.UnrecognizedFailure)
		{
			throw new IOException($"Junction creation failed unexpectedly ({junction.Diagnostic}).");
		}

		try
		{
			CreateSymbolicLink(link, target, forcedProbe);
			return DirectoryLinkKind.SymbolicLink;
		}
		catch (Exception failure) when (IsRecognizedSymbolicLinkCapabilityFailure(failure))
		{
			Skip.If(
				true,
				$"Directory links are unavailable ({junction.Diagnostic}; " +
				$"symbolic-link {failure.GetType().Name}, native code {GetNativeErrorCode(failure)}).");
			throw new InvalidOperationException("The dynamic-skip helper returned unexpectedly.");
		}
	}

	private static DirectoryLinkKind CreateDirectoryLinkOrSkip(
		string link,
		string target,
		DirectoryLinkKind requiredKind)
	{
		if (requiredKind == DirectoryLinkKind.Junction)
		{
			JunctionAttempt junction = TryCreateJunction(link, target, ForcedProbe.None);
			return junction.Outcome != JunctionOutcome.Created
				? throw new IOException(
					$"The second junction could not be created after the capability probe succeeded ({junction.Diagnostic}).")
				: DirectoryLinkKind.Junction;
		}

		try
		{
			CreateSymbolicLink(link, target, ForcedProbe.None);
			return DirectoryLinkKind.SymbolicLink;
		}
		catch (Exception failure) when (IsRecognizedSymbolicLinkCapabilityFailure(failure))
		{
			throw new IOException(
				$"Symbolic-link capability disappeared after its probe succeeded " +
				$"({failure.GetType().Name}, native code {GetNativeErrorCode(failure)}).",
				failure);
		}
	}

	private static JunctionAttempt TryCreateJunction(
		string link,
		string target,
		ForcedProbe forcedProbe)
	{
		if (forcedProbe == ForcedProbe.CapabilityUnavailable)
		{
			return JunctionAttempt.RecognizedCapabilityUnavailable("forced capability probe");
		}

		if (forcedProbe == ForcedProbe.UnrecognizedJunctionFailure)
		{
			return JunctionAttempt.UnrecognizedFailure("forced unrecognized junction failure");
		}

		string? pathRoot = Path.GetPathRoot(link);
		string canonicalTarget;
		string? canonicalTargetRoot;
		try
		{
			canonicalTarget = Canonicalize(target);
			FileAttributes targetAttributes = File.GetAttributes(canonicalTarget);
			if ((targetAttributes & FileAttributes.Directory) == 0)
			{
				return JunctionAttempt.UnrecognizedFailure("target is not an existing directory");
			}

			canonicalTargetRoot = Path.GetPathRoot(canonicalTarget);
			if (string.IsNullOrEmpty(canonicalTargetRoot)
				|| new DriveInfo(canonicalTargetRoot).DriveType == DriveType.Network)
			{
				return JunctionAttempt.UnrecognizedFailure("target is not on a local volume");
			}
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			return JunctionAttempt.UnrecognizedFailure(
				$"target probe {failure.GetType().Name}, native code {GetNativeErrorCode(failure)}");
		}

		if (string.IsNullOrEmpty(pathRoot)
			|| !string.Equals(pathRoot, canonicalTargetRoot, StringComparison.OrdinalIgnoreCase))
		{
			return JunctionAttempt.UnrecognizedFailure("link and target are not on the same local volume");
		}

		try
		{
			string driveFormat = new DriveInfo(pathRoot).DriveFormat;
			if (!string.Equals(driveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
			{
				return JunctionAttempt.RecognizedCapabilityUnavailable("non-NTFS volume");
			}
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			return JunctionAttempt.UnrecognizedFailure(
				$"volume probe {failure.GetType().Name}, native code {GetNativeErrorCode(failure)}");
		}

		string substituteName = @"\??\" + canonicalTarget;
		byte[] substituteNameBytes = Encoding.Unicode.GetBytes(substituteName);
		byte[] printNameBytes = Encoding.Unicode.GetBytes(canonicalTarget);
		const int mountPointHeaderSize = 16;
		const int pathBufferHeaderSize = 8;
		int pathBufferLength = substituteNameBytes.Length + sizeof(char) + printNameBytes.Length + sizeof(char);
		byte[] reparseBuffer = new byte[mountPointHeaderSize + pathBufferLength];
		BinaryPrimitives.WriteUInt32LittleEndian(reparseBuffer.AsSpan(0, 4), IoReparseTagMountPoint);
		BinaryPrimitives.WriteUInt16LittleEndian(
			reparseBuffer.AsSpan(4, 2),
			checked((ushort)(pathBufferHeaderSize + pathBufferLength)));
		BinaryPrimitives.WriteUInt16LittleEndian(reparseBuffer.AsSpan(8, 2), 0);
		BinaryPrimitives.WriteUInt16LittleEndian(reparseBuffer.AsSpan(10, 2), checked((ushort)substituteNameBytes.Length));
		BinaryPrimitives.WriteUInt16LittleEndian(
			reparseBuffer.AsSpan(12, 2),
			checked((ushort)(substituteNameBytes.Length + sizeof(char))));
		BinaryPrimitives.WriteUInt16LittleEndian(reparseBuffer.AsSpan(14, 2), checked((ushort)printNameBytes.Length));
		substituteNameBytes.CopyTo(reparseBuffer, mountPointHeaderSize);
		printNameBytes.CopyTo(reparseBuffer, mountPointHeaderSize + substituteNameBytes.Length + sizeof(char));

		try
		{
			_ = Directory.CreateDirectory(link);
			using SafeFileHandle handle = CreateFile(
				link,
				GenericWrite,
				FileShare.Read | FileShare.Write | FileShare.Delete,
				IntPtr.Zero,
				FileMode.Open,
				FileFlagBackupSemantics | FileFlagOpenReparsePoint,
				IntPtr.Zero);
			return handle.IsInvalid
				? NativeJunctionFailure("CreateFile", Marshal.GetLastWin32Error())
				: SetAndValidateJunction(handle, reparseBuffer, link);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			return JunctionAttempt.UnrecognizedFailure(
				$"native setup {failure.GetType().Name}, native code {GetNativeErrorCode(failure)}");
		}
		finally
		{
			if (TryGetAttributes(link, out FileAttributes attributes)
				&& (attributes & FileAttributes.ReparsePoint) == 0)
			{
				Directory.Delete(link, recursive: false);
			}
		}
	}

	private static JunctionAttempt SetAndValidateJunction(
		SafeFileHandle handle,
		byte[] reparseBuffer,
		string link)
	{
		return DeviceIoControl(
			handle,
			FsctlSetReparsePoint,
			reparseBuffer,
			checked((uint)reparseBuffer.Length),
			IntPtr.Zero,
			0,
			out _,
			IntPtr.Zero)
			? ValidateCreatedJunction(link)
			: NativeJunctionFailure("FSCTL_SET_REPARSE_POINT", Marshal.GetLastWin32Error());
	}

	private static JunctionAttempt ValidateCreatedJunction(string link)
	{
		return !TryGetAttributes(link, out FileAttributes attributes)
			|| (attributes & FileAttributes.ReparsePoint) == 0
			? JunctionAttempt.UnrecognizedFailure("native operation succeeded without a reparse point")
			: JunctionAttempt.Created();
	}

	private static JunctionAttempt NativeJunctionFailure(string operation, int errorCode)
	{
		return errorCode is ErrorInvalidFunction or ErrorNotSupported or ErrorPrivilegeNotHeld
			? JunctionAttempt.RecognizedCapabilityUnavailable($"{operation} native code {errorCode}")
			: JunctionAttempt.UnrecognizedFailure($"{operation} native code {errorCode}");
	}

	private static void CreateSymbolicLink(string link, string target, ForcedProbe forcedProbe)
	{
		if (forcedProbe == ForcedProbe.CapabilityUnavailable)
		{
			throw new Win32Exception(ErrorPrivilegeNotHeld);
		}

		_ = Directory.CreateSymbolicLink(link, target);
	}

	private static bool IsRecognizedSymbolicLinkCapabilityFailure(Exception failure)
	{
		return failure is PlatformNotSupportedException
			|| IsRecognizedSymbolicLinkNativeCapabilityFailure(failure);
	}

	private static bool IsRecognizedSymbolicLinkNativeCapabilityFailure(Exception failure)
	{
		return failure is IOException
			or Win32Exception
			or UnauthorizedAccessException
			&& GetNativeErrorCode(failure) is
				ErrorInvalidFunction
				or ErrorNotSupported
				or ErrorPrivilegeNotHeld;
	}

	private static int GetNativeErrorCode(Exception failure)
	{
		return failure is Win32Exception win32
			? win32.NativeErrorCode
			: failure.HResult & 0xFFFF;
	}

	private static ForcedProbe GetForcedProbe()
	{
		return Environment.GetEnvironmentVariable(ForcedProbeEnvironmentVariable) switch
		{
			null or "" => ForcedProbe.None,
			"capability-unavailable" => ForcedProbe.CapabilityUnavailable,
			"assertion-failure-after-first-link" => ForcedProbe.AssertionFailureAfterFirstLink,
			"unrecognized-junction-failure" => ForcedProbe.UnrecognizedJunctionFailure,
			_ => throw new InvalidOperationException("Unknown forced integration-test probe."),
		};
	}

	private static string GetTestTempBase()
	{
		string? forcedTempBase = Environment.GetEnvironmentVariable(ForcedTempBaseEnvironmentVariable);
		return Canonicalize(string.IsNullOrEmpty(forcedTempBase) ? Path.GetTempPath() : forcedTempBase);
	}

	private static RootClaim ClaimUniqueRoot(
		string tempBase,
		string rootNamePrefix = "BroChoos.SystemStoragePathGate.")
	{
		string canonicalTempBase = Canonicalize(tempBase);
		string? forcedRootId = Environment.GetEnvironmentVariable(ForcedRootIdEnvironmentVariable);
		int attempts = string.IsNullOrEmpty(forcedRootId) ? RootCreationAttempts : 1;
		_ = Directory.CreateDirectory(canonicalTempBase);

		for (int attempt = 0; attempt < attempts; attempt++)
		{
			Guid rootId = string.IsNullOrEmpty(forcedRootId)
				? Guid.NewGuid()
				: Guid.ParseExact(forcedRootId, "N");
			string root = Path.Combine(canonicalTempBase, $"{rootNamePrefix}{rootId:N}");
			if (!CreateDirectory(root, IntPtr.Zero))
			{
				int errorCode = Marshal.GetLastWin32Error();
				if (errorCode == ErrorAlreadyExists && string.IsNullOrEmpty(forcedRootId))
				{
					continue;
				}

				throw new IOException(
					errorCode == ErrorAlreadyExists
						? "The forced integration-test root already exists; ownership was not claimed."
						: $"CreateDirectory failed with native code {errorCode}.",
					new Win32Exception(errorCode));
			}

			string sentinelPath = Path.Combine(root, OwnershipSentinelName);
			string sentinelIdentity = Guid.NewGuid().ToString("N");
			try
			{
				using (FileStream sentinel = new(
					sentinelPath,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None,
					bufferSize: 128,
					FileOptions.WriteThrough))
				{
					byte[] identityBytes = Encoding.ASCII.GetBytes(sentinelIdentity);
					sentinel.Write(identityBytes);
					sentinel.Flush(flushToDisk: true);
				}

				VerifyDirectOrdinaryClaimedRoot(root, canonicalTempBase, sentinelPath);
				return new(root, sentinelPath, sentinelIdentity);
			}
			catch
			{
				// The directory-name create is the smallest unavoidable Win32 race. Without
				// the exclusive sentinel identity, this process never owns or cleans the root.
				throw;
			}
		}

		throw new IOException($"Unable to allocate a unique integration-test root after {attempts} attempts.");
	}

	private static void VerifyDirectOrdinaryClaimedRoot(string root, string expectedParent, string sentinelPath)
	{
		DirectoryInfo rootInfo = new(root);
		string? actualParent = rootInfo.Parent?.FullName;
		if (!string.Equals(Canonicalize(actualParent ?? string.Empty), expectedParent, StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("The integration-test root is not a direct child of its selected temp base.");
		}

		FileAttributes attributes = File.GetAttributes(root);
		if ((attributes & FileAttributes.Directory) == 0
			|| (attributes & FileAttributes.ReparsePoint) != 0)
		{
			throw new IOException("The integration-test root is not an ordinary directory.");
		}

		string[] entries = Directory.GetFileSystemEntries(root);
		if (entries.Length != 1
			|| !string.Equals(entries[0], sentinelPath, StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("The claimed integration-test root contains an unexpected entry.");
		}
	}

	private static void CleanupClaimedRoot(
		RootClaim claim,
		string linkA,
		string linkB,
		string realTarget)
	{
		if (!SentinelMatches(claim))
		{
			throw new IOException("Cleanup refused because the root ownership sentinel is missing or mismatched.");
		}

		DeleteDirectoryLinkIfPresent(linkA);
		DeleteDirectoryLinkIfPresent(linkB);
		DeleteOrdinaryDirectoryIfPresent(realTarget);

		if (!SentinelMatches(claim))
		{
			throw new IOException("Cleanup refused because the root ownership sentinel changed.");
		}

		File.Delete(claim.SentinelPath);
		DeleteOrdinaryDirectoryIfPresent(claim.Root);
		EnsurePathAbsent(claim.Root);
	}

	private static bool SentinelMatches(RootClaim claim)
	{
		try
		{
			return string.Equals(
				File.ReadAllText(claim.SentinelPath, Encoding.ASCII),
				claim.SentinelIdentity,
				StringComparison.Ordinal);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static void EnsurePathAbsent(string path)
	{
		if (TryGetAttributes(path, out _))
		{
			throw new IOException("The unique integration-test root was not removed.");
		}
	}

	private static string Canonicalize(string path)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
	}

	private static void DeleteDirectoryLinkIfPresent(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		if (!TryGetAttributes(path, out FileAttributes attributes))
		{
			return;
		}

		if ((attributes & FileAttributes.ReparsePoint) == 0)
		{
			throw new IOException("Cleanup refused to delete a directory-link path that is not a reparse point.");
		}

		Directory.Delete(path, recursive: false);
	}

	private static void DeleteOrdinaryDirectoryIfPresent(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		if (!TryGetAttributes(path, out FileAttributes attributes))
		{
			return;
		}

		if ((attributes & FileAttributes.ReparsePoint) != 0)
		{
			throw new IOException("Cleanup refused to delete an unexpected reparse point as an ordinary directory.");
		}

		Directory.Delete(path, recursive: false);
	}

	private static bool TryGetAttributes(string path, out FileAttributes attributes)
	{
		try
		{
			attributes = File.GetAttributes(path);
			return true;
		}
		catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
		{
			attributes = default;
			return false;
		}
	}

	private enum DirectoryLinkKind
	{
		Junction,
		SymbolicLink,
	}

	private enum ForcedProbe
	{
		None,
		CapabilityUnavailable,
		AssertionFailureAfterFirstLink,
		UnrecognizedJunctionFailure,
	}

	private enum JunctionOutcome
	{
		Created,
		RecognizedCapabilityUnavailable,
		UnrecognizedFailure,
	}

	private sealed record RootClaim(string Root, string SentinelPath, string SentinelIdentity);

	private sealed record JunctionAttempt(JunctionOutcome Outcome, string Diagnostic)
	{
		public static JunctionAttempt Created()
		{
			return new(JunctionOutcome.Created, "created");
		}

		public static JunctionAttempt RecognizedCapabilityUnavailable(string diagnostic)
		{
			return new(JunctionOutcome.RecognizedCapabilityUnavailable, diagnostic);
		}

		public static JunctionAttempt UnrecognizedFailure(string diagnostic)
		{
			return new(JunctionOutcome.UnrecognizedFailure, diagnostic);
		}
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

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
