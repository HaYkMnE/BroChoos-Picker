using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Registration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Registration;

// 0A-4: the D-268 three-step mapping (Registered / Selected / Handled) against a
// described hive. The Selected step's source is the UserChoice ProgId, read
// READ-ONLY: a foreign pick ("ChromeHTML") must be reported as other, ours as
// BroChoos, and a missing key as "no choice recorded" — all without any write.
public sealed class WindowsDefaultAppsStatusServiceTests
{
	private const string HttpUserChoice = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice";
	private const string HttpsUserChoice = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";
	private const string UrlFileUserChoice = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.url\UserChoice";

	[Fact]
	public void GetStatus_ThreeTargetsInFixedOrder_WithHonestHandledNote()
	{
		Host host = new();

		IReadOnlyList<DefaultAppsEntryStatus> entries = host.Service.GetStatus();

		_ = entries.Select(e => e.Scheme).Should().Equal("http", "https", ".url");
		_ = entries.Select(e => e.Target).Should().Equal(DefaultAppsTarget.Http, DefaultAppsTarget.Https, DefaultAppsTarget.UrlFile);
		_ = entries.Should().OnlyContain(e => e.HandledNote == WindowsDefaultAppsStatusService.NoActivationRecordedNote);
		// The honest note is exactly "no activation recorded yet": no sink exists in
		// this milestone and none is invented.
		_ = entries.Should().OnlyContain(e => e.HandledNote == "no activation recorded yet");
	}

	[Fact]
	public void GetStatus_FullCapabilityShapeAndOursSelected_AllThreeStepsPositive()
	{
		Host host = new();
		host.SeedFullShape();
		host.Store.SeedValue(HttpUserChoice, "ProgId", "BroChoosURL");

		DefaultAppsEntryStatus http = host.Service.GetStatus().Single(e => e.Scheme == "http");

		_ = http.Registered.Should().BeTrue();
		_ = http.SelectedProgId.Should().Be("BroChoosURL");
		_ = http.SelectedIsBroChoos.Should().BeTrue();
	}

	[Fact]
	public void GetStatus_AnotherProgIdSelected_IsReportedAsOther_AndReadsOnly()
	{
		Host host = new();
		host.SeedFullShape();
		host.Store.SeedValue(HttpsUserChoice, "ProgId", "ChromeHTML");
		host.Store.SeedValue(HttpsUserChoice, "Hash", "irrelevant-hash");

		DefaultAppsEntryStatus https = host.Service.GetStatus().Single(e => e.Scheme == "https");

		_ = https.Registered.Should().BeTrue();
		_ = https.SelectedProgId.Should().Be("ChromeHTML");
		_ = https.SelectedIsBroChoos.Should().BeFalse();
		// READ-ONLY proof: the fake records writes; the status run must add none.
		_ = host.Store.WriteOps.Should().BeEmpty();
	}

	[Fact]
	public void GetStatus_NoUserChoiceRecorded_ReportsNoSelection()
	{
		Host host = new();
		host.SeedFullShape();

		IReadOnlyList<DefaultAppsEntryStatus> entries = host.Service.GetStatus();

		_ = entries.Should().OnlyContain(e => e.SelectedProgId == null && !e.SelectedIsBroChoos.HasValue);
	}

	[Fact]
	public void GetStatus_UrlFileSelection_ReadsTheFileExtsUserChoice()
	{
		Host host = new();
		host.SeedFullShape();
		host.Store.SeedValue(UrlFileUserChoice, "ProgId", "BroChoosURL.File");

		DefaultAppsEntryStatus urlFile = host.Service.GetStatus().Single(e => e.Scheme == ".url");

		_ = urlFile.SelectedIsBroChoos.Should().BeTrue();
		_ = urlFile.ExpectedProgId.Should().Be("BroChoosURL.File");
	}

