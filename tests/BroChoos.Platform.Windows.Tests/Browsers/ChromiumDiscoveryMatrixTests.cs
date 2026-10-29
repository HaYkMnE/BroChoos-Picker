using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

public sealed class ChromiumDiscoveryMatrixTests
{
	[Fact]
	public void Detect_KnownPathMatrix_IncludesChromiumFamiliesAndChannels()
	{
		using TempDirectory temp = new();
		string localAppData = Path.Combine(temp.Path, "LocalAppData");
		string programFiles = Path.Combine(temp.Path, "Program Files");
		string programFilesX86 = Path.Combine(temp.Path, "Program Files (x86)");

		string edgeStable = CreateExe(programFilesX86, @"Microsoft\Edge\Application\msedge.exe");
		string edgeBeta = CreateExe(programFilesX86, @"Microsoft\Edge Beta\Application\msedge.exe");
		string chromeStable = CreateExe(programFiles, @"Google\Chrome\Application\chrome.exe");
		string chromeCanary = CreateExe(localAppData, @"Google\Chrome SxS\Application\chrome.exe");
		string braveStable = CreateExe(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe");
		string braveNightly = CreateExe(localAppData, @"BraveSoftware\Brave-Browser-Nightly\Application\brave.exe");

		SeedLocalState(localAppData, @"Google\Chrome\User Data\Local State", "Default", "Profile 1");
		SeedLocalState(localAppData, @"Microsoft\Edge\User Data\Local State", "Default");
		SeedLocalState(localAppData, @"BraveSoftware\Brave-Browser\User Data\Local State", "Profile 9");

		FakeEnvironmentProbe environment = new(
		[
			edgeStable,
			edgeBeta,
			chromeStable,
			chromeCanary,
			braveStable,
			braveNightly,
		],
		programFiles,
		programFilesX86,
		localAppData);

		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			environment,
			new FakeRegistrySource([]));

		IReadOnlyList<BrowserCandidate> result = detector.Detect();

		_ = result.Select(c => c.BrowserId).Should().Contain(
			"edge-stable",
			"edge-beta",
			"chrome-stable",
			"chrome-canary",
			"brave-stable",
			"brave-nightly");

		BrowserCandidate chromeStableCandidate = result.Single(c => c.BrowserId == "chrome-stable");
		_ = chromeStableCandidate.ProfileIds.Should().Equal("Default", "Profile 1");
		_ = chromeStableCandidate.HasUnresolvedProfiles.Should().BeFalse();
		_ = chromeStableCandidate.IsCustomBrowser.Should().BeFalse();
		_ = chromeStableCandidate.IsPortableBrowser.Should().BeFalse();

		BrowserCandidate braveStableCandidate = result.Single(c => c.BrowserId == "brave-stable");
		_ = braveStableCandidate.ProfileIds.Should().Equal("Profile 9");
	}

	[Fact]
	public void Detect_MalformedOrHugeLocalState_PreservesCandidateAndMarksUnresolved()
	{
		using TempDirectory temp = new();
		string localAppData = Path.Combine(temp.Path, "LocalAppData");
		string programFiles = Path.Combine(temp.Path, "Program Files");
		string programFilesX86 = Path.Combine(temp.Path, "Program Files (x86)");

		string chromeStable = CreateExe(programFiles, @"Google\Chrome\Application\chrome.exe");
		string edgeStable = CreateExe(programFilesX86, @"Microsoft\Edge\Application\msedge.exe");

		string malformedLocalState = Path.Combine(localAppData, @"Google\Chrome\User Data\Local State");
		_ = Directory.CreateDirectory(Path.GetDirectoryName(malformedLocalState)!);
		File.WriteAllText(malformedLocalState, "{ this is not json }");

		string hugeLocalState = Path.Combine(localAppData, @"Microsoft\Edge\User Data\Local State");
		_ = Directory.CreateDirectory(Path.GetDirectoryName(hugeLocalState)!);
		File.WriteAllBytes(hugeLocalState, new byte[2_200_000]);

		FakeEnvironmentProbe environment = new([chromeStable, edgeStable], programFiles, programFilesX86, localAppData);
		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			environment,
			new FakeRegistrySource([]));

		IReadOnlyList<BrowserCandidate> result = detector.Detect();

		BrowserCandidate chrome = result.Single(c => c.BrowserId == "chrome-stable");
		_ = chrome.ProfileIds.Should().BeEmpty();
		_ = chrome.HasUnresolvedProfiles.Should().BeTrue();

		BrowserCandidate edge = result.Single(c => c.BrowserId == "edge-stable");
		_ = edge.ProfileIds.Should().BeEmpty();
		_ = edge.HasUnresolvedProfiles.Should().BeTrue();
	}

	private static string CreateExe(string root, string relative)
	{
		string path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
		_ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "stub");
		return path;
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
		string localAppData) : IEnvironmentProbe
	{
		private readonly HashSet<string> _existingFiles = new(existingFiles, StringComparer.OrdinalIgnoreCase);

		public string? CurrentProcessPath => @"C:\\Dev\\BroChoos\\BroChoos.exe";

		public string? GetEnvironmentVariable(string name)
		{
			return name switch
			{
				"ProgramFiles" => programFiles,
				"ProgramFiles(x86)" => programFilesX86,
				"LocalAppData" => localAppData,
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
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "brochoos-w61-" + Guid.NewGuid().ToString("N"));
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
