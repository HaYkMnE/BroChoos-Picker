using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

// Two REGRESSIONS pinned here, both discovered while reviewing the known-path tier:
//
//   N1 (HIGH)   A known-path Firefox reported ZERO profiles on any machine where Firefox
//               was found by install path — the common case — because the loop built every
//               candidate through the Chromium builder. GetLocalStateTemplate has no Firefox
//               arm, so the template was null and the candidate came back with an empty,
//               permanently-unresolved profile list. The correct Firefox reader existed all
//               along, but only the registry tier used it.
//
//   #45 (MED)   Edge/Chrome Beta and Dev are SEPARATE installs with their own directories
//               (see EnumerateKnownPaths), yet their Local State template pointed at the
//               STABLE browser's user-data root — so Beta and Dev silently reported the
//               stable browser's profile list regardless of what they actually contained.
//
// These live in their own file rather than in WindowsBrowserDetectorTests because they need
// the REAL file system for the profile readers: ChromiumProfileReader/FirefoxProfileReader
// are constructed internally with their production file-system adapters and are deliberately
// not injectable from the detector (that seam belongs to the readers' own unit tests). The
// pattern — a temp tree plus a fake environment probe that reports which exes exist — is the
// one ChromiumDiscoveryMatrixTests already established.
public sealed class KnownPathProfileResolutionTests
{
	// ---------------------------------------------------------------------------------
	// N1: known-path Firefox must read its profiles.
	// ---------------------------------------------------------------------------------

	[Fact]
	public void Detect_KnownPathFirefox_ReadsProfilesFromTheSharedProfilesIni()
	{
		using TempDirectory temp = new();
		string programFiles = Path.Combine(temp.Path, "Program Files");
		string appData = Path.Combine(temp.Path, "AppData");

		string firefox = CreateExe(programFiles, @"Mozilla Firefox\firefox.exe");
		SeedProfilesIni(appData, @"Mozilla\Firefox\profiles.ini", "default-release", "work");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[firefox],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData: Path.Combine(temp.Path, "LocalAppData"),
			appData);

