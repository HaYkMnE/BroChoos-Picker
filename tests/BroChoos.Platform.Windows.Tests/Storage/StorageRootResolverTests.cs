using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// D-072 storage-mode resolution: the three modes resolved through fakes, subpaths
// under the resolved root, and eager provisioning pinned to EXACTLY
// Root/Backups/Logs/Temp — themes/, diagnostics/ and exports/ must never be
// created here.
public sealed class StorageRootResolverTests
{
	// The installed-mode test below is the MUST NOT BREAK control for the env-derived gate:
	// the OLD default composition path, driven through the OLD constructor with no fakes.
	// Its fixture is an ordinary local fixed-volume path on C:, which every machine this
	// suite runs on has. The portable-marker tests use D:\Portable, and a D: volume is NOT
	// assumed to exist, so they inject the fake drive-type probe and path inspector.
	// The recorder is the witness — nothing here touches the disk.

	[Fact]
	public void InstalledMode_NoPortableSignals_RootIsLocalAppDataBroChoos()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"C:\Dev\BroChoos\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = new(environment, fileSystem);

		_ = resolver.Root.Should().Be(@"C:\Users\SomeUser\AppData\Local\BroChoos");
		_ = resolver.Backups.Should().Be(@"C:\Users\SomeUser\AppData\Local\BroChoos\backups");
		_ = resolver.Logs.Should().Be(@"C:\Users\SomeUser\AppData\Local\BroChoos\logs");
		_ = resolver.Temp.Should().Be(@"C:\Users\SomeUser\AppData\Local\BroChoos\temp");
	}

	[Fact]
	public void PortableMarker_NextToRunningExecutable_RootIsExeDirBroChoosConfig()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add(@"D:\Portable\.portable");
		RecordingFileSystem fileSystem = new();

		// Fakes keep the gate off the real disk: no real D: volume is needed.
		StorageRootResolver resolver = new(environment, fileSystem, null, new FakeDriveTypeProbe(), new FakePathInspector());

		_ = resolver.Root.Should().Be(@"D:\Portable\BroChoosConfig");
		_ = resolver.Backups.Should().Be(@"D:\Portable\BroChoosConfig\backups");
		_ = resolver.Logs.Should().Be(@"D:\Portable\BroChoosConfig\logs");
		_ = resolver.Temp.Should().Be(@"D:\Portable\BroChoosConfig\temp");
	}

	[Fact]
	public void ExplicitPortableDataArgument_WinsOverMarkerAndInstalledMode()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add(@"D:\Portable\.portable");
		RecordingFileSystem fileSystem = new();
		FakeDriveTypeProbe driveTypeProbe = new();
		FakePathInspector pathInspector = new();

		StorageRootResolver resolver = new(
			environment,
			fileSystem,
			@"E:\Custom\Data",
			driveTypeProbe,
			pathInspector);

		_ = resolver.Root.Should().Be(@"E:\Custom\Data");
		_ = resolver.Backups.Should().Be(@"E:\Custom\Data\backups");
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void ExplicitPortableData_EmptyOrWhitespace_FallsThroughToNextPriority(string portableDataArgument)
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add(@"D:\Portable\.portable");
		RecordingFileSystem fileSystem = new();

		StorageRootResolver resolver = new(environment, fileSystem, portableDataArgument, new FakeDriveTypeProbe(), new FakePathInspector());

		_ = resolver.Root.Should().Be(@"D:\Portable\BroChoosConfig");
	}

	[Fact]
	public void ExplicitPortableData_RelativePath_IsRejectedBeforeProvisioning()
	{
		const string SuppliedValue = @"brochoos-secret-relative-marker\Data";
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		RecordingFileSystem fileSystem = new();

		Action act = () => _ = new StorageRootResolver(
			environment,
			fileSystem,
			SuppliedValue,
			new FakeDriveTypeProbe(),
			new FakePathInspector());

		ArgumentException thrown = act.Should().ThrowExactly<ArgumentException>().Which;
		_ = thrown.ParamName.Should().Be("portableDataArgument");
		_ = thrown.Message.Should().Contain("--portable-data");
		_ = thrown.Message.Should().NotContain(SuppliedValue);
		_ = thrown.InnerException.Should().BeNull();
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
	}

	[Fact]
	public void ExplicitPortableData_TrailingSlash_IsNormalizedWithoutTrailingSeparator()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		RecordingFileSystem fileSystem = new();
		FakeDriveTypeProbe driveTypeProbe = new();
		FakePathInspector pathInspector = new();

		StorageRootResolver resolver = new(
			environment,
			fileSystem,
			@"E:\Custom\Data\",
			driveTypeProbe,
			pathInspector);

		_ = resolver.Root.Should().Be(@"E:\Custom\Data");
		_ = resolver.Root.Should().NotEndWith(Path.DirectorySeparatorChar.ToString());
		_ = resolver.Root.Should().NotEndWith(Path.AltDirectorySeparatorChar.ToString());
	}

	[Fact]
	public void Resolve_EagerlyCreatesOnlyRootBackupsLogsTemp()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"C:\Dev\BroChoos\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		RecordingFileSystem fileSystem = new();

		_ = new StorageRootResolver(environment, fileSystem);

		_ = fileSystem.CreatedDirectories.Should().BeEquivalentTo(
		[
			@"C:\Users\SomeUser\AppData\Local\BroChoos",
			@"C:\Users\SomeUser\AppData\Local\BroChoos\backups",
			@"C:\Users\SomeUser\AppData\Local\BroChoos\logs",
			@"C:\Users\SomeUser\AppData\Local\BroChoos\temp",
		]);

		// themes/, diagnostics/ and exports/ are subsystem-owned and NOT provisioned.
		_ = fileSystem.CreatedDirectories.Should().NotContain(path =>
			path.EndsWith("themes", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith("diagnostics", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith("exports", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void InstalledMode_WithNoLocalAppData_Throws()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"C:\Dev\BroChoos\BroChoos.exe",
			LocalAppData = null,
		};
		RecordingFileSystem fileSystem = new();

		Action act = () => _ = new StorageRootResolver(environment, fileSystem);

		_ = act.Should().Throw<IOException>();
	}

	// D-075 / D-097, ENV LEG. The marker branch used to hand Path.Combine's result to the
	// provisioning loop unexamined. It is now gated like --portable-data, and TWO published
	// behaviours changed with it — both pinned here because each was previously reachable
	// and each now fails closed:
	//
	//   1. An image whose directory has no directory part (a bare relative ProcessPath)
	//      used to throw ArgumentNullException out of Path.Combine for the MARKER probe
	//      itself, because "".portable is legitimate but "BroChoosConfig" is not: those
	//      branches threw for different reasons depending on which one ran. The failure is
	//      now a privacy-preserving ArgumentException before any probe or provisioning.
	//   2. A reparse-point deployment — the marker image launched from inside a junction —
	//      used to provision straight through the link. It now fails closed, and the
	//      message names neither the link nor its target nor the option that was never
	//      supplied.
	//
	// THE FIXTURE HAS TO PLACE THE RESOLVER INSIDE THE MARKER BRANCH, and the first form
	// below no longer does. MEASURED ON .NET 10: GetDirectoryName returns "" (not null)
	// for a bare file name — the ArgumentNullException the comment above records came from
	// the null the OLD ProcessPath could supply, not from this one — and the marker branch
	// is guarded by `!string.IsNullOrEmpty(exeDirectory)`, so with "" it is SKIPPED
	// ENTIRELY. The resolver then falls through to the installed branch and resolves
	// successfully, which is why the original assertion read "no exception was thrown".
	// The ProcessPath below keeps the property under test — a directory part that is not
	// an ordinary absolute drive root — while actually reaching the branch, and the marker
	// key is the string the probe composes, because FileExists compares ORDINAL and the
	// probe's Path.Combine(".portable") is NOT the same key as a literal ".portable".
	[Fact]
	public void PortableMarker_DeploymentWithoutADirectoryPart_IsRejectedWithoutNamingASuppliedOption()
	{
		const string Marker = "marker-null-directory-secret";
		FakeEnvironment environment = new()
		{
			ProcessPath = Marker + @"\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add(Marker + @"\.portable");
		RecordingFileSystem fileSystem = new();

		Action act = () => _ = new StorageRootResolver(environment, fileSystem);

		ArgumentException thrown = act.Should().ThrowExactly<ArgumentException>().Which;
		_ = thrown.ParamName.Should().Be("portableDataArgument");
		_ = thrown.Message.Should().Contain("the .portable marker data root");
		_ = thrown.Message.Should().NotContain("--portable-data");

		// The REASON, not just the context: this fixture's ProcessPath has a directory
		// part that is a bare relative leaf, so the candidate this branch composes is not
		// an ordinary absolute drive path and the ORIGIN check is what refuses it. Naming
		// that reason is what makes the assertion discriminate the origin thread at all —
		// the context string above is shared by every rejection the marker leg can raise,
		// and NotContain("--portable-data") alone stays green even when the shared-helper
		// context threading (components/volume/budget) is reverted, because this fixture
		// never reaches those helpers.
		_ = thrown.Message.Should().Contain("origin is not an ordinary absolute drive path");
		_ = thrown.InnerException.Should().BeNull();
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
	}

	// The reparse case on the REAL gate chain, but WITHOUT assuming a privilege this
	// machine does not have. Creating a directory link needs SeCreateSymbolicLinkPrivilege
	// or Developer Mode, so a test that made one would SKIP on the machine this was
	// verified on and prove nothing. A path component that ends in a trailing dot is the
	// unprivileged stand-in for the same class of defect: the marker branch used to hand
	// it straight to the provisioning loop, and the real gate now refuses it before any
	// probe or directory creation. (The chain-level reparse behaviour is pinned with the
	// fake gate in PortableDataRootSafetyTests, where it cannot skip.)
	//
	// TWO MACHINE FACTS ARE MEASURED HERE RATHER THAN ASSUMED, because getting either
	// wrong makes this pass for the wrong reason:
	//   * the volume is D:, not C:, because the chain walk starts by inspecting the
	//     volume ROOT. C:\ exists on this machine, so the walk would proceed INTO the
	//     candidate; D:\ exists too but the marker/carrier directories below it do not,
	//     which is the ordinary missing-leaf shape. Either way the rejection comes from
	//     the LEXICAL component check, which runs before any filesystem call — that is the
	//     property under test and it does not depend on what the disk looks like.
	//   * the marker key must be EXACTLY the string the probe composes. GetDirectoryName
	//     does NOT strip the trailing dot, so the probe's key is
	//     Path.Combine(exeDirectory, ".portable") = 'D:\<marker>.\.portable' and FileExists
	//     compares ORDINAL: the original fixture's 'D:\<marker>.\ .portable' (with a
	//     space) could never match, so the marker branch was never entered and the
	//     resolver fell through to the installed root, which is why this read "no
	//     exception was thrown".
	[Fact]
	public void PortableMarker_InvalidCandidateComponent_FailsClosedThroughTheRealGate()
	{
		const string Marker = "marker-invalid-component-secret";
		FakeEnvironment environment = new()
		{
			ProcessPath = $@"D:\{Marker}.\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add($@"D:\{Marker}.\.portable");
		RecordingFileSystem fileSystem = new();

		Action act = () => _ = new StorageRootResolver(environment, fileSystem);

		ArgumentException thrown = act.Should().ThrowExactly<ArgumentException>().Which;
		_ = thrown.ParamName.Should().Be("portableDataArgument");
		_ = thrown.Message.Should().Contain("the .portable marker data root");
		_ = thrown.Message.Should().NotContain("--portable-data");

		// The REASON, not just the context: the carrier directory ends in a trailing dot,
		// which is the unprivileged stand-in for an unsafe component, so ValidateComponents
		// is the helper that refuses this candidate. Pinning the exact wording is what makes
		// this assertion discriminate the COMPONENT thread by REASON: the context assertion
		// above only proves that SOME environment-worded rejection escaped translation, and
		// a second, unrelated check (volume, chain, budget) firing first would satisfy it
		// just as well. The reason text names the one check that is actually under test.
		_ = thrown.Message.Should().Contain("a path component has an unsafe ending");
		_ = thrown.Message.Should().NotContain(Marker);
		_ = thrown.InnerException.Should().BeNull();
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
	}

	// F-4b: the TraversalBudget context thread ON THE ENV LEG. The CLI leg's budget
	// exhaustion is pinned in PortableDataRootSafetyTests (a 1025th inspection injected
	// with a fake gate), but that fixture supplies --portable-data, so it exercises the
	// CLI create context. Nothing exercised the case the TraversalBudget class was
	// explicitly built for: the budget carries the caller's context so an ENV-derived
	// walk's exhaustion is worded by the env leg. That is the gap closed here.
	//
	// The shape is deliberately the SIMPLEST one that reaches the budget: an installed
	// deployment whose %LocalAppData% is an ordinary local path with far more than
	// MaxInspectedComponents components, and a gate that reports every one of them
	// Ordinary. The walk therefore descends the whole path and the 1025th inspection is
	// the one the budget refuses — before any provisioning, and worded for the env leg.
	//
	// WHY A FAKE GATE AND NOT THE REAL ONE: the real gate would have to observe 1025
	// REAL directories on disk, and the depth needed to reach the cap cannot portably
	// exist on a developer machine. The fake proves the property that matters and cannot
	// skip: the budget is consulted, its exhaustion is an environment-worded rejection,
	// and the value stays suppressed.
	[Fact]
	public void InstalledMode_TraversalBudgetExhaustedOnEnvDerivedWalk_IsRejectedWithTheEnvLegWording()
	{
		const string Marker = "installed-budget-secret";
		int components = StorageRootResolver.MaxInspectedComponents + 1;
		string deepLocalAppData = @"C:\" + string.Join('\\', Enumerable.Repeat("a", components));
		FakeEnvironment environment = new()
		{
			// No process path: the marker branch cannot be reached, so the installed
			// branch — the env-derived leg this test exists for — is the one under test.
			ProcessPath = null,
			LocalAppData = deepLocalAppData,
		};
		RecordingFileSystem fileSystem = new();
		AlwaysOrdinaryPathInspector pathInspector = new();
		FakeDriveTypeProbe driveTypeProbe = new();

		void act()
		{
			_ = new StorageRootResolver(
				environment,
				fileSystem,
				null,
				driveTypeProbe,
				pathInspector);
		}

		ArgumentException thrown = ((Action)act).Should().ThrowExactly<ArgumentException>().Which;
		_ = thrown.ParamName.Should().Be("portableDataArgument");
		_ = thrown.Message.Should().Contain("the installed data root");
		_ = thrown.Message.Should().NotContain("--portable-data");

		// The specific reason, and the budget actually being the check that fired. The
		// volume probe is the only one consulted before the walk, and no component is
		// unsafe or absent here, so if this reason is absent the budget was never reached.
		_ = thrown.Message.Should().Contain("the path traversal budget was exhausted");

		// The budget is operation-wide, so the gate stops exactly at the cap.
		_ = pathInspector.InspectCalls.Should().Be(StorageRootResolver.MaxInspectedComponents);
		_ = thrown.InnerException.Should().BeNull();
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
		_ = thrown.Message.Should().NotContain(Marker);
	}

	// D-075 / D-097, VALUE LEG. Both inputs are refused by Path.GetFullPath itself, and
	// they are here for different reasons: the 40 000-character one is the PRIVACY case
	// (untranslated, .NET's PathTooLongException quotes the whole value), the embedded
	// NUL is the CONTRACT case (its message leaks nothing, but ParamName would name
	// GetFullPath's own "path"). Both must come out as one translated ArgumentException.
	//
	// The marker is what makes the leak assertion meaningful: it is a substring of the
	// value that no correct message could contain by coincidence.
	[Theory]
	[InlineData("nul-inside")]
	[InlineData("too-long")]
	public void ExplicitPortableData_RefusedByNormalization_ThrowsWithoutEchoingTheValue(string shape)
	{
		const string Marker = "brochoos-secret-value-marker";
		string value = shape == "nul-inside"
			? "C:\\" + Marker + "\0x"
			: @"C:\" + Marker + new string('L', 40000);
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		RecordingFileSystem fileSystem = new();

		Action act = () => _ = new StorageRootResolver(
			environment,
			fileSystem,
			value,
			new FakeDriveTypeProbe(),
			new FakePathInspector());

		ArgumentException thrown = act.Should().Throw<ArgumentException>().Which;

		// The caller's parameter, not Path.GetFullPath's "path" — this is the evidence
		// that the translation ran rather than .NET's own exception escaping.
		_ = thrown.ParamName.Should().Be("portableDataArgument");
		_ = thrown.Message.Should().NotContain(Marker);
		_ = thrown.Message.Should().Contain("--portable-data");

		// No chaining: an InnerException would carry the untranslated message, which
		// Bootstrap's exception-object logging would then format back into the log.
		_ = thrown.InnerException.Should().BeNull();

		// The failure is lexical and happens before any provisioning, so no partial
		// tree is left behind — and nothing was created under a path this bad.
		_ = fileSystem.CreatedDirectories.Should().BeEmpty();
	}

	// D-075 / D-097, DISK LEG. Every type here was MEASURED coming out of
	// Directory.CreateDirectory on .NET 10, and every one of them quotes the full path
	// in its own message. UnauthorizedAccessException is in the list precisely because
	// it does NOT derive from IOException, so a `catch (IOException)` would miss it.
	[Theory]
	[InlineData(typeof(IOException))]
	[InlineData(typeof(DirectoryNotFoundException))]
	[InlineData(typeof(PathTooLongException))]
	[InlineData(typeof(UnauthorizedAccessException))]
	public void Provisioning_FailureThatQuotesThePath_IsTranslatedWithoutEchoingIt(Type failureType)
	{
		const string Root = @"E:\brochoos-secret-root-marker";
		string leakingMessage = $"Access to the path '{Root}' is denied.";
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		ThrowingFileSystem fileSystem = new(
			(Exception)Activator.CreateInstance(failureType, leakingMessage)!);

		Action act = () => _ = new StorageRootResolver(
			environment,
			fileSystem,
			Root,
			new FakeDriveTypeProbe(),
			new FakePathInspector());

		IOException thrown = act.Should().Throw<IOException>().Which;

		_ = thrown.Message.Should().NotContain("brochoos-secret-root-marker");
		_ = thrown.Message.Should().NotContain(leakingMessage);
		_ = thrown.InnerException.Should().BeNull();

		// The type NAME survives, because it is static text and it is what separates
		// "denied" from "parent missing" for an operator.
		_ = thrown.Message.Should().Contain(failureType.Name);
	}

	// The other half of the disk leg: an ArgumentException from the seam is NOT
	// translated. Measured — CreateDirectory raises it for an embedded NUL and its
	// message names no path, so translating would buy no privacy while reshaping a
	// bad-value signal into an I/O failure.
	[Fact]
	public void Provisioning_ArgumentException_IsNotTranslated()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		ThrowingFileSystem fileSystem = new(new ArgumentException("Null character in path.", "path"));

		Action act = () => _ = new StorageRootResolver(
			environment,
			fileSystem,
			@"E:\Custom\Data",
			new FakeDriveTypeProbe(),
			new FakePathInspector());

		// Not IOException: the original type reaches the caller unchanged.
		_ = act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("path");
	}

	// A failed provision LEAVES what it created in place. Deleting directories to tidy
	// up would be a destructive action on a caller-supplied path, so the seam has no
	// delete member at all and the recorder is the witness: the two directories created
	// before the failure are still recorded after it.
	[Fact]
	public void Provisioning_FailurePartWayThrough_LeavesWhatWasAlreadyCreated()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		ThrowingFileSystem fileSystem = new(new IOException("denied"), throwOnCall: 3);

		Action act = () => _ = new StorageRootResolver(
			environment,
			fileSystem,
			@"E:\Custom\Data",
			new FakeDriveTypeProbe(),
			new FakePathInspector());

		_ = act.Should().Throw<IOException>();
		_ = fileSystem.CreatedDirectories.Should().Equal(
			@"E:\Custom\Data",
			@"E:\Custom\Data\backups");
	}

	private sealed class FakeEnvironment : IEnvironmentProbe
	{
		public string? ProcessPath { get; init; }

		public string? LocalAppData { get; init; }

		public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

		public string? CurrentProcessPath => ProcessPath;

		public string? GetEnvironmentVariable(string name)
		{
			return name.Equals("LocalAppData", StringComparison.OrdinalIgnoreCase)
				? LocalAppData
				: null;
		}

		public bool FileExists(string path)
		{
			return ExistingFiles.Contains(path);
		}
	}

	private sealed class RecordingFileSystem : IStorageFileSystem
	{
		private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

		public IReadOnlyCollection<string> CreatedDirectories => _directories;

		public void CreateDirectory(string path)
		{
			_ = _directories.Add(path);
		}
	}

	private sealed class FakeDriveTypeProbe : IStorageDriveTypeProbe
	{
		public DriveType GetDriveType(string root)
		{
			return DriveType.Fixed;
		}
	}

	private sealed class FakePathInspector : IStoragePathGate
	{
		public StoragePathEntry Inspect(string path)
		{
			return path.Length == 3 ? StoragePathEntry.Ordinary : StoragePathEntry.Missing;
		}

		public string? ReadImmediateReparseTarget(string path)
		{
			throw new InvalidOperationException("No fake path is a reparse point.");
		}
	}

	// Reports EVERY path Ordinary and counts inspections, so a walk is forced all the way
	// down an arbitrarily deep env-derived candidate and the traversal budget is the only
	// check that can stop it. No reparse point is ever reported, so the reparse budget is
	// untouched and the inspection cap is unambiguously the one under test.
	private sealed class AlwaysOrdinaryPathInspector : IStoragePathGate
	{
		public int InspectCalls { get; private set; }

		public StoragePathEntry Inspect(string path)
		{
			InspectCalls++;
			return StoragePathEntry.Ordinary;
		}

		public string? ReadImmediateReparseTarget(string path)
		{
			throw new InvalidOperationException("No fake path is a reparse point.");
		}
	}

	// Records like RecordingFileSystem but fails on a chosen call, so both "which
	// exception types are translated" and "what survives a partial provision" can be
	// asserted without a real disk. Ordered, unlike the set above, because the partial
	// case is about the sequence.
	private sealed class ThrowingFileSystem(Exception failure, int throwOnCall = 1) : IStorageFileSystem
	{
		private readonly List<string> _directories = [];
		private int _calls;

		public IReadOnlyList<string> CreatedDirectories => _directories;

		public void CreateDirectory(string path)
		{
			_calls++;
			if (_calls == throwOnCall)
			{
				throw failure;
			}

			_directories.Add(path);
		}
	}
}
