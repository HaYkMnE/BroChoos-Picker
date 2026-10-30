using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Cli;
using BroChoos.Core.Runners;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// This file sits in BroChoos.Platform.Windows.Tests, which has its own Browsers/Urls/
// SingleInstance child namespaces holding the per-area test classes. An unqualified
// "Browsers.WindowsBrowserDetector" therefore binds to the TEST namespace and does not
// compile. These aliases are declared at file scope, so they resolve from the global
// root and name the product types unambiguously.
using PlatformBrowsers = BroChoos.Platform.Windows.Browsers;
using PlatformSingleInstance = BroChoos.Platform.Windows.SingleInstance;
using PlatformStorage = BroChoos.Platform.Windows.Storage;
using PlatformUrls = BroChoos.Platform.Windows.Urls;
using RegistrationTypeAliases = BroChoos.Platform.Windows.Registration;

namespace BroChoos.Platform.Windows.Tests;

// (F5) The DI graph is a real failure surface with no test behind it: Program.cs builds
// the container with ValidateOnBuild, so a dropped registration is a STARTUP crash, not a
// compile error — and every existing test constructs its subjects by hand with fakes, so
// the whole suite stays green while the shipped executable cannot start.
//
// This lives in the Platform.Windows test project because the Windows extension is where
// the five 0A-2 registrations are (IEnvironmentProbe, IProcessStarter,
// IRegistryBrowserSource, IBrowserDetector, IBrowserLauncher) and BroChoos.Core.Tests
// cannot reference it.
//
// The registrations mirror Program.cs: AddLogging + AddCoreServices +
// AddWindowsPlatformServices. The build options are NOT identical — ValidateScopes is
// additionally enabled here, so this test is stricter than startup rather than a replica
// of it.
public sealed class ServiceCompositionTests
{
	[Fact]
	public void BuildServiceProvider_WithValidation_SucceedsForTheRealComposition()
	{
		// ValidateOnBuild walks every registration and fails here rather than at the
		// first GetRequiredService, so this alone catches a missing dependency anywhere
		// in the graph. ValidateScopes additionally rejects a scoped service captured by
		// a singleton — every registration is currently a singleton, and this is what
		// keeps a later scoped one from being captured silently.
		Func<ServiceProvider> act = BuildProvider;

		ServiceProvider provider = act.Should().NotThrow().Subject;
		provider.Dispose();
	}

	// Bootstrap is the entry type Program.cs resolves; resolving it pulls CliParser,
	// UrlRunner and SafeModeRunner, and through UrlRunner the whole browser graph.
	[Fact]
	public void Resolve_Bootstrap_ReturnsTheEntryTypeProgramResolves()
	{
		using ServiceProvider provider = BuildProvider();

		_ = provider.GetRequiredService<Bootstrap>().Should().NotBeNull();
	}

	// The five 0A-2 registrations plus the two 0A-1 ones, each asserted through the
	// ABSTRACTION and against the concrete implementation type. Asserting the type is
	// what makes this fail if a registration is ever replaced by a stub or a different
	// implementation, not merely dropped.
	[Theory]
	[InlineData(typeof(IBrowserDetector), typeof(PlatformBrowsers.WindowsBrowserDetector))]
	[InlineData(typeof(IBrowserLauncher), typeof(PlatformBrowsers.WindowsBrowserLauncher))]
	[InlineData(typeof(ISystemFallbackLauncher), typeof(PlatformBrowsers.SystemFallbackLauncher))]
	[InlineData(typeof(IUrlValidator), typeof(PlatformUrls.DefaultUrlValidator))]
	[InlineData(typeof(ISingleInstanceGuard), typeof(PlatformSingleInstance.WindowsSingleInstanceGuard))]
	[InlineData(typeof(ICapabilityRegistrationService), typeof(RegistrationTypeAliases.WindowsCapabilityRegistrationService))]
	[InlineData(typeof(IDefaultAppsStatusService), typeof(RegistrationTypeAliases.WindowsDefaultAppsStatusService))]
	[InlineData(typeof(IDefaultAppsGuide), typeof(RegistrationTypeAliases.WindowsDefaultAppsGuide))]
	public void Resolve_ServicesUrlRunnerNeeds_AreRegisteredWithTheRealImplementation(Type serviceType, Type expectedImplementation)
	{
		using ServiceProvider provider = BuildProvider();

		object resolved = provider.GetRequiredService(serviceType);

		_ = resolved.Should().BeOfType(expectedImplementation);
	}

