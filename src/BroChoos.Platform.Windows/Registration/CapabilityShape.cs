using BroChoos.Core.Abstractions;

namespace BroChoos.Platform.Windows.Registration;

// THE registration shape, declared once (v13 §14) and shared by the registration
// service (write/unregister), the status service (verify step 1), and the tests.
//
// Two layers, both per-user HKCU (D-273):
//   1. Scheme keys Software\Classes\{http,https,.url} carrying "URL Protocol"=""
//      and shell\open\command -> "\"<exe>\" \"%1\"" (the v13 §14 shape).
//   2. The Default Programs capability model: RegisteredApplications points at
//      a StartMenuInternet\BroChoos Capabilities key whose URLAssociations /
//      FileAssociations map each type onto BroChoos ProgIDs
//      (Software\Classes\BroChoosURL, BroChoosURL.File) with their own
//      shell\open\command. The ProgID layer is REQUIRED for honesty: Default
//      Apps would otherwise offer BroChoos with keys it cannot resolve.
//
// NOTHING under Microsoft\Windows\...\UserChoice is ever written (D-187); the
// UserChoice paths below exist for the status service's READ-ONLY snapshot only.
internal sealed record DefaultAppsTargetShape(
	DefaultAppsTarget Target,
	string Scheme,
	string ProgId,
	string FriendlyName,
	string SchemeKeyPath,
	string ProgIdKeyPath,
	string UserChoiceKeyPath,
	string AssociationMapValueName);

internal static class CapabilityShape
{
	internal const string ProgIdUrl = "BroChoosURL";
	internal const string ProgIdUrlFile = "BroChoosURL.File";
	internal const string AppClientKeyPath = @"Software\Clients\StartMenuInternet\BroChoos";
	internal const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
	internal const string RegisteredApplicationsValueName = "BroChoos";
	internal const string ApplicationDescription = "Choose which browser and profile opens each link";

	private const string ClassesRoot = @"Software\Classes";

	// Fixed deterministic order: http, https, .url.
	internal static IReadOnlyList<DefaultAppsTargetShape> Targets { get; } =
	[
		new DefaultAppsTargetShape(
			DefaultAppsTarget.Http,
			"http",
			ProgIdUrl,
			"BroChoos URL",
			ClassesRoot + @"\http",
			ClassesRoot + @"\" + ProgIdUrl,
			@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice",
			"http"),
		new DefaultAppsTargetShape(
			DefaultAppsTarget.Https,
			"https",
			ProgIdUrl,
			"BroChoos URL",
			ClassesRoot + @"\https",
			ClassesRoot + @"\" + ProgIdUrl,
			@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice",
			"https"),
		new DefaultAppsTargetShape(
			DefaultAppsTarget.UrlFile,
			".url",
			ProgIdUrlFile,
			"BroChoos URL file",
			ClassesRoot + @"\.url",
			ClassesRoot + @"\" + ProgIdUrlFile,
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.url\UserChoice",
			".url"),
	];

	// Every (path, name, data) write of a full declaration. openCommand /
	// applicationIcon are built by the caller from the running executable path
	// (already through the PathSafety gate). A name of null means the key's
	// default (unnamed) value.
	internal static IReadOnlyList<CapabilityWriteOperation> BuildWriteOperations(string openCommand, string applicationIcon)
	{
		List<CapabilityWriteOperation> operations = [];

		// Layer 1: scheme keys (v13 §14 verbatim shape).
		foreach (DefaultAppsTargetShape shape in Targets)
		{
			operations.Add(new CapabilityWriteOperation(shape.SchemeKeyPath, "URL Protocol", string.Empty));
			operations.Add(new CapabilityWriteOperation(shape.SchemeKeyPath + @"\shell\open\command", null, openCommand));
		}

		// Layer 2: ProgID entries the capability model resolves through.
		foreach (string progId in new[] { ProgIdUrl, ProgIdUrlFile })
		{
			string progIdKey = ClassesRoot + @"\" + progId;
			string friendlyName = progId == ProgIdUrl ? "BroChoos URL" : "BroChoos URL file";
			operations.Add(new CapabilityWriteOperation(progIdKey, null, friendlyName));
			operations.Add(new CapabilityWriteOperation(progIdKey, "URL Protocol", string.Empty));
			operations.Add(new CapabilityWriteOperation(progIdKey + @"\shell\open\command", null, openCommand));
		}

		// Layer 3: StartMenuInternet client + capabilities, and RegisteredApplications.
		operations.Add(new CapabilityWriteOperation(AppClientKeyPath, null, "BroChoos"));
		operations.Add(new CapabilityWriteOperation(AppClientKeyPath + @"\shell\open\command", null, openCommand));
		operations.Add(new CapabilityWriteOperation(AppClientKeyPath + @"\Capabilities", "ApplicationName", "BroChoos"));
		operations.Add(new CapabilityWriteOperation(AppClientKeyPath + @"\Capabilities", "ApplicationDescription", ApplicationDescription));
		operations.Add(new CapabilityWriteOperation(AppClientKeyPath + @"\Capabilities", "ApplicationIcon", applicationIcon));
		operations.Add(new CapabilityWriteOperation(AppClientKeyPath + @"\Capabilities\URLAssociations", "http", ProgIdUrl));
		operations.Add(new CapabilityWriteOperation(AppClientKeyPath + @"\Capabilities\URLAssociations", "https", ProgIdUrl));
		operations.Add(new CapabilityWriteOperation(AppClientKeyPath + @"\Capabilities\FileAssociations", ".url", ProgIdUrlFile));
		operations.Add(new CapabilityWriteOperation(RegisteredApplicationsPath, RegisteredApplicationsValueName, AppClientKeyPath + @"\Capabilities"));

		return operations;
	}

