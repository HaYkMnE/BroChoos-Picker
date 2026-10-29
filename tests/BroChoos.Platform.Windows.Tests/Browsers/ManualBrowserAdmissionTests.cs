using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

// W6.2 manual/portable admission — the REAL gate, on REAL files.
//
// These tests deliberately do not fake the filesystem: they create a throwaway executable
// under the machine's own TEMP directory, run the production admission path over it, and let
// PathSafety consult the live volume table. A test that stubbed File.Exists and the volume
// probe would be testing the stub, and the two facts this gate exists to establish — "the
// path is on a local volume" and "the file is actually there" — are exactly the facts a stub
// would invent.
//
// The refusal cases assert the SPECIFIC reason code rather than "not admitted", because the
// whole user-facing value of the refusal is that it says which check refused. A gate that
// answered "manual-browser.path-rejected" for a missing file would send a reader to the wrong
// half of the code.
public sealed class ManualBrowserAdmissionTests : IDisposable
{
	private readonly string _tempRoot = Path.Combine(
		Path.GetTempPath(),
		"brochoos-w62-admission-" + Guid.NewGuid().ToString("N"));

	private readonly ManualBrowserAdmitter _admitter = new(new SystemEnvironmentProbe());

	public ManualBrowserAdmissionTests()
	{
		_ = Directory.CreateDirectory(_tempRoot);
	}

