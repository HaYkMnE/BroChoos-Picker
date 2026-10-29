using System.Diagnostics;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Registration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Registration;

// 0A-4, D-187 hard invariant: ANY write or delete under a UserChoice key must
// throw, at every layer. The real store is exercised directly (the guard runs as
// each member's FIRST statement, before any registry handle is opened, so these
// tests touch no real registry state), plus the full service run is checked
// against a recording fake to prove no operation ever targets UserChoice.
public sealed class UserChoiceWriteGuardTests
{
	private const string UrlAssociationsUserChoice = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice";
	private const string FileExtsUserChoice = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.url\UserChoice";

	[Theory]
	[InlineData(UrlAssociationsUserChoice)]
	[InlineData(FileExtsUserChoice)]
	[InlineData(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\userchoice")]
	[InlineData("UserChoice")]
	public void Guard_UserChoicePathInAnyPosition_Throws(string path)
	{
		Action act = () => UserChoiceWriteGuard.ThrowIfUserChoicePath(path);

		_ = act.Should().Throw<InvalidOperationException>().WithMessage("*D-187*");

		// The delete allowlist refuses these too (a UserChoice path lies outside
		// every product-owned delete root).
		Action deleteAct = () => UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);

		_ = deleteAct.Should().Throw<InvalidOperationException>();
	}

	[Theory]
	[InlineData(@"Software\Classes\http")]
	[InlineData(@"Software\Classes\http\shell\open\command")]
	[InlineData(@"Software\Clients\StartMenuInternet\BroChoos")]
	[InlineData(@"Software\RegisteredApplications")]
	public void Guard_ProductOwnedPaths_Pass(string path)
	{
		Action act = () => UserChoiceWriteGuard.ThrowIfUserChoicePath(path);

		_ = act.Should().NotThrow();

		// These lie at/under the allowlisted delete roots, so the delete gate
		// passes them as well.
		Action deleteAct = () => UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);

		_ = deleteAct.Should().NotThrow();
	}

	// Every WRITE member of the REAL store refuses a UserChoice path before any
	// registry handle could be opened — this is the layer a future refactor
	// cannot route around.
	[Fact]
	public void RealStore_AllWriteMembers_ThrowOnUserChoicePaths()
	{
		HkcuRegistryCapabilityStore store = new();

		Action set = () => store.SetValue(UrlAssociationsUserChoice, "ProgId", "BroChoosURL");
		Action delValue = () => store.DeleteValue(UrlAssociationsUserChoice, "ProgId");
		Action delTree = () => store.DeleteSubKeyTree(UrlAssociationsUserChoice);
		Action delEmpty = () => store.DeleteSubKeyIfEmpty(UrlAssociationsUserChoice);

		_ = set.Should().Throw<InvalidOperationException>();
		_ = delValue.Should().Throw<InvalidOperationException>();
		_ = delTree.Should().Throw<InvalidOperationException>();
		_ = delEmpty.Should().Throw<InvalidOperationException>();

		// The escalation the segment-only guard missed: a tree delete aimed at an
		// ANCESTOR of a UserChoice key (no "UserChoice" segment anywhere in the
		// path) would wipe the user's own pick. The delete allowlist refuses all
		// three delete verbs before any hive access, so this touches nothing real.
		const string userChoiceAncestor = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http";
		Action delAncestorTree = () => store.DeleteSubKeyTree(userChoiceAncestor);
		Action delAncestorValue = () => store.DeleteValue(userChoiceAncestor, "ProgId");
		Action delAncestorEmpty = () => store.DeleteSubKeyIfEmpty(userChoiceAncestor);

		_ = delAncestorTree.Should().Throw<InvalidOperationException>().WithMessage("*allowlisted*");
		_ = delAncestorValue.Should().Throw<InvalidOperationException>().WithMessage("*allowlisted*");
		_ = delAncestorEmpty.Should().Throw<InvalidOperationException>().WithMessage("*allowlisted*");

		// The allowlisted direction is asserted on the static guard itself: this
		// test machine's real hive may hold a genuine BroChoos registration, so
		// the real store's delete members are deliberately NOT called on
		// allowlisted paths here (they would mutate real product state).
		Action allowlistedDelete = () => UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(@"Software\Classes\http\shell\open\command");

		_ = allowlistedDelete.Should().NotThrow();
	}

