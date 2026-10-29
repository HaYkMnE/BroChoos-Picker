using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BroChoos.Release.Tests;

// Release pipeline contract: .github/workflows/release.yml, the self-contained publish, and the
// package-manager templates under packaging/. Everything here reads files from the repository;
// only the changelog-notes tests start a powershell.exe child, and it writes only under %TEMP%.
//
// WHY THESE PINS. The product identity (name, publisher, AppId, exe name, portable marker) is
// spelled in several files that different tools read: MSBuild, Inno Setup, winget and Scoop. A
// rename in one of them produces a release whose upgrade, uninstall entry or portable mode is
// silently wrong, so each value is read from its owner and compared with every copy.
public sealed partial class ReleasePipelineTests
{
	private const string WorkflowRelativePath = @".github\workflows\release.yml";
	private const string InstallerRelativePath = @"packaging\BroChoos.Installer\BroChoos.per-user.iss";
	private const string PackagerRelativePath = @"packaging\BroChoos.Installer\package-per-user-installer.ps1";
	private const string BuildScriptRelativePath = @"tools\build-release.ps1";
	private const string NotesScriptRelativePath = @"tools\extract-changelog-notes.ps1";
	private const string ResolverRelativePath = @"src\BroChoos.Platform.Windows\Storage\StorageRootResolver.cs";
	private const string WingetLocaleRelativePath = @"packaging\winget\BroChoos.BroChoosPicker.locale.en-US.yaml";
	private const string WingetInstallerRelativePath = @"packaging\winget\BroChoos.BroChoosPicker.installer.yaml";
	private const string WingetVersionRelativePath = @"packaging\winget\BroChoos.BroChoosPicker.yaml";
	private const string ScoopRelativePath = @"packaging\scoop\brochoos-picker.json";

	// The upgrade identity of the shipped installer. Changing it makes the next release install
	// side by side with the previous one instead of upgrading it.
	private const string FrozenAppId = "D4D2C59D-B9D0-44D8-A44D-2AE5D749F9C5";

	private static readonly string[] ShippedProjects =
	[
		@"src\BroChoos.App\BroChoos.App.csproj",
		@"src\BroChoos.LaunchHelper\BroChoos.LaunchHelper.csproj",
		@"src\BroChoos.StorageHelper\BroChoos.StorageHelper.csproj",
		@"src\BroChoos.NetworkHelper\BroChoos.NetworkHelper.csproj",
		@"src\BroChoos.UpdateHelper\BroChoos.UpdateHelper.csproj",
	];

	private static string RepositoryRoot => InstallerLifecycleTests.FindRepositoryRoot();

