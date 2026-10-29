using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Win32;
using Xunit;

namespace BroChoos.Release.Tests;

// W10.1 release-engineering suite: the plan-mandated `InstallerLifecycleTests`.
//
// WHAT THIS SUITE PINS. The plan's W10.1 "Files/symbols" clause mandates that the
// installer assets live at `packaging/BroChoos.Installer/` and that the release test
// project is `tests/BroChoos.Release.Tests/BroChoos.Release.Tests.csproj`; W10.1 is
// "the sole creator/initial owner of the release test project and its solution entry".
// This class asserts the plan-mandated LOCATIONS and the installation contract the
// installer script must encode, so a future drift back to an ad-hoc `tools/` path (or
// the loss of the per-user/no-UserChoice/registration-recovery contract) turns red here
// instead of passing silently.
//
// WHAT THIS SUITE DOES NOT CLAIM. Package BUILD is gated on the owner-pinned Inno Setup
// toolchain. The owner record pins `6.4.3`; this machine has `6.7.2`, so the packager's
// fail-closed version gate correctly refuses and NO packaged artifact exists. These tests
// therefore verify the script/packager CONTRACT and their fail-closed wiring. They
// deliberately do NOT compile a setup with ISCC and do NOT assert that packaging works.
//
// The `dotnet test` invocation is filtered on `InstallerLifecycleTests` (the plan's
// TDD-RED command), so this class carries exactly that name.
public sealed partial class InstallerLifecycleTests
{
	private const string InstallerRelativePath = @"packaging\BroChoos.Installer\BroChoos.per-user.iss";
	private const string PackagingScriptRelativePath = @"packaging\BroChoos.Installer\package-per-user-installer.ps1";
	private const string PlanTestProjectRelativePath = @"tests\BroChoos.Release.Tests\BroChoos.Release.Tests.csproj";

	// Upper bound for any `powershell.exe` child this suite spawns. A child that blows past it
	// is terminated and surfaced as a TimeoutException; the old helper discarded WaitForExit's
	// boolean and then read ExitCode off a live process, hiding exactly this failure.
	//
	// THE PRODUCTION DEFAULT, and it stays 180_000. RunPowerShell's optional
	// `timeoutMilliseconds` parameter defaults to this value, so both real callers
	// (PackagingPreflight_* and ReadInstalledInnoSetupVersion) are unchanged at three
	// minutes. ONLY the timeout test passes a short bound, so it can observe the kill+throw
	// contract in ~1 s instead of paying the full 180 s. Exposed as `internal` (not
	// `private`) purely so that test can name the same constant the helper uses by default.
	internal const int ProcessTimeoutMilliseconds = 180_000;

	private static string RepositoryRoot => FindRepositoryRoot();

	[Fact]
	public void Installer_Assets_LiveAtPlanMandatedPackagingPath()
	{
		string installerPath = Path.Combine(RepositoryRoot, InstallerRelativePath);
		string packagerPath = Path.Combine(RepositoryRoot, PackagingScriptRelativePath);

		_ = File.Exists(installerPath).Should().BeTrue(
			$"W10.1 mandates the installer script under `packaging/BroChoos.Installer/`; expected {installerPath}");
		_ = File.Exists(packagerPath).Should().BeTrue(
			$"W10.1 mandates the packaging script under `packaging/BroChoos.Installer/`; expected {packagerPath}");
	}

	[Fact]
	public void LegacyToolsInstallerPath_IsGone()
	{
		string legacyScript = Path.Combine(RepositoryRoot, @"tools\installer\BroChoos.per-user.iss");
		string legacyPackager = Path.Combine(RepositoryRoot, @"tools\package-per-user-installer.ps1");
		string legacyTests = Path.Combine(RepositoryRoot, @"tools\tests\w10.1-per-user-installer.tests.ps1");

		_ = File.Exists(legacyScript).Should().BeFalse("the installer moved to the plan-mandated packaging/ path");
		_ = File.Exists(legacyPackager).Should().BeFalse("the packager moved to the plan-mandated packaging/ path");
		_ = File.Exists(legacyTests).Should().BeFalse("the installer slice tests moved under the release-test ownership");
	}