	// The remaining Windows registrations are internal-facing seams, so they are pinned
	// by resolvability only — dropping one still breaks the graph above, and this names
	// which one for the reader.
	[Fact]
	public void Resolve_PlatformSeams_AreRegistered()
	{
		using ServiceProvider provider = BuildProvider();

		_ = provider.GetRequiredService<PlatformBrowsers.IEnvironmentProbe>().Should().NotBeNull();
		_ = provider.GetRequiredService<PlatformBrowsers.IProcessStarter>().Should().NotBeNull();
		_ = provider.GetRequiredService<PlatformBrowsers.IRegistryBrowserSource>().Should().NotBeNull();
	}

	// 0A-4: the capability-declaration seams (HKCU write store, artifacts sink,
	// wall clock). The UserChoice-write guard travels inside the real store's
	// implementation — it needs no registration of its own.
	[Fact]
	public void Resolve_RegistrationSeams_AreRegistered()
	{
		using ServiceProvider provider = BuildProvider();

		_ = provider.GetRequiredService<RegistrationTypeAliases.IRegistryCapabilityStore>().Should().BeOfType<RegistrationTypeAliases.HkcuRegistryCapabilityStore>();
		_ = provider.GetRequiredService<RegistrationTypeAliases.IRegistrationArtifacts>().Should().BeOfType<RegistrationTypeAliases.LocalAppDataRegistrationArtifacts>();
		_ = provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
	}

	// 0A-5 storage seams. All four are STATELESS and INERT: their constructors take
	// no path, resolve no root and touch no disk, so having them in the eagerly
	// resolved graph costs nothing. Asserted through the abstraction AND against the
	// concrete type for the same reason as the 0A-2 block above — a stub swapped in
	// here would still resolve.
	//
	// ConfigFileObserver is registered CONCRETELY because it has no interface: it is
	// a pure decision layer over IStorageReadFileSystem, so the seam it needs is the
	// one that is abstracted, not the observer itself.
	[Theory]
	[InlineData(typeof(PlatformStorage.IStorageFileSystem), typeof(PlatformStorage.SystemStorageFileSystem))]
	[InlineData(typeof(PlatformStorage.IStorageReadFileSystem), typeof(PlatformStorage.SystemStorageReadFileSystem))]
	[InlineData(typeof(PlatformStorage.ConfigFileObserver), typeof(PlatformStorage.ConfigFileObserver))]
	[InlineData(typeof(PlatformStorage.IConfigDocumentWriterFactory), typeof(PlatformStorage.AtomicConfigDocumentWriterFactory))]
	public void Resolve_StorageSeams_AreRegisteredWithTheRealImplementation(Type serviceType, Type expectedImplementation)
	{
		using ServiceProvider provider = BuildProvider();

		object resolved = provider.GetRequiredService(serviceType);

		_ = resolved.Should().BeOfType(expectedImplementation);
	}

