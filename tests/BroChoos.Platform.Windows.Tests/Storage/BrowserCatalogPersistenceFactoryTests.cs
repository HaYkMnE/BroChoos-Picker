using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

// 0A-5: the FACTORY's throw contract, which is a privacy boundary and not merely a type
// list. Create constructs StorageRootResolver on the RAW --portable-data value, and the
// failures that construction can raise are built by .NET AROUND that value:
// Path.GetFullPath rejects the argument by name (StorageRootResolver.cs:186) and
// Directory.CreateDirectory embeds the path in its message (StorageRootResolver.cs:98-101).
// The resolver now translates both legs so the value never reaches a message; these tests
// pin that from the FACTORY's side, which is the surface a caller actually holds. Bootstrap
// logs exception messages, so an untranslated failure would print the user's data root.
//
// The marker token is what makes the substring assertions bite: it appears in no other
// string in the product, so "the message does not contain it" cannot pass by accident.
public sealed class BrowserCatalogPersistenceFactoryTests
{
	private const string Marker = "BROCHOOS-LEAK-MARKER-9Q7";

	// The REAL provisioning seam, so this pins the production chain rather than a fake's
	// idea of it. A path UNDER AN EXISTING FILE can never become a directory, which makes
	// the failure deterministic and needs no privilege, and nothing is created either way:
	// the resolver's FIRST CreateDirectory (the root itself) is the one that fails.
	[Fact]
	public void Create_WhenTheRealFileSystemRefusesTheRoot_ThrowsAValueFreeIoException()
	{
		string existingFile = Environment.ProcessPath ?? typeof(BrowserCatalogPersistenceFactoryTests).Assembly.Location;
		_ = File.Exists(existingFile).Should().BeTrue("this probe needs a real file to block directory creation");
		string suppliedRoot = Path.Combine(existingFile, Marker);
		BrowserCatalogPersistenceFactory factory = BuildFactory(new SystemStorageFileSystem());

		Action act = () => factory.Create(suppliedRoot);

		IOException thrown = act.Should().ThrowExactly<IOException>().Which;
		_ = thrown.Message.Should().NotContain(Marker);
		_ = thrown.InnerException.Should().BeNull();
	}

	// The four failures Directory.CreateDirectory actually raises on a root it cannot
	// provision, injected through the seam so each one is exercised exactly once and
	// nothing depends on machine state. UnauthorizedAccessException is the reason this
	// matters most: it is a SystemException, NOT an IOException, so it satisfied no part
	// of the old contract while carrying the path in its message.
	[Theory]
	[InlineData("unauthorized")]
	[InlineData("directory-not-found")]
	[InlineData("path-too-long")]
	[InlineData("io")]
	public void Create_WhenProvisioningFails_TranslatesToAValueFreeIoException(string failureKind)
	{
		string suppliedRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, Marker);
		BrowserCatalogPersistenceFactory factory = BuildFactory(
			new ThrowingFileSystem(BuildProvisioningFailure(failureKind, suppliedRoot)));

		Action act = () => factory.Create(suppliedRoot);

		IOException thrown = act.Should().ThrowExactly<IOException>().Which;
		_ = thrown.Message.Should().NotContain(Marker);
		_ = thrown.InnerException.Should().BeNull();
	}

	// The normalization leg, which fails BEFORE any provisioning: the seam throws if it is
	// reached, so a pass here also proves no directory was attempted. A NUL is the one
	// character Path.GetFullPath still refuses outright, so this needs no machine state.
	// ParamName is asserted because it is the evidence of translation: the untranslated
	// exception names Path.GetFullPath's own parameter, and a caller has no such parameter.
	[Fact]
	public void Create_WhenTheValueCannotBeNormalized_ThrowsAnArgumentExceptionNamingTheParameterNotTheValue()
	{
		string suppliedRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, Marker + "\0x");
		BrowserCatalogPersistenceFactory factory = BuildFactory(new ThrowingFileSystem(
			new InvalidOperationException("Provisioning must not be reached when the value cannot be normalized.")));

		Action act = () => factory.Create(suppliedRoot);

		ArgumentException thrown = act.Should().ThrowExactly<ArgumentException>().Which;
		_ = thrown.Message.Should().NotContain(Marker);
		_ = thrown.ParamName.Should().Be("portableDataArgument");
		_ = thrown.InnerException.Should().BeNull();
	}

	// Modelled on the messages .NET itself produces for these three cases, because the
	// message is the INPUT to the translation under test: a fake that failed silently
	// would make the no-leak assertion vacuous.
	private static Exception BuildProvisioningFailure(string failureKind, string suppliedRoot)
	{
		return failureKind switch
		{
			"unauthorized" => new UnauthorizedAccessException($"Access to the path '{suppliedRoot}' is denied."),
			"directory-not-found" => new DirectoryNotFoundException($"Could not find a part of the path '{suppliedRoot}'."),
			"path-too-long" => new PathTooLongException($"The specified path, file name, or both are too long: '{suppliedRoot}'."),
			"io" => new IOException($"The filename, directory name, or volume label syntax is incorrect. : '{suppliedRoot}'"),
			_ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, "Unknown failure kind."),
		};
	}

	// Everything except the provisioning seam is the real, inert composition: the observer
	// and the writer factory touch no disk until they are used, and no test here gets far
	// enough to use them.
	private static BrowserCatalogPersistenceFactory BuildFactory(IStorageFileSystem provisioning)
	{
		return new(
			new InertEnvironment(),
			provisioning,
			new ConfigFileObserver(new SystemStorageReadFileSystem()),
			new AtomicConfigDocumentWriterFactory(),
			TimeProvider.System);
	}

	// Every test supplies a non-null --portable-data value, which is the highest D-072
	// priority, so the environment is never consulted. Inert rather than fake for exactly
	// that reason: a resolver that reached this probe would be ignoring the argument.
	private sealed class InertEnvironment : IEnvironmentProbe
	{
		public string? CurrentProcessPath => null;

		public string? GetEnvironmentVariable(string name)
		{
			return null;
		}

		public bool FileExists(string path)
		{
			return false;
		}
	}

	private sealed class ThrowingFileSystem(Exception failure) : IStorageFileSystem
	{
		public void CreateDirectory(string path)
		{
			throw failure;
		}
	}
}