	// Every key a backup snapshot must hold: every key the write set can mutate,
	// PLUS all their intermediate ancestors (D-299: the pre-operation state of
	// what we are about to touch). The removal plan prunes empty parents walking
	// up command -> open -> shell -> scheme root, so the intermediate
	// <scheme>\shell and <scheme>\shell\open keys (and every other ancestor,
	// whose subkey list mutates the moment we create or prune one of our own
	// keys) must be captured BEFORE the first mutation — otherwise a foreign
	// verb or value parked under them would be unrecoverable.
	internal static IReadOnlyList<string> SnapshotPaths()
	{
		List<string> paths = [RegisteredApplicationsPath];
		foreach (CapabilityWriteOperation operation in BuildWriteOperations(string.Empty, string.Empty))
		{
			AddWithAncestors(paths, operation.Path);
		}

		return paths;
	}

	// Adds the path itself and every ancestor (shell, open, Classes, ...), each
	// exactly once, deduplicated case-insensitively.
	private static void AddWithAncestors(List<string> paths, string path)
	{
		string current = path;
		while (current.Length != 0)
		{
			if (!paths.Contains(current, StringComparer.OrdinalIgnoreCase))
			{
				paths.Add(current);
			}

			int cut = current.LastIndexOf('\\');
			current = cut > 0 ? current[..cut] : string.Empty;
		}
	}

	// D-268 step 1 for one target: ALL capability pieces matching the expected
	// declaration. A partial shape (e.g. after a hand edit) is reported honestly
	// as NOT registered — "half registered" would mislead the guide step.
	internal static bool IsTargetRegistered(IRegistryCapabilityStore store, DefaultAppsTargetShape shape, string openCommand)
	{
		string appClientCapabilities = AppClientKeyPath + @"\Capabilities";
		string associationSubKey = shape.Target == DefaultAppsTarget.UrlFile ? "FileAssociations" : "URLAssociations";

		return ReadStringValue(store, shape.SchemeKeyPath, "URL Protocol") is ""
			&& ReadStringValue(store, shape.SchemeKeyPath + @"\shell\open\command", null) == openCommand
			&& ReadStringValue(store, shape.ProgIdKeyPath, null) == shape.FriendlyName
			&& ReadStringValue(store, shape.ProgIdKeyPath, "URL Protocol") is ""
			&& ReadStringValue(store, shape.ProgIdKeyPath + @"\shell\open\command", null) == openCommand
			&& ReadStringValue(store, appClientCapabilities + @"\" + associationSubKey, shape.AssociationMapValueName) == shape.ProgId
			&& ReadStringValue(store, RegisteredApplicationsPath, RegisteredApplicationsValueName) == appClientCapabilities;
	}

	// Untrusted-string comparison: registry content is read DoNotExpand and
	// compared ordinally case-insensitive (registry value semantics are
	// case-insensitive); it is never executed, never used as a path.
	private static string? ReadStringValue(IRegistryCapabilityStore store, string path, string? name)
	{
		RegistryKeyState state = store.ReadKey(path);
		if (!state.Exists || state.ReadFailed)
		{
			return null;
		}

		string wanted = name ?? string.Empty;
		foreach (RegistryValueState value in state.Values)
		{
			if (value.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
			{
				return value.Data;
			}
		}

		return null;
	}
}

internal sealed record CapabilityWriteOperation(string Path, string? Name, string Data);
