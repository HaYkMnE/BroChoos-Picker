using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

public sealed class PortableDataRootSafetyTests
{
	public static TheoryData<string, string> UnsafeOrigins =>
		new()
		{
			{ @"origin-relative-secret\Data", "origin-relative-secret" },
			{ @"C:origin-drive-relative-secret", "origin-drive-relative-secret" },
			{ @"\\server\share\origin-unc-secret", "origin-unc-secret" },
			{ @"//server/share/origin-forward-unc-secret", "origin-forward-unc-secret" },
			{ @"\\?\C:\origin-device-secret", "origin-device-secret" },
			{ @"\\.\C:\origin-dot-device-secret", "origin-dot-device-secret" },
			{ @"\\?\UNC\server\share\origin-device-unc-secret", "origin-device-unc-secret" },
		};

	public static TheoryData<string, string> UnsafeComponents =>
		new()
		{
		{ @"C:\CON\component-con-secret", "component-con-secret" },
		{ @"C:\con.txt\component-con-extension-secret", "component-con-extension-secret" },
		{ @"C:\PRN\component-prn-secret", "component-prn-secret" },
		{ @"C:\AUX.log\component-aux-secret", "component-aux-secret" },
		{ @"C:\NUL\component-nul-secret", "component-nul-secret" },
		{ @"C:\CLOCK$.txt\component-clock-secret", "component-clock-secret" },
		{ @"C:\COM1\component-com-secret", "component-com-secret" },
		{ @"C:\com9.log\component-com-extension-secret", "component-com-extension-secret" },
		{ @"C:\LPT1\component-lpt-secret", "component-lpt-secret" },
		{ @"C:\lpt9.log\component-lpt-extension-secret", "component-lpt-extension-secret" },
		{ @"C:\safe:component-ads-secret", "component-ads-secret" },
		{ @"C:\safe\component-child-ads-secret:stream", "component-child-ads-secret" },
		{ @"C:\component-trailing-dot-secret.", "component-trailing-dot-secret" },
		{ @"C:\ \component-space-secret", "component-space-secret" },
		{ @"C:\. \component-dot-space-secret", "component-dot-space-secret" },
		{ @"C:\component-trailing-space-secret \child", "component-trailing-space-secret" },
		{ @"C:\CONIN$\component-conin-secret", "component-conin-secret" },
		{ @"C:\CONOUT$.txt\component-conout-secret", "component-conout-secret" },
		{ @"C:\COM¹\component-com-superscript-secret", "component-com-superscript-secret" },
		{ @"C:\LPT³.log\component-lpt-superscript-secret", "component-lpt-superscript-secret" },
		};

	public static TheoryData<string, string> UnsafeReparseTargets =>
		new()
		{
		{ @"target-relative-secret\Data", "target-relative-secret" },
		{ @"D:target-drive-relative-secret", "target-drive-relative-secret" },
		{ @"\\server\share\target-unc-secret", "target-unc-secret" },
		{ @"\\?\D:\target-device-secret", "target-device-secret" },
		{ @"D:\CON\target-reserved-secret", "target-reserved-secret" },
		{ @"D:\target-ads-secret:stream", "target-ads-secret" },
		{ @"D:\target-trailing-dot-secret.", "target-trailing-dot-secret" },
		{ @"D:\target-trailing-space-secret ", "target-trailing-space-secret" },
		{ "D:\\target-nul-secret\0x", "target-nul-secret" },
		{ @"D:\target-too-long-secret" + new string('L', 40000), "target-too-long-secret" },
		};

	public static TheoryData<DriveType> AllowedDriveTypes =>
	[
		DriveType.Fixed,
		DriveType.Removable,
		DriveType.Ram,
	];

	public static TheoryData<DriveType> RefusedDriveTypes =>
	[
		DriveType.Network,
		DriveType.CDRom,
		DriveType.Unknown,
		DriveType.NoRootDirectory,
	];

	[Theory]
	[MemberData(nameof(UnsafeOrigins))]
	public void ExplicitPortableData_UnsafeOrigin_IsRejectedBeforeAnyProbeOrProvisioning(
		string suppliedValue,
		string secretMarker)
	{
		ThrowingDriveTypeProbe driveTypeProbe = new(new InvalidOperationException("drive probe must not run"));
		ThrowingPathInspector pathInspector = new(new InvalidOperationException("path inspection must not run"));
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(suppliedValue, fileSystem, driveTypeProbe, pathInspector);
		}

