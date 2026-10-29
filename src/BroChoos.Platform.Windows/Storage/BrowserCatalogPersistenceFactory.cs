using BroChoos.Platform.Windows.Browsers;

namespace BroChoos.Platform.Windows.Storage;

// The DI-SAFE way to obtain a BrowserCatalogPersistenceService, and the reason it is a
// factory rather than a registration of the service itself.
//
// MEASURED CONSTRAINT, not a style preference. Program builds the container and resolves
// the root graph — ForwardedUrlRouter, ISingleInstanceGuard, Bootstrap (Program.cs:95-104)
// — BEFORE Bootstrap.RunAsync parses the command line (Bootstrap.cs:24). So every type
// reachable from that graph is CONSTRUCTED while --safe-mode and --portable-data are still
// unknown. Two consequences, and both point the same way:
//
//  1. CONSTRUCTING StorageRootResolver IS PROVISIONING. Its constructor creates
//     Root/backups/logs/temp (StorageRootResolver.cs:98-101) — that is its documented
//     contract, not an accident. A registered resolver would therefore create four
//     directories at startup on command lines that must never mutate anything: --help, a
//     rejected URL, and safe mode itself, which exists precisely to touch nothing.
//  2. THE ROOT IS NOT KNOWN YET ANYWAY. --portable-data is the highest-priority input to
//     the resolver (D-072), and it has not been parsed at that point, so a resolver built
//     during composition would be built on the WRONG root and could not be corrected.
//
// WHAT THIS TYPE THEREFORE PROMISES: its own constructor performs ZERO I/O. It holds only
// the stateless seams that are themselves registered — the environment probe, the
// provisioning seam, the observer, the writer factory and the clock — and none of those
// touches the disk when constructed either. The read seam is NOT among them: the observer
// is the type that takes it, and the persistence service reads only through the observer.
// All the I/O lives in Create, which is called by a caller that already has the parsed
// argument.
//
// SCOPE, stated because the absence is deliberate: this slice registers the factory and
// nothing calls it in production yet. Activating a caller waits on an owner decision about
// the consent flow, so wiring one here would ship an unapproved write path.
public interface IBrowserCatalogPersistenceFactory
{
	// portableDataArgument is the RAW --portable-data value (ParsedArgs.PortableDataPath),
	// or null when the option was absent. Null is not an error: it means the resolver's
	// remaining D-072 priority applies (a .portable marker next to the running image, then
	// installed mode under %LocalAppData%).
	//
	// CALLING THIS PROVISIONS DIRECTORIES, because it constructs the resolver. That is the
	// whole point of the deferral: the four directories appear when a caller has decided
	// to persist, never at startup.
	//
	// Throws ArgumentException when a nonblank explicit value cannot be normalized. This
	// is the bad-command-line lane; the message suppresses the value and ParamName names
	// portableDataArgument. Throws IOException when no root can be resolved, or when a
	// resolved root cannot be provisioned. That is the unusable-storage lane; its message
	// suppresses the path. Neither translated exception carries the original as an inner
	// exception, because Bootstrap formats inner messages and would re-expose the value.
	BrowserCatalogPersistenceService Create(string? portableDataArgument);
}

// The production factory. Constructor: assignments and null checks only — no path is
// composed, no root is resolved, no directory is created, nothing is read. The class is
// registered as a singleton, and that is safe precisely because of that emptiness.
public sealed class BrowserCatalogPersistenceFactory : IBrowserCatalogPersistenceFactory
{
	private readonly IEnvironmentProbe _environment;
	private readonly IStorageFileSystem _provisioning;
	private readonly ConfigFileObserver _observer;
	private readonly IConfigDocumentWriterFactory _writerFactory;
	private readonly TimeProvider _timeProvider;

	public BrowserCatalogPersistenceFactory(
		IEnvironmentProbe environment,
		IStorageFileSystem provisioning,
		ConfigFileObserver observer,
		IConfigDocumentWriterFactory writerFactory,
		TimeProvider timeProvider)
	{
		ArgumentNullException.ThrowIfNull(environment);
		ArgumentNullException.ThrowIfNull(provisioning);
		ArgumentNullException.ThrowIfNull(observer);
		ArgumentNullException.ThrowIfNull(writerFactory);
		ArgumentNullException.ThrowIfNull(timeProvider);

		_environment = environment;
		_provisioning = provisioning;
		_observer = observer;
		_writerFactory = writerFactory;
		_timeProvider = timeProvider;
	}

	public BrowserCatalogPersistenceService Create(string? portableDataArgument)
	{
		// The provisioning moment, and the ONLY one this type owns. The resolver is built
		// here — never held as a field — so a second call with a different argument
		// resolves a different root instead of silently reusing the first one.
		StorageRootResolver storageRoot = new(_environment, _provisioning, portableDataArgument);

		return new BrowserCatalogPersistenceService(
			storageRoot,
			_observer,
			_writerFactory,
			_timeProvider);
	}
}
