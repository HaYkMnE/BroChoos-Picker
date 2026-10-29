using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Storage;

// A backup is named across the Core/Platform boundary by bare file NAME, never by
// path: Core is platform-neutral, does no I/O and composes no paths, and
// AtomicJsonFileStore already redacts its own failure messages to the name only.
public sealed class BackupNameTests
{
	[Fact]
	public void Construct_BareFileName_IsAccepted()
	{
		BackupName name = new("app.json.bak.2");

		_ = name.Value.Should().Be("app.json.bak.2");
		_ = name.ToString().Should().Be("app.json.bak.2");
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Construct_EmptyOrWhitespace_IsRejected(string value)
	{
		Action act = () => _ = new BackupName(value);

		_ = act.Should().Throw<ArgumentException>();
	}

	// A decision must not be able to smuggle a directory, a drive, an alternate
	// data stream or a traversal segment across the boundary as a "name".
	[Theory]
	[InlineData(@"backups\app.json")]
	[InlineData("backups/app.json")]
	[InlineData(@"..\..\app.json")]
	[InlineData(@"C:\app.json")]
	[InlineData("app.json:stream")]
	[InlineData(".")]
	[InlineData("..")]
	public void Construct_AnythingPathLike_IsRejected(string value)
	{
		Action act = () => _ = new BackupName(value);

		_ = act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Equality_SameName_IsValueEqual()
	{
		_ = new BackupName("app.json.bak").Should().Be(new BackupName("app.json.bak"));
		_ = new BackupName("app.json.bak").Should().NotBe(new BackupName("app.json.bak.2"));
	}
}