		AssertSafetyRejection(act, suppliedValue, fileSystem, secretMarker);
		_ = driveTypeProbe.Calls.Should().Be(0);
		_ = pathInspector.Calls.Should().Be(0);
	}

	[Fact]
	public void ExplicitPortableData_BareVolumeRoot_IsRejectedBeforeDiskInspection()
	{
		const string SuppliedValue = @" C:\ ";
		ThrowingDriveTypeProbe driveTypeProbe = new(new InvalidOperationException("drive probe must not run"));
		ThrowingPathInspector pathInspector = new(new InvalidOperationException("path inspection must not run"));
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(SuppliedValue, fileSystem, driveTypeProbe, pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem);
		_ = driveTypeProbe.Calls.Should().Be(0);
		_ = pathInspector.Calls.Should().Be(0);
	}

	[Theory]
	[MemberData(nameof(UnsafeComponents))]
	public void ExplicitPortableData_UnsafeWindowsComponent_IsRejectedBeforeDiskInspection(
		string suppliedValue,
		string secretMarker)
	{
		ThrowingDriveTypeProbe driveTypeProbe = new(new InvalidOperationException("drive probe must not run"));
		ThrowingPathInspector pathInspector = new(new InvalidOperationException("path inspection must not run"));
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(suppliedValue, fileSystem, driveTypeProbe, pathInspector);
		}

		AssertSafetyRejection(act, suppliedValue, fileSystem, secretMarker);
		_ = driveTypeProbe.Calls.Should().Be(0);
		_ = pathInspector.Calls.Should().Be(0);
	}

	[Theory]
	[MemberData(nameof(AllowedDriveTypes))]
	public void ExplicitPortableData_AllowedVolumeAndOrdinaryComponents_AreProvisioned(DriveType driveType)
	{
		FakeDriveTypeProbe driveTypeProbe = new(driveType);
		FakePathInspector pathInspector = SafePathInspector(@"C:\");
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = CreateResolver(
			@" C:\ordinary\..\Data.v1\child ",
			fileSystem,
			driveTypeProbe,
			pathInspector);

		_ = resolver.Root.Should().Be(@"C:\Data.v1\child");
		_ = fileSystem.CreatedDirectories.Should().StartWith(@"C:\Data.v1\child");
	}

	[Theory]
	[MemberData(nameof(RefusedDriveTypes))]
	public void ExplicitPortableData_RefusedVolumeType_FailsClosedWithoutProvisioning(DriveType driveType)
	{
		const string SuppliedValue = @"C:\Data";
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
			SuppliedValue,
			fileSystem,
			new FakeDriveTypeProbe(driveType),
			SafePathInspector(@"C:\"));
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem);
	}

	[Fact]
	public void ExplicitPortableData_OrdinaryDriveProbeFailure_FailsClosedWithoutProvisioning()
	{
		const string SuppliedValue = @"C:\drive-probe-value-secret";
		const string FailureMarker = "drive-probe-detail-secret";
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
			SuppliedValue,
			fileSystem,
			new ThrowingDriveTypeProbe(new IOException(FailureMarker)),
			SafePathInspector(@"C:\"));
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem, "drive-probe-value-secret", FailureMarker);
	}

	[Theory]
	[InlineData("out-of-memory")]
	[InlineData("stack-overflow")]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201", Justification = "The test deliberately constructs reserved fatal exception types to verify the resolver does not swallow fatal failures.")]
	public void ExplicitPortableData_FatalDriveProbeFailure_IsNotSwallowed(string failureKind)
	{
		Exception failure = failureKind == "out-of-memory"
			? new OutOfMemoryException()
			: new StackOverflowException();
		RecordingFileSystem fileSystem = new();
		Action act = () => _ = CreateResolver(
			@"C:\Data",
			fileSystem,
			new ThrowingDriveTypeProbe(failure),
			SafePathInspector(@"C:\"));

		Exception thrown = act.Should().Throw<Exception>().Which;
		_ = thrown.GetType().Should().Be(failure.GetType());
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
	}

	[Theory]
	[InlineData("inspect", "out-of-memory")]
	[InlineData("inspect", "stack-overflow")]
	[InlineData("resolve", "out-of-memory")]
	[InlineData("resolve", "stack-overflow")]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201", Justification = "The test deliberately constructs reserved fatal exception types to verify the resolver does not swallow fatal failures.")]
	public void ExplicitPortableData_FatalPathInspectorFailure_IsNotSwallowed(
		string operation,
		string failureKind)
	{
		Exception failure = failureKind == "out-of-memory"
			? new OutOfMemoryException()
			: new StackOverflowException();
		RecordingFileSystem fileSystem = new();
		IStoragePathGate pathInspector = operation == "inspect"
			? new ThrowingPathInspector(failure)
			: ReparseWithResolveFailure(failure);
		Action act = () => _ = CreateResolver(
			@"C:\Linked\fatal-inspector-value-secret",
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);

		Exception thrown = act.Should().Throw<Exception>().Which;
		_ = thrown.GetType().Should().Be(failure.GetType());
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
	}

	[Fact]
	public void ExplicitPortableData_NonexistentLeafBelowSafeAncestors_IsAllowed()
	{
		FakePathInspector pathInspector = SafePathInspector(@"C:\", @"C:\Existing");
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = CreateResolver(
			@"C:\Existing\New\Leaf",
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);

		_ = resolver.Root.Should().Be(@"C:\Existing\New\Leaf");
		_ = pathInspector.InspectedPaths.Should().Equal(@"C:\", @"C:\Existing", @"C:\Existing\New");
		_ = fileSystem.CreatedDirectories.Should().HaveCount(4);
	}

	[Fact]
	public void ExplicitPortableData_ReparseAncestorWithSafeExistingImmediateTarget_IsAllowed()
	{
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		driveTypeProbe.Set(@"D:\", DriveType.Removable);
		FakePathInspector pathInspector = SafePathInspector(
			@"C:\",
			@"D:\",
			@"D:\Actual",
			@"D:\Actual\Data");
		pathInspector.SetReparsePoint(@"C:\Linked", @"D:\Actual\Data");
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = CreateResolver(
			@"C:\Linked\Leaf",
			fileSystem,
			driveTypeProbe,
			pathInspector);

		_ = resolver.Root.Should().Be(@"C:\Linked\Leaf");
		_ = driveTypeProbe.ProbedRoots.Should().Contain([@"C:\", @"D:\"]);
		_ = fileSystem.CreatedDirectories.Should().HaveCount(4);
	}

	[Fact]
	public void ExplicitPortableData_ReparseLeafResolvingToBareVolumeRoot_IsRejected()
	{
		const string SuppliedValue = @"C:\bare-target-value-secret";
		FakePathInspector pathInspector = SafePathInspector(@"C:\", @"D:\");
		pathInspector.SetReparsePoint(SuppliedValue, @"D:\ordinary\..");
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
				SuppliedValue,
				fileSystem,
				new FakeDriveTypeProbe(DriveType.Fixed),
				pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem, "bare-target-value-secret");
	}

	[Fact]
	public void ExplicitPortableData_ReparseAncestorResolvingToBareVolumeRootWithSafeSuffix_IsAllowed()
	{
		FakePathInspector pathInspector = SafePathInspector(@"C:\", @"D:\");
		pathInspector.SetReparsePoint(@"C:\Linked", @"D:\");
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = CreateResolver(
			@"C:\Linked\Leaf",
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);

		_ = resolver.Root.Should().Be(@"C:\Linked\Leaf");
		_ = pathInspector.InspectedPaths.Should().Contain(@"D:\Leaf");
		_ = fileSystem.CreatedDirectories.Should().HaveCount(4);
	}

	[Fact]
	public void ExplicitPortableData_ReparseAncestorResolvingToRefusedVolume_IsRejected()
	{
		const string SuppliedValue = @"C:\Linked\Leaf";
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		driveTypeProbe.Set(@"Z:\", DriveType.Network);
		FakePathInspector pathInspector = SafePathInspector(@"C:\");
		pathInspector.SetReparsePoint(@"C:\Linked", @"Z:\Remote\Data");
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(SuppliedValue, fileSystem, driveTypeProbe, pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem);
	}

	[Fact]
	public void ExplicitPortableData_SuffixReparseReachedThroughEarlierReparse_IsRejected()
	{
		const string SuppliedValue = @"C:\Linked\Existing\Nested\suffix-value-secret";
		const string TargetMarker = "suffix-network-target-secret";
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		driveTypeProbe.Set(@"Z:\", DriveType.Network);
		FakePathInspector pathInspector = SafePathInspector(
			@"C:\",
			@"D:\",
			@"D:\Existing");
		pathInspector.SetReparsePoint(@"C:\Linked", @"D:\");
		pathInspector.SetReparsePoint(
			@"D:\Existing\Nested",
			@"Z:\" + TargetMarker);
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(SuppliedValue, fileSystem, driveTypeProbe, pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem, "suffix-value-secret", TargetMarker);
		_ = pathInspector.InspectedPaths.Should().Contain(@"D:\Existing\Nested");
		_ = pathInspector.InspectedPaths.Should().NotContain(@"C:\Linked\Existing");
	}

	[Fact]
	public void ExplicitPortableData_ReparseInEffectiveSuffix_IsValidated()
	{
		const string SuppliedValue = @"C:\First\Second\successive-value-secret";
		const string TargetMarker = "successive-network-target-secret";
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		driveTypeProbe.Set(@"Z:\", DriveType.Network);
		FakePathInspector pathInspector = SafePathInspector(
			@"C:\",
			@"D:\",
			@"D:\FirstTarget");
		pathInspector.SetReparsePoint(@"C:\First", @"D:\FirstTarget");
		pathInspector.SetReparsePoint(@"D:\FirstTarget\Second", @"Z:\" + TargetMarker);
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(SuppliedValue, fileSystem, driveTypeProbe, pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem, "successive-value-secret", TargetMarker);
		_ = pathInspector.ResolvedPaths.Should().Equal(@"C:\First", @"D:\FirstTarget\Second");
	}

	[Fact]
	public void ExplicitPortableData_EachImmediateReparseTarget_IsGateValidated()
	{
		const string SuppliedValue = @"C:\Linked\Leaf";
		const string IntermediateTarget = @"D:\Intermediate\hop-secret";
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		driveTypeProbe.Set(@"D:\", DriveType.Network);
		FakePathInspector pathInspector = SafePathInspector(@"C:\");
		pathInspector.SetReparsePoint(@"C:\Linked", IntermediateTarget);
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(SuppliedValue, fileSystem, driveTypeProbe, pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem, "hop-secret");
		_ = pathInspector.ResolvedPaths.Should().Equal(@"C:\Linked");
		_ = pathInspector.InspectedPaths.Should().NotContain(IntermediateTarget);
	}

	[Fact]
	public void ExplicitPortableData_ReparseTargetWithNetworkReparseAncestor_IsRejected()
	{
		const string SuppliedValue = @"C:\Linked\Leaf";
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		driveTypeProbe.Set(@"Z:\", DriveType.Network);
		FakePathInspector pathInspector = SafePathInspector(@"C:\", @"D:\", @"Z:\");
		pathInspector.SetReparsePoint(@"C:\Linked", @"D:\Intermediate\Data");
		pathInspector.SetReparsePoint(@"D:\Intermediate", @"Z:\Remote\Data");
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(SuppliedValue, fileSystem, driveTypeProbe, pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem);
	}

	[Fact]
	public void ExplicitPortableData_ResolvedTargetWithMissingComponent_IsRejected()
	{
		const string SuppliedValue = @"C:\Linked\Leaf";
		FakePathInspector pathInspector = SafePathInspector(@"C:\", @"D:\", @"D:\Actual");
		pathInspector.SetReparsePoint(@"C:\Linked", @"D:\Actual\Missing");
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
			SuppliedValue,
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem);
	}

	[Fact]
	public void ExplicitPortableData_ReparseTargetCycle_IsRejected()
	{
		const string SuppliedValue = @"C:\Linked\Leaf";
		FakePathInspector pathInspector = SafePathInspector(@"C:\", @"D:\");
		pathInspector.SetReparsePoint(@"C:\Linked", @"D:\Loop");
		pathInspector.SetReparsePoint(@"D:\Loop", @"C:\Linked");
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
			SuppliedValue,
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem);
	}

	[Fact]
	public void ExplicitPortableData_ReparseChainAtTraversalLimit_IsAllowed()
	{
		const string SuppliedValue = @"C:\chain-entry";
		FakePathInspector pathInspector = ReparseChainInspector(
			SuppliedValue,
			StorageRootResolver.MaxReparseResolutions,
			out string terminalTarget);
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = CreateResolver(
			SuppliedValue,
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);

		_ = resolver.Root.Should().Be(SuppliedValue);
		_ = pathInspector.ResolvedPaths.Should().HaveCount(StorageRootResolver.MaxReparseResolutions);
		_ = pathInspector.InspectedPaths.Should().Contain(terminalTarget);
		_ = fileSystem.CreatedDirectories.Should().HaveCount(4);
	}

	[Fact]
	public void ExplicitPortableData_ReparseChainBeyondTraversalLimit_IsRejectedWithoutProvisioningOrDisclosure()
	{
		const string SuppliedValue = @"C:\chain-limit-value-secret";
		FakePathInspector pathInspector = ReparseChainInspector(
			SuppliedValue,
			StorageRootResolver.MaxReparseResolutions + 1,
			out string terminalTarget);
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
				SuppliedValue,
				fileSystem,
				new FakeDriveTypeProbe(DriveType.Fixed),
				pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem, "chain-limit-value-secret", terminalTarget);
		_ = pathInspector.ResolvedPaths.Should().HaveCount(StorageRootResolver.MaxReparseResolutions);
	}

	[Fact]
	public void SystemStoragePathGate_RequestsOneImmediateReparseHop()
	{
		string source = File.ReadAllText(FindStorageRootResolverSource());

		_ = source.Should().Contain("ResolveLinkTarget(false)");
		_ = source.Should().NotContain("ResolveLinkTarget(true)");
	}

	[Fact]
	public void ExplicitPortableData_Exactly1024Inspections_AreAccepted()
	{
		const int MaximumInspections = StorageRootResolver.MaxInspectedComponents;
		_ = MaximumInspections.Should().Be(1024);
		FakePathInspector pathInspector = ExistingComponentPathInspector(
			MaximumInspections - 1,
			out string suppliedValue);
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = CreateResolver(
			suppliedValue,
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);

		_ = resolver.Root.Should().Be(suppliedValue);
		_ = pathInspector.InspectedPaths.Should().HaveCount(MaximumInspections);
		_ = pathInspector.InspectCalls.Should().Be(MaximumInspections);
		_ = fileSystem.CreatedDirectories.Should().HaveCount(4);
	}

	[Fact]
	public void ExplicitPortableData_1025thInspection_IsRejectedBeforeInspectOrProvisioningWithoutDisclosure()
	{
		const int MaximumInspections = StorageRootResolver.MaxInspectedComponents;
		_ = MaximumInspections.Should().Be(1024);
		FakePathInspector pathInspector = ExistingComponentPathInspector(
			MaximumInspections,
			out string suppliedValue);
		string uninspectedPath = suppliedValue;
		pathInspector.RejectInspecting(uninspectedPath);
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
				suppliedValue,
				fileSystem,
				new FakeDriveTypeProbe(DriveType.Fixed),
				pathInspector);
		}

		AssertSafetyRejection(act, suppliedValue, fileSystem);
		_ = pathInspector.InspectCalls.Should().Be(MaximumInspections);
		_ = pathInspector.RejectedInspectCalls.Should().Be(0);
		_ = pathInspector.InspectedPaths.Should().NotContain(uninspectedPath);
	}

	[Fact]
	public void ExplicitPortableData_1025thInspectionAcrossRecursiveEffectivePath_IsRejectedBeforeInspectOrProvisioningWithoutDisclosure()
	{
		const int MaximumInspections = StorageRootResolver.MaxInspectedComponents;
		_ = MaximumInspections.Should().Be(1024);
		const string SuppliedPrefix = @"C:\Linked";
		const string Target = @"D:\Target";
		const int OriginalPrefixInspections = 2;
		const int EffectiveTargetPrefixInspections = 2;
		int suffixComponents = MaximumInspections
			- OriginalPrefixInspections
			- EffectiveTargetPrefixInspections
			+ 1;
		string suffix = BuildRepeatedComponentSuffix(suffixComponents);
		string suppliedValue = SuppliedPrefix + suffix;
		string uninspectedEffectivePath = Target + suffix;
		FakePathInspector pathInspector = ExistingComponentPathInspector(
			Target,
			suffixComponents,
			out _);
		pathInspector.SetExisting(@"C:\");
		pathInspector.SetReparsePoint(SuppliedPrefix, Target);
		pathInspector.RejectInspecting(uninspectedEffectivePath);
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
				suppliedValue,
				fileSystem,
				new FakeDriveTypeProbe(DriveType.Fixed),
				pathInspector);
		}

		AssertSafetyRejection(act, suppliedValue, fileSystem);
		_ = pathInspector.InspectCalls.Should().Be(MaximumInspections);
		_ = pathInspector.RejectedInspectCalls.Should().Be(0);
		_ = pathInspector.InspectedPaths.Should().NotContain(uninspectedEffectivePath);
		_ = pathInspector.ResolvedPaths.Should().Equal(SuppliedPrefix);
	}

	[Theory]
	[InlineData("unresolved")]
	[InlineData("broken")]
	[InlineData("throwing")]
	public void ExplicitPortableData_UnverifiableReparsePoint_IsRejected(string shape)
	{
		const string SuppliedValue = @"C:\Linked\resolution-value-secret";
		const string FailureMarker = "resolution-detail-secret";
		FakePathInspector pathInspector = SafePathInspector(@"C:\");
		pathInspector.SetReparsePoint(
			@"C:\Linked",
			shape == "unresolved" ? null : @"D:\Missing\resolution-target-secret");
		if (shape == "throwing")
		{
			pathInspector.ResolveFailure = new UnauthorizedAccessException(FailureMarker);
		}

		RecordingFileSystem fileSystem = new();
		void act()
		{
			_ = CreateResolver(
			SuppliedValue,
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);
		}

		AssertSafetyRejection(
			act,
			SuppliedValue,
			fileSystem,
			"resolution-value-secret",
			"resolution-target-secret",
			FailureMarker);
	}

	[Fact]
	public void ExplicitPortableData_OrdinaryPathInspectionFailure_FailsClosed()
	{
		const string SuppliedValue = @"C:\inspection-value-secret";
		const string FailureMarker = "inspection-detail-secret";
		RecordingFileSystem fileSystem = new();
		void act()
		{
			_ = CreateResolver(
			SuppliedValue,
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			new ThrowingPathInspector(new IOException(FailureMarker)));
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem, "inspection-value-secret", FailureMarker);
	}

	[Theory]
	[MemberData(nameof(UnsafeReparseTargets))]
	public void ExplicitPortableData_UnsafeReparseTarget_IsRejectedWithoutProvisioningOrDisclosure(
		string target,
		string targetMarker)
	{
		const string SuppliedValue = @"C:\Linked\unsafe-target-value-secret";
		FakePathInspector pathInspector = SafePathInspector(@"C:\");
		pathInspector.SetReparsePoint(@"C:\Linked", target);
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = CreateResolver(
				SuppliedValue,
				fileSystem,
				new FakeDriveTypeProbe(DriveType.Fixed),
				pathInspector);
		}

		AssertSafetyRejection(act, SuppliedValue, fileSystem, "unsafe-target-value-secret", targetMarker);
	}

	[Theory]
	[InlineData(@"D:\Actual\.\target-canonical-secret", @"D:\Actual\target-canonical-secret")]
	[InlineData(@"D:\Actual\target-trailing-separator-secret\", @"D:\Actual\target-trailing-separator-secret")]
	public void ExplicitPortableData_CanonicalSafeReparseTarget_IsAccepted(
		string target,
		string canonicalTarget)
	{
		FakePathInspector pathInspector = SafePathInspector(
			@"C:\",
			@"D:\",
			@"D:\Actual",
			canonicalTarget);
		pathInspector.SetReparsePoint(@"C:\Linked", target);
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = CreateResolver(
			@"C:\Linked\target-accepted-secret",
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);

		_ = resolver.Root.Should().Be(@"C:\Linked\target-accepted-secret");
		_ = pathInspector.InspectedPaths.Should().Contain(canonicalTarget);
		_ = pathInspector.InspectedPaths.Should().NotContain(target);
		_ = fileSystem.CreatedDirectories.Should().HaveCount(4);
	}

	[Fact]
	public void ExplicitPortableData_InspectionReceivesOnlyCanonicalPaths()
	{
		FakePathInspector pathInspector = SafePathInspector(
			@"C:\",
			@"C:\Mixed",
			@"C:\Mixed\canonical-inspection-secret");
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = CreateResolver(
			@"C:/Mixed/./canonical-inspection-secret/Leaf",
			fileSystem,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);

		_ = resolver.Root.Should().Be(@"C:\Mixed\canonical-inspection-secret\Leaf");
		_ = pathInspector.InspectedPaths.Should().Equal(
			@"C:\",
			@"C:\Mixed",
			@"C:\Mixed\canonical-inspection-secret",
			@"C:\Mixed\canonical-inspection-secret\Leaf");
	}

	// THE PREMISE OF THIS TEST CHANGED, and the rename is the record. It used to assert
	// that a blank --portable-data value fell through to the marker branch WITHOUT the
	// explicit-root safety gate running at all — the gate fakes threw if touched, and
	// "not invoked" was the property. That is no longer what the code promises: the
	// blank value still falls through exactly as before (it selects the marker branch
	// and the root is unchanged), but the marker branch is now itself gated through the
	// same lexical/volume/chain sequence as --portable-data, so those probes DO run.
	// What stays true is the thing this test was really protecting: a blank argument is
	// not an error, it does not flip the user's explicit-path expectation, and it
	// provisions the same four directories.
	//
	// THE FIXTURE WAS PHYSICALLY IMPOSSIBLE AND IS NOW COHERENT. The gate used to be
	// handed SafePathInspector(@"D:\") alone — it reported D:\Portable as MISSING while the
	// environment probe simultaneously reported D:\Portable\.portable as an EXISTING FILE.
	// A marker file cannot exist without its parent directory (measured: creating the file
	// creates/locates the directory), so that machine state cannot occur, and the chain
	// gate correctly stops at the first missing component: with D:\Portable missing, the
	// sequence legitimately inspects ONLY D:\ and D:\Portable. The assertion that demanded
	// the leaf be inspected anyway was pinning behaviour that contradicted both the disk
	// and the other inspection-set fixture (ExplicitPortableData_NonexistentLeafBelowSafeAncestors_IsAllowed,
	// where a missing intermediate stops the walk). D:\Portable is therefore declared
	// existing, exactly as MarkerDeployment_MissingLeafBelowExistingCarrier already does for
	// the same candidate — and the property this test exists for (blank argument selects the
	// marker branch, root unchanged, four directories, gate ran) is untouched by that.
	[Fact]
	public void BlankPortableData_FallsThroughToMarkerBranch_WhichIsNowSafetyGated()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add(@"D:\Portable\.portable");
		RecordingFileSystem fileSystem = new();
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		FakePathInspector pathInspector = SafePathInspector(@"D:\", @"D:\Portable");

		StorageRootResolver resolver = new(
			environment,
			fileSystem,
			"   ",
			driveTypeProbe,
			pathInspector);

		_ = resolver.Root.Should().Be(@"D:\Portable\BroChoosConfig");
		_ = fileSystem.CreatedDirectories.Should().HaveCount(4);

		// The gate ran: the marker root is now inspected from its volume root down.
		_ = driveTypeProbe.ProbedRoots.Should().Equal(@"D:\");
		_ = pathInspector.InspectedPaths.Should().Equal(@"D:\", @"D:\Portable", @"D:\Portable\BroChoosConfig");
	}

	// ---- ENV-DERIVED BRANCHES: marker (portable mode) -------------------------------
	//
	// The two branches below used to be validated NOWHERE. Their candidates are composed
	// from environment values — the running image's directory, and %LocalAppData% — and
	// the old code returned Path.Combine's result straight to the provisioning loop. The
	// shapes here are the ones that composition happily produces: measured on .NET 10,
	// Path.GetDirectoryName and Path.Combine accept all of them without throwing, which is
	// exactly why "it does not throw" was never the same claim as "it is contained".
	// Each row is a machine state, not a user argument: the marker/installed branch only
	// ever sees what the deployment hands it.

	public static TheoryData<string, string> UnsafeMarkerDeployments =>
		new()
		{
			{ @"\\server\share\marker-unc-secret\BroChoos.exe", "marker-unc-secret" },
			{ @"\\?\C:\marker-device-secret\BroChoos.exe", "marker-device-secret" },
			{ @"C:\marker-ads-secret:stream\BroChoos.exe", "marker-ads-secret" },
			{ @"C:\CON\marker-con-secret\BroChoos.exe", "marker-con-secret" },
			{ @"C:\marker-trailing-dot-secret.\BroChoos.exe", "marker-trailing-dot-secret" },
			{ @"marker-relative-secret\BroChoos.exe", "marker-relative-secret" },
		};

	public static TheoryData<string, string> UnsafeInstalledDeployments =>
		new()
		{
			{ @"\\server\share\installed-unc-secret", "installed-unc-secret" },
			{ @"\\?\C:\installed-device-secret", "installed-device-secret" },
			{ @"C:\installed-ads-secret:stream", "installed-ads-secret" },
			{ @"C:\installed-con-secret\CON", "installed-con-secret" },
			{ @"C:\installed-trailing-dot-secret.", "installed-trailing-dot-secret" },
			{ @"installed-relative-secret", "installed-relative-secret" },
		};

	// The assertion shape differs from AssertSafetyRejection in two deliberate ways:
	// ParamName is still the shared portableDataArgument create-context (the catch
	// filters key on it), but the message must NOT name --portable-data — this branch runs
	// precisely when that option was absent, and claiming it was rejected would be false —
	// and the gate may legitimately have run before the rejection, so no call-count
	// assertion belongs here.
	private static void AssertEnvDerivedRejection(
		Action act,
		string createContext,
		RecordingFileSystem fileSystem,
		params string[] secretMarkers)
	{
		ArgumentException thrown = act.Should().ThrowExactly<ArgumentException>().Which;
		_ = thrown.ParamName.Should().Be("portableDataArgument");
		_ = thrown.Message.Should().Contain(createContext);
		_ = thrown.Message.Should().NotContain("--portable-data");
		foreach (string marker in secretMarkers)
		{
			_ = marker.Should().NotBeNullOrWhiteSpace();
			_ = thrown.Message.Should().NotContain(marker);
		}

		_ = thrown.InnerException.Should().BeNull();
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
	}

	[Theory]
	[MemberData(nameof(UnsafeMarkerDeployments))]
	public void PortableMarker_UnsafeDeploymentShape_IsRejectedWithoutProvisioningOrDisclosure(
		string processPath,
		string secretMarker)
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = processPath,
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};

		// The marker path is derived from the SAME directory as the candidate, so the
		// fake must agree with the derivation rather than hardcode it: the branch is
		// reached through FileExists, and a mismatched key would silently test the
		// installed branch instead.
		string? exeDirectory = Path.GetDirectoryName(processPath);
		_ = exeDirectory.Should().NotBeNull();
		_ = environment.ExistingFiles.Add(Path.Combine(exeDirectory!, ".portable"));
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = new StorageRootResolver(
				environment,
				fileSystem,
				null,
				new FakeDriveTypeProbe(DriveType.Fixed),
				SafePathInspector());
		}

		AssertEnvDerivedRejection(act, "the .portable marker data root", fileSystem, secretMarker);
	}

	[Theory]
	[MemberData(nameof(UnsafeInstalledDeployments))]
	public void InstalledMode_UnsafeDeploymentShape_IsRejectedWithoutProvisioningOrDisclosure(
		string localAppData,
		string secretMarker)
	{
		// No process path, so the marker branch cannot be reached and the installed
		// branch is the one under test.
		FakeEnvironment environment = new()
		{
			ProcessPath = null,
			LocalAppData = localAppData,
		};
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = new StorageRootResolver(
				environment,
				fileSystem,
				null,
				new FakeDriveTypeProbe(DriveType.Fixed),
				SafePathInspector());
		}

		AssertEnvDerivedRejection(act, "the installed data root", fileSystem, secretMarker);
	}

	// A refused volume is a real deployment state, not a hostile string: a roaming or
	// UNC-redirected %LocalAppData%. The CLI branch has always refused these; the env
	// branch now does too.
	[Theory]
	[MemberData(nameof(RefusedDriveTypes))]
	public void InstalledMode_RefusedVolumeType_FailsClosedWithoutProvisioning(DriveType driveType)
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = null,
			LocalAppData = @"Z:\installed-volume-secret",
		};
		RecordingFileSystem fileSystem = new();
		FakeDriveTypeProbe driveTypeProbe = new(driveType);

		void act()
		{
			_ = new StorageRootResolver(
				environment,
				fileSystem,
				null,
				driveTypeProbe,
				SafePathInspector(@"Z:\"));
		}

		// Rejection happens after the volume probe for this shape, and the probe names
		// no path in its own failure — but the marker gives the assertion teeth anyway.
		AssertEnvDerivedRejection(act, "the installed data root", fileSystem, "installed-volume-secret");
		_ = driveTypeProbe.ProbedRoots.Should().Equal(@"Z:\");
	}

	[Fact]
	public void MarkerDeployment_RepositoryRootCarrierPath_IsRejectedWithoutDisclosure()
	{
		// The realistic residue of a checkout launched straight from a working copy: the
		// carrier's own directory name ends in a trailing dot, which Windows strips on
		// canonicalization. The CLI branch has always refused this shape; the marker
		// branch now does too.
		const string Marker = "marker-canonicalization-secret";
		FakeEnvironment environment = new()
		{
			ProcessPath = $@"D:\{Marker}\.\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add($@"D:\{Marker}\.\.portable");
		RecordingFileSystem fileSystem = new();

		void act()
		{
			_ = new StorageRootResolver(
				environment,
				fileSystem,
				null,
				new FakeDriveTypeProbe(DriveType.Fixed),
				SafePathInspector());
		}

		AssertEnvDerivedRejection(act, "the .portable marker data root", fileSystem, Marker);
	}

	// The realistic deployment defect the gate now catches: %LocalAppData% redirected to a
	// reparse point (documented for SSD tuning and used by profile-migration tools). This
	// one goes through the FAKE gate deliberately, and not merely for convenience: creating
	// a real directory link needs SeCreateSymbolicLinkPrivilege or Developer Mode, which is
	// absent on the machine this was verified on, so a real-link test would SKIP here and
	// silently prove nothing. The fake proves the property that matters and cannot skip:
	// the env branch walks the existing chain, sees the reparse point, resolves the one
	// immediate hop, and does NOT provision the marker root through it.
	[Fact]
	public void InstalledMode_ReparseCarrierInExistingChain_FailsClosedAndSuppressesTheCarrier()
	{
		const string Carrier = "installed-reparse-carrier-secret";
		const string Target = @"D:\reparse-target-secret\Actual";
		RecordingFileSystem fileSystem = new();
		FakePathInspector pathInspector = SafePathInspector(@"Z:\");
		pathInspector.SetReparsePoint($@"Z:\{Carrier}", Target);
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		driveTypeProbe.Set(@"D:\", DriveType.Network);

		FakeEnvironment environment = new()
		{
			ProcessPath = null,
			LocalAppData = $@"Z:\{Carrier}",
		};

		void act()
		{
			_ = new StorageRootResolver(
				environment,
				fileSystem,
				null,
				driveTypeProbe,
				pathInspector);
		}

		AssertEnvDerivedRejection(act, "the installed data root", fileSystem, Carrier, Target);

		// The chain was walked exactly as far as the hop, and the network-volume target
		// was classified before anything could be provisioned.
		_ = driveTypeProbe.ProbedRoots.Should().Contain([@"Z:\", @"D:\"]);
	}

	// The same shape on the MARKER branch, which had no gate at all before this change.
	[Fact]
	public void MarkerDeployment_ReparseCarrierInExistingChain_FailsClosed()
	{
		const string Carrier = "marker-reparse-carrier-secret";
		const string Target = @"D:\marker-reparse-target-secret\Actual";
		RecordingFileSystem fileSystem = new();
		FakePathInspector pathInspector = SafePathInspector(@"Z:\");
		pathInspector.SetReparsePoint($@"Z:\{Carrier}", Target);
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		driveTypeProbe.Set(@"D:\", DriveType.Network);

		FakeEnvironment environment = new()
		{
			ProcessPath = $@"Z:\{Carrier}\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add($@"Z:\{Carrier}\.portable");

		void act()
		{
			_ = new StorageRootResolver(
				environment,
				fileSystem,
				null,
				driveTypeProbe,
				pathInspector);
		}

		AssertEnvDerivedRejection(act, "the .portable marker data root", fileSystem, Carrier, Target);
	}

	// ---- ENV-DERIVED BRANCHES: positive controls -----------------------------------
	//
	// These are the MUST NOT BREAK half of the change: an ordinary local deployment has
	// to resolve to exactly the root it always resolved to, and now additionally has to
	// pass the gate. The fixtures are local fixed volumes so the real gate classifies
	// them without touching the network.

	[Fact]
	public void MarkerDeployment_OrdinaryLocalPath_IsResolvedAndGated()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add(@"D:\Portable\.portable");
		RecordingFileSystem fileSystem = new();
		FakeDriveTypeProbe driveTypeProbe = new(DriveType.Fixed);
		FakePathInspector pathInspector = SafePathInspector(@"D:\", @"D:\Portable");

		StorageRootResolver resolver = new(
			environment,
			fileSystem,
			null,
			driveTypeProbe,
			pathInspector);

		_ = resolver.Root.Should().Be(@"D:\Portable\BroChoosConfig");
		_ = resolver.Backups.Should().Be(@"D:\Portable\BroChoosConfig\backups");
		_ = fileSystem.CreatedDirectories.Should().Equal(
			@"D:\Portable\BroChoosConfig",
			@"D:\Portable\BroChoosConfig\backups",
			@"D:\Portable\BroChoosConfig\logs",
			@"D:\Portable\BroChoosConfig\temp");
		_ = driveTypeProbe.ProbedRoots.Should().Equal(@"D:\");
	}

	[Fact]
	public void MarkerDeployment_MissingLeafBelowExistingCarrier_IsAllowed()
	{
		// The ordinary case the chain gate must NOT reject: the marker root does not exist
		// yet — that is the whole point of provisioning it — so only the volume and every
		// required existing ancestor have to pass.
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add(@"D:\Portable\.portable");
		RecordingFileSystem fileSystem = new();
		FakePathInspector pathInspector = SafePathInspector(@"D:\", @"D:\Portable");

		StorageRootResolver resolver = new(
			environment,
			fileSystem,
			null,
			new FakeDriveTypeProbe(DriveType.Fixed),
			pathInspector);

		_ = resolver.Root.Should().Be(@"D:\Portable\BroChoosConfig");
		_ = pathInspector.InspectedPaths.Should().Equal(@"D:\", @"D:\Portable", @"D:\Portable\BroChoosConfig");
		_ = fileSystem.CreatedDirectories.Should().HaveCount(4);
	}

	[Fact]
	public void InstalledMode_OrdinaryLocalAppData_IsResolvedAndGatedThroughTheRealGate()
	{
		// Real gate, real volume classification, no disk touched: C: is a fixed local
		// volume on every Windows machine this suite runs on, and the candidate below it
		// is missing, which the chain gate accepts after the volume root.
		FakeEnvironment environment = new()
		{
			ProcessPath = null,
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = new(environment, fileSystem);

		_ = resolver.Root.Should().Be(@"C:\Users\SomeUser\AppData\Local\BroChoos");
		_ = fileSystem.CreatedDirectories.Should().Equal(
			@"C:\Users\SomeUser\AppData\Local\BroChoos",
			@"C:\Users\SomeUser\AppData\Local\BroChoos\backups",
			@"C:\Users\SomeUser\AppData\Local\BroChoos\logs",
			@"C:\Users\SomeUser\AppData\Local\BroChoos\temp");
	}

	private static StorageRootResolver CreateResolver(
		string suppliedValue,
		RecordingFileSystem fileSystem,
		IStorageDriveTypeProbe driveTypeProbe,
		IStoragePathGate pathInspector)
	{
		return new StorageRootResolver(
			new FakeEnvironment(),
			fileSystem,
			suppliedValue,
			driveTypeProbe,
			pathInspector);
	}

	private static void AssertSafetyRejection(
		Action act,
		string suppliedValue,
		RecordingFileSystem fileSystem,
		params string[] secretMarkers)
	{
		ArgumentException thrown = act.Should().ThrowExactly<ArgumentException>().Which;
		_ = thrown.ParamName.Should().Be("portableDataArgument");
		_ = thrown.Message.Should().Contain("--portable-data");
		_ = thrown.Message.Should().NotContain(suppliedValue);
		foreach (string marker in secretMarkers)
		{
			_ = marker.Should().NotBeNullOrWhiteSpace();
			_ = thrown.Message.Should().NotContain(marker);
		}

		_ = thrown.InnerException.Should().BeNull();
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
	}

	private static FakePathInspector ReparseWithResolveFailure(Exception failure)
	{
		FakePathInspector inspector = SafePathInspector(@"C:\");
		inspector.SetReparsePoint(@"C:\Linked", @"D:\unused");
		inspector.ResolveFailure = failure;
		return inspector;
	}

	private static FakePathInspector SafePathInspector(params string[] existingPaths)
	{
		FakePathInspector inspector = new();
		foreach (string path in existingPaths)
		{
			inspector.SetExisting(path);
		}

		return inspector;
	}

	private static FakePathInspector ReparseChainInspector(
		string entryPath,
		int reparsePointCount,
		out string terminalTarget)
	{
		FakePathInspector inspector = SafePathInspector(@"C:\", @"D:\");
		string current = entryPath;
		for (int index = 0; index < reparsePointCount; index++)
		{
			string next = $@"D:\chain-{index:D2}";
			inspector.SetReparsePoint(current, next);
			current = next;
		}

		terminalTarget = current;
		inspector.SetExisting(terminalTarget);
		return inspector;
	}

	private static FakePathInspector ExistingComponentPathInspector(
		int componentCount,
		out string path)
	{
		return ExistingComponentPathInspector(@"C:\", componentCount, out path);
	}

	private static FakePathInspector ExistingComponentPathInspector(
		string prefix,
		int componentCount,
		out string path)
	{
		FakePathInspector inspector = SafePathInspector(prefix[..3]);
		string current = Path.TrimEndingDirectorySeparator(prefix);
		if (current.Length > 3)
		{
			inspector.SetExisting(current);
		}

		for (int index = 0; index < componentCount; index++)
		{
			current += current.EndsWith(Path.DirectorySeparatorChar) ? "a" : @"\a";
			inspector.SetExisting(current);
		}

		path = current;
		return inspector;
	}

	private static string BuildRepeatedComponentSuffix(int componentCount)
	{
		return string.Concat(Enumerable.Repeat(@"\a", componentCount));
	}

	private static string FindStorageRootResolverSource()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string solutionPath = Path.Combine(directory.FullName, "BroChoos.sln");
			if (File.Exists(solutionPath))
			{
				return Path.Combine(
					directory.FullName,
					"src",
					"BroChoos.Platform.Windows",
					"Storage",
					"StorageRootResolver.cs");
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Could not locate StorageRootResolver.cs from the test output directory.");
	}

	private sealed class FakeEnvironment : IEnvironmentProbe
	{
		public string? ProcessPath { get; init; }

		public string? LocalAppData { get; init; }

		public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

		public string? CurrentProcessPath => ProcessPath;

		public string? GetEnvironmentVariable(string name)
		{
			return name.Equals("LocalAppData", StringComparison.OrdinalIgnoreCase) ? LocalAppData : null;
		}

		public bool FileExists(string path)
		{
			return ExistingFiles.Contains(path);
		}
	}

	private sealed class RecordingFileSystem : IStorageFileSystem
	{
		private readonly List<string> _createdDirectories = [];

		public IReadOnlyList<string> CreatedDirectories => _createdDirectories;

		public void CreateDirectory(string path)
		{
			_createdDirectories.Add(path);
		}
	}

	private sealed class FakeDriveTypeProbe(DriveType defaultDriveType) : IStorageDriveTypeProbe
	{
		private readonly Dictionary<string, DriveType> _driveTypes = new(StringComparer.OrdinalIgnoreCase);

		public List<string> ProbedRoots { get; } = [];

		public DriveType GetDriveType(string root)
		{
			ProbedRoots.Add(root);
			return _driveTypes.TryGetValue(root, out DriveType driveType) ? driveType : defaultDriveType;
		}

		public void Set(string root, DriveType driveType)
		{
			_driveTypes[root] = driveType;
		}
	}

	private sealed class ThrowingDriveTypeProbe(Exception failure) : IStorageDriveTypeProbe
	{
		public int Calls { get; private set; }

		public DriveType GetDriveType(string root)
		{
			Calls++;
			throw failure;
		}
	}

	private sealed class FakePathInspector : IStoragePathGate
	{
		private readonly Dictionary<string, StoragePathEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, string?> _targets = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _rejectedInspectPaths = new(StringComparer.OrdinalIgnoreCase);

		public Exception? ResolveFailure { get; set; }

		public List<string> InspectedPaths { get; } = [];

		public List<string> ResolvedPaths { get; } = [];

		public int InspectCalls { get; private set; }

		public int RejectedInspectCalls { get; private set; }

		public StoragePathEntry Inspect(string path)
		{
			if (_rejectedInspectPaths.Contains(path))
			{
				RejectedInspectCalls++;
				throw new InvalidOperationException("The over-budget Inspect call must be rejected by the gate.");
			}

			InspectCalls++;
			InspectedPaths.Add(path);
			return _entries.TryGetValue(path, out StoragePathEntry entry) ? entry : StoragePathEntry.Missing;
		}

		public string? ReadImmediateReparseTarget(string path)
		{
			ResolvedPaths.Add(path);
			return ResolveFailure is null
				? _targets[path]
				: throw ResolveFailure;
		}

		public void SetExisting(string path)
		{
			_entries[path] = StoragePathEntry.Ordinary;
		}

		public void SetReparsePoint(string path, string? target)
		{
			_entries[path] = StoragePathEntry.ReparsePoint;
			_targets[path] = target;
		}

		public void RejectInspecting(string path)
		{
			_ = _rejectedInspectPaths.Add(path);
		}
	}

	private sealed class ThrowingPathInspector(Exception failure) : IStoragePathGate
	{
		public int Calls { get; private set; }

		public StoragePathEntry Inspect(string path)
		{
			Calls++;
			throw failure;
		}

		public string? ReadImmediateReparseTarget(string path)
		{
			throw failure;
		}
	}
}
