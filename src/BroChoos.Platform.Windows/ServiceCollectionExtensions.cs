using BroChoos.Core.Abstractions;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Network;
using BroChoos.Platform.Windows.SingleInstance;
using BroChoos.Platform.Windows.Urls;
using Microsoft.Extensions.DependencyInjection;

namespace BroChoos.Platform.Windows;

public static class ServiceCollectionExtensions
{
	public static IServiceCollection AddWindowsPlatformServices(this IServiceCollection services)
	{
		// F-2 (2A): IUrlValidator is registered BEFORE the guard, because the guard's
		// forwarded-URL scheme gate takes it as a constructor dependency. The ordering is
		// not what makes it work (the container resolves by type, not by registration
		// order) — it is what makes the dependency visible to the reader at the line that
		// matters. Before this, the validator reached only CliParser, so the pipe path had
		// no scheme check at all.
		_ = services.AddSingleton<IUrlValidator, DefaultUrlValidator>();
		_ = services.AddSingleton<ISingleInstanceGuard, WindowsSingleInstanceGuard>();
		_ = services.AddSingleton<ExecutableIdentityVerifier>();
		_ = services.AddSingleton<OneShotSecretValidator>();
		_ = services.AddSingleton<IIpcTrustPolicyProvider, OwnerRulingFileIpcTrustPolicyProvider>();
		_ = services.AddSingleton<IpcPeerAuthenticator>();

		// Browser detection and launching (0A-2). The environment/registry/process
		// seams are registered too so the graph validates on build.
		_ = services.AddSingleton<IEnvironmentProbe, SystemEnvironmentProbe>();
		_ = services.AddSingleton<IProcessStarter, ProcessStarter>();
		_ = services.AddSingleton<IRegistryBrowserSource, RegistryBrowserSource>();
		_ = services.AddSingleton<IBrowserDetector, WindowsBrowserDetector>();
		_ = services.AddSingleton<IBrowserLauncher, WindowsBrowserLauncher>();
		// W6.2 manual/portable admission. Stateless: it holds the environment probe and two
		// pure profile readers, so registering it provisions nothing and resolves no path.
		_ = services.AddSingleton<ManualBrowserAdmitter>();
		_ = services.AddSingleton<ILaunchHelperProcessRunner, LaunchHelperProcessRunner>();
		_ = services.AddSingleton<ILaunchHelperClient, LaunchHelperClient>();
		_ = services.AddSingleton<INetworkHelperProcessRunner, NetworkHelperProcessRunner>();
		_ = services.AddSingleton<INetworkHelperClient, NetworkHelperClient>();

		// 0A-3: the picker's F key ("system/fallback", D-130). Same IProcessStarter
		// seam as the browser launcher: no shell strings, results as LaunchResult.
		_ = services.AddSingleton<ISystemFallbackLauncher, SystemFallbackLauncher>();

		// 0A-4 default-apps capability surface (D-187/D-273): HKCU-only registry
		// declaration, three-step read-only status, and the guide that opens
		// ms-settings:defaultapps. Registration never writes UserChoice; the write
		// seam itself throws on any UserChoice path (pinned by tests).
		_ = services.AddSingleton(TimeProvider.System);
		_ = services.AddSingleton<Registration.IRegistryCapabilityStore, Registration.HkcuRegistryCapabilityStore>();
		_ = services.AddSingleton<Registration.IRegistrationArtifacts, Registration.LocalAppDataRegistrationArtifacts>();
		_ = services.AddSingleton<ICapabilityRegistrationService, Registration.WindowsCapabilityRegistrationService>();
		_ = services.AddSingleton<IDefaultAppsStatusService, Registration.WindowsDefaultAppsStatusService>();
		_ = services.AddSingleton<IDefaultAppsGuide, Registration.WindowsDefaultAppsGuide>();

		// W8.2 Safe Mode recovery. The PROVIDER is registered, not the actions, for
		// exactly the reason BrowserCatalogPersistenceFactory is registered and the
		// resolver is not: composing the actions resolves a storage root, which IS
		// provisioning, and that must not happen at startup on a command line that may
		// be --help, a rejected URL, or --safe-mode itself. The provider's constructor
		// performs zero I/O; the resolver is built inside Create, once the parsed
		// --portable-data value is known.
		_ = services.AddSingleton<Recovery.SafeModeRecoveryActionsFactory>();
		_ = services.AddSingleton<Recovery.ISafeModeRecoveryActionsFactory>(sp => sp.GetRequiredService<Recovery.SafeModeRecoveryActionsFactory>());
		// The Core runner resolves the Core-owned provider name; the same singleton
		// answers both, so there is exactly one recovery environment per process.
		_ = services.AddSingleton<Core.Runners.ISafeModeRecoveryActionsProvider>(sp => sp.GetRequiredService<Recovery.SafeModeRecoveryActionsFactory>());
		_ = services.AddSingleton<Recovery.IDataFolderOpener, Recovery.ExplorerDataFolderOpener>();

		// 0A-5 config storage, INERT SEAMS ONLY. Every type below is stateless: its
		// constructor takes no path, resolves no storage root and touches no disk, so
		// having it in the eagerly resolved graph provisions nothing.
		//
		// WHY THAT PROPERTY IS THE WHOLE POINT HERE. Program.cs builds the provider and
		// resolves the root graph (ForwardedUrlRouter, ISingleInstanceGuard, Bootstrap)
		// BEFORE Bootstrap.RunAsync parses the arguments, so anything reachable from that
		// graph is constructed before --safe-mode is known. StorageRootResolver's
		// constructor IS the provisioning step (it calls CreateDirectory for
		// Root/backups/logs/temp), so registering it would create directories at startup
		// on a command line that must never mutate anything — --help, a rejected URL, or
		// safe mode itself.
		//
		// DELIBERATELY NOT REGISTERED, for that reason: StorageRootResolver /
		// IStorageRootResolver and BrowserCatalogPersistenceService, which needs one.
		// ConfigRepairExecutor, IStorageRepairFileSystem and IConfigRepairLog are not
		// registered either: browser-catalog persistence does not use them, and this slice
		// registers nothing it does not need.
		_ = services.AddSingleton<Storage.IStorageFileSystem, Storage.SystemStorageFileSystem>();
		_ = services.AddSingleton<Storage.IStorageReadFileSystem, Storage.SystemStorageReadFileSystem>();

		// Concretely, because it has no interface: the observer is a pure decision layer
		// and the seam it reads through (IStorageReadFileSystem) is the abstracted one.
		_ = services.AddSingleton<Storage.ConfigFileObserver>();
		_ = services.AddSingleton<Storage.IConfigDocumentWriterFactory, Storage.AtomicConfigDocumentWriterFactory>();
		_ = services.AddSingleton<Storage.IPostConsentDiscoveryStore, Storage.PostConsentDiscoveryStore>();
		_ = services.AddSingleton<Storage.PostConsentDiscoveryPublisher>();

		// The deferral seam. Registered because its own constructor performs zero I/O; it
		// only holds the four seams above plus IEnvironmentProbe and TimeProvider. The
		// resolver and the persistence service are built inside its Create, from the
		// parsed --portable-data value — which does not exist yet at composition time.
		//
		// NOTHING IN PRODUCTION CALLS Create YET, and that is the shape this slice was
		// asked for: activating a caller waits on an owner decision about the consent
		// flow, so the registration ships the seam without shipping a write path.
		_ = services.AddSingleton<Storage.IBrowserCatalogPersistenceFactory, Storage.BrowserCatalogPersistenceFactory>();
		_ = services.AddSingleton<IActivationHistoryStoreFactory, Storage.ActivationHistoryStoreFactory>();

		// W7.1: the rules-document store, deferred for the SAME measured reason as the two
		// factories above. The store itself needs a StorageRootResolver, and building one
		// provisions four directories, so only the zero-I/O factory is registered here.
		_ = services.AddSingleton<IRulesStoreFactory, Storage.RulesStoreFactory>();

		// W8.1: the settings-document store, deferred for the SAME measured reason. The
		// settings window is opened only by the --settings entry point, so provisioning
		// on any other command line would be exactly the mistake the deferral prevents.
		_ = services.AddSingleton<ISettingsStoreFactory, Storage.SettingsStoreFactory>();

		// Picker icons: the shell reader behind the on-disk icon cache. Zero I/O to register; the
		// root is resolved when the picker opens, like the stores above.
		_ = services.AddSingleton<Icons.IProgramIconSourceFactory, Icons.ProgramIconSourceFactory>();

		return services;
	}
}
