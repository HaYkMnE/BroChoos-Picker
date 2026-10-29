using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

public sealed class FirefoxProfileReaderTests
{
	[Fact]
	public void Parse_ValidRelativeAndAbsoluteProfiles_ReturnsIdsWithoutUnresolved()
	{
		const string ini = """
		[Profile0]
		Name=default-release
		IsRelative=1
		Path=Profiles/abc.default-release

		[Profile1]
		Name=work
		IsRelative=0
		Path=C:\\Users\\dev\\AppData\\Roaming\\Mozilla\\Firefox\\Profiles\\work
		""";

		FirefoxProfileReadResult result = FirefoxProfileReader.Parse(ini);

		_ = result.ProfileIds.Should().Equal("default-release", "work");
		_ = result.HasUnresolvedProfiles.Should().BeFalse();
	}

	[Fact]
	public void Parse_TraversalOrMalformedEntries_MarksUnresolved()
	{
		const string ini = """
		[Profile0]
		Name=bad-relative
		IsRelative=1
		Path=..\\..\\secret

		[Profile1]
		Name=bad-absolute
		IsRelative=0
		Path=\\\\server\\share\\profile
		""";

		FirefoxProfileReadResult result = FirefoxProfileReader.Parse(ini);

		_ = result.ProfileIds.Should().BeEmpty();
		_ = result.HasUnresolvedProfiles.Should().BeTrue();
	}

	[Fact]
	public void TryRead_MissingFile_ReturnsUnresolvedAndNoProfiles()
	{
		FirefoxProfileReader reader = new(new FakeFirefoxProfileFileSystem(exists: false, content: string.Empty));

		FirefoxProfileReadResult result = reader.TryRead(@"C:\\Users\\dev\\AppData\\Roaming\\Mozilla\\Firefox\\profiles.ini");

		_ = result.ProfileIds.Should().BeEmpty();
		_ = result.HasUnresolvedProfiles.Should().BeTrue();
	}

	private sealed class FakeFirefoxProfileFileSystem(bool exists, string content) : IFirefoxProfileFileSystem
	{
		public bool FileExists(string path)
		{
			return exists;
		}

		public string ReadAllText(string path)
		{
			return content;
		}
	}
}