	[GeneratedRegex(@"^\s*-?\s*uses:\s*(?<ref>\S+)(?<rest>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex UsesLine();

	[GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
	private static partial Regex ShaPinnedReference();

	[GeneratedRegex(@"^\s*#\s*v\d+(\.\d+)*\s*$", RegexOptions.CultureInvariant)]
	private static partial Regex VersionComment();

	[GeneratedRegex(@"^(?<indent>\s*)(-\s+)?run:\s*(?<inline>.*)$", RegexOptions.CultureInvariant)]
	private static partial Regex RunKey();

	[GeneratedRegex(@"^#define\s+(?<name>\w+)\s+""(?<value>[^""]*)""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex IssDefine();

	[GeneratedRegex(@"^\s*-?\s*(?<key>[A-Za-z]+):\s*(?<value>.*?)\s*$", RegexOptions.CultureInvariant)]
	private static partial Regex YamlScalar();

	[GeneratedRegex(@"const string (?<name>\w+) = ""(?<value>[^""]*)"";", RegexOptions.CultureInvariant)]
	private static partial Regex CSharpStringConst();

	[GeneratedRegex(@"^\$portableMarker = '(?<value>[^']*)'", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex BuildScriptPortableMarker();

	[GeneratedRegex(@"<(?<name>Product|Company)>(?<value>[^<]*)</", RegexOptions.CultureInvariant)]
	private static partial Regex BuildPropsIdentity();

	[Fact]
	public void ReleaseWorkflow_PinsEveryUsesByFullCommitShaWithVersionComment()
	{
		string workflow = ReadRepositoryFile(WorkflowRelativePath);
		MatchCollection uses = UsesLine().Matches(workflow);

		_ = uses.Count.Should().BeGreaterThan(0, "the release workflow uses checkout, setup-dotnet, artifact and attest actions");
		foreach (Match use in uses)
		{
			string reference = use.Groups["ref"].Value;
			_ = ShaPinnedReference().IsMatch(reference).Should().BeTrue(
				$"'{reference}' must be pinned to a full 40-hex commit SHA, not a tag or branch");
			_ = VersionComment().IsMatch(use.Groups["rest"].Value).Should().BeTrue(
				$"'{reference}' must carry the tag it was resolved from as a trailing '# vX.Y.Z' comment");
		}
	}

	[Fact]
	public void ReleaseWorkflow_HasNoExpressionsInsideRunScripts()
	{
		List<string> runScripts = ExtractRunScripts(ReadRepositoryFile(WorkflowRelativePath));

		_ = runScripts.Should().NotBeEmpty("the release workflow has run steps");
		foreach (string script in runScripts)
		{
			_ = script.Should().NotContain("${{",
				"workflow expressions inside run: are template-injected into the shell; pass values through env: instead");
		}
	}

	[Fact]
	public void ReleaseWorkflow_GrantsWritePermissionsOnlyToTheTagOnlyReleaseJob()
	{
		string workflow = ReadRepositoryFile(WorkflowRelativePath);
		string[] lines = [.. workflow.Split('\n').Select(line => line.TrimEnd('\r'))];

		int topLevel = Array.IndexOf(lines, "permissions:");
		_ = topLevel.Should().BeGreaterThan(-1, "the workflow must declare a top-level permissions block");
		_ = lines[topLevel + 1].Trim().Should().Be("contents: read", "the default for every job is read-only");

		string[] grants = ["contents: write", "id-token: write", "attestations: write"];
		foreach (string grant in grants)
		{
			_ = lines.Count(line => line.Trim() == grant).Should().Be(1, $"'{grant}' belongs to the release job only");
		}

		string releaseJob = workflow[workflow.IndexOf("\n  release:", StringComparison.Ordinal)..];
		_ = releaseJob.Should().Contain("if: github.event_name == 'push'", "only a pushed tag may publish");
		_ = releaseJob.Should().Contain("persist-credentials: false", "the write token must not stay in .git/config");
		_ = releaseJob.Should().Contain("contents: write");
		_ = releaseJob.Should().Contain("id-token: write");
		_ = releaseJob.Should().Contain("attestations: write");
		_ = releaseJob.Should().Contain("gh release create");
		_ = releaseJob.Should().Contain("subject-checksums: dist/SHA256SUMS.txt",
			"the attestation must cover both release files, which SHA256SUMS.txt lists");
	}

	[Fact]
	public void ReleaseWorkflow_TriggersOnVersionTagsAndDispatch_OnWindows2022()
	{
		string workflow = ReadRepositoryFile(WorkflowRelativePath);

		_ = workflow.Should().Contain("- 'v*.*.*'");
		_ = workflow.Should().Contain("workflow_dispatch:");
		_ = workflow.Should().NotContain("runs-on: windows-latest");
		_ = workflow.Should().Contain("runs-on: windows-2022");
		_ = workflow.Should().Contain("tools/build-release.ps1", "the workflow and local dry runs share one build script");
	}

	[Fact]
	public void ReleaseWorkflow_ReusesTheCiInnoSetupInstallStepVerbatim()
	{
		string ci = ReadRepositoryFile(@".github\workflows\ci.yml");
		string release = ReadRepositoryFile(WorkflowRelativePath);

		string ciStep = ExtractStep(ci, "- name: Install Inno Setup (pinned, hash + signer verified)");
		string releaseStep = ExtractStep(release, "- name: Install Inno Setup (pinned, hash + signer verified)");

		_ = releaseStep.Should().Be(ciStep, "the release build must install the same hash- and signer-verified Inno Setup as CI");
		string[] pins =
		[
			"INNO_SETUP_VERSION: '6.7.2'",
			"INNO_SETUP_SHA256: '9F27F8386E554EB093336A1CA5C2DCACB7DBF04AB020889491D7AC53C38A12FF'",
			"INNO_SETUP_SIGNER_THUMBPRINT: 'E0AB19C8D38CBF9C44709925122A7A02F8C70CB7'",
		];
		foreach (string pin in pins)
		{
			_ = release.Should().Contain(pin);
		}
	}

	[Fact]
	public void Publish_IsSelfContained_ForTheAppAndEveryHelper()
	{
		string packager = ReadRepositoryFile(PackagerRelativePath);

		_ = packager.Should().Contain("'--self-contained', 'true'");
		_ = packager.Should().NotContain("'--self-contained', 'false'");
		_ = packager.Should().Contain("'/p:BroChoosSelfContained=true'",
			"SelfContained is stripped from ProjectReference builds, so the helpers need the custom property");
		_ = packager.Should().Contain("'/p:PublishSingleFile=false'",
			"the App starts its helpers as loose exes next to BroChoos.exe");

		foreach (string project in ShippedProjects)
		{
			_ = ReadRepositoryFile(project).Should().Contain("<SelfContained>$(BroChoosSelfContained)</SelfContained>",
				$"{project} ships in the publish folder and must follow the release self-contained switch");
		}

		string buildScript = ReadRepositoryFile(BuildScriptRelativePath);
		_ = buildScript.Should().Contain("'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll'",
			"the release build must prove the runtime is inside the publish folder");
		_ = buildScript.Should().Contain("includedFrameworks",
			"every shipped exe must have a self-contained runtimeconfig");
	}

	[Fact]
	public void Publish_LeavesOutCreateDump()
	{
		_ = ReadRepositoryFile(ShippedProjects[0]).Should().Contain(
			"<ResolvedFileToPublish Remove=\"@(ResolvedFileToPublish)\" Condition=\"'%(Filename)%(Extension)' == 'createdump.exe'\" />",
			"the portable folder goes on PATH, so the crash-dump tool must not ship");
		_ = ReadRepositoryFile(BuildScriptRelativePath).Should().Contain("Publish folder contains createdump.exe",
			"the release build must fail if createdump.exe comes back");
	}

	[Fact]
	public void Identity_IsConsistentAcrossExeInstallerWingetAndScoop()
	{
		Dictionary<string, string> iss = IssDefine().Matches(ReadRepositoryFile(InstallerRelativePath))
			.ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
		string appName = iss["AppName"];
		string publisher = iss["AppPublisher"];

		_ = appName.Should().Be("BroChoos Picker");
		_ = publisher.Should().Be("BroChoos");

		Dictionary<string, string> props = BuildPropsIdentity().Matches(ReadRepositoryFile("Directory.Build.props"))
			.ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
		_ = props["Product"].Should().Be(appName, "the exe version resource must show the installer's product name");
		_ = props["Company"].Should().Be(publisher, "the exe version resource must show the installer's publisher");

		Dictionary<string, string> locale = ReadYamlScalars(WingetLocaleRelativePath);
		_ = locale["PackageName"].Should().Be(appName);
		_ = locale["Publisher"].Should().Be(publisher);

		string installer = ReadRepositoryFile(WingetInstallerRelativePath);
		_ = installer.Should().Contain($"DisplayName: {appName}");
		_ = installer.Should().Contain($"Publisher: {publisher}");

		using JsonDocument scoop = JsonDocument.Parse(ReadRepositoryFile(ScoopRelativePath));
		JsonElement shortcut = scoop.RootElement.GetProperty("shortcuts")[0];
		_ = shortcut[1].GetString().Should().Be(appName);
		_ = scoop.RootElement.GetProperty("description").GetString().Should().StartWith(appName);
	}

	[Fact]
	public void UpgradeIdentity_AppIdAndExeName_AreFrozen()
	{
		string iss = ReadRepositoryFile(InstallerRelativePath);

		_ = iss.Should().Contain($"AppId={{{{{FrozenAppId}}}");
		_ = iss.Should().Contain("#define AppExeName \"BroChoos.exe\"");
		_ = ReadRepositoryFile(ShippedProjects[0]).Should().Contain("<AssemblyName>BroChoos</AssemblyName>");
		_ = ReadRepositoryFile(WingetInstallerRelativePath).Should().Contain($"'{{{FrozenAppId}}}_is1'",
			"winget correlates the installed app through the Inno uninstall key {AppId}_is1");
	}

	[Fact]
	public void InstallerOutputName_MatchesTheReleaseAssetName()
	{
		_ = ReadRepositoryFile(InstallerRelativePath).Should().Contain("OutputBaseFilename=BroChoos-Picker-Setup-{#AppVersion}-x64");
		string buildScript = ReadRepositoryFile(BuildScriptRelativePath);
		_ = buildScript.Should().Contain("$installerName = \"BroChoos-Picker-Setup-$Version-x64.exe\"");
		_ = buildScript.Should().Contain("$portableName = \"BroChoos-Picker-$Version-x64-portable.zip\"");
	}

	[Fact]
	public void Winget_ManifestSet_Is1_12_0_WithPerUserInnoAndPortableZip()
	{
		string[] manifests = [WingetVersionRelativePath, WingetLocaleRelativePath, WingetInstallerRelativePath];
		foreach (string path in manifests)
		{
			Dictionary<string, string> scalars = ReadYamlScalars(path);
			_ = scalars["PackageIdentifier"].Should().Be("BroChoos.BroChoosPicker", path);
			_ = scalars["ManifestVersion"].Should().Be("1.12.0", path);
			_ = scalars["PackageVersion"].Should().Be("__VERSION__", path);
		}

		_ = ReadYamlScalars(WingetVersionRelativePath)["DefaultLocale"].Should().Be("en-US");

		string installer = ReadRepositoryFile(WingetInstallerRelativePath);
		_ = ReadRepositoryFile(InstallerRelativePath).Should().Contain("PrivilegesRequired=lowest",
			"Scope: user below is only true while the Inno installer is per-user");
		_ = installer.Should().Contain("InstallerType: inno");
		_ = installer.Should().Contain("Scope: user");
		_ = installer.Should().Contain("InstallerType: zip");
		_ = installer.Should().Contain("NestedInstallerType: portable");
		_ = installer.Should().Contain("RelativeFilePath: BroChoos.exe");
		_ = installer.Should().Contain("ArchiveBinariesDependOnPath: true",
			"BroChoos.exe finds .portable and its helpers next to Environment.ProcessPath; a WinGet\\Links symlink would break both");
		_ = installer.Should().NotContain("PortableCommandAlias:",
			"an alias that differs from the file name only by case makes winget hardlink BroChoos.exe onto itself");
	}

	[Fact]
	public void License_IsMitInWingetAndScoop()
	{
		Dictionary<string, string> locale = ReadYamlScalars(WingetLocaleRelativePath);
		_ = locale["License"].Should().Be("MIT");
		_ = locale["LicenseUrl"].Should().Be("https://github.com/HaYkMnE/BroChoos-Picker/blob/v__VERSION__/LICENSE",
			"each filled manifest must link the LICENSE of its own tag, not whatever the default branch holds later");
		_ = ReadRepositoryFile(@"tools\update-winget-manifest.ps1").Should().Contain("'__VERSION__' = $Version",
			"the fill script replaces __VERSION__ everywhere, including inside LicenseUrl");

		using JsonDocument scoop = JsonDocument.Parse(ReadRepositoryFile(ScoopRelativePath));
		_ = scoop.RootElement.GetProperty("license").GetString().Should().Be("MIT");
	}

	// LICENSE itself is NOT asserted here: it arrives with the launch/docs merge. These pin the two
	// fail-closed gates that stop a release (or a winget manifest) without it.
	[Fact]
	public void License_IsGatedInTheWorkflowAndTheWingetFillScript()
	{
		string workflow = ReadRepositoryFile(WorkflowRelativePath);
		string gate = ExtractStep(workflow, "- name: Check LICENSE (MIT, repository root)");
		_ = gate.Should().Contain("LICENSE_PATH: LICENSE");
		_ = gate.Should().Contain("LICENSE_FIRST_LINE: MIT License");
		_ = gate.Should().Contain("throw", "a missing or non-MIT LICENSE must fail the job");

		int gateIndex = workflow.IndexOf("- name: Check LICENSE (MIT, repository root)", StringComparison.Ordinal);
		int verifyIndex = workflow.IndexOf("\n  verify:", StringComparison.Ordinal);
		int buildIndex = workflow.IndexOf("\n  build:", StringComparison.Ordinal);
		_ = gateIndex.Should().BeInRange(verifyIndex, buildIndex, "the gate belongs to the verify job, which build and release need");

		string script = ReadRepositoryFile(@"tools\update-winget-manifest.ps1");
		_ = script.Should().Contain("'cat-file', '-e', $licenseObject");
		_ = script.Should().Contain("$licenseObject = \"v$($Version):LICENSE\"");
		_ = script.Should().Contain("Tag v$Version has no LICENSE at the repository root",
			"a tag without LICENSE must stop the manifest fill");
	}

	// The LICENSE gate, run for real in Windows PowerShell 5.1 (powershell.exe, the default host)
	// against a scratch copy of the script: one folder per case, each removed afterwards. This is
	// what catches the 5.1 trap where EAP=Stop turns git's stderr into a NativeCommandError.
	[SkippableFact]
	public void WingetFill_LicenseGate_BehavesPerCase_OnWindowsPowerShell51()
	{
		Skip.If(FindOnPath("git.exe") is null, "git is not on PATH; the cat-file gate cannot be exercised");

		string scratchRoot = Path.Combine(
			Path.GetTempPath(),
			"brochoos-license-gate-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
		_ = Directory.CreateDirectory(scratchRoot);
		try
		{
			// (a) tag without LICENSE: the script's own message, non-zero exit, nothing written.
			string noLicense = CreateScratchScriptCopy(scratchRoot, "tag-without-license");
			RunGit(noLicense, "init", "-q");
			RunGit(noLicense, "add", "-A");
			RunGit(noLicense, "commit", "-q", "-m", "no license");
			RunGit(noLicense, "tag", "v1.0.0");
			ProcessOutcome a = RunWingetFill(noLicense, scratchRoot);
			_ = a.ExitCode.Should().NotBe(0, a.Output);
			_ = a.Output.Should().Contain("Tag v1.0.0 has no LICENSE at the repository root, but the manifest links v1.0.0:LICENSE.");
			_ = a.Output.Should().NotContain("NativeCommandError", "git's stderr must not escape as a PowerShell error");
			_ = Directory.Exists(Path.Combine(noLicense, "out")).Should().BeFalse("no manifest may be written for a tag without LICENSE");

			// (b) not a git repository: a warning, then the fill continues.
			string notRepo = CreateScratchScriptCopy(scratchRoot, "not-a-repo");
			ProcessOutcome b = RunWingetFill(notRepo, scratchRoot);
			_ = b.ExitCode.Should().Be(0, b.Output);
			_ = b.Output.Should().Contain("is not a git repository; cannot check that v1.0.0:LICENSE exists.");
			_ = b.Output.Should().NotContain("NativeCommandError");
			_ = File.Exists(Path.Combine(notRepo, "out", "BroChoos.BroChoosPicker.locale.en-US.yaml")).Should().BeTrue(b.Output);

			// (c) tag with LICENSE: OK, manifests written.
			string withLicense = CreateScratchScriptCopy(scratchRoot, "tag-with-license");
			File.WriteAllText(Path.Combine(withLicense, "LICENSE"), "MIT License\n");
			RunGit(withLicense, "init", "-q");
			RunGit(withLicense, "add", "-A");
			RunGit(withLicense, "commit", "-q", "-m", "with license");
			RunGit(withLicense, "tag", "v1.0.0");
			ProcessOutcome c = RunWingetFill(withLicense, scratchRoot);
			_ = c.ExitCode.Should().Be(0, c.Output);
			_ = c.Output.Should().Contain("OK: v1.0.0:LICENSE exists.");
			_ = File.Exists(Path.Combine(withLicense, "out", "BroChoos.BroChoosPicker.locale.en-US.yaml")).Should().BeTrue(c.Output);
		}
		finally
		{
			DeleteScratchTree(scratchRoot);
		}
	}

	private sealed record ProcessOutcome(int ExitCode, string Output);

	// A minimal repository layout the fill script needs: tools\ with the script, packaging\winget\
	// with the real templates, and a SHA256SUMS.txt naming both 1.0.0 assets.
	private static string CreateScratchScriptCopy(string scratchRoot, string name)
	{
		string root = Path.Combine(scratchRoot, name);
		string templates = Path.Combine(root, "packaging", "winget");
		_ = Directory.CreateDirectory(Path.Combine(root, "tools"));
		_ = Directory.CreateDirectory(templates);
		File.Copy(Path.Combine(RepositoryRoot, @"tools\update-winget-manifest.ps1"), Path.Combine(root, "tools", "update-winget-manifest.ps1"));
		foreach (string template in Directory.GetFiles(Path.Combine(RepositoryRoot, @"packaging\winget"), "*.yaml"))
		{
			File.Copy(template, Path.Combine(templates, Path.GetFileName(template)));
		}

		string zeros = new('0', 64);
		File.WriteAllText(
			Path.Combine(root, "SHA256SUMS.txt"),
			$"{zeros}  BroChoos-Picker-Setup-1.0.0-x64.exe\n{zeros}  BroChoos-Picker-1.0.0-x64-portable.zip\n");
		return root;
	}

	// Windows PowerShell 5.1 wraps Write-Warning text at the console width when output is
	// redirected (measured: the not-a-repo warning broke after "...not-a-repo is"), so the output
	// is matched with every whitespace run collapsed to one space.
	[GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
	private static partial Regex WhitespaceRun();

	private static ProcessOutcome RunWingetFill(string root, string ceiling)
	{
		ProcessOutcome raw = RunProcess(
			"powershell.exe",
			root,
			ceiling,
			"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "tools", "update-winget-manifest.ps1"),
			"-Version", "1.0.0", "-Sha256SumsPath", Path.Combine(root, "SHA256SUMS.txt"), "-OutputDir", Path.Combine(root, "out"));
		return raw with { Output = WhitespaceRun().Replace(raw.Output, " ") };
	}

	private static void RunGit(string root, params string[] arguments)
	{
		string[] withIdentity = ["-c", "user.name=release-tests", "-c", "user.email=release-tests@invalid", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false", .. arguments];
		ProcessOutcome outcome = RunProcess("git", root, Path.GetDirectoryName(root)!, withIdentity);
		_ = outcome.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)} failed: {outcome.Output}");
	}

	// GIT_CEILING_DIRECTORIES stops git from walking above the scratch folder, so the
	// "not a git repository" case cannot be satisfied by some repository higher up in %TEMP%.
	private static ProcessOutcome RunProcess(string fileName, string workingDirectory, string ceiling, params string[] arguments)
	{
		ProcessStartInfo startInfo = new(fileName)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		startInfo.Environment["GIT_CEILING_DIRECTORIES"] = ceiling;
		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(startInfo)!;
		Task<string> stdout = process.StandardOutput.ReadToEndAsync();
		Task<string> stderr = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(120_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException($"{fileName} did not exit within 120 s.");
		}

		return new ProcessOutcome(process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
	}

	private static string? FindOnPath(string fileName)
	{
		return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(directory => Path.Combine(directory, fileName))
			.FirstOrDefault(File.Exists);
	}

	// git writes its object files read-only, which Directory.Delete refuses, so attributes are
	// cleared first. Only the per-test folder under %TEMP% is ever passed here.
	//
	// BEST EFFORT BY CONTRACT. This runs in a finally block, so an exception here would REPLACE
	// the assertion failure the test is reporting. A short-lived handle (git or powershell.exe
	// still exiting, an antivirus scan) is the expected cause, so the delete is retried once after
	// a short pause; if that also fails the folder is left in %TEMP% rather than hiding the
	// test's real outcome.
	private static void DeleteScratchTree(string root)
	{
		const int retryDelayMilliseconds = 500;

		for (int attempt = 1; attempt <= 2; attempt++)
		{
			try
			{
				if (!Directory.Exists(root))
				{
					return;
				}

				foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				{
					File.SetAttributes(file, FileAttributes.Normal);
				}

				Directory.Delete(root, recursive: true);
				return;
			}
			catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
			{
				if (attempt == 2)
				{
					// Left behind on purpose: the test's own result must stay the reported one.
					return;
				}

				Thread.Sleep(retryDelayMilliseconds);
			}
		}
	}

	[Fact]
	public void AppIcon_IsWiredIntoTheExesWindowsAndInstaller()
	{
		const string iconRelativePath = @"src\BroChoos.App\Assets\brochoos.ico";
		byte[] icon = File.ReadAllBytes(Path.Combine(RepositoryRoot, iconRelativePath));
		_ = icon.Length.Should().BeGreaterThan(6);
		_ = icon[..4].Should().Equal([0, 0, 1, 0], "an .ico file starts with reserved=0 and type=1");

		string app = ReadRepositoryFile(ShippedProjects[0]);
		_ = app.Should().Contain(@"<ApplicationIcon>Assets\brochoos.ico</ApplicationIcon>");
		_ = app.Should().Contain(@"<AvaloniaResource Include=""Assets\brochoos.ico"" />");
		foreach (string helper in ShippedProjects.Skip(1))
		{
			_ = ReadRepositoryFile(helper).Should().Contain(@"<ApplicationIcon>..\BroChoos.App\Assets\brochoos.ico</ApplicationIcon>", helper);
		}

		string iss = ReadRepositoryFile(InstallerRelativePath);
		_ = iss.Should().Contain(@"SetupIconFile=..\..\src\BroChoos.App\Assets\brochoos.ico");
		_ = File.Exists(Path.GetFullPath(Path.Combine(RepositoryRoot, @"packaging\BroChoos.Installer", @"..\..\src\BroChoos.App\Assets\brochoos.ico")))
			.Should().BeTrue("SetupIconFile resolves relative to the .iss");

		_ = ReadRepositoryFile(@"src\BroChoos.App\Settings\SettingsWindow.axaml")
			.Should().Contain(@"Icon=""avares://BroChoos/Assets/brochoos.ico""", "the Settings window shows in the taskbar and Alt+Tab");
	}

	[Fact]
	public void Installer_RemovesTheOldBroChoosStartMenuShortcuts()
	{
		string iss = ReadRepositoryFile(InstallerRelativePath);

		_ = iss.Should().Contain("UsePreviousGroup=no");
		_ = iss.Should().Contain("[InstallDelete]");
		_ = iss.Should().Contain(@"Type: files; Name: ""{autoprograms}\BroChoos\BroChoos.lnk""");
		_ = iss.Should().Contain(@"Type: files; Name: ""{autoprograms}\BroChoos\Uninstall BroChoos.lnk""");
		_ = iss.Should().Contain(@"Type: dirifempty; Name: ""{autoprograms}\BroChoos""");
	}

	[Fact]
	public void PortableMarkerAndConfigFolder_MatchTheStorageResolver()
	{
		Dictionary<string, string> constants = CSharpStringConst().Matches(ReadRepositoryFile(ResolverRelativePath))
			.ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
		string marker = constants["PortableMarkerFileName"];
		string dataFolder = constants["PortableDataFolderName"];

		Match scriptMarker = BuildScriptPortableMarker().Match(ReadRepositoryFile(BuildScriptRelativePath));
		_ = scriptMarker.Success.Should().BeTrue("tools/build-release.ps1 must name the marker it adds to the zip");
		_ = scriptMarker.Groups["value"].Value.Should().Be(marker,
			"the zip marker must be the exact file name StorageRootResolver looks for next to the exe");

		using JsonDocument scoop = JsonDocument.Parse(ReadRepositoryFile(ScoopRelativePath));
		_ = scoop.RootElement.GetProperty("persist").GetString().Should().Be(dataFolder,
			"Scoop must persist the folder portable mode writes settings to");
	}

	[Fact]
	public void Scoop_ChecksAndAutoupdatesFromGitHubReleases()
	{
		using JsonDocument scoop = JsonDocument.Parse(ReadRepositoryFile(ScoopRelativePath));
		JsonElement root = scoop.RootElement;

		_ = root.GetProperty("checkver").GetProperty("github").GetString()
			.Should().Be("https://github.com/HaYkMnE/BroChoos-Picker");

		JsonElement update = root.GetProperty("autoupdate").GetProperty("architecture").GetProperty("64bit");
		_ = update.GetProperty("url").GetString().Should().Be(
			"https://github.com/HaYkMnE/BroChoos-Picker/releases/download/v$version/BroChoos-Picker-$version-x64-portable.zip");
		_ = update.GetProperty("hash").GetProperty("url").GetString().Should().EndWith("/v$version/SHA256SUMS.txt");
	}

	[Fact]
	public void ChangelogNotes_UseTheMatchingSection()
	{
		const string changelog = "# Changelog\n\n## [1.1.0] - 2026-11-30\n\n- Later.\n\n## [1.0.0] - 2026-10-29\n\n### Added\n\n- First public release.\n\n## [0.9.0]\n\n- Older.\n";

		string notes = RunNotesExtractor("1.0.0", changelog);

		_ = notes.Should().Contain("### Added").And.Contain("- First public release.");
		_ = notes.Should().NotContain("Later.").And.NotContain("Older.");
		_ = notes.Should().Contain("SHA256SUMS.txt");
	}

	[Fact]
	public void ChangelogNotes_FallBackToAGenericNote_WhenTheSectionOrFileIsMissing()
	{
		string missingSection = RunNotesExtractor("2.0.0", "# Changelog\n\n## 1.0.0\n\n- Something.\n");
		string missingFile = RunNotesExtractor("2.0.0", changelog: null);

		string[] outputs = [missingSection, missingFile];
		foreach (string notes in outputs)
		{
			_ = notes.Should().StartWith("BroChoos Picker 2.0.0 for Windows 10 and 11 (x64).");
			_ = notes.Should().NotContain("Something.");
		}
	}

	private static string ReadRepositoryFile(string relativePath)
	{
		return File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));
	}

	private static Dictionary<string, string> ReadYamlScalars(string relativePath)
	{
		Dictionary<string, string> scalars = new(StringComparer.Ordinal);
		foreach (string line in ReadRepositoryFile(relativePath).Split('\n'))
		{
			if (line.StartsWith(' ') || line.StartsWith('#'))
			{
				continue;
			}

			Match match = YamlScalar().Match(line.TrimEnd('\r'));
			if (match.Success && match.Groups["value"].Value.Length > 0)
			{
				scalars[match.Groups["key"].Value] = match.Groups["value"].Value;
			}
		}

		return scalars;
	}

	// Collects every run: script: an inline value, or the block whose lines are indented deeper
	// than the run: key.
	private static List<string> ExtractRunScripts(string workflow)
	{
		string[] lines = [.. workflow.Split('\n').Select(line => line.TrimEnd('\r'))];
		List<string> scripts = [];

		for (int index = 0; index < lines.Length; index++)
		{
			Match run = RunKey().Match(lines[index]);
			if (!run.Success)
			{
				continue;
			}

			string inline = run.Groups["inline"].Value.Trim();
			if (inline is not ("|" or ">" or "|-" or ">-"))
			{
				scripts.Add(inline);
				continue;
			}

			int keyIndent = run.Groups["indent"].Value.Length;
			List<string> block = [];
			while (index + 1 < lines.Length
				&& (lines[index + 1].Trim().Length == 0 || lines[index + 1].Length - lines[index + 1].TrimStart().Length > keyIndent))
			{
				index++;
				block.Add(lines[index]);
			}

			scripts.Add(string.Join('\n', block));
		}

		return scripts;
	}

	// One step: its "- name:" line up to the next sibling step or the end of the steps list.
	private static string ExtractStep(string workflow, string nameLine)
	{
		string[] lines = [.. workflow.Split('\n').Select(line => line.TrimEnd('\r'))];
		int start = Array.FindIndex(lines, line => line.Trim() == nameLine);
		_ = start.Should().BeGreaterThan(-1, $"step '{nameLine}' must exist");

		int indent = lines[start].Length - lines[start].TrimStart().Length;
		int end = start + 1;
		while (end < lines.Length
			&& (lines[end].Trim().Length == 0 || lines[end].Length - lines[end].TrimStart().Length > indent))
		{
			end++;
		}

		return string.Join('\n', lines[start..end]).TrimEnd();
	}

	private static string RunNotesExtractor(string version, string? changelog)
	{
		string work = Path.Combine(
			Path.GetTempPath(),
			"brochoos-release-notes-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
		_ = Directory.CreateDirectory(work);
		try
		{
			string changelogPath = Path.Combine(work, "CHANGELOG.md");
			if (changelog is not null)
			{
				File.WriteAllText(changelogPath, changelog);
			}

			string outFile = Path.Combine(work, "notes.md");
			string script = Path.Combine(RepositoryRoot, NotesScriptRelativePath);
			ProcessStartInfo startInfo = new("powershell.exe")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			string[] arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Version", version, "-OutFile", outFile, "-ChangelogPath", changelogPath];
			foreach (string argument in arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}

			using Process process = Process.Start(startInfo)!;
			Task<string> stdout = process.StandardOutput.ReadToEndAsync();
			Task<string> stderr = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(60_000))
			{
				process.Kill(entireProcessTree: true);
				throw new TimeoutException("extract-changelog-notes.ps1 did not exit within 60 s.");
			}

			_ = process.ExitCode.Should().Be(0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
			return File.ReadAllText(outFile);
		}
		finally
		{
			Directory.Delete(work, recursive: true);
		}
	}
}
