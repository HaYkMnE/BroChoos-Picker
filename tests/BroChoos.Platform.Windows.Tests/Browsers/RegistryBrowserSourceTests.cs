using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

public sealed class RegistryBrowserSourceTests
{
	private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
	private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

	// Moved here from ShellCommandParserTests: this is RegistryBrowserSource's parser,
	// not the shell-command parser's.
	[Theory]
	[InlineData(@"Software\Clients\StartMenuInternet\Google Chrome\Capabilities", "Google Chrome")]
	[InlineData(@"SOFTWARE\Clients\StartMenuInternet\Microsoft Edge\Capabilities", "Microsoft Edge")]
	[InlineData(@"Software\Clients\Mail\Contoso\Capabilities", null)]
	[InlineData(@"Software\Clients\StartMenuInternet", null)]
	public void ExtractClientName_ResolvesStartMenuInternetKeysOnly(string capabilitiesPath, string? expected)
	{
		_ = RegistryBrowserSource.ExtractClientName(capabilitiesPath).Should().Be(expected);
	}

	// 255 is the Win32 key-name limit and is still usable; anything longer would make
	// RegistryKey.OpenSubKey throw ArgumentException, so it must be rejected up front.
	[Theory]
	[InlineData(255, true)]
	[InlineData(256, false)]
	[InlineData(4096, false)]
	public void ExtractClientName_RejectsNamesLongerThanTheRegistryKeyLimit(int nameLength, bool expectAccepted)
	{
		string clientName = new('a', nameLength);
		string capabilitiesPath = $@"Software\Clients\StartMenuInternet\{clientName}\Capabilities";

		string? actual = RegistryBrowserSource.ExtractClientName(capabilitiesPath);

		_ = actual.Should().Be(expectAccepted ? clientName : null);
	}

	// (a) An attacker-writable RegisteredApplications value naming a >255-char client
	// used to reach OpenSubKey and throw ArgumentException out of Detect().
	[Fact]
	public void GetRegistrations_RegisteredApplicationsNameExceedsKeyLimit_YieldsNothingAndDoesNotThrow()
	{
		string hostileClient = new('a', 300);
		FakeHive hkcu = new();
		_ = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = hkcu.AddKey(RegistryBrowserSource.RegisteredApplicationsPath)
			.WithValue("Hostile", $@"Software\Clients\StartMenuInternet\{hostileClient}\Capabilities");

		IReadOnlyList<RegistryBrowserRegistration> result = CreateSource(hkcu: hkcu).GetRegistrations();

		_ = result.Should().BeEmpty();
	}

	// The same hostile name must not take the whole Detect() down either: this is the
	// F1 blocker end to end (exit 4, never exit 1).
	[Fact]
	public void Detect_RegisteredApplicationsNameExceedsKeyLimit_ReturnsEmptyWithoutThrowing()
	{
		string hostileClient = new('a', 300);
		FakeHive hkcu = new();
		_ = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = hkcu.AddKey(RegistryBrowserSource.RegisteredApplicationsPath)
			.WithValue("Hostile", $@"Software\Clients\StartMenuInternet\{hostileClient}\Capabilities");

		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			new FakeEnvironmentProbe([]),
			CreateSource(hkcu: hkcu));