	[Fact]
	public void ReleaseTestProject_IsAtPlanMandatedPath()
	{
		string projectPath = Path.Combine(RepositoryRoot, PlanTestProjectRelativePath);

		_ = File.Exists(projectPath).Should().BeTrue(
			$"W10.1 is the sole creator/initial owner of the release test project; expected {projectPath}");
	}

	[Fact]
	public void InstallerScript_EncodesPerUserNoElevationContract()
	{
		string script = File.ReadAllText(Path.Combine(RepositoryRoot, InstallerRelativePath));

		_ = script.Should().Contain("PrivilegesRequired=lowest",
			"the installer must run asInvoker without elevation");
		_ = script.Should().Contain(@"DefaultDirName={localappdata}\Programs\BroChoos",
			"per-user install target is %LocalAppData%\\Programs\\BroChoos");
		_ = script.Should().NotContain("PrivilegesRequiredOverridesAllowed",
			"no machine-wide elevation override path may be offered");
	}

	[Fact]
	public void InstallerScript_EncodesRegistrationRecoveryAndNoUserChoiceWrites()
	{
		string script = File.ReadAllText(Path.Combine(RepositoryRoot, InstallerRelativePath));

		_ = script.Should().Contain("--unregister",
			"uninstall must remove BroChoos-owned registration");
		_ = script.Should().Contain("--register",
			"install must declare BroChoos-owned registration through the product CLI");
		_ = script.Should().Contain("attempting recovery cycle",
			"a failed registration must have one bounded recovery cycle");
		_ = script.Should().NotContain("UserChoice",
			"the installer must never write the Windows UserChoice key");
	}

	[Fact]
	public void PackagingScript_PreservesFailClosedToolchainVersionGate()
	{
		string packager = File.ReadAllText(Path.Combine(RepositoryRoot, PackagingScriptRelativePath));

		_ = packager.Should().Contain("toolchain.version",
			"the packager must read the exact owner-pinned toolchain version");
		_ = packager.Should().Contain("Inno Setup version mismatch",
			"a version mismatch must fail closed with an explicit diagnosis");
		_ = packager.Should().Contain("validate-installer-toolchain-approval.ps1",
			"the packager must invoke the owner-approval validator as its preflight");
	}

	// The fail-closed proof is BEHAVIOURAL, not textual: run the packager in -ValidateOnly
	// mode against the real owner record and assert it does NOT report PASS while the
	// pinned 6.4.3 differs from the installed toolchain. This is the only test here that
	// executes a process; the owner record/validator stay at their plan-owned `tools/` and
	// `docs/decisions/` locations (the plan did NOT move those).
	//
	// ENVIRONMENT PRECONDITIONS ARE REPORTED AS SKIPS, NEVER AS PASSES. A missing compiler, a
	// compiler whose version the packager itself cannot attribute, or a pin that already equals
	// the installed version all leave nothing to assert about the mismatch branch. Each of those
	// used to `return` and show up as a green pass; they are now runtime skips with the reason.
	[SkippableFact]
	public void PackagingPreflight_FailsClosed_WhenInstalledToolchainDiffersFromOwnerPin()
	{
		string? iscc = ResolveInstalledIscc();
		Skip.If(
			iscc is null,
			"no Inno Setup ISCC.exe found (PATH, then InstallLocation of the HKCU / HKLM / HKLM\\WOW6432Node " +
			"Inno Setup uninstall entries, then the well-known install folders); the mismatch branch cannot be exercised");

		string pinnedVersion = ReadOwnerPinnedVersion();
		string? installedVersion = ReadInstalledInnoSetupVersion(iscc!);

		Skip.If(
			installedVersion is null,
			$"the packager's own registry detection cannot attribute a version to '{iscc}' (no uninstall entry " +
			"whose InstallLocation matches its folder); the packager would stop on detection, not on the version gate");

		Skip.If(
			string.Equals(pinnedVersion, installedVersion, StringComparison.Ordinal),
			$"installed Inno Setup {installedVersion} equals the owner pin {pinnedVersion}; there is no mismatch to refuse");

		string packagerPath = Path.Combine(RepositoryRoot, PackagingScriptRelativePath);
		ProcessResult result = RunPowerShell(
			$"-NoProfile -File \"{packagerPath}\" -ValidateOnly -Configuration Release -Runtime win-x64 -IsccPath \"{iscc}\"");

		_ = result.ExitCode.Should().NotBe(0,
			$"the fail-closed version gate must refuse pinned {pinnedVersion} vs installed {installedVersion}");
		_ = result.CombinedOutput.Should().Contain("Inno Setup version mismatch",
			"the refusal must name the version mismatch, not a detection defect");
		_ = result.CombinedOutput.Should().NotContain("W10.1_PACKAGE_PREFLIGHT=PASS",
			"a differing toolchain must never emit the preflight PASS token");
	}