	// 0A-5: the persistence FACTORY, and why this slice needs one at all.
	//
	// MEASURED CONSTRAINT, re-verified in this worktree. Program.cs builds the provider
	// and resolves the root graph — ForwardedUrlRouter, ISingleInstanceGuard, Bootstrap
	// (Program.cs:95-104) — BEFORE Bootstrap.RunAsync parses the arguments
	// (Bootstrap.cs:24), so every type reachable from that graph is constructed before
	// --safe-mode is known. And constructing StorageRootResolver IS provisioning: its
	// constructor calls CreateDirectory for Root/backups/logs/temp
	// (StorageRootResolver.cs:98-101). Registering the resolver would therefore create
	// four directories at startup, on command lines that must never mutate anything.
	//
	// The factory is what defers that: IT is registered, the resolver is NOT. The
	// property that makes the registration safe is asserted here directly — the
	// constructor performs ZERO I/O, it only holds already-registered stateless seams.
	// The provisioning seam is a RECORDER, so a resolver built in the constructor would
	// have recorded four paths; an empty recorder is the assertion.
	//
	// The read seam appears only as ConfigFileObserver's constructor argument, which is
	// the ONLY place on this path that needs one: the persistence service reads through
	// the observer, so the factory holds no read seam of its own (0A-5 debt 15).
	[Fact]
	public void Construct_BrowserCatalogPersistenceFactory_PerformsNoIo()
	{
		RecordingFileSystem provisioning = new();
		PlatformStorage.SystemStorageReadFileSystem readFileSystem = new();

		PlatformStorage.BrowserCatalogPersistenceFactory factory = new(
			new FakeEnvironment { ProcessPath = @"D:\Portable\BroChoos.exe", LocalAppData = @"C:\Users\SomeUser\AppData\Local" },
			provisioning,
			new PlatformStorage.ConfigFileObserver(readFileSystem),
			new PlatformStorage.AtomicConfigDocumentWriterFactory(),
			TimeProvider.System);

		_ = factory.Should().NotBeNull();
		_ = provisioning.CreatedDirectories.Should().BeEmpty();
	}

	// The other half: the factory is REGISTERED, as a singleton of the real type, and
	// RESOLVING it still creates nothing. That last clause is what a reader would
	// otherwise have to take on trust from the test above — ValidateOnBuild walks every
	// registration, and the singleton is materialized here twice.
	//
	// The probe asserts the candidate roots are UNCHANGED rather than ABSENT, and the
	// distinction is measured rather than defensive: %LocalAppData%\BroChoos already
	// exists on this machine (0A-4 registration artifacts land there), so an absence
	// assertion would fail for a reason that has nothing to do with this slice. `temp`
	// under that root is created ONLY by StorageRootResolver, so a provisioning
	// regression still shows up as a change.
	[Fact]
	public void Resolve_BrowserCatalogPersistenceFactory_IsASingletonAndProvisionsNothing()
	{
		IReadOnlyList<bool> before = ProbeCandidateRoots();

		using ServiceProvider provider = BuildProvider();
		object resolved = provider.GetRequiredService<PlatformStorage.IBrowserCatalogPersistenceFactory>();

		_ = resolved.Should().BeOfType<PlatformStorage.BrowserCatalogPersistenceFactory>();
		_ = resolved.Should().BeSameAs(provider.GetRequiredService<PlatformStorage.IBrowserCatalogPersistenceFactory>());
		_ = ProbeCandidateRoots().Should().Equal(before);
	}

	// 0A-5: Create(null) means "--portable-data was absent", so the resolver's REMAINING
	// D-072 priority applies — a .portable marker next to the RUNNING image wins over
	// installed mode. Both null branches are listed because they are independent
	// (StorageRootResolver.cs:135-150), and the marker case is the one that would still
	// pass if the factory ignored the environment seam altogether.
	//
	// Provisioning is asserted through the RECORDER, so the resolved root is pinned
	// without creating anything on the real disk. The order is the resolver's own
	// (root, backups, logs, temp). The writer capture is a SECOND, independent
	// assertion: it proves the returned SERVICE was built on that root rather than only
	// the resolver — a factory that provisioned correctly and then handed the service a
	// different resolver would pass the recorder check alone.
	[Theory]
	[InlineData(false, @"C:\Users\SomeUser\AppData\Local\BroChoos")]
	[InlineData(true, @"D:\Portable\BroChoosConfig")]
	public void Create_WithNullArgument_UsesTheExistingDefaultPriority(bool portableMarkerPresent, string expectedRoot)
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		if (portableMarkerPresent)
		{
			_ = environment.ExistingFiles.Add(@"D:\Portable\.portable");
		}

		RecordingFileSystem provisioning = new();
		CapturingWriterFactory writers = new();
		PlatformStorage.BrowserCatalogPersistenceFactory factory = new(
			environment,
			provisioning,
			new PlatformStorage.ConfigFileObserver(new AbsentFileSystem()),
			writers,
			TimeProvider.System);

