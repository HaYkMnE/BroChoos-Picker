using System.Text.Json;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Registration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Registration;

// 0A-4: the capability declaration flow against a descri-bed hive. The fake store
// mirrors the real one INCLUDING the UserChoice guard (any attempt to write
// under UserChoice throws in the fake too), so "writes land only in the listed
// HKCU paths" and "backup precedes the first write" are proven from the recorded
// operation log, and idempotency is proven by comparing full state dumps.
public sealed class WindowsCapabilityRegistrationServiceTests
{
	private static readonly DateTimeOffset FixedNow = new(2026, 7, 31, 10, 20, 30, TimeSpan.Zero);

	// The full v13 §14 write set, pinned verbatim so a silent shape change fails here.
	private static readonly string[] ExpectedWritePaths =
	[
		@"Software\Classes\http",
		@"Software\Classes\http\shell\open\command",
		@"Software\Classes\https",
		@"Software\Classes\https\shell\open\command",
		@"Software\Classes\.url",
		@"Software\Classes\.url\shell\open\command",
		@"Software\Classes\BroChoosURL",
		@"Software\Classes\BroChoosURL\shell\open\command",
		@"Software\Classes\BroChoosURL.File",
		@"Software\Classes\BroChoosURL.File\shell\open\command",
		@"Software\Clients\StartMenuInternet\BroChoos",
		@"Software\Clients\StartMenuInternet\BroChoos\shell\open\command",
		@"Software\Clients\StartMenuInternet\BroChoos\Capabilities",
		@"Software\Clients\StartMenuInternet\BroChoos\Capabilities\URLAssociations",
		@"Software\Clients\StartMenuInternet\BroChoos\Capabilities\FileAssociations",
		@"Software\RegisteredApplications",
	];

	private const string ExpectedOpenCommand = @"""C:\Dev\BroChoos\BroChoos.exe"" ""%1""";