	// Reading the user's own pick is legitimate (D-268 step 2): ReadKey on a
	// UserChoice path must NOT throw. A never-existing scheme keeps this off any
	// real key; the point is only that no guard fires on the read path.
	[Fact]
	public void RealStore_ReadKey_OnAUserChoicePath_DoesNotThrow()
	{
		HkcuRegistryCapabilityStore store = new();

		RegistryKeyState state = store.ReadKey(
			@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\brochoos-no-such-scheme\UserChoice");

		_ = state.Exists.Should().BeFalse();
	}

	// End-to-end: a full register+unregister cycle against a recording fake must
	// contain ZERO operations under any UserChoice path. Combined with the guards
	// above, this is the "any attempt throws" invariant pinned at service level.
	[Fact]
	public void FullCycle_NoOperationEverTargetsUserChoice()
	{
		UserChoiceWriteGuardRecordingStore store = new();
		RecordingArtifacts artifacts = new();
		WindowsCapabilityRegistrationService service = new(
			NullLogger<WindowsCapabilityRegistrationService>.Instance,
			new Probe(@"C:\Dev\BroChoos\BroChoos.exe"),
			store,
			artifacts,
			TimeProvider.System);

		_ = service.RegisterAll();
		_ = service.UnregisterAll();

		_ = store.Ops.Should().NotBeEmpty();
		// One ordinal-ignore-case clause suffices ("UserChoice" covers every casing).
		_ = store.Ops.Should().NotContain(op => op.Contains("UserChoice", StringComparison.OrdinalIgnoreCase));
	}

	private sealed class Probe(string? processPath) : IEnvironmentProbe
	{
		public string? CurrentProcessPath { get; } = processPath;

		public string? GetEnvironmentVariable(string name)
		{
			return null;
		}

		public bool FileExists(string path)
		{
			return true;
		}
	}

	private sealed class UserChoiceWriteGuardRecordingStore : IRegistryCapabilityStore
	{
		private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

		public List<string> Ops { get; } = [];

		public RegistryKeyState ReadKey(string path)
		{
			return _keys.TryGetValue(path, out Dictionary<string, string>? values)
				? new RegistryKeyState(true, false, [.. values.Select(kv => new RegistryValueState(kv.Key, "String", kv.Value))], [])
				: RegistryKeyState.Missing;
		}

		public void SetValue(string path, string? name, string data)
		{
			UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
			Ops.Add(path);
			Dictionary<string, string> values = _keys.TryGetValue(path, out Dictionary<string, string>? existing)
				? existing
				: _keys[path] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			values[name ?? string.Empty] = data;
		}

		public bool DeleteValue(string path, string name)
		{
			UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
			UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
			Ops.Add(path);
			return _keys.TryGetValue(path, out Dictionary<string, string>? values) && values.Remove(name);
		}

		public bool DeleteSubKeyTree(string path)
		{
			UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
			UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
			Ops.Add(path);
			string prefix = path + "\\";
			List<string> doomed = [.. _keys.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
			foreach (string key in doomed)
			{
				_ = _keys.Remove(key);
			}

			return doomed.Count > 0;
		}

		public bool DeleteSubKeyIfEmpty(string path)
		{
			UserChoiceWriteGuard.ThrowIfUserChoicePath(path);
			UserChoiceWriteGuard.ThrowIfDeletePathIsNotProductOwned(path);
			Ops.Add(path);
			return _keys.TryGetValue(path, out Dictionary<string, string>? values) && values.Count == 0 && _keys.Remove(path);
		}
	}

	private sealed class RecordingArtifacts : IRegistrationArtifacts
	{
		public bool IsAvailable => true;

		public string BackupsDirectoryPath => @"X:\backups";

		public string SystemChangesLogPath => @"X:\logs\system_changes.log";

		public string WriteBackupFile(string fileName, string json)
		{
			return @"X:\backups\" + fileName;
		}

		public void AppendLogLines(IReadOnlyList<string> lines)
		{
		}
	}
}

// 0A-4: the guide opens the settings page through the single sanctioned
// UseShellExecute=true exception (spec-pinned) with a constant protocol URI.
public sealed class WindowsDefaultAppsGuideTests
{
	[Fact]
	public void TryOpen_UsesShellExecuteWithTheExactConstantProtocolUri()
	{
		CapturingStarter starter = new();
		WindowsDefaultAppsGuide guide = new(NullLogger<WindowsDefaultAppsGuide>.Instance, starter);

		bool result = guide.TryOpenDefaultAppsPage();

		_ = result.Should().BeTrue();
		_ = starter.Captured.Should().NotBeNull();
		ProcessStartInfo captured = starter.Captured!;
		_ = captured.FileName.Should().Be("ms-settings:defaultapps");
		_ = captured.UseShellExecute.Should().BeTrue("ms-settings: is a protocol only the shell can open — the one sanctioned exception, documented in the class remark");
		_ = captured.Arguments.Should().BeEmpty();
		_ = captured.ArgumentList.Should().BeEmpty();
	}

	[Fact]
	public void TryOpen_WhenTheShellReturnsNoProcess_IsFailOpenFalse()
	{
		WindowsDefaultAppsGuide guide = new(NullLogger<WindowsDefaultAppsGuide>.Instance, new CapturingStarter { Result = false });

		_ = guide.TryOpenDefaultAppsPage().Should().BeFalse();
	}

	[Fact]
	public void TryOpen_WhenTheShellThrows_IsFailOpenFalse()
	{
		WindowsDefaultAppsGuide guide = new(NullLogger<WindowsDefaultAppsGuide>.Instance, new CapturingStarter { ThrowOnStart = true });

		_ = guide.TryOpenDefaultAppsPage().Should().BeFalse();
	}

	private sealed class CapturingStarter : IProcessStarter
	{
		public ProcessStartInfo? Captured { get; private set; }

		public bool Result { get; set; } = true;

		public bool ThrowOnStart { get; set; }

		public bool Start(ProcessStartInfo startInfo)
		{
			Captured = startInfo;
			return ThrowOnStart ? throw new InvalidOperationException("shell unavailable") : Result;
		}
	}
}