	// DEFECT #30 regression guard, part 1: the pipe-deadlock half.
	//
	// The old helper read stdout to completion BEFORE touching stderr. With a child that fills
	// the bounded stderr pipe buffer, the child blocks on its write while the parent blocks
	// waiting for stdout to close - neither side can advance. A child that simply writes a lot
	// to stderr is enough to hang the old pattern.
	//
	// THE REAL THRESHOLD IS ONE OS PIPE BUFFER (~4096 bytes), NOT 16 MB. The audit measured
	// `stderrChars` freezing at exactly 4096: a synchronous Write of a 4096-byte chunk fills
	// the buffer and the child's NEXT write blocks until the parent drains, so the sequential
	// parent - still waiting for stdout to close - deadlocks after that single chunk. Any
	// volume ABOVE one buffer therefore discriminates identically; the extra megabytes only
	// added cost and skew. 256 KB is 64x the buffer, is >= the 64 KB the audit proved suffices
	// to hang the old pattern, and keeps a wide margin so the test cannot become flaky.
	//
	// DEADLOCK-PROOFING OF THE TEST ITSELF. If the bug came back, asserting through the shared
	// WaitForExit(180_000) helper would hang CI for three minutes rather than fail. So this test
	// does NOT call RunPowerShell: it spawns the child directly and applies an independent,
	// SHORT 10-second bound. It asserts the helper-style concurrent drain completes the child
	// well inside that bound. Because THIS COPY drains concurrently, its WaitForExit(10_000) is
	// reached immediately, so a regression to a sequential read INSIDE THIS COPY leaves `exited`
	// false at the 10-second bound, the child is killed, and the assertion fails in 10s.
	//
	// WHAT THIS BOUND DOES *NOT* COVER (KNOWN LIMITATION - stated plainly, not claimed away).
	// The 10-second bound protects this test's OWN drain copy only. A regression to the
	// sequential read INSIDE RunPowerShell is NOT caught fast - by this test OR by the timeout
	// test (part 2). In both, the blocking read runs BEFORE WaitForExit ever arms the bound, so
	// neither test can fail fast on that regression: they HANG (measured: the timeout test hung
	// 301s on the same regression, no exception). The bounded-kill contract is therefore
	// unverified for that specific regression by this suite. Do NOT read the 10s bound below as
	// a fast-red guarantee for RunPowerShell itself; it is a fast-red guarantee only for the
	// copy inlined here. Closing this gap needs a design where the bound is armed outside the
	// code under test while still driving the REAL helper (e.g. a reader on a separate thread
	// the parent can abandon), which this shape deliberately does not build.
	//
	// WHY THIS TEST COPIES THE ASYNC SHAPE INSTEAD OF CALLING RunPowerShell. It is tempting to
	// route this flood through the real helper (now that it honours an injected bound) to also
	// catch a regression INSIDE RunPowerShell rather than in this copy. That was built and
	// measured, and it is WORSE: on a sequential regression the blocked read happens BEFORE the
	// helper ever reaches WaitForExit, so the injected bound can never fire and the test HANGS
	// instead of failing fast (a 120s+ probe hang, no exception). The direct-spawn shape below
	// is the only one whose independent bound is armed outside the code under test, which is
	// exactly what makes a regression in THIS COPY fail in 10s here.
	[Fact]
	public async Task RunPowerShell_CompletesWhenChildFloodsStderr_DrainingPipesConcurrently()
	{
		// 256 KB to stderr is well past the ~4 KB OS pipe-buffer threshold: a parent that reads
		// stdout first is provably stuck with this child (see the audit evidence).
		const int stderrKilobytes = 256;
		const int fastBoundMilliseconds = 10_000;
		const int stderrBytes = stderrKilobytes * 1024;
		string script =
			$"$n = {stderrBytes}; $s = 'E' * 4096; $w = [Console]::Error; " +
			"for ($i = 0; $i -lt ($n / 4096); $i++) { $w.Write($s) }";

		ProcessStartInfo startInfo = new("powershell.exe", "-NoProfile -Command \"" + script + "\"")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		using Process process = Process.Start(startInfo)!;
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		bool exited = process.WaitForExit(fastBoundMilliseconds);

		if (!exited)
		{
			process.Kill(entireProcessTree: true);
		}

		_ = exited.Should().BeTrue(
			"concurrent draining of stdout and stderr must let a child that fills the stderr " +
			"pipe buffer (here " + stderrKilobytes + " KB) exit; a sequential ReadToEnd(stdout)-" +
			"first helper deadlocks on exactly this child");
		_ = (await stderrTask).Length.Should().Be(
			stderrBytes,
			"the entire stderr payload must be drained, not truncated by the timeout");
		_ = (await stdoutTask).Should().BeEmpty(
			"this child writes only to stderr");
	}