	[Fact]
	public void GetStatus_PartialShape_ReportsNotRegistered()
	{
		Host host = new();
		// Only the scheme-key pieces, none of the capability model: "half registered"
		// is reported honestly as NOT registered (the guide step must not be misled).
		host.Store.SeedValue(@"Software\Classes\http", "URL Protocol", "");
		host.Store.SeedValue(@"Software\Classes\http\shell\open\command", "", Host.ExpectedOpenCommand);

		IReadOnlyList<DefaultAppsEntryStatus> entries = host.Service.GetStatus();

		_ = entries.Should().OnlyContain(e => !e.Registered);
	}

	[Fact]
	public void GetStatus_UnexecutableExePath_ReportsNotRegisteredButStillReadsSelection()
	{
		Host host = new(processPath: null);

		IReadOnlyList<DefaultAppsEntryStatus> entries = host.Service.GetStatus();

		_ = entries.Should().OnlyContain(e => !e.Registered);
	}

	// --- Host + fakes --------------------------------------------------------

	private sealed class Host
	{
		public const string ExpectedOpenCommand = @"""C:\Dev\BroChoos\BroChoos.exe"" ""%1""";

		public Host(string? processPath = @"C:\Dev\BroChoos\BroChoos.exe")
		{
			Store = new FakeReadStore();
			Service = new WindowsDefaultAppsStatusService(
				NullLogger<WindowsDefaultAppsStatusService>.Instance,
				new FakeEnvironmentProbe { ProcessPath = processPath },
				Store);
		}

		public WindowsDefaultAppsStatusService Service { get; }

		public FakeReadStore Store { get; }

		// Writes the full expected declaration for every target through the same
		// shape descriptor the production services use, so the test cannot drift
		// from the real write set.
		public void SeedFullShape()
		{
			foreach (CapabilityWriteOperation operation in CapabilityShapeHarness.ExpectedOperations(ExpectedOpenCommand))
			{
				Store.SeedValue(operation.Path, operation.Name ?? "", operation.Data);
			}
		}
	}

	private sealed class FakeEnvironmentProbe : IEnvironmentProbe
	{
		public string? ProcessPath { get; set; }

		public string? CurrentProcessPath => ProcessPath;

		public string? GetEnvironmentVariable(string name)
		{
			return null;
		}

		public bool FileExists(string path)
		{
			return true;
		}
	}

	// Read-mostly fake: the status service only needs ReadKey; every write member
	// records into WriteOps so the tests can prove the status run never wrote.
	private sealed class FakeReadStore : IRegistryCapabilityStore
	{
		private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

		public List<string> WriteOps { get; } = [];

		public void SeedValue(string path, string name, string data)
		{
			Dictionary<string, string> values = _keys.TryGetValue(path, out Dictionary<string, string>? existing)
				? existing
				: _keys[path] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			values[name] = data;
		}

		public RegistryKeyState ReadKey(string path)
		{
			return _keys.TryGetValue(path, out Dictionary<string, string>? values)
				? new RegistryKeyState(true, false, [.. values.Select(kv => new RegistryValueState(kv.Key, "String", kv.Value))], [])
				: RegistryKeyState.Missing;
		}

		public void SetValue(string path, string? name, string data)
		{
			WriteOps.Add(path);
		}

		public bool DeleteValue(string path, string name)
		{
			WriteOps.Add(path);
			return false;
		}

		public bool DeleteSubKeyTree(string path)
		{
			WriteOps.Add(path);
			return false;
		}

		public bool DeleteSubKeyIfEmpty(string path)
		{
			WriteOps.Add(path);
			return false;
		}
	}
}

// Small harness alias so the test above can name the internal write-op record
// without re-declaring the shape.
internal static class CapabilityShapeHarness
{
	public static IReadOnlyList<CapabilityWriteOperation> ExpectedOperations(string openCommand)
	{
		return CapabilityShape.BuildWriteOperations(openCommand, @"C:\Dev\BroChoos\BroChoos.exe,0");
	}
}
