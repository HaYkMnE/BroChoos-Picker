using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Storage;

namespace BroChoos.Platform.Windows.Icons;

// Composes the icon source the picker uses: the STA shell reader behind the on-disk cache in
// the run's data root. Resolving the root reuses StorageRootResolver, the same rule (installed
// %LocalAppData%\BroChoos, portable <exe>\BroChoosConfig or --portable-data) every other file
// follows. If no root resolves, icons still load - just without the disk cache.
public interface IProgramIconSourceFactory
{
	IProgramIconSource Create(string? portableDataPath);
}

public sealed class ProgramIconSourceFactory(IEnvironmentProbe environment, IStorageFileSystem storageFileSystem) : IProgramIconSourceFactory
{
	private readonly IEnvironmentProbe _environment = environment;
	private readonly IStorageFileSystem _storageFileSystem = storageFileSystem;

	public IProgramIconSource Create(string? portableDataPath)
	{
		ShellIconReader shell = new();
		try
		{
			StorageRootResolver roots = new(_environment, _storageFileSystem, portableDataPath);
			return IconDiskCache.ForDataRoot(shell, roots.Root);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return shell;
		}
	}
}
