using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

public sealed class ShellCommandParserTests
{
	[Theory]
	[InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" -- \"%1\"", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
	[InlineData("\"C:\\Edge\\msedge.exe\"", @"C:\Edge\msedge.exe")]
	[InlineData(@"C:\Edge\msedge.exe --single-argument %1", @"C:\Edge\msedge.exe")]
	[InlineData(@"   C:\Edge\msedge.exe   ", @"C:\Edge\msedge.exe")]
	public void ExtractExecutablePath_ReadsTheExecutableOnly(string command, string expected)
	{
		_ = ShellCommandParser.ExtractExecutablePath(command).Should().Be(expected);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("\"")]
	[InlineData("\"\"")]
	// Relative or bare names must not become a ProcessStartInfo.FileName (PATH lookup risk).
	[InlineData("msedge.exe %1")]
	[InlineData(@"..\..\msedge.exe")]
	// A non-exe target is out of contract for a browser registration.
	[InlineData(@"C:\Windows\System32\cmd.exe.bat")]
	[InlineData(@"C:\Edge\msedge")]
	// Unquoted paths with spaces are ambiguous by the registry convention; the first
	// token is not a real executable, so the entry is dropped rather than guessed.
	[InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe %1")]
	public void ExtractExecutablePath_RejectsMalformedCommands(string command)
	{
		_ = ShellCommandParser.ExtractExecutablePath(command).Should().BeNull();
	}

	// (k) A registry value can be absent or of a non-string type, which reaches the
	// parser as null. That is a normal machine state, not a programmer error.
	[Fact]
	public void ExtractExecutablePath_NullCommand_ReturnsNull()
	{
		_ = ShellCommandParser.ExtractExecutablePath(null).Should().BeNull();
	}

	// (f) UNC is the F2 blocker: Path.IsPathFullyQualified returns TRUE for
	// \\server\share\x.exe, so the old check let a remote executable through.
	[Theory]
	[InlineData("\"\\\\server\\share\\msedge.exe\" %1")]
	[InlineData("\"\\\\server\\share\\Edge\\msedge.exe\"")]
	[InlineData(@"\\server\share\msedge.exe --single-argument %1")]
	[InlineData("\"\\\\10.0.0.5\\c$\\Windows\\msedge.exe\"")]
	public void ExtractExecutablePath_RejectsUncPaths(string command)
	{
		_ = ShellCommandParser.ExtractExecutablePath(command).Should().BeNull();
	}

	// (g) Win32 device paths are equally fully-qualified and equally unacceptable.
	// Path.GetFullPath passes both forms through verbatim, so only an explicit
	// drive-letter check rejects them.
	[Theory]
	[InlineData(@"\\.\pipe\x.exe")]
	[InlineData("\"\\\\.\\pipe\\x.exe\" %1")]
	[InlineData(@"\\?\C:\Program Files\Microsoft\Edge\Application\msedge.exe")]
	[InlineData("\"\\\\?\\C:\\Edge\\msedge.exe\"")]
	[InlineData(@"\\?\UNC\server\share\msedge.exe")]
	public void ExtractExecutablePath_RejectsDevicePaths(string command)
	{
		_ = ShellCommandParser.ExtractExecutablePath(command).Should().BeNull();
	}

	// (h) Alternate data stream. Documented outcome on .NET 10 / Windows: the path is
	// fully qualified AND drive-rooted, and Path.GetFullPath preserves the ":stream.exe"
	// suffix verbatim, so this candidate IS ACCEPTED by the parser. It is not a hole in
	// this milestone: the detector additionally requires the file to exist and its
	// Path.GetFileName to equal msedge.exe/chrome.exe, and "msedge.exe:stream.exe"
	// satisfies neither. Asserted explicitly so a future runtime change is caught here
	// rather than silently altering behaviour.
	[Fact]
	public void ExtractExecutablePath_AlternateDataStreamPath_IsReturnedVerbatimAndFailsTheDetectorNameCheck()
	{
		const string adsPath = @"C:\Edge\msedge.exe:stream.exe";

		string? actual = ShellCommandParser.ExtractExecutablePath($"\"{adsPath}\" %1");

		_ = actual.Should().Be(adsPath);
		// The detector's identity check is what rejects it.
		_ = Path.GetFileName(actual!).Should().NotBe("msedge.exe");
		_ = Path.GetFileName(actual!).Should().Be("msedge.exe:stream.exe");
	}

	// (i) An over-long path makes Path.GetFullPath throw PathTooLongException; the
	// parser must degrade to null rather than let it escape into detection.
	[Fact]
	public void ExtractExecutablePath_ExtremelyLongPath_ReturnsNullWithoutThrowing()
	{
		string huge = @"C:\" + new string('a', 40_000) + @"\msedge.exe";

		// (F10) Invoked ONCE and asserted on the captured result. The old
		// NotThrow()-then-call-again shape parsed the 40k path twice and only proved the
		// "no throw" claim for the first call and the "null" claim for the second.
		Func<string?> act = () => ShellCommandParser.ExtractExecutablePath($"\"{huge}\" %1");
		string? actual = act.Should().NotThrow().Subject;

		_ = actual.Should().BeNull();
	}

	// (j) A trailing dot is stripped by Win32 path normalization, so "msedge.exe."
	// would resolve to a real executable. It is rejected earlier, by the .exe suffix
	// check, which runs on the raw candidate before normalization.
	[Theory]
	[InlineData(@"C:\Edge\msedge.exe.")]
	[InlineData("\"C:\\Edge\\msedge.exe.\" %1")]
	[InlineData(@"C:\Edge\msedge.exe..")]
	public void ExtractExecutablePath_TrailingDotPath_ReturnsNull(string command)
	{
		_ = ShellCommandParser.ExtractExecutablePath(command).Should().BeNull();
	}

	// A trailing space is stripped by the same normalization; the suffix check catches
	// it only because the candidate is trimmed first, so pin the outcome.
	[Fact]
	public void ExtractExecutablePath_QuotedPathWithTrailingSpace_IsTrimmedAndAccepted()
	{
		_ = ShellCommandParser.ExtractExecutablePath("\"C:\\Edge\\msedge.exe \" %1").Should().Be(@"C:\Edge\msedge.exe");
	}

	// Normalization is part of the contract: the returned value is what becomes
	// ProcessStartInfo.FileName, so it must already be canonical.
	[Theory]
	[InlineData("\"C:\\Edge\\..\\Edge\\msedge.exe\"", @"C:\Edge\msedge.exe")]
	[InlineData("\"C:/Edge/msedge.exe\"", @"C:\Edge\msedge.exe")]
	[InlineData("\"C:\\Edge\\.\\msedge.exe\"", @"C:\Edge\msedge.exe")]
	public void ExtractExecutablePath_NormalizesAcceptedPaths(string command, string expected)
	{
		_ = ShellCommandParser.ExtractExecutablePath(command).Should().Be(expected);
	}
}
