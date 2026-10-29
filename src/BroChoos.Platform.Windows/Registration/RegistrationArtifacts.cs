using BroChoos.Platform.Windows.Browsers;

namespace BroChoos.Platform.Windows.Registration;

// The ONLY file sink BroChoos writes in 0A-4 (D-291/D-299): JSON registration
// backups and the locked-schema system_changes.log, both under
// %LocalAppData%\BroChoos. No config schema is introduced; the log file is the
// D-291-mandated audit trail and is explicitly not a "new schema file".
public interface IRegistrationArtifacts
{
	// False when %LocalAppData% is unset and therefore no backup/log location
	// can be resolved. Registration refuses to run without a backup sink.
	bool IsAvailable { get; }

	string BackupsDirectoryPath { get; }

	string SystemChangesLogPath { get; }

	// fileName is a bare, service-generated file name (timestamp plus a short
	// uniqueness suffix, never user input). Returns the FULL path for the
	// console success message; the full path never reaches system_changes.log
	// (D-291 redaction).
	string WriteBackupFile(string fileName, string json);

	// Appends JSONL lines in one write. Throws on I/O failure; the caller
	// decides how to degrade (a log failure must not pretend the registry work
	// did not happen, and must not resurrect it as a crash either).
	void AppendLogLines(IReadOnlyList<string> lines);
}

public sealed class LocalAppDataRegistrationArtifacts : IRegistrationArtifacts
{
	private readonly string _root;

	public LocalAppDataRegistrationArtifacts(IEnvironmentProbe environment)
	{
		string? localAppData = environment.GetEnvironmentVariable("LocalAppData");
		_root = string.IsNullOrWhiteSpace(localAppData)
			? string.Empty
			: Path.Combine(localAppData, "BroChoos");
	}

	public bool IsAvailable => _root.Length != 0;

	public string BackupsDirectoryPath => Path.Combine(_root, "backups");

	public string SystemChangesLogPath => Path.Combine(_root, "logs", "system_changes.log");

	public string WriteBackupFile(string fileName, string json)
	{
		if (!IsAvailable)
		{
			throw new IOException("LocalAppData is unavailable; no backup location can be resolved.");
		}

		// Defense in depth: fileName is service-generated, but the artifacts
		// boundary never accepts a rooted/traversal name anyway.
		if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
		{
			throw new ArgumentException("Backup file name must be a bare file name.", nameof(fileName));
		}

		_ = Directory.CreateDirectory(BackupsDirectoryPath);
		string fullPath = Path.Combine(BackupsDirectoryPath, fileName);
		File.WriteAllText(fullPath, json);
		return fullPath;
	}

	public void AppendLogLines(IReadOnlyList<string> lines)
	{
		if (!IsAvailable)
		{
			throw new IOException("LocalAppData is unavailable; no log location can be resolved.");
		}

		_ = Directory.CreateDirectory(Path.GetDirectoryName(SystemChangesLogPath)!);
		File.AppendAllLines(SystemChangesLogPath, lines);
	}
}