	// DEFECT #30 regression guard, part 2: the silent-timeout half.
	//
	// The old helper discarded the WaitForExit(180_000) boolean and then read process.ExitCode
	// off a live process. RunPowerShell must instead surface a hung child as a diagnostic
	// TimeoutException that names the timeout, and must not return a bogus ProcessResult.
	//
	// THE BOUND IS INJECTED, so this test proves the kill+throw CONTRACT without paying the
	// production three minutes: the helper's optional `timeoutMilliseconds` seam carries a
	// SHORT 1_000 ms bound here, and the assertion pins that THIS bound - not the 180_000
	// default - is what the message names. The two real callers omit the parameter and still
	// run at the untouched ProcessTimeoutMilliseconds (180_000 ms) default.
	[Fact]
	public void RunPowerShell_ThrowsDiagnosticTimeout_InsteadOfReportingAHungChild()
	{
		// A bound short enough to observe in ~1 s. 1_000 ms is two orders of magnitude below
		// the production default, and the child overruns it by ~300x (`Start-Sleep 300`).
		const int shortBoundMilliseconds = 1_000;

		// `Start-Sleep 300` sleeps for 300 s; the helper must give up long before that.
		Action act = () => RunPowerShell(
			"-NoProfile -Command \"Start-Sleep 300\"",
			timeoutMilliseconds: shortBoundMilliseconds);

		TimeoutException thrown = act.Should().Throw<TimeoutException>(
			"a child that never exits must be reported, not silently read as a result").Which;
		_ = thrown.Message.Should().Contain(
			shortBoundMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
			"the diagnostic must name the timeout it exceeded so the failure is actionable");
		_ = thrown.Message.Should().NotContain(
			ProcessTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
			"the message must report the bound actually applied, not the production default");
	}

	// DEFECT #30 regression guard, part 3: the unvalidated-seam half.
	//
	// RunPowerShell's optional `timeoutMilliseconds` is validated, not trusted. A NEGATIVE value
	// is the dangerous case: `WaitForExit(-1)` means INFINITE, so accepting it would silently
	// disable the bound and let a hung child fall through to ExitCode - the exact old defect this
	// seam exists to test. Values below -1 are also unacceptable because WaitForExit throws its
	// own ArgumentOutOfRangeException before the kill block, replacing the timeout contract with
	// a frame-level error. The helper must therefore refuse a negative bound with a clear
	// ArgumentOutOfRangeException that NAMES the parameter, and must NOT silently clamp it (a
	// clamp would hide the caller bug). `null` still means the 180_000 default and 0/positive are
	// still accepted, so the two real callers - which omit the parameter - are unaffected.
	[Fact]
	public void RunPowerShell_RejectsNegativeTimeout_RatherThanDisablingTheBound()
	{
		Action act = () => RunPowerShell(
			"-NoProfile -Command \"Start-Sleep 300\"",
			timeoutMilliseconds: -1);

		ArgumentOutOfRangeException thrown = act.Should().Throw<ArgumentOutOfRangeException>(
			"a negative bound must be refused, not passed to WaitForExit(-1) where it waits forever").Which;
		_ = thrown.ParamName.Should().Be(
			"timeoutMilliseconds",
			"the failure must name the offending parameter so the caller bug is actionable");
	}