	[Fact]
	public void Admit_PortableExecutable_IsAcceptedWithThePortableFlagsAndNoInventedProfiles()
	{
		string exePath = CreateExecutable("BroChoosTestPortable.exe");

		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, exePath));

		_ = result.Admitted.Should().BeTrue();
		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.Admitted);
		_ = result.Candidate.Should().NotBeNull();
		_ = result.Candidate!.ExePath.Should().Be(exePath);
		_ = result.Candidate.IsPortableBrowser.Should().BeTrue();
		_ = result.Candidate.IsCustomBrowser.Should().BeFalse();
		_ = result.Candidate.Channel.Should().Be("portable");
		_ = result.Candidate.DetectionSource.Should().Be("portable");
		// No Local State / profiles.ini beside the fixture. The reader's contract is that a
		// MISSING profile list is UNRESOLVED (true), not empty-and-settled: the gate must not
		// present "we could not read a profile list" as "this browser has no profiles".
		// Pinned here rather than assumed, because the difference is the whole needs-verification
		// state W6.2 requires.
		_ = result.Candidate.ProfileIds.Should().BeEmpty();
		_ = result.Candidate.HasUnresolvedProfiles.Should().BeTrue();
	}

	[Fact]
	public void Admit_CustomExecutable_IsAcceptedWithTheCustomFlags()
	{
		string exePath = CreateExecutable("MyOwnBrowser.exe");

		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, exePath, "My Own Browser"));

		_ = result.Admitted.Should().BeTrue();
		_ = result.Candidate!.IsCustomBrowser.Should().BeTrue();
		_ = result.Candidate.IsPortableBrowser.Should().BeFalse();
		_ = result.Candidate.DisplayName.Should().Be("My Own Browser");
		_ = result.Candidate.BrowserId.Should().MatchRegex("^custom-myownbrowser-[0-9a-f]{8}$");
	}

	// #44: the id used to be keyed on the file stem alone, so two portable chrome.exe
	// installs in different folders both became "portable-chromium-chrome" and the second
	// replaced the first in the catalog. The id now carries a short stable hash of the
	// normalized full path: distinct installs get distinct ids.
	[Fact]
	public void Admit_TwoPortableInstallsWithTheSameFileName_GetDistinctIds()
	{
		string first = CreateFile(Path.Combine("one", "chrome.exe"));
		string second = CreateFile(Path.Combine("two", "chrome.exe"));

		string firstId = _admitter.Admit(new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, first)).Candidate!.BrowserId;
		string secondId = _admitter.Admit(new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, second)).Candidate!.BrowserId;

		_ = firstId.Should().MatchRegex("^portable-chromium-chrome-[0-9a-f]{8}$");
		_ = secondId.Should().MatchRegex("^portable-chromium-chrome-[0-9a-f]{8}$");
		_ = firstId.Should().NotBe(secondId);
	}

	// #44 follow-up: the SAME install reached through a directory junction must get the SAME
	// id as when reached directly; otherwise re-adding it via the junction path makes a
	// second card. A junction needs no privilege on NTFS, so this runs on a normal dev box;
	// it skips only when the volume refuses reparse points.
	[SkippableFact]
	public void Admit_SameInstallThroughADirectoryJunction_GetsTheSameIdAsTheDirectPath()
	{
		string realExe = CreateFile(Path.Combine("real-install", "chrome.exe"));
		string realDirectory = Path.GetDirectoryName(realExe)!;
		string junction = Path.Combine(_tempRoot, "junction-install");
		string? failure = JunctionFixture.TryCreate(junction, realDirectory);
		Skip.If(failure is not null, $"Directory junctions are unavailable here ({failure}).");
		string viaJunction = Path.Combine(junction, "chrome.exe");
		_ = File.Exists(viaJunction).Should().BeTrue("the junction must expose the real install");

		string directId = _admitter.Admit(new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, realExe)).Candidate!.BrowserId;
		ManualBrowserAdmissionResult linked = _admitter.Admit(new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, viaJunction));

		_ = linked.Candidate!.BrowserId.Should().Be(directId);
		_ = linked.Candidate.ExePath.Should().Be(viaJunction, "the id is resolved, the launch path stays what the user picked");
	}

	// #44: re-admitting the SAME install must give the SAME id (that is what makes the
	// catalog's same-id reconciliation a dedupe), including when the user's spelling of the
	// path differs only in case or separators — Windows paths are case-insensitive.
	[Fact]
	public void Admit_SamePathReadmitted_GetsTheSameIdRegardlessOfCaseAndSeparators()
	{
		string exePath = CreateFile(Path.Combine("same", "chrome.exe"));
		string respelled = exePath.ToUpperInvariant().Replace('\\', '/');

		string firstId = _admitter.Admit(new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, exePath)).Candidate!.BrowserId;
		string secondId = _admitter.Admit(new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, exePath)).Candidate!.BrowserId;
		string respelledId = _admitter.Admit(new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, respelled)).Candidate!.BrowserId;

		_ = secondId.Should().Be(firstId);
		_ = respelledId.Should().Be(firstId);
	}

	// The hash is a pure function of the normalized path, pinned by a fixed vector so a
	// change of algorithm (which would orphan every persisted manual id) breaks a test.
	[Fact]
	public void BuildBrowserId_IsAPureFunctionOfKindStemAndNormalizedPath()
	{
		_ = ManualBrowserAdmitter.BuildBrowserId(ManualBrowserKind.Portable, @"D:\Apps\Chrome\chrome.exe")
			.Should().Be(ManualBrowserAdmitter.BuildBrowserId(ManualBrowserKind.Portable, @"d:\apps\chrome\CHROME.EXE"));
		_ = ManualBrowserAdmitter.BuildBrowserId(ManualBrowserKind.Custom, @"D:\Apps\Chrome\chrome.exe")
			.Should().StartWith("custom-chromium-chrome-");
		_ = ManualBrowserAdmitter.BuildBrowserId(ManualBrowserKind.Portable, @"D:\Apps\Chrome\chrome.exe")
			.Should().Be("portable-chromium-chrome-" + ExpectedHash(@"D:\APPS\CHROME\CHROME.EXE"));
	}

	private static string ExpectedHash(string upperPath)
	{
		byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(upperPath));
		return Convert.ToHexStringLower(digest)[..8];
	}

	[Fact]
	public void Admit_CustomUrlPath_IsRefusedByThePathGate()
	{
		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, "https://example.com/browser.exe"));

		_ = result.Admitted.Should().BeFalse();
		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.PathRejected);
		_ = result.Candidate.Should().BeNull();
	}

	// The shapes PathSafety already refuses for the known-path and registry tiers must be
	// refused identically here. Asserted through the admission result so the requirement
	// "manual selection never scans or trusts a remote path" is pinned at the surface the
	// user actually reaches.
	[Theory]
	[InlineData(@"\\hostile\share\msedge.exe")]
	[InlineData(@"\\?\C:\Windows\System32\cmd.exe")]
	[InlineData(@"\\.\pipe\x.exe")]
	[InlineData("edge.exe")]
	[InlineData(@"C:edge.exe")]
	[InlineData(@"..\..\edge.exe")]
	public void Admit_NonLocalOrRelativePath_IsRefused(string candidate)
	{
		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, candidate));

		_ = result.Admitted.Should().BeFalse();
		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.PathRejected);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Admit_EmptyPath_IsRefusedWithItsOwnReason(string? candidate)
	{
		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, candidate!));

		_ = result.Admitted.Should().BeFalse();
		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.EmptyPath);
	}

	[Fact]
	public void Admit_LocalPathThatDoesNotExist_IsRefusedAsMissingRatherThanRejected()
	{
		string missing = Path.Combine(_tempRoot, "not-there.exe");

		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, missing));

		_ = result.Admitted.Should().BeFalse();
		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.MissingFile);
	}

	[Fact]
	public void Admit_DirectoryPath_IsRefusedAsNotAFile()
	{
		string directory = Path.Combine(_tempRoot, "a-folder.exe");
		_ = Directory.CreateDirectory(directory);

		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, directory));

		_ = result.Admitted.Should().BeFalse();
		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.NotAFile);
	}

	// A script or a shortcut is not an executable image. Refusing the extension BEFORE the
	// self-reference and profile steps keeps a ".bat" from ever being described as a browser
	// the user could pick.
	[Theory]
	[InlineData("browser.txt")]
	[InlineData("browser.lnk")]
	[InlineData("browser.bat")]
	public void Admit_NonExecutableExtension_IsRefusedAsNotAnExecutable(string fileName)
	{
		string exePath = CreateFile(fileName);

		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, exePath));

		_ = result.Admitted.Should().BeFalse();
		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.NotAnExecutable);
	}

	// An extensionless file IS admitted (a portable build whose host has no extension is a
	// real shape, and File.Exists already answered for it). Documented here so the extension
	// rule cannot be "tightened" into refusing it by accident.
	[Fact]
	public void Admit_ExtensionlessExecutableFile_IsAccepted()
	{
		string exePath = CreateFile("portable-browser");

		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Portable, exePath));

		_ = result.Admitted.Should().BeTrue();
	}

	// D-304 recursion guard at admission: the running image is a real, existing local .exe,
	// so this exercises the walk rather than a fixture that happens to look like one.
	[Fact]
	public void Admit_TheRunningExecutable_IsRefusedAsSelfReference()
	{
		string? self = Environment.ProcessPath;
		_ = self.Should().NotBeNullOrWhiteSpace();

		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, self!));

		_ = result.Admitted.Should().BeFalse();
		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.SelfReference);
	}

	// The claim "admission never scans all drives" is only meaningful if the gate cannot
	// enumerate. A recursive scan of the temp root would take unbounded time and touch
	// unrelated directories; this asserts the gate answers about the named file alone, by
	// naming a file that does not exist while siblings DO exist.
	[Fact]
	public void Admit_MissingFileBesideExistingFiles_AnswersAboutTheNamedFileOnly()
	{
		_ = CreateExecutable("present-a.exe");
		_ = CreateExecutable("present-b.exe");

		string missing = Path.Combine(_tempRoot, "absent-c.exe");

		ManualBrowserAdmissionResult result = _admitter.Admit(
			new ManualBrowserAdmissionRequest(ManualBrowserKind.Custom, missing));

		_ = result.Code.Should().Be(ManualBrowserAdmissionCodes.MissingFile);
	}

	public void Dispose()
	{
		// A junction is unlinked first and non-recursively, so the recursive delete below
		// can never walk through it into the directory it points at.
		string junction = Path.Combine(_tempRoot, "junction-install");
		try
		{
			if (Directory.Exists(junction) && File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint))
			{
				Directory.Delete(junction, recursive: false);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best effort, like the tree delete below.
		}

		// Removes this test's own throwaway tree. Nothing outside it is touched, and the
		// temp root is named with a fresh guid so a failure here can never delete a real
		// directory.
		try
		{
			Directory.Delete(_tempRoot, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best effort by contract: a leaked temp directory is not a test failure.
		}
	}

	private string CreateExecutable(string fileName)
	{
		return CreateFile(fileName);
	}

	private string CreateFile(string fileName)
	{
		string path = Path.Combine(_tempRoot, fileName);
		_ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x00]);
		return path;
	}
}