		_ = detector.Detect().Should().BeEmpty();
	}

	// (b) A REG_DWORD/REG_BINARY command value is not a launch command.
	[Fact]
	public void GetRegistrations_CommandValueIsNotAString_IsSkipped()
	{
		FakeHive hkcu = new();
		FakeKey clients = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = clients.AddSubKey("Microsoft Edge").AddSubKey(@"shell\open\command").WithDefaultValue(42);

		_ = CreateSource(hkcu: hkcu).GetRegistrations().Should().BeEmpty();
	}

	// A non-string value in RegisteredApplications must not be coerced either.
	[Fact]
	public void GetRegistrations_RegisteredApplicationsValueIsNotAString_IsSkipped()
	{
		FakeHive hkcu = new();
		FakeKey clients = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = clients.AddSubKey("Microsoft Edge").AddSubKey(@"shell\open\command").WithDefaultValue($"\"{EdgePath}\" %1");
		_ = hkcu.AddKey(RegistryBrowserSource.RegisteredApplicationsPath).WithValue("Edge", new byte[] { 1, 2, 3 });

		IReadOnlyList<RegistryBrowserRegistration> result = CreateSource(hkcu: hkcu).GetRegistrations();

		// Only the StartMenuInternet tier contributes; the binary value is ignored.
		_ = result.Should().ContainSingle();
		_ = result[0].DetectionSource.Should().Be(RegistryBrowserSource.SourceStartMenuInternet);
	}

	// (c) HKLM is the more authoritative tier and must be enumerated first.
	[Fact]
	public void GetRegistrations_SameClientInBothHives_ReturnsBothWithLocalMachineFirst()
	{
		FakeHive hklm = new();
		_ = hklm.AddKey(RegistryBrowserSource.StartMenuInternetPath)
			.AddSubKey("Microsoft Edge")
			.AddSubKey(@"shell\open\command")
			.WithDefaultValue($"\"{EdgePath}\" --machine %1");

		FakeHive hkcu = new();
		_ = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath)
			.AddSubKey("Microsoft Edge")
			.AddSubKey(@"shell\open\command")
			.WithDefaultValue($"\"{EdgePath}\" --user %1");

		IReadOnlyList<RegistryBrowserRegistration> result = CreateSource(hklm, hkcu).GetRegistrations();

		_ = result.Should().HaveCount(2);
		_ = result[0].ShellOpenCommand.Should().Contain("--machine");
		_ = result[1].ShellOpenCommand.Should().Contain("--user");
	}

	// (d) A RegisteredApplications entry pointing somewhere other than
	// StartMenuInternet cannot be resolved to a launchable command in this milestone.
	[Fact]
	public void GetRegistrations_RegisteredApplicationsValueWithoutStartMenuInternet_IsSkipped()
	{
		FakeHive hkcu = new();
		_ = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = hkcu.AddKey(RegistryBrowserSource.RegisteredApplicationsPath)
			.WithValue("Contoso Mail", @"Software\Clients\Mail\Contoso\Capabilities");

		_ = CreateSource(hkcu: hkcu).GetRegistrations().Should().BeEmpty();
	}

	[Fact]
	public void GetRegistrations_RegisteredApplicationsResolvesThroughClientKey_IsReported()
	{
		FakeHive hkcu = new();
		FakeKey clients = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = clients.AddSubKey("Google Chrome").AddSubKey(@"shell\open\command").WithDefaultValue($"\"{ChromePath}\" -- %1");
		_ = hkcu.AddKey(RegistryBrowserSource.RegisteredApplicationsPath)
			.WithValue("Google Chrome", @"Software\Clients\StartMenuInternet\Google Chrome\Capabilities");

		IReadOnlyList<RegistryBrowserRegistration> result = CreateSource(hkcu: hkcu).GetRegistrations();

		// Once from the StartMenuInternet tier, once from RegisteredApplications; the
		// detector is what de-duplicates by resolved path.
		_ = result.Should().HaveCount(2);
		_ = result.Should().Contain(r => r.DetectionSource == RegistryBrowserSource.SourceRegisteredApplications);
	}

	// (F7 defence in depth) REG_EXPAND_SZ values are read with
	// RegistryValueOptions.DoNotExpandEnvironmentNames precisely BECAUSE a vendor- or
	// HKCU-writable value must not splice this process's (inheritable, forgeable)
	// environment into a path heading for ProcessStartInfo.FileName. That decision only
	// holds if nothing downstream expands it either. This proves the second layer: the
	// unexpanded value travels intact through the source and the parser, and PathSafety
	// refuses it because "%ProgramFiles%\..." is NOT fully qualified (verified on
	// .NET 10 — Path.IsPathFullyQualified returns false, so it never reaches
	// GetFullPath and can never be resolved against the current directory).
	// No registry writes anywhere: the hive shape is described by the existing fakes.
	[Theory]
	[InlineData(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe")]
	[InlineData(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe")]
	[InlineData(@"%LocalAppData%\Google\Chrome\Application\chrome.exe")]
	[InlineData(@"%SystemRoot%\..\Edge\msedge.exe")]
	public void Detect_RegistryValueIsUnexpanded_IsRejectedByThePathGate(string unexpandedValue)
	{
		FakeHive hkcu = new();
		FakeKey clients = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = clients.AddSubKey("Microsoft Edge").AddSubKey(@"shell\open\command").WithDefaultValue($"\"{unexpandedValue}\" %1");

		RegistryBrowserSource source = CreateSource(hkcu: hkcu);

		// Layer 1: the source hands the value on VERBATIM. Asserted so the test cannot
		// pass for the wrong reason — if the source ever started expanding, this fails
		// here instead of quietly making the detector assertion below meaningless.
		IReadOnlyList<RegistryBrowserRegistration> registrations = source.GetRegistrations();
		_ = registrations.Should().ContainSingle();
		_ = registrations[0].ShellOpenCommand.Should().Contain(unexpandedValue);

		// Layer 2: the parser recovers the candidate and PathSafety refuses it, because
		// "%VAR%\..." is not fully qualified.
		_ = ShellCommandParser.ExtractExecutablePath(registrations[0].ShellOpenCommand).Should().BeNull();

		// Layer 3: end to end, the unexpanded value produces no launch target. Only the
		// unexpanded LITERAL is registered as an existing file, so nothing else on this
		// machine state can contribute a candidate — an empty result can only come from
		// the gate. The companion test below is what proves expansion would have
		// produced one, which is the half this assertion cannot show on its own.
		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			new FakeEnvironmentProbe([unexpandedValue]),
			source);

		_ = detector.Detect().Should().BeEmpty();
	}

	// The other half of the F7 argument: had the value been expanded anywhere in the
	// pipeline, it WOULD have resolved to a launchable target. The paths here sit under
	// "\Portable\" so they are unreachable through the known-path table — only expansion
	// of the registry value could produce them. The expanded form is registered as an
	// existing file and the unexpanded literal is NOT, so the detector still finds
	// nothing: proof that no layer expands, not merely that the target was absent.
	[Theory]
	[InlineData(@"%ProgramFiles%\Portable\Edge\msedge.exe", @"C:\Program Files\Portable\Edge\msedge.exe")]
	[InlineData(@"%ProgramFiles(x86)%\Portable\Edge\msedge.exe", @"C:\Program Files (x86)\Portable\Edge\msedge.exe")]
	[InlineData(@"%LocalAppData%\Portable\Chrome\chrome.exe", @"C:\Users\dev\AppData\Local\Portable\Chrome\chrome.exe")]
	public void Detect_UnexpandedRegistryValueWhoseExpansionWouldBeLaunchable_StillYieldsNothing(string unexpandedValue, string expandedPath)
	{
		FakeHive hkcu = new();
		FakeKey clients = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = clients.AddSubKey("Microsoft Edge").AddSubKey(@"shell\open\command").WithDefaultValue($"\"{unexpandedValue}\" %1");

		// Only the EXPANDED path exists on this described machine.
		FakeEnvironmentProbe environment = new([expandedPath]);

		// Sanity: the expanded path is itself a perfectly acceptable launch target, so
		// the gate is not rejecting it for some unrelated reason.
		_ = PathSafety.NormalizeLocalExecutablePath(expandedPath).Should().Be(expandedPath);
		_ = environment.FileExists(expandedPath).Should().BeTrue();

		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			environment,
			CreateSource(hkcu: hkcu));

		_ = detector.Detect().Should().BeEmpty();
	}

	// The unquoted form of the same value: the parser's first-token branch must not turn
	// it into a candidate either.
	[Fact]
	public void Detect_UnquotedUnexpandedRegistryValue_IsRejectedByThePathGate()
	{
		const string unexpanded = @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe";
		FakeHive hkcu = new();
		FakeKey clients = hkcu.AddKey(RegistryBrowserSource.StartMenuInternetPath);
		_ = clients.AddSubKey("Microsoft Edge").AddSubKey(@"shell\open\command").WithDefaultValue($"{unexpanded} --single-argument %1");

		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			new FakeEnvironmentProbe([unexpanded]),
			CreateSource(hkcu: hkcu));

		_ = detector.Detect().Should().BeEmpty();
	}

	[Fact]
	public void GetRegistrations_HiveIsMissing_ReturnsEmptyWithoutThrowing()
	{
		_ = CreateSource(new FakeHive(), new FakeHive()).GetRegistrations().Should().BeEmpty();
	}

	[Theory]
	[InlineData(typeof(UnauthorizedAccessException))]
	[InlineData(typeof(System.Security.SecurityException))]
	[InlineData(typeof(IOException))]
	[InlineData(typeof(ArgumentException))]
	public void GetRegistrations_HiveAccessThrowsRecoverableFailure_ReturnsEmptyWithoutThrowing(Type exceptionType)
	{
		ThrowingHive hive = new((Exception)Activator.CreateInstance(exceptionType)!);

		_ = CreateSource(hive, hive).GetRegistrations().Should().BeEmpty();
	}

	// (e) The detector must survive a registry source that throws outright, and still
	// report the known-path candidates it found on its own.
	[Fact]
	public void Detect_RegistrySourceThrows_ReturnsKnownPathResultWithoutPropagating()
	{
		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			new FakeEnvironmentProbe([EdgePath]),
			new ThrowingRegistrySource(new InvalidOperationException("registry unavailable")));

		IReadOnlyList<BrowserCandidate> result = detector.Detect();

		_ = result.Should().ContainSingle();
		_ = result[0].BrowserId.Should().Be("edge-stable");
		_ = result[0].DetectionSource.Should().Be("known-path");
	}

	[Fact]
	public void Detect_RegistrySourceThrows_AndNoKnownPaths_ReturnsEmptyWithoutPropagating()
	{
		WindowsBrowserDetector detector = new(
			NullLogger<WindowsBrowserDetector>.Instance,
			new FakeEnvironmentProbe([]),
			new ThrowingRegistrySource(new UnauthorizedAccessException()));

		_ = detector.Detect().Should().BeEmpty();
	}

	private static RegistryBrowserSource CreateSource(IRegistryHiveReader? hklm = null, IRegistryHiveReader? hkcu = null)
	{
		return new RegistryBrowserSource(hklm ?? new FakeHive(), hkcu ?? new FakeHive());
	}

	// --- Fakes describing a hive shape -------------------------------------------

	private sealed class FakeHive : IRegistryHiveReader
	{
		private readonly Dictionary<string, FakeKey> _keys = new(StringComparer.OrdinalIgnoreCase);

		public FakeKey AddKey(string path)
		{
			FakeKey key = new();
			_keys[path] = key;
			return key;
		}

		public IRegistryKeyReader? OpenSubKey(string path)
		{
			return _keys.TryGetValue(path, out FakeKey? key) ? key : null;
		}
	}

	private sealed class FakeKey : IRegistryKeyReader
	{
		private readonly Dictionary<string, FakeKey> _subKeys = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
		private object? _defaultValue;

		// Accepts a multi-segment relative path and creates the whole chain, mirroring
		// how the real registry stores "Microsoft Edge\shell\open\command" as nested
		// keys rather than one literally named key.
		public FakeKey AddSubKey(string path)
		{
			FakeKey current = this;

			foreach (string segment in SplitPath(path))
			{
				if (!current._subKeys.TryGetValue(segment, out FakeKey? next))
				{
					next = new FakeKey();
					current._subKeys[segment] = next;
				}

				current = next;
			}

			return current;
		}

		private static string[] SplitPath(string path)
		{
			return path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
		}

		public FakeKey WithValue(string name, object value)
		{
			_values[name] = value;
			return this;
		}

		public FakeKey WithDefaultValue(object value)
		{
			_defaultValue = value;
			return this;
		}

		public IReadOnlyList<string> GetSubKeyNames()
		{
			return [.. _subKeys.Keys];
		}

		public IReadOnlyList<string> GetValueNames()
		{
			return [.. _values.Keys];
		}

		// A null name means the key's default (unnamed) value, matching RegistryKey.
		public object? GetValue(string? name)
		{
			return name is null
				? _defaultValue
				: _values.TryGetValue(name, out object? value) ? value : null;
		}

		public IRegistryKeyReader? OpenSubKey(string path)
		{
			FakeKey? current = this;

			// Walk the chain segment by segment, exactly as the real API does. Verified
			// against .NET 10 on Windows: OpenSubKey validates EACH SEGMENT against the
			// 255-character key-name limit and throws ArgumentException when one exceeds
			// it, while a long TOTAL path made of short segments is accepted. That is
			// precisely the F1 blocker, so the fake has to reproduce it faithfully.
			foreach (string segment in SplitPath(path))
			{
				if (segment.Length > RegistryBrowserSource.MaxKeyNameLength)
				{
					throw new ArgumentException("Registry key name is too long.", nameof(path));
				}

				if (current is null || !current._subKeys.TryGetValue(segment, out current))
				{
					current = null;
				}
			}

			return current;
		}

		public void Dispose()
		{
			// Nothing unmanaged in the fake; the interface requires the member.
		}
	}

	private sealed class ThrowingHive(Exception exception) : IRegistryHiveReader
	{
		private readonly Exception _exception = exception;

		public IRegistryKeyReader? OpenSubKey(string path)
		{
			throw _exception;
		}
	}

	private sealed class ThrowingRegistrySource(Exception exception) : IRegistryBrowserSource
	{
		private readonly Exception _exception = exception;

		public IReadOnlyList<RegistryBrowserRegistration> GetRegistrations()
		{
			throw _exception;
		}
	}

	private sealed class FakeEnvironmentProbe(IEnumerable<string> existingFiles) : IEnvironmentProbe
	{
		private readonly HashSet<string> _existingFiles = new(existingFiles, StringComparer.OrdinalIgnoreCase);

		public string? CurrentProcessPath => @"C:\Dev\BroChoos\BroChoos.exe";

		public string? GetEnvironmentVariable(string name)
		{
			return name switch
			{
				"ProgramFiles" => @"C:\Program Files",
				"ProgramFiles(x86)" => @"C:\Program Files (x86)",
				"LocalAppData" => @"C:\Users\dev\AppData\Local",
				_ => null,
			};
		}

		public bool FileExists(string path)
		{
			return _existingFiles.Contains(path);
		}
	}
}