		PlatformStorage.BrowserCatalogPersistenceService service = factory.Create(null);

		_ = provisioning.CreatedDirectories.Should().Equal(
			expectedRoot,
			Path.Combine(expectedRoot, "backups"),
			Path.Combine(expectedRoot, "logs"),
			Path.Combine(expectedRoot, "temp"));

		_ = service.Persist([new("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")])
			.Status.Should().Be(PlatformStorage.BrowserCatalogPersistenceStatus.Persisted);
		_ = writers.DestinationPath.Should().Be(Path.Combine(expectedRoot, "browsers.json"));
		_ = writers.BackupsDirectory.Should().Be(Path.Combine(expectedRoot, "backups"));
	}

	// 0A-5: a NON-NULL argument is the highest D-072 priority and must win over both
	// remaining modes, so the fake machine here has BOTH a .portable marker and a
	// LocalAppData — either would produce a different root if the argument were dropped.
	// The value is also normalized by the resolver (trim, fully qualify, strip the
	// trailing separator), which is why the input carries a trailing slash and the
	// expectation does not.
	[Fact]
	public void Create_WithNonNullArgument_HonoursThatPathOverEveryOtherMode()
	{
		FakeEnvironment environment = new()
		{
			ProcessPath = @"D:\Portable\BroChoos.exe",
			LocalAppData = @"C:\Users\SomeUser\AppData\Local",
		};
		_ = environment.ExistingFiles.Add(@"D:\Portable\.portable");
		RecordingFileSystem provisioning = new();
		CapturingWriterFactory writers = new();
		PlatformStorage.BrowserCatalogPersistenceFactory factory = new(
			environment,
			provisioning,
			new PlatformStorage.ConfigFileObserver(new AbsentFileSystem()),
			writers,
			TimeProvider.System);

		// The argument root must sit on a volume that exists where the test runs: the resolver's
		// volume gate reads the REAL drive type, and a hosted runner has no E: drive (NoRootDirectory
		// is rightly rejected). Provisioning is faked, so nothing is created under %TEMP%.
		// GetFullPath expands an 8.3 short name in %TEMP% (e.g. RUNNER~1) the way the resolver does.
		string argumentRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "brochoos-arg-root-" + Guid.NewGuid().ToString("N")[..12]));
		PlatformStorage.BrowserCatalogPersistenceService service = factory.Create(argumentRoot + @"\");

		_ = provisioning.CreatedDirectories.Should().Equal(
			argumentRoot,
			Path.Combine(argumentRoot, "backups"),
			Path.Combine(argumentRoot, "logs"),
			Path.Combine(argumentRoot, "temp"));

		_ = service.Persist([new("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")])
			.Status.Should().Be(PlatformStorage.BrowserCatalogPersistenceStatus.Persisted);
		_ = writers.DestinationPath.Should().Be(Path.Combine(argumentRoot, "browsers.json"));
	}

	// 0A-5: the service the CONTAINER's factory returns is usable, end to end, against a
	// real root. Everything here is the production wiring — the factory is resolved from
	// the composed provider, so the seams are the registered SystemStorage* ones and the
	// real atomic writer, not fakes.
	//
	// A TEMP ROOT is passed as the --portable-data value, which is what makes this safe
	// to run: the four provisioned directories and the written catalog all land under a
	// fresh GUID path that the test deletes. The assertion is that the write reached THAT
	// root, so a factory that resolved a different one would fail here even though its
	// unit-level tests pass.
	[Fact]
	public void Create_FromTheContainer_ReturnsAServiceThatPersistsUnderTheRequestedRoot()
	{
		string root = Path.Combine(Path.GetTempPath(), "brochoos-di-persist-" + Guid.NewGuid().ToString("N")[..12]);

		try
		{
			using ServiceProvider provider = BuildProvider();
			PlatformStorage.IBrowserCatalogPersistenceFactory factory =
				provider.GetRequiredService<PlatformStorage.IBrowserCatalogPersistenceFactory>();

			PlatformStorage.BrowserCatalogPersistenceService service = factory.Create(root);

			// Create is the provisioning moment: the resolver ran, so the four
			// directories exist now and only now.
			_ = Directory.Exists(root).Should().BeTrue();
			_ = Directory.Exists(Path.Combine(root, "backups")).Should().BeTrue();
			_ = Directory.Exists(Path.Combine(root, "logs")).Should().BeTrue();
			_ = Directory.Exists(Path.Combine(root, "temp")).Should().BeTrue();

			PlatformStorage.BrowserCatalogPersistenceResult result = service.Persist(
			[
				new("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path"),
			]);

			_ = result.Status.Should().Be(PlatformStorage.BrowserCatalogPersistenceStatus.Persisted);
			_ = File.Exists(Path.Combine(root, "browsers.json")).Should().BeTrue();
			_ = Directory.GetFiles(root, "*.tmp").Should().BeEmpty();
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	// 0A-5: THE PROPERTY THIS WHOLE SLICE EXISTS TO PRESERVE, asserted through the REAL
	// composed container rather than through hand-built fakes. Program.cs resolves the
	// entry graph before Bootstrap.RunAsync parses anything (Program.cs:95-104 then
	// Bootstrap.cs:24), so this test does the same in the same order: build the provider,
	// resolve the graph, then run the three command lines that must never mutate anything
	// — --help, --safe-mode, and no URL at all.
	//
	// The candidate root travels as --portable-data, which is the highest D-072 priority
	// and therefore the value a provisioning regression would resolve. It lives under the
	// temp directory and does not exist, so its APPEARANCE is the assertion — the same
	// device BootstrapTests uses for the parser-level version of this property. The real
	// installed and portable roots are probed too, because a regression that ignored the
	// argument would land there instead.
	//
	// Console.SetOut is process-wide; this assembly disables test parallelization
	// (AssemblyInfo.cs), so the redirection cannot race with another class. It is captured
	// only to keep the run's output clean — help text and the safe-mode line are expected.
	[Theory]
	[InlineData("", ExitCodes.NoArgs)]
	[InlineData("--help", ExitCodes.Success)]
	[InlineData("--safe-mode", ExitCodes.SafeModeOk)]
	public async Task ResolveAndRunBootstrap_OnCommandLinesThatMustNotMutate_CreatesNoStorageDirectory(
		string companionArgument,
		int expectedExitCode)
	{
		string candidate = Path.Combine(Path.GetTempPath(), $"brochoos-di-purity-{Guid.NewGuid():N}");
		string[] args = companionArgument.Length == 0
			? ["--portable-data", candidate]
			: ["--portable-data", candidate, companionArgument];
		IReadOnlyList<bool> realRootsBefore = ProbeCandidateRoots();
		TextWriter originalOut = Console.Out;

		try
		{
			Console.SetOut(new StringWriter());

			using ServiceProvider provider = BuildProvider();
			Bootstrap bootstrap = provider.GetRequiredService<Bootstrap>();

			int exitCode = await bootstrap.RunAsync(args, CancellationToken.None);

			_ = exitCode.Should().Be(expectedExitCode);
			_ = Directory.Exists(candidate).Should().BeFalse();
			_ = Directory.Exists(Path.Combine(candidate, "backups")).Should().BeFalse();
			_ = Directory.Exists(Path.Combine(candidate, "logs")).Should().BeFalse();
			_ = Directory.Exists(Path.Combine(candidate, "temp")).Should().BeFalse();
			_ = ProbeCandidateRoots().Should().Equal(realRootsBefore);
		}
		finally
		{
			Console.SetOut(originalOut);

			// Reachable only if a regression provisioned it; leaves temp clean either way.
			if (Directory.Exists(candidate))
			{
				Directory.Delete(candidate, recursive: true);
			}
		}
	}

	// 0A-5: the ABSENCES that make the slice inert, pinned as behaviour rather than as a
	// comment. GetService (not GetRequiredService) returns null for an unregistered type,
	// so this asserts what the composed container CANNOT hand anyone.
	//
	// WHY IT IS WORTH A TEST. The purity test above proves the Bootstrap graph provisions
	// nothing; it cannot prove that a later slice did not register the resolver, because
	// the container does not construct a singleton nobody resolves. This one closes that
	// gap: a registration of StorageRootResolver, or of the persistence service that needs
	// one, fails HERE — before it can reach anything eagerly resolved.
	//
	// The repair types are listed for a different reason: browser-catalog persistence does
	// not use them, and registering an unused write/repair surface is scope the slice
	// refused. If a later slice genuinely needs them, this test is the place to say so.
	[Theory]
	[InlineData(typeof(PlatformStorage.IStorageRootResolver))]
	[InlineData(typeof(PlatformStorage.StorageRootResolver))]
	[InlineData(typeof(PlatformStorage.BrowserCatalogPersistenceService))]
	[InlineData(typeof(PlatformStorage.ConfigRepairExecutor))]
	[InlineData(typeof(PlatformStorage.IStorageRepairFileSystem))]
	[InlineData(typeof(PlatformStorage.IConfigRepairLog))]
	public void Resolve_ProvisioningAndRepairTypes_AreDeliberatelyNotRegistered(Type serviceType)
	{
		using ServiceProvider provider = BuildProvider();

		_ = provider.GetService(serviceType).Should().BeNull();
	}

	// The Core runners and the parser, resolved from the same container: a Core-side
	// registration going missing is the same startup crash as a platform-side one.
	[Fact]
	public void Resolve_CoreServices_AreRegistered()
	{
		using ServiceProvider provider = BuildProvider();

		_ = provider.GetRequiredService<CliParser>().Should().NotBeNull();
		_ = provider.GetRequiredService<UrlRunner>().Should().NotBeNull();
		_ = provider.GetRequiredService<SafeModeRunner>().Should().NotBeNull();
	}

	// Singletons by registration: resolving twice must hand back the same instance. The
	// single-instance guard in particular owns a named pipe, so a per-call instance would
	// be a real defect rather than a style question.
	[Fact]
	public void Resolve_Twice_ReturnsTheSameSingletonInstances()
	{
		using ServiceProvider provider = BuildProvider();

		_ = provider.GetRequiredService<Bootstrap>().Should().BeSameAs(provider.GetRequiredService<Bootstrap>());
		_ = provider.GetRequiredService<ISingleInstanceGuard>().Should().BeSameAs(provider.GetRequiredService<ISingleInstanceGuard>());
		_ = provider.GetRequiredService<IBrowserLauncher>().Should().BeSameAs(provider.GetRequiredService<IBrowserLauncher>());
	}

	private static ServiceProvider BuildProvider()
	{
		ServiceCollection services = new();

		// A logging provider is part of the graph: every service above takes an
		// ILogger<T>. No console output is configured, so the test writes nothing.
		_ = services.AddLogging();

		_ = services.AddCoreServices();
		_ = services.AddWindowsPlatformServices();

		// 0A-3: UrlRunner also needs the picker surface. The real picker is
		// AvaloniaPicker in BroChoos.App, which this project cannot reference (the
		// UI stack must stay out of product graphs), so an inert stand-in completes
		// the graph exactly the way Program completes it with the real one.
		_ = services.AddSingleton<IPicker, FakePicker>();

		return services.BuildServiceProvider(new ServiceProviderOptions
		{
			ValidateOnBuild = true,
			ValidateScopes = true,
		});
	}

	// The real storage roots this machine's composition could provision, probed for
	// existence only. The installed root is where StorageRootResolver would land with no
	// --portable-data and no .portable marker (StorageRootResolver.cs:147-150); its four
	// eagerly-created subdirectories are listed because `temp` in particular is created
	// by nothing else in the product, which is what makes this probe sensitive.
	private static IReadOnlyList<bool> ProbeCandidateRoots()
	{
		string? localAppData = Environment.GetEnvironmentVariable("LocalAppData");
		string installedRoot = string.IsNullOrWhiteSpace(localAppData)
			? Path.Combine(Path.GetTempPath(), "brochoos-no-localappdata-sentinel")
			: Path.Combine(localAppData, "BroChoos");
		string? exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
		string portableRoot = string.IsNullOrEmpty(exeDirectory)
			? Path.Combine(Path.GetTempPath(), "brochoos-no-processpath-sentinel")
			: Path.Combine(exeDirectory, "BroChoosConfig");

		return
		[
			Directory.Exists(installedRoot),
			Directory.Exists(Path.Combine(installedRoot, "backups")),
			Directory.Exists(Path.Combine(installedRoot, "logs")),
			Directory.Exists(Path.Combine(installedRoot, "temp")),
			Directory.Exists(portableRoot),
		];
	}

	// Read seam over an EMPTY machine: every file and directory is absent, so the
	// observer reports Absent and the write path is licensed without any real disk.
	// Deliberately not a recording fake — what these tests assert is the resolved ROOT,
	// which the provisioning recorder and the writer capture between them.
	private sealed class AbsentFileSystem : PlatformStorage.IStorageReadFileSystem
	{
		public bool FileExists(string path)
		{
			return false;
		}

		public bool DirectoryExists(string path)
		{
			return false;
		}

		public Stream OpenRead(string path)
		{
			throw new FileNotFoundException("The fake filesystem is empty.", path);
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return [];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return DateTime.UnixEpoch;
		}

		public long GetLength(string path)
		{
			throw new FileNotFoundException("The fake filesystem is empty.", path);
		}
	}

	// Captures the paths the persistence service hands the writer, so a test can prove
	// the SERVICE (not merely the resolver) was built on the expected root. Writes
	// nothing.
	private sealed class CapturingWriterFactory : PlatformStorage.IConfigDocumentWriterFactory, PlatformStorage.IBrowserCatalogDocumentWriter
	{
		public string? BackupsDirectory { get; private set; }

		public string? DestinationPath { get; private set; }

		public PlatformStorage.IBrowserCatalogDocumentWriter Create(string backupsDirectory)
		{
			BackupsDirectory = backupsDirectory;
			return this;
		}

		public void CreateAbsent(
			string destinationPath,
			Core.Storage.BrowsersDocument document,
			Core.Storage.ExpectedDocumentGeneration expectedGeneration,
			Core.Storage.PersistClearance clearance,
			TimeSpan writeLockTimeout,
			Action<long, Core.Storage.BrowsersDocument> onCommitted)
		{
			DestinationPath = destinationPath;
			onCommitted(1, document with { Generation = 1 });
		}

		public void ReconcileCompatible(
			string destinationPath,
			Core.Storage.BrowsersDocument document,
			Core.Storage.ExpectedDocumentGeneration expectedGeneration,
			Core.Storage.ReconcileClearance clearance,
			TimeSpan writeLockTimeout,
			Action<long, Core.Storage.BrowsersDocument> onCommitted)
		{
			DestinationPath = destinationPath;
			onCommitted(checked(document.Generation + 1), document with { Generation = checked(document.Generation + 1) });
		}
	}

	// Inert IPicker stand-in: never shown, only present so the container can
	// validate the same shape Program resolves at startup.
	private sealed class FakePicker : IPicker
	{
		public Task<int> ShowAsync(PickerRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(0);
		}
	}

	// 0A-5 provisioning RECORDER. Same shape as StorageRootResolverTests' fake and for
	// the same reason: what must be asserted is WHICH directories are created and WHEN,
	// which only an injected seam can observe. A list, not a set, so the order the
	// resolver provisions in stays visible.
	private sealed class RecordingFileSystem : PlatformStorage.IStorageFileSystem
	{
		private readonly List<string> _directories = [];

		public IReadOnlyList<string> CreatedDirectories => _directories;

		public void CreateDirectory(string path)
		{
			_directories.Add(path);
		}
	}

	// Describes a whole machine state for the D-072 priority without touching the real
	// disk: the running image's path, the .portable marker's presence, and LocalAppData.
	private sealed class FakeEnvironment : PlatformBrowsers.IEnvironmentProbe
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
}