	[Fact]
	public void Register_WritesLandOnlyInTheListedHkcuPaths()
	{
		Host host = new();

		CapabilityRegistrationResult result = host.Service.RegisterAll();

		_ = result.Succeeded.Should().BeTrue();
		IReadOnlyList<string> writtenPaths = host.Store.RecordedWritePaths();

		// Exact set equality over DISTINCT paths (one key legitimately receives
		// several values in one run: 21 writes land on these 16 keys).
		_ = writtenPaths.Distinct(StringComparer.OrdinalIgnoreCase).Should().BeEquivalentTo(ExpectedWritePaths);
		// Belt and braces: HKCU-relative only, never an absolute/hive-qualified or
		// forbidden path. UserChoice/HKLM must appear NOWHERE in written paths.
		_ = writtenPaths.Should().OnlyContain(p =>
			p.StartsWith(@"Software\", StringComparison.Ordinal)
			&& !p.Contains("UserChoice", StringComparison.OrdinalIgnoreCase)
			&& !p.Contains("HKLM", StringComparison.OrdinalIgnoreCase)
			&& !p.Contains("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Register_WritesTheExpectedValues()
	{
		Host host = new();

		_ = host.Service.RegisterAll();

		FakeCapabilityStore store = host.Store;
		_ = store.GetValue(@"Software\Classes\http", "URL Protocol").Should().Be("");
		_ = store.GetValue(@"Software\Classes\http\shell\open\command", "").Should().Be(ExpectedOpenCommand);
		_ = store.GetValue(@"Software\Classes\https\shell\open\command", "").Should().Be(ExpectedOpenCommand);
		_ = store.GetValue(@"Software\Classes\.url\shell\open\command", "").Should().Be(ExpectedOpenCommand);
		_ = store.GetValue(@"Software\Classes\BroChoosURL", "URL Protocol").Should().Be("");
		_ = store.GetValue(@"Software\Classes\BroChoosURL", "").Should().Be("BroChoos URL");
		_ = store.GetValue(@"Software\Classes\BroChoosURL.File", "").Should().Be("BroChoos URL file");
		_ = store.GetValue(@"Software\Clients\StartMenuInternet\BroChoos", "").Should().Be("BroChoos");
		// User-visible in Windows Default Apps: pinned so a wording change is deliberate.
		_ = store.GetValue(@"Software\Clients\StartMenuInternet\BroChoos\Capabilities", "ApplicationName").Should().Be("BroChoos");
		_ = store.GetValue(@"Software\Clients\StartMenuInternet\BroChoos\Capabilities", "ApplicationDescription").Should().Be("Choose which browser and profile opens each link");
		_ = store.GetValue(@"Software\Clients\StartMenuInternet\BroChoos\Capabilities\URLAssociations", "http").Should().Be("BroChoosURL");
		_ = store.GetValue(@"Software\Clients\StartMenuInternet\BroChoos\Capabilities\URLAssociations", "https").Should().Be("BroChoosURL");
		_ = store.GetValue(@"Software\Clients\StartMenuInternet\BroChoos\Capabilities\FileAssociations", ".url").Should().Be("BroChoosURL.File");
		_ = store.GetValue(@"Software\RegisteredApplications", "BroChoos").Should().Be(@"Software\Clients\StartMenuInternet\BroChoos\Capabilities");
	}

	[Fact]
	public void Register_IsIdempotent_RerunProducesTheSameState()
	{
		Host host = new();

		_ = host.Service.RegisterAll();
		string firstRun = host.Store.DumpState();

		_ = host.Service.RegisterAll();
		string secondRun = host.Store.DumpState();

		_ = secondRun.Should().Be(firstRun);

		// F5: both runs share one fixed clock, so their backup names would
		// collide on the bare timestamp shape — the uniqueness suffix must keep
		// them two distinct files rather than letting the second run silently
		// overwrite the first run's pre-state.
		const string expectedPrefix = "registration-20260731T102030000Z-";
		_ = host.Artifacts.Backups.Should().HaveCount(2,
			"two registered runs in one millisecond must keep BOTH pre-state backups on disk");
		_ = host.Artifacts.Backups.Keys.Should().OnlyContain(name =>
			name.StartsWith(expectedPrefix, StringComparison.Ordinal)
			&& name.EndsWith(".json", StringComparison.Ordinal)
			&& name.Length == expectedPrefix.Length + 4 + ".json".Length,
			"the D-299 base name stays recognizable and only a 4-hex uniqueness suffix is appended");
	}

	[Fact]
	public void Register_BackupIsWrittenBeforeTheFirstRegistryWrite()
	{
		Host host = new();

		_ = host.Service.RegisterAll();

		int backupIndex = host.Ops.FindIndex(op => op.StartsWith("backup:", StringComparison.Ordinal));
		int firstWriteIndex = host.Ops.FindIndex(op => op.StartsWith("reg-", StringComparison.Ordinal));

		_ = backupIndex.Should().BeGreaterOrEqualTo(0);
		_ = firstWriteIndex.Should().BeGreaterThan(backupIndex, "D-299: the pre-write state must exist on disk before ANY registry mutation");
	}

	[Fact]
	public void Register_SnapshotsPreexistingForeignStateForRestore()
	{
		Host host = new();
		host.Store.SeedValue(@"Software\Classes\http", "ForeignMarker", "keep-me");
		host.Store.SeedValue(@"Software\RegisteredApplications", "OtherApp", @"Software\Other\Capabilities");
		// Foreign content INSIDE the scheme\shell subtree (F1): the removal plan
		// prunes through these intermediate keys, so their pre-state must be
		// captured BEFORE the first mutation or a foreign verb parked under
		// shell would be unrecoverable.
		host.Store.SeedValue(@"Software\Classes\https\shell", "", "legacy-shell-default");
		host.Store.SeedValue(@"Software\Classes\https\shell\legacy-verb\command", "", @"C:\Legacy\app.exe");

		_ = host.Service.RegisterAll();

		// The single register backup must contain the foreign pre-state verbatim.
		_ = host.Artifacts.Backups.Should().ContainSingle();
		string backupJson = host.Artifacts.Backups.Values.Single();
		using JsonDocument doc = JsonDocument.Parse(backupJson);
		JsonElement root = doc.RootElement;
		string raw = root.GetRawText();
		_ = raw.Should().Contain("ForeignMarker");
		_ = raw.Should().Contain("keep-me");
		_ = raw.Should().Contain("OtherApp");
		_ = raw.Should().Contain("legacy-shell-default");
		_ = raw.Should().Contain("legacy-verb");
		// The intermediate keys themselves are snapshot members (F1), not just
		// the leaf keys the write set names.
		string[] snapshotPaths = [.. root.GetProperty("keys").EnumerateArray()
			.Select(key => key.GetProperty("path").GetString()!)];
		_ = snapshotPaths.Should().Contain(@"HKCU\Software\Classes\https\shell");
		_ = snapshotPaths.Should().Contain(@"HKCU\Software\Classes\https\shell\open");
		// And the backup itself is a local restore artifact, so the REAL command under
		// registration is recorded as metadata for a manual restore (the D-291 LOG
		// redaction rules apply to system_changes.log, not to this file).
		_ = root.GetProperty("declaredOpenCommand").GetString().Should().Be(ExpectedOpenCommand);
	}

	[Fact]
	public void Register_AppendsWellFormedRedactedJsonLines()
	{
		Host host = new();

		_ = host.Service.RegisterAll();

		IReadOnlyList<string> lines = host.Artifacts.LogLines;
		// 1 backup + 21 writes (16 keys, several multi-value) + 1 verify line.
		_ = lines.Should().HaveCount(23);

		foreach (string line in lines)
		{
			using JsonDocument doc = JsonDocument.Parse(line);
			JsonElement root = doc.RootElement;

			// Locked D-291 schema: exactly ts / action / result / path.
			_ = root.EnumerateObject().Select(p => p.Name).Should()
				.BeEquivalentTo(["ts", "action", "result", "path"]);

			_ = root.GetProperty("action").GetString().Should().StartWith("registration.");
			_ = root.GetProperty("result").GetInt32().Should().BeOneOf(0, 1);
			_ = root.GetProperty("ts").GetDateTimeOffset().Should().Be(FixedNow);

			string path = root.GetProperty("path").GetString()!;
			// Redaction proof: no full filesystem path, no URL, no user profile.
			_ = path.Should().NotContain(":\\");
			_ = path.Should().NotContain("://");
			_ = path.Should().NotContain("C:");
			_ = path.Should().NotContain("c:");
		}

		// The whole log must not reconstruct where the user keeps anything: the fake
		// machine's LocalAppData prefix (containing the user name "dev") appears NOWHERE.
		string wholeLog = string.Join('\n', lines);
		_ = wholeLog.Should().NotContain(@"C:\Users");
		_ = wholeLog.Should().NotContain(@"\dev\", "the fake user's profile folder name must never reach the log");
		// Nor does the exe path (it could live under a user profile in real life).
		_ = wholeLog.Should().NotContain("BroChoos.exe");
	}

	[Fact]
	public void Register_PerTargetVerificationReportsRegistered()
	{
		Host host = new();

		CapabilityRegistrationResult result = host.Service.RegisterAll();

		_ = result.Succeeded.Should().BeTrue();
		_ = result.Outcomes.Should().HaveCount(3);
		_ = result.Outcomes.Should().OnlyContain(o => o.Registered && o.Error == null);
		// D-299 base name recognizable, F5 uniqueness suffix appended.
		_ = Path.GetFileName(result.BackupFilePath).Should().MatchRegex(@"^registration-20260731T102030000Z-[0-9a-f]{4}\.json$");
		_ = result.RemovedKeyPaths.Should().BeEmpty();
	}

	[Fact]
	public void Register_WithoutBackupSink_RefusesAndDoesNotTouchTheRegistry()
	{
		Host host = new(artifactsAvailable: false);

		CapabilityRegistrationResult result = host.Service.RegisterAll();

		_ = result.Succeeded.Should().BeFalse();
		_ = host.Store.RecordedWritePaths().Should().BeEmpty();
		_ = host.Ops.Should().NotContain(op => op.StartsWith("backup:", StringComparison.Ordinal));
	}

	[Fact]
	public void Register_UnusableExePath_RefusesAndDoesNotTouchTheRegistry()
	{
		// A UNC-running exe can never become a local shell command; the PathSafety
		// gate refuses it before any write (fail-closed).
		Host host = new(processPath: @"\\server\share\BroChoos.exe");

		CapabilityRegistrationResult result = host.Service.RegisterAll();

		_ = result.Succeeded.Should().BeFalse();
		_ = host.Store.RecordedWritePaths().Should().BeEmpty();
	}

	[Fact]
	public void Register_NonStringForeignContentInSnapshot_DoesNotThrow()
	{
		Host host = new();
		host.Store.SeedRawValue(@"Software\Classes\http", "Flags", 42);

		CapabilityRegistrationResult result = host.Service.RegisterAll();

		_ = result.Succeeded.Should().BeTrue();
		string backupJson = host.Artifacts.Backups.Values.First();
		_ = backupJson.Should().Contain("\"DWord\"");
		_ = backupJson.Should().Contain("42");
	}

	// A locked-down hive (writes denied mid-flight): the run must FAIL HONESTLY,
	// keep the backup, record the failed action in the log, and verify-1 — never
	// pretend a partial shape is a registration, never crash.
	[Fact]
	public void Register_WriteDeniedMidFlight_FailsHonestly_AfterTheBackup()
	{
		Host host = new();
		host.Store.ThrowOnSetValueNumber = 3;

		CapabilityRegistrationResult result = host.Service.RegisterAll();

		_ = result.Succeeded.Should().BeFalse();
		_ = result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
		_ = result.BackupFilePath.Should().NotBeNull("D-299: the backup precedes ANY write, so a mid-flight failure is always covered");
		_ = result.Outcomes.Should().OnlyContain(o => !o.Registered);

		// Ordering: backup seen before any write; the 3rd write is the failed one.
		List<string> ops = host.Ops;
		int backupIndex = ops.FindIndex(op => op.StartsWith("backup:", StringComparison.Ordinal));
		int firstWriteIndex = ops.FindIndex(op => op.StartsWith("reg-", StringComparison.Ordinal));
		_ = firstWriteIndex.Should().BeGreaterThan(backupIndex);

		// Audit trail: backup 0, two writes 0, one write 1, verify 1.
		IReadOnlyList<string> lines = host.Artifacts.LogLines;
		_ = lines.Should().Contain(l => l.Contains("\"action\":\"registration.write\"") && l.Contains("\"result\":1"));
		_ = lines.Should().Contain(l => l.Contains("\"action\":\"registration.verify\"") && l.Contains("\"result\":1"));
	}

	// --- Unregister ---------------------------------------------------------

	[Fact]
	public void Unregister_RemovesOnlyBroChoosOwnedPieces_AndPreservesForeignContent()
	{
		Host host = new();
		_ = host.Service.RegisterAll();
		// .url gets a foreign default; http gets a foreign value; a foreign app lives
		// in RegisteredApplications. All seeded AFTER the register so both coexist.
		host.Store.SeedValue(@"Software\Classes\.url", "", "InternetShortcut");
		host.Store.SeedValue(@"Software\Classes\http", "ForeignMarker", "keep-me");
		host.Store.SeedValue(@"Software\RegisteredApplications", "OtherApp", @"Software\Other\Capabilities");
		// The F1 regression class: foreign content parked INSIDE the <scheme>\shell
		// subtree — a legacy per-user verb (with its own value and command), a
		// custom default value on shell itself, and a foreign shell\open\command2
		// key sitting next to our verb. Unregister must take out ONLY the
		// BroChoos-owned verb and default value, never the foreign neighbors —
		// a shell-subtree delete (the pre-fix behavior) would destroy them all.
		host.Store.SeedValue(@"Software\Classes\https\shell", "", "legacy-shell-default");
		host.Store.SeedValue(@"Software\Classes\https\shell\legacy-verb", "CustomFlag", "yes");
		host.Store.SeedValue(@"Software\Classes\https\shell\legacy-verb\command", "", @"C:\Legacy\app.exe");
		host.Store.SeedValue(@"Software\Classes\https\shell\open\command2", "", @"C:\Other\app2.exe");

		CapabilityRegistrationResult result = host.Service.UnregisterAll();

		_ = result.Succeeded.Should().BeTrue();

		FakeCapabilityStore store = host.Store;
		// BroChoos-owned trees are gone entirely.
		_ = store.KeyExists(@"Software\Classes\BroChoosURL").Should().BeFalse();
		_ = store.KeyExists(@"Software\Classes\BroChoosURL.File").Should().BeFalse();
		_ = store.KeyExists(@"Software\Clients\StartMenuInternet\BroChoos").Should().BeFalse();
		_ = store.GetValue(@"Software\RegisteredApplications", "BroChoos").Should().BeNull();
		// Posted pieces on shared scheme keys are gone...
		_ = store.GetValue(@"Software\Classes\http", "URL Protocol").Should().BeNull();
		_ = store.GetValue(@"Software\Classes\.url", "URL Protocol").Should().BeNull();
		_ = store.GetValue(@"Software\Classes\https", "URL Protocol").Should().BeNull();
		// ...and shell trees holding ONLY BroChoos pieces are pruned root-up...
		_ = store.KeyExists(@"Software\Classes\http\shell").Should().BeFalse();
		_ = store.KeyExists(@"Software\Classes\.url\shell").Should().BeFalse(".url\\shell held only BroChoos pieces, so the empty-prune walks command -> open -> shell and takes it all");
		// ...while FOREIGN content on the same keys survives untouched.
		_ = store.GetValue(@"Software\Classes\http", "ForeignMarker").Should().Be("keep-me");
		_ = store.KeyExists(@"Software\Classes\http").Should().BeTrue();
		_ = store.GetValue(@"Software\Classes\.url", "").Should().Be("InternetShortcut");
		_ = store.KeyExists(@"Software\Classes\.url").Should().BeTrue("the foreign default value keeps the shared key alive");
		_ = store.GetValue(@"Software\RegisteredApplications", "OtherApp").Should().Be(@"Software\Other\Capabilities");
		// https\shell mixes BroChoos and foreign pieces: the BroChoos-owned verb
		// is pruned (default value deleted, then the emptied command key itself)...
		_ = store.KeyExists(@"Software\Classes\https\shell\open\command").Should().BeFalse("the BroChoos-owned verb is pruned even with foreign siblings present");
		// ...but EVERY foreign neighbor survives verbatim, and the pruning stops
		// at the first key that is no longer empty (open still holds command2).
		_ = store.GetValue(@"Software\Classes\https\shell", "").Should().Be("legacy-shell-default");
		_ = store.KeyExists(@"Software\Classes\https\shell\legacy-verb\command").Should().BeTrue("a foreign verb under shell must never be deleted");
		_ = store.GetValue(@"Software\Classes\https\shell\legacy-verb", "CustomFlag").Should().Be("yes");
		_ = store.GetValue(@"Software\Classes\https\shell\legacy-verb\command", "").Should().Be(@"C:\Legacy\app.exe");
		_ = store.GetValue(@"Software\Classes\https\shell\open\command2", "").Should().Be(@"C:\Other\app2.exe");
		_ = store.KeyExists(@"Software\Classes\https\shell\open").Should().BeTrue("open still owns the foreign command2 key");
		_ = store.KeyExists(@"Software\Classes\https\shell").Should().BeTrue("shell still owns the foreign verb and its default value");
		_ = store.KeyExists(@"Software\Classes\https").Should().BeTrue("foreign content under shell keeps the shared scheme key alive");

		// Reported removals name only BroChoos-owned items.
		_ = result.RemovedKeyPaths.Should().Contain(p => p.Contains("BroChoosURL", StringComparison.Ordinal));
		_ = result.RemovedKeyPaths.Should().NotContain(p => p.Contains("ForeignMarker", StringComparison.Ordinal));
	}

	[Fact]
	public void Unregister_BackupIsWrittenBeforeAnyDelete()
	{
		Host host = new();
		_ = host.Service.RegisterAll();
		host.Ops.Clear();

		_ = host.Service.UnregisterAll();

		int backupIndex = host.Ops.FindIndex(op => op.StartsWith("backup:", StringComparison.Ordinal));
		int firstDeleteIndex = host.Ops.FindIndex(op => op.StartsWith("reg-", StringComparison.Ordinal));

		_ = backupIndex.Should().BeGreaterOrEqualTo(0);
		_ = firstDeleteIndex.Should().BeGreaterThan(backupIndex, "D-299: pre-removal state on disk first");
	}

	[Fact]
	public void Unregister_OnACleanHive_IsAnHonestNoOp()
	{
		Host host = new();

		CapabilityRegistrationResult result = host.Service.UnregisterAll();

		_ = result.Succeeded.Should().BeTrue();
		_ = result.RemovedKeyPaths.Should().BeEmpty();
		_ = result.BackupFilePath.Should().NotBeNull();
		// A backup was still taken (proof the nothing-to-remove state existed).
		_ = host.Artifacts.Backups.Should().ContainSingle();
	}

	[Fact]
	public void FullCycle_RegisterThenUnregister_LeavesNoBroChoosKeysBehind()
	{
		Host host = new();

		_ = host.Service.RegisterAll();
		CapabilityRegistrationResult unreg = host.Service.UnregisterAll();

		_ = unreg.Succeeded.Should().BeTrue();
		string state = host.Store.DumpState();
		// Only the generic parent containers (Software, Software\Classes, ...)
		// may remain; nothing BroChoos-owned, nothing scheme-key-related.
		_ = state.Should().NotContain("BroChoos");
		_ = state.Should().NotContain(@"Classes\http");
		_ = state.Should().NotContain(@"Classes\.url");
		_ = state.Should().NotContain(@"Classes\https");
	}

	// --- Host + fakes --------------------------------------------------------

	private sealed class Host
	{
		public Host(bool artifactsAvailable = true, string? processPath = @"C:\Dev\BroChoos\BroChoos.exe")
		{
			Ops = [];
			Store = new FakeCapabilityStore(Ops);
			Artifacts = new FakeRegistrationArtifacts(Ops, artifactsAvailable);
			FakeEnvironmentProbe environment = new() { ProcessPath = processPath };
			Service = new WindowsCapabilityRegistrationService(
				NullLogger<WindowsCapabilityRegistrationService>.Instance,
				environment,
				Store,
				Artifacts,
				new FixedTimeProvider(FixedNow));
		}

		public List<string> Ops { get; }

		public FakeCapabilityStore Store { get; }

		public FakeRegistrationArtifacts Artifacts { get; }

		public WindowsCapabilityRegistrationService Service { get; }
	}

	private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private readonly DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}
	}

	private sealed class FakeEnvironmentProbe : IEnvironmentProbe
	{
		public string? ProcessPath { get; set; }

		public string? CurrentProcessPath => ProcessPath;

		public string? GetEnvironmentVariable(string name)
		{
			return name == "LocalAppData" ? @"C:\Users\dev\AppData\Local" : null;
		}

		public bool FileExists(string path)
		{
			return true;
		}
	}

	private sealed class FakeRegistrationArtifacts(List<string> ops, bool available) : IRegistrationArtifacts
	{
		public Dictionary<string, string> Backups { get; } = new(StringComparer.OrdinalIgnoreCase);

		public List<string> LogLines { get; } = [];

		public bool IsAvailable => available;

		public string BackupsDirectoryPath => @"C:\Users\dev\AppData\Local\BroChoos\backups";

		public string SystemChangesLogPath => @"C:\Users\dev\AppData\Local\BroChoos\logs\system_changes.log";

		public string WriteBackupFile(string fileName, string json)
		{
			if (!available)
			{
				throw new IOException("no sink");
			}

			ops.Add("backup:" + fileName);
			Backups[fileName] = json;
			return Path.Combine(BackupsDirectoryPath, fileName);
		}

		public void AppendLogLines(IReadOnlyList<string> lines)
		{
			ops.Add("log:append");
			LogLines.AddRange(lines);
		}
	}

	// In-memory HKCU-shaped store. Deliberately reproduces the real seam's
	// semantics: case-insensitive key names, "" as the default value name, chain
	// creation on SetValue, conditional empty-key removal, and — critically — the
	// SAME UserChoice write guard and delete allowlist, so a service bug would
	// throw here too.
	private sealed class FakeCapabilityStore(List<string> ops) : IRegistryCapabilityStore
	{
		private readonly Dictionary<string, FakeKey> _keys = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, string> _rawKinds = new(StringComparer.OrdinalIgnoreCase);

		private List<string> Ops { get; } = ops;

		public int? ThrowOnSetValueNumber { get; set; }

		private int _setValueCount;

		private sealed class FakeKey
		{
			public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

			public HashSet<string> SubKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
		}

		public IReadOnlyList<string> RecordedWritePaths()
		{
			return [.. Ops.Where(op => op.StartsWith("reg-set:", StringComparison.Ordinal)).Select(op => op["reg-set:".Length..])];
		}

		public void SeedValue(string path, string name, string data)
		{
			SetValue(path, name.Length == 0 ? null : name, data, record: false);
		}

		// A foreign non-string value (REG_DWORD etc.) living next to ours. The kind
		// override is kept per path+value so the snapshot reads it back as DWord.
		public void SeedRawValue(string path, string name, object data)
		{
			SetValue(path, name, Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture)!, record: false);
			_rawKinds[path + "\0" + name] = data is int ? "DWord" : "Unknown";
		}

		public string? GetValue(string path, string name)
		{
			return _keys.TryGetValue(path, out FakeKey? key) && key.Values.TryGetValue(name, out string? data) ? data : null;
		}

		public bool KeyExists(string path)
		{
			return _keys.ContainsKey(path);
		}

		// Deterministic full-state dump for the idempotency and clean-cycle proofs.
		public string DumpState()
		{
			return _keys.Count == 0
				? "(empty)"
				: string.Join("|", _keys
				.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
				.Select(kv => kv.Key + "#" + string.Join(",", kv.Value.Values
					.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
					.Select(v => v.Key + "=" + v.Value))
					+ "%" + string.Join(",", kv.Value.SubKeys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))));
		}

		// IRegistryCapabilityStore -----------------------------------------------

		public RegistryKeyState ReadKey(string path)
		{
			if (!_keys.TryGetValue(path, out FakeKey? key))
			{
				return RegistryKeyState.Missing;
			}

			List<RegistryValueState> values = [.. key.Values.Select(kv => new RegistryValueState(kv.Key, _rawKinds.GetValueOrDefault(path + "\0" + kv.Key, "String"), kv.Value))];
			return new RegistryKeyState(true, false, values, [.. key.SubKeys]);
		}

		public void SetValue(string path, string? name, string data)
		{
			SetValue(path, name, data, record: true);
		}

		private void SetValue(string path, string? name, string data, bool record)
		{
			UserChoiceWriteGuard.ThrowIfUserChoicePath(path);

			if (record)
			{
				_setValueCount++;
				if (ThrowOnSetValueNumber == _setValueCount)
				{
					Ops.Add("reg-set-fail:" + path);
					throw new UnauthorizedAccessException("synthetic: hive denies writes");
				}

				Ops.Add("reg-set:" + path);
			}

			string[] segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
			string current = string.Empty;
			FakeKey? parent = null;

			foreach (string segment in segments)
			{
				current = current.Length == 0 ? segment : current + "\\" + segment;
				if (!_keys.TryGetValue(current, out FakeKey? key))
				{
					key = new FakeKey();
					_keys[current] = key;
					_ = (parent?.SubKeys.Add(segment));
				}

				parent = key;
			}

			parent!.Values[name ?? string.Empty] = data;
		}

		public bool DeleteValue(string path, string name)
		{
			UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
			UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
			Ops.Add("reg-delvalue:" + path);
			return _keys.TryGetValue(path, out FakeKey? key) && key.Values.Remove(name);
		}

		public bool DeleteSubKeyTree(string path)
		{
			UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
			UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
			Ops.Add("reg-deltree:" + path);

			if (!_keys.ContainsKey(path))
			{
				return false;
			}

			RemoveTree(path);
			return true;
		}

		public bool DeleteSubKeyIfEmpty(string path)
		{
			UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
			UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
			Ops.Add("reg-delempty:" + path);

			if (!_keys.TryGetValue(path, out FakeKey? key))
			{
				return false;
			}

			if (key.Values.Count != 0 || key.SubKeys.Count != 0)
			{
				return false;
			}

			RemoveTree(path);
			return true;
		}

		private void RemoveTree(string path)
		{
			string prefix = path + "\\";
			foreach (string candidate in _keys.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
			{
				_ = _keys.Remove(candidate);
			}

			// Unhook from the parent's subkey list, mirroring a real tree delete.
			int cut = path.LastIndexOf('\\');
			if (cut > 0 && _keys.TryGetValue(path[..cut], out FakeKey? parent))
			{
				_ = parent.SubKeys.Remove(path[(cut + 1)..]);
			}
		}
	}
}