		BrowserCandidate candidate = result.Single(c => c.BrowserId == "firefox-stable");
		_ = candidate.DetectionSource.Should().Be("known-path");
		// The whole point of N1: profiles are READ, not silently empty.
		_ = candidate.ProfileIds.Should().Equal("default-release", "work");
		_ = candidate.HasUnresolvedProfiles.Should().BeFalse();
	}

	// Mozilla documents that on Windows the Developer and Nightly builds get their own
	// PROGRAM directory but share the profile by default: in the mainstream same-machine
	// case the channel does not fork profiles.ini. This pins the code's chosen behaviour
	// (one shared root), so a future "give Dev its own root" change must argue with the
	// vendor's documented default rather than break a test that merely happened to pass.
	// Known limit: Nightly may create a separate profile root in some configurations, and
	// Firefox 67+ uses installs.ini; if that becomes the norm for a target build, update the
	// production constant and this test together.
	[Fact]
	public void Detect_KnownPathFirefoxDeveloperAndNightly_ReadTheOneSharedProfilesIniRoot()
	{
		using TempDirectory temp = new();
		string programFiles = Path.Combine(temp.Path, "Program Files");
		string appData = Path.Combine(temp.Path, "AppData");

		string developerEdition = CreateExe(programFiles, @"Firefox Developer Edition\firefox.exe");
		string nightly = CreateExe(programFiles, @"Firefox Nightly\firefox.exe");
		SeedProfilesIni(appData, @"Mozilla\Firefox\profiles.ini", "shared");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[developerEdition, nightly],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData: Path.Combine(temp.Path, "LocalAppData"),
			appData);

		BrowserCandidate developer = result.Single(c => c.BrowserId == "firefox-developer");
		BrowserCandidate nightlyCandidate = result.Single(c => c.BrowserId == "firefox-nightly");

		_ = developer.ProfileIds.Should().Equal("shared");
		_ = nightlyCandidate.ProfileIds.Should().Equal("shared");
		_ = developer.HasUnresolvedProfiles.Should().BeFalse();
		_ = nightlyCandidate.HasUnresolvedProfiles.Should().BeFalse();
	}

	// The fix must not trade one failure mode for another: a Firefox with no readable
	// profiles.ini still yields a candidate, still reports zero profiles, and still marks
	// them unresolved. Fail-closed is preserved, not bypassed by the new dispatch.
	[Fact]
	public void Detect_KnownPathFirefoxWithNoProfilesIni_StaysFailClosed()
	{
		using TempDirectory temp = new();
		string programFiles = Path.Combine(temp.Path, "Program Files");

		string firefox = CreateExe(programFiles, @"Mozilla Firefox\firefox.exe");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[firefox],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData: Path.Combine(temp.Path, "LocalAppData"),
			appData: Path.Combine(temp.Path, "AppData"));

		BrowserCandidate candidate = result.Single();
		_ = candidate.BrowserId.Should().Be("firefox-stable");
		_ = candidate.ProfileIds.Should().BeEmpty();
		_ = candidate.HasUnresolvedProfiles.Should().BeTrue();
	}

	// An unset %AppData% is an ordinary machine state. The Firefox template must then resolve
	// to null and be handed to the reader as an empty path — which is the reader's own
	// "unresolved, no profiles" branch — rather than crashing or probing a literal "%AppData%".
	[Fact]
	public void Detect_KnownPathFirefoxWithNoAppDataVariable_StaysFailClosed()
	{
		using TempDirectory temp = new();
		string programFiles = Path.Combine(temp.Path, "Program Files");

		string firefox = CreateExe(programFiles, @"Mozilla Firefox\firefox.exe");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[firefox],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData: Path.Combine(temp.Path, "LocalAppData"),
			appData: null);

		BrowserCandidate candidate = result.Single();
		_ = candidate.ProfileIds.Should().BeEmpty();
		_ = candidate.HasUnresolvedProfiles.Should().BeTrue();
	}

	// ---------------------------------------------------------------------------------
	// #45: Beta and Dev must read THEIR OWN Local State, never the stable browser's.
	// ---------------------------------------------------------------------------------

	[Fact]
	public void Detect_KnownPathEdgeBetaAndDev_ReadTheirOwnLocalState()
	{
		using TempDirectory temp = new();
		string programFiles = Path.Combine(temp.Path, "Program Files");
		string localAppData = Path.Combine(temp.Path, "LocalAppData");

		string beta = CreateExe(programFiles, @"Microsoft\Edge Beta\Application\msedge.exe");
		string dev = CreateExe(programFiles, @"Microsoft\Edge Dev\Application\msedge.exe");

		// Deliberately NO stable Edge install and NO stable Edge Local State: any profile
		// Beta or Dev reports can only have come from its own root.
		SeedLocalState(localAppData, @"Microsoft\Edge Beta\User Data\Local State", "BetaProfile");
		SeedLocalState(localAppData, @"Microsoft\Edge Dev\User Data\Local State", "DevProfile");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[beta, dev],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData,
			appData: Path.Combine(temp.Path, "AppData"));

		_ = result.Single(c => c.BrowserId == "edge-beta").ProfileIds.Should().Equal("BetaProfile");
		_ = result.Single(c => c.BrowserId == "edge-dev").ProfileIds.Should().Equal("DevProfile");
	}

	[Fact]
	public void Detect_KnownPathChromeBetaAndDev_ReadTheirOwnLocalState()
	{
		using TempDirectory temp = new();
		string programFiles = Path.Combine(temp.Path, "Program Files");
		string localAppData = Path.Combine(temp.Path, "LocalAppData");

		string beta = CreateExe(programFiles, @"Google\Chrome Beta\Application\chrome.exe");
		string dev = CreateExe(programFiles, @"Google\Chrome Dev\Application\chrome.exe");

		SeedLocalState(localAppData, @"Google\Chrome Beta\User Data\Local State", "ChromeBetaProfile");
		SeedLocalState(localAppData, @"Google\Chrome Dev\User Data\Local State", "ChromeDevProfile");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[beta, dev],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData,
			appData: Path.Combine(temp.Path, "AppData"));

		_ = result.Single(c => c.BrowserId == "chrome-beta").ProfileIds.Should().Equal("ChromeBetaProfile");
		_ = result.Single(c => c.BrowserId == "chrome-dev").ProfileIds.Should().Equal("ChromeDevProfile");
	}

	// The SHARPEST anti-regression pin for #45. Only the STABLE Edge Local State exists here,
	// and only a Beta install is on a known path. Before the fix Beta borrowed the stable
	// template and reported the stable profile list — this test would then have seen
	// "StableProfile" where it demands an unresolved, empty list. It therefore fails on the
	// defect and passes only when Beta genuinely looks at its own (absent) root.
	[Fact]
	public void Detect_KnownPathEdgeBetaWithOnlyStableLocalState_DoesNotBorrowStableProfiles()
	{
		using TempDirectory temp = new();
		string programFiles = Path.Combine(temp.Path, "Program Files");
		string localAppData = Path.Combine(temp.Path, "LocalAppData");

		string beta = CreateExe(programFiles, @"Microsoft\Edge Beta\Application\msedge.exe");

		SeedLocalState(localAppData, @"Microsoft\Edge\User Data\Local State", "StableProfile");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[beta],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData,
			appData: Path.Combine(temp.Path, "AppData"));

		BrowserCandidate candidate = result.Single();
		_ = candidate.BrowserId.Should().Be("edge-beta");
		_ = candidate.ProfileIds.Should().BeEmpty();
		_ = candidate.HasUnresolvedProfiles.Should().BeTrue();
	}

	// The mirror of the pin above for the Chrome family, so a fix that corrects only Edge
	// fails here instead of passing on a technicality.
	[Fact]
	public void Detect_KnownPathChromeDevWithOnlyStableLocalState_DoesNotBorrowStableProfiles()
	{
		using TempDirectory temp = new();
		string programFiles = Path.Combine(temp.Path, "Program Files");
		string localAppData = Path.Combine(temp.Path, "LocalAppData");

		string dev = CreateExe(programFiles, @"Google\Chrome Dev\Application\chrome.exe");

		SeedLocalState(localAppData, @"Google\Chrome\User Data\Local State", "StableProfile");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[dev],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData,
			appData: Path.Combine(temp.Path, "AppData"));

		BrowserCandidate candidate = result.Single();
		_ = candidate.BrowserId.Should().Be("chrome-dev");
		_ = candidate.ProfileIds.Should().BeEmpty();
		_ = candidate.HasUnresolvedProfiles.Should().BeTrue();
	}

	// Canary keeps its own "SxS" root; #45 must not have "simplified" it onto the stable path.
	// Canary is installed under %LocalAppData%, so it exercises the LocalAppData template.
	[Fact]
	public void Detect_KnownPathEdgeCanary_ReadsTheSxSRoot()
	{
		using TempDirectory temp = new();
		string localAppData = Path.Combine(temp.Path, "LocalAppData");
		string programFiles = Path.Combine(temp.Path, "Program Files");

		string canary = CreateExe(localAppData, @"Microsoft\Edge SxS\Application\msedge.exe");
		SeedLocalState(localAppData, @"Microsoft\Edge SxS\User Data\Local State", "CanaryProfile");

		IReadOnlyList<BrowserCandidate> result = Detect(
			[canary],
			programFiles,
			programFilesX86: Path.Combine(temp.Path, "Program Files (x86)"),
			localAppData,
			appData: Path.Combine(temp.Path, "AppData"));

		_ = result.Single(c => c.BrowserId == "edge-canary").ProfileIds.Should().Equal("CanaryProfile");
	}

	// ---------------------------------------------------------------------------------
	// Shared helpers
	// ---------------------------------------------------------------------------------

	private static IReadOnlyList<BrowserCandidate> Detect(
		IEnumerable<string> existingFiles,
		string programFiles,
		string programFilesX86,
		string localAppData,
		string? appData)
	{
		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			new FakeEnvironmentProbe(existingFiles, programFiles, programFilesX86, localAppData, appData),
			new FakeRegistrySource([]));

		return detector.Detect();
	}

	private static string CreateExe(string root, string relative)
	{
		string path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
		_ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "stub");
		return path;
	}

	// A real profiles.ini on disk, so the production FirefoxProfileReader (which owns a real
	// file-system adapter and is intentionally not injectable from the detector) reads it.
	private static void SeedProfilesIni(string appData, string relative, params string[] profileNames)
	{
		string path = Path.Combine(appData, relative.Replace('\\', Path.DirectorySeparatorChar));
		_ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		System.Text.StringBuilder ini = new();
		_ = ini.Append("[General]").Append('\n');
		_ = ini.Append("StartWithLastProfile=1").Append('\n');
		for (int i = 0; i < profileNames.Length; i++)
		{
			_ = ini.Append('\n');
			_ = ini.Append("[Profile").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("]\n");
			_ = ini.Append("Name=").Append(profileNames[i]).Append('\n');
			_ = ini.Append("IsRelative=1\n");
			_ = ini.Append("Path=Profiles/").Append(profileNames[i]).Append('\n');
		}

		File.WriteAllText(path, ini.ToString());
	}

	private static void SeedLocalState(string localAppData, string relative, params string[] profileIds)
	{
		string path = Path.Combine(localAppData, relative.Replace('\\', Path.DirectorySeparatorChar));
		_ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string entries = string.Join(",", profileIds.Select(id => $"\"{id}\":{{}}"));
		string json = $$"""
		{
		  "profile": {
		    "info_cache": {
		      {{entries}}
		    }
		  }
		}
		""";
		File.WriteAllText(path, json);
	}

	private sealed class FakeEnvironmentProbe(
		IEnumerable<string> existingFiles,
		string programFiles,
		string programFilesX86,
		string localAppData,
		string? appData) : IEnvironmentProbe
	{
		private readonly HashSet<string> _existingFiles = new(existingFiles, StringComparer.OrdinalIgnoreCase);

		public string? CurrentProcessPath => @"C:\Dev\BroChoos\BroChoos.exe";

		public string? GetEnvironmentVariable(string name)
		{
			return name switch
			{
				"ProgramFiles" => programFiles,
				"ProgramFiles(x86)" => programFilesX86,
				"LocalAppData" => localAppData,
				"AppData" => appData,
				_ => null,
			};
		}

		public bool FileExists(string path)
		{
			return _existingFiles.Contains(path);
		}
	}

	private sealed class FakeRegistrySource(IReadOnlyList<RegistryBrowserRegistration> registrations) : IRegistryBrowserSource
	{
		public IReadOnlyList<RegistryBrowserRegistration> GetRegistrations()
		{
			return registrations;
		}
	}

	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "brochoos-n1-" + Guid.NewGuid().ToString("N"));
			_ = Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public void Dispose()
		{
			if (Directory.Exists(Path))
			{
				Directory.Delete(Path, recursive: true);
			}
		}
	}
}