	private static string ReadOwnerPinnedVersion()
	{
		string recordPath = Path.Combine(
			RepositoryRoot, @"docs\decisions\owner-rulings\installer-toolchain-approval.json");
		using JsonDocument record = JsonDocument.Parse(File.ReadAllText(recordPath));

		return record.RootElement.GetProperty("toolchain").GetProperty("version").GetString()!;
	}

	// The uninstall roots the packager scans, IN THE PACKAGER'S ORDER
	// (packaging/BroChoos.Installer/package-per-user-installer.ps1, Get-IsccVersionFromRegistry:
	// HKCU, then HKLM, then HKLM\WOW6432Node). Reading only HKCU\...\Inno Setup 6_is1 missed every
	// machine-wide install, which is exactly how hosted runners install Inno Setup.
	private static readonly (RegistryHive Hive, string SubKey)[] InnoUninstallRoots =
	[
		(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
		(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
		(RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
	];

	private sealed record InnoUninstallEntry(string Version, string InstallLocation);

	// Same patterns as the packager: '(?i)Inno\s+Setup' on DisplayName, '^(\d+\.\d+\.\d+)' on DisplayVersion.
	[GeneratedRegex(@"Inno\s+Setup", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex InnoSetupDisplayName();

	[GeneratedRegex(@"^(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
	private static partial Regex SemverPrefix();

	// Mirrors the packager's filter exactly: DisplayName must match /Inno\s+Setup/i, DisplayVersion
	// must START with X.Y.Z (only that prefix is kept), and InstallLocation must be present and a
	// valid path. Entries failing any of these are skipped, as the packager skips them.
	//
	// Registry64 for HKLM: the packager runs in 64-bit powershell.exe, where `HKLM:\Software` is
	// the 64-bit view and `HKLM:\Software\WOW6432Node` is spelled out explicitly. Opening the
	// 64-bit view here reads the same two keys whatever this test host's bitness is.
	private static IEnumerable<InnoUninstallEntry> EnumerateInnoUninstallEntries()
	{
		foreach ((RegistryHive hive, string subKey) in InnoUninstallRoots)
		{
			RegistryView view = hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default;
			using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
			using RegistryKey? root = baseKey.OpenSubKey(subKey);
			if (root is null)
			{
				continue;
			}

			foreach (string childName in root.GetSubKeyNames())
			{
				using RegistryKey? child = root.OpenSubKey(childName);
				string? displayName = (child?.GetValue("DisplayName") as string)?.Trim();
				if (string.IsNullOrWhiteSpace(displayName) || !InnoSetupDisplayName().IsMatch(displayName))
				{
					continue;
				}

				Match semver = SemverPrefix().Match((child!.GetValue("DisplayVersion") as string)?.Trim() ?? string.Empty);
				if (!semver.Success)
				{
					continue;
				}

				string? location = (child.GetValue("InstallLocation") as string)?.Trim();
				if (string.IsNullOrWhiteSpace(location))
				{
					continue;
				}

				string normalized;
				try
				{
					normalized = Path.GetFullPath(location).TrimEnd('\\');
				}
				catch (Exception invalid) when (invalid is ArgumentException or NotSupportedException or PathTooLongException)
				{
					continue;
				}

				yield return new InnoUninstallEntry(semver.Groups[1].Value, normalized);
			}
		}
	}

	// The version the PACKAGER would attribute to this compiler: the first uninstall entry, in
	// packager order, whose InstallLocation equals the compiler's directory. No fallback to an
	// unmatched key - the packager has none either (side-by-side Inno Setup 6/7 would otherwise
	// attribute the wrong release), so `null` here means the packager fails on detection.
	private static string? ReadInstalledInnoSetupVersion(string compilerPath)
	{
		string compilerDirectory = Path.GetDirectoryName(Path.GetFullPath(compilerPath))!.TrimEnd('\\');

		return EnumerateInnoUninstallEntries()
			.FirstOrDefault(entry => string.Equals(entry.InstallLocation, compilerDirectory, StringComparison.OrdinalIgnoreCase))
			?.Version;
	}

	// Candidate order:
	//   1. ISCC.exe inside the InstallLocation of each Inno Setup uninstall entry, in the
	//      packager's root order. These are the only compilers the packager can attribute a
	//      version to, so preferring them makes this test reach the version gate.
	//   2. ISCC.exe on PATH - the packager's own discovery when -IsccPath is omitted.
	//   3. The well-known install folders (per-user and both Program Files).
	private static string? ResolveInstalledIscc()
	{
		IEnumerable<string> fromRegistry = EnumerateInnoUninstallEntries()
			.Select(entry => Path.Combine(entry.InstallLocation, "ISCC.exe"));

		IEnumerable<string> fromPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(directory => Path.Combine(directory, "ISCC.exe"));

		string[] wellKnown =
		[
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Inno Setup 6\ISCC.exe"),
			@"C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
			@"C:\Program Files\Inno Setup 6\ISCC.exe",
		];

		return fromRegistry.Concat(fromPath).Concat(wellKnown).FirstOrDefault(File.Exists);
	}

	private static ProcessResult RunPowerShell(string arguments, int? timeoutMilliseconds = null)
	{
		// The optional bound is a TEST SEAM, not a production knob: both real callers omit it
		// and inherit ProcessTimeoutMilliseconds (180 s), so their behaviour is untouched.
		//
		// ACCEPTED RANGE: `null` -> ProcessTimeoutMilliseconds (180_000 ms); `0` and every
		// positive value -> that exact bound. A NEGATIVE value is rejected rather than
		// clamped. Both halves of that choice are load-bearing:
		//   * `WaitForExit(-1)` is INFINITE, so accepting -1 would silently DISABLE the bound
		//     and resurrect the old defect this seam exists to test (reading ExitCode off a
		//     live process). A bound that can be turned off by accident is not a bound.
		//   * `WaitForExit` throws ArgumentOutOfRangeException for any value below -1, i.e.
		//     BEFORE the kill block, so that shape would surface a frame-level error instead
		//     of the diagnostic timeout contract.
		// Silently clamping to 0 would hide the caller bug, so the value is refused explicitly
		// and the exception names the parameter.
		if (timeoutMilliseconds is < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(timeoutMilliseconds),
				timeoutMilliseconds,
				"timeoutMilliseconds must be null (the 180_000 ms default) or a value in [0, int.MaxValue]; " +
				"a negative bound is rejected because WaitForExit(-1) waits without limit.");
		}

		int timeout = timeoutMilliseconds ?? ProcessTimeoutMilliseconds;

		ProcessStartInfo startInfo = new("powershell.exe", arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		using Process process = Process.Start(startInfo)!;

		// Drain BOTH pipes CONCURRENTLY. Reading stdout to completion before touching stderr is
		// the classic sequential-ReadToEnd deadlock: a child that fills the stderr pipe buffer
		// (a bounded OS pipe) blocks on its write while this process is still blocked waiting
		// for stdout to close, and neither side can advance. Starting both reads before waiting
		// removes that structure entirely, whatever the child's output volume.
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();

		// A discarded WaitForExit boolean used to let a hung child fall through to
		// `process.ExitCode` on a still-running process (InvalidOperationException) or a wrong
		// result. Surface the timeout instead of proceeding silently.
		if (!process.WaitForExit(timeout))
		{
			// GUARDED BEST-EFFORT KILL, the same INTENT as
			// StorageHelperClient.KillAbandonedProcess (a best-effort kill that must never
			// replace the reported outcome) - NOT the same idiom. The differences are
			// deliberate and this helper does not share them:
			//   * No `process.HasExited` pre-check. KillAbandonedProcess runs while the child
			//     may still be alive, so it checks first. Here `WaitForExit(timeout)` ALREADY
			//     returned false, i.e. the child is known alive at that instant; a pre-check
			//     would only re-open the race it exists to close.
			//   * A `when` filter that keeps OutOfMemoryException/StackOverflowException
			//     propagating, where KillAbandonedProcess catches a flat `Exception`. An
			//     unrecoverable process-level fault must not be swallowed here.
			// Process.Kill throws InvalidOperationException when the child exited in the race
			// between the false WaitForExit and this call, and Win32Exception when the tree walk
			// itself fails. UNGUARDED, such a throw would REPLACE the TimeoutException below -
			// masking the very diagnostic this contract exists to emit - with an exception that
			// says nothing about the timeout. The catch is total EXCEPT for OutOfMemoryException
			// and StackOverflowException, which are unrecoverable process-level faults and must
			// never be swallowed; the `when` filter keeps those propagating.
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception killFailure) when (killFailure is not OutOfMemoryException and not StackOverflowException)
			{
				// Best effort by contract: the child is already gone or its tree is unreachable.
				// The timeout remains the honest report, so this failure is deliberately dropped.
			}

			// Reap the two read tasks rather than leaving them abandoned. Once the tree is
			// killed the pipes close, so both drains complete promptly; a bounded WaitAll here
			// means no reader is still holding the pipe handles when the `using` disposes the
			// Process.
			//
			// THIS CLEANUP MUST NOT BE ABLE TO CHANGE THE REPORTED EXCEPTION. `Task.WaitAll`
			// throws AggregateException when ANY task has already faulted, and it does so even
			// when a timeout is supplied - the fault is observed before the wait begins. A
			// faulted read task (a drain that threw while the child was being killed) would
			// therefore REPLACE the TimeoutException below, so the diagnostic this contract
			// exists to emit would lose to an AggregateException that says nothing about the
			// timeout. The guard makes the reaping genuinely best-effort: its own result AND
			// its own fault are deliberately discarded, exactly as the kill's are above. The
			// same OOM/StackOverflow filter applies, since those must still propagate.
			try
			{
				_ = Task.WaitAll([stdoutTask, stderrTask], timeout);
			}
			catch (Exception reapFailure) when (reapFailure is not OutOfMemoryException and not StackOverflowException)
			{
				// Best effort by contract: the timeout is the only outcome this branch may
				// report, so a fault while reaping the readers is deliberately dropped.
			}

			throw new TimeoutException(
				$"RunPowerShell: child `powershell.exe {arguments}` did not exit within " +
				$"{timeout} ms and was terminated rather than reported as a result.");
		}

		// WaitForExit(ms) only guarantees the process object is updated; the stream-reader
		// tasks (which drain on thread-pool threads) may still be flushing the tail of the
		// pipes. Awaiting here keeps output completeness deterministic.
		string stdout = stdoutTask.GetAwaiter().GetResult();
		string stderr = stderrTask.GetAwaiter().GetResult();
		StringBuilder combined = new();
		_ = combined.Append(stdout).Append(stderr);

		return new ProcessResult(process.ExitCode, stdout, combined.ToString());
	}

	private sealed record ProcessResult(int ExitCode, string StandardOutput, string CombinedOutput);

	internal static string FindRepositoryRoot()
	{
		string? overrideRoot = Environment.GetEnvironmentVariable("BROCHOOS_RELEASE_SOURCE_ROOT");
		if (!string.IsNullOrWhiteSpace(overrideRoot))
		{
			string full = Path.GetFullPath(overrideRoot);
			return File.Exists(Path.Combine(full, "BroChoos.sln"))
				? full
				: throw new InvalidOperationException($"BROCHOOS_RELEASE_SOURCE_ROOT must contain BroChoos.sln: {full}");
		}

		DirectoryInfo? current = new(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "BroChoos.sln")))
			{
				return current.FullName;
			}

			current = current.Parent;
		}

		throw new InvalidOperationException("Could not locate BroChoos.sln from the test execution directory.");
	}
}
