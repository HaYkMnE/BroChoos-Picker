using System.Text;
using BroChoos.Core.Storage;
using BroChoos.Platform.Windows.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Storage;

public sealed class DirectCopyRemediationTests
{
	[Theory]
	[InlineData(BackupRaceMutation.Replace)]
	[InlineData(BackupRaceMutation.Truncate)]
	[InlineData(BackupRaceMutation.Mutate)]
	public void Promote_ValidationToStagingRace_StillPublishesTheValidatedCapturedBytes(
		BackupRaceMutation mutation)
	{
		const string destinationPath = "app.json";
		const string backupsDirectory = "backups";
		const string validatedBackupBody = /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":7,\"handEditedByTheUser\":\"validated\"}";
		const string corruptBody = "not json";
		string backupName = $"app.json.20260101120000000.{Guid.NewGuid():N}.bak";

		RaceDisk disk = new(mutation);
		disk.AddFile(destinationPath, corruptBody);
		disk.AddBackup(backupName, validatedBackupBody);

		ConfigRepairOutcome outcome = Build(disk).Execute(destinationPath, backupsDirectory, new AppDocument());

		_ = outcome.BackupPromoted.Should().BeTrue();
		_ = disk.ReadText(destinationPath).Should().Be(validatedBackupBody,
			"the destination must be published from the immutable bytes captured/validated before the race, not from the backup path after it changed");
		_ = disk.CopyCalls.Should().Be(0,
			"promotion must not publish by path-based direct copy after validation");
		_ = disk.ReadAllBytesCalls.Should().Be(1,
			"backup bytes must be captured exactly once and reused for validation and commit");

		string quarantined = disk.FindSingleQuarantinedPath();
		_ = disk.ReadText(quarantined).Should().Be(corruptBody,
			"the pre-repair live bytes must remain recoverable");
	}

	[Fact]
	public void Promote_DeleteRace_FailsClosedWithoutPublishingSubstitutedBytes_AndPreservesPriorLiveGeneration()
	{
		const string destinationPath = "app.json";
		const string backupsDirectory = "backups";
		const string validatedBackupBody = /*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":7,\"handEditedByTheUser\":\"validated\"}";
		const string corruptBody = "not json";
		string backupName = $"app.json.20260101120000000.{Guid.NewGuid():N}.bak";

		RaceDisk disk = new(BackupRaceMutation.Delete);
		disk.AddFile(destinationPath, corruptBody);
		disk.AddBackup(backupName, validatedBackupBody);

		ConfigRepairOutcome outcome = Build(disk).Execute(destinationPath, backupsDirectory, new AppDocument());

		_ = outcome.BackupPromoted.Should().BeFalse();
		_ = outcome.DestinationUsable.Should().BeFalse();
		_ = disk.Exists(destinationPath).Should().BeFalse(
			"failed promotion after quarantine must not republish substituted bytes");

		string quarantined = disk.FindSingleQuarantinedPath();
		_ = disk.ReadText(quarantined).Should().Be(corruptBody,
			"the pre-repair live bytes must remain recoverable even when promotion fails");
	}

	private static ConfigRepairExecutor Build(RaceDisk disk)
	{
		return new(
			new ConfigFileObserver(disk),
			disk,
			disk,
			disk,
			TimeProvider.System,
			new InMemoryConfigRepairLog());
	}

	public enum BackupRaceMutation
	{
		None,
		Replace,
		Truncate,
		Mutate,
		Delete,
	}

	private sealed class RaceDisk(BackupRaceMutation raceMutation) : IStorageReadFileSystem, IStorageRepairFileSystem, IStorageFileSystem, IConfigDocumentWriter
	{
		private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
		private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "backups" };

		public int CopyCalls { get; private set; }
		public int ReadAllBytesCalls { get; private set; }

		public void AddFile(string path, string text)
		{
			_files[path] = Encoding.UTF8.GetBytes(text);
		}

		public void AddBackup(string name, string text)
		{
			_files[Path.Combine("backups", name)] = Encoding.UTF8.GetBytes(text);
		}

		public string ReadText(string path)
		{
			return Encoding.UTF8.GetString(_files[path]);
		}

		public bool Exists(string path)
		{
			return _files.ContainsKey(path);
		}

		public string FindSingleQuarantinedPath()
		{
			return _files.Keys.Single(path => path.StartsWith(Path.Combine("backups", "corrupted") + Path.DirectorySeparatorChar, StringComparison.Ordinal));
		}

		public bool FileExists(string path)
		{
			return _files.ContainsKey(path);
		}

		public bool DirectoryExists(string path)
		{
			return _directories.Contains(path);
		}

		public Stream OpenRead(string path)
		{
			return new MemoryStream(_files[path], writable: false);
		}

		public IEnumerable<string> EnumerateFiles(string directory)
		{
			return [.. _files.Keys.Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.Ordinal))];
		}

		public DateTime GetLastWriteTimeUtc(string path)
		{
			return _files.ContainsKey(path)
				? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
				: new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		}

		public long GetLength(string path)
		{
			return _files[path].Length;
		}

		public void CreateDirectory(string path)
		{
			_ = _directories.Add(path);
		}

		public void Move(string sourcePath, string destinationPath)
		{
			if (_files.ContainsKey(destinationPath))
			{
				throw new IOException("the destination already exists");
			}

			_files[destinationPath] = _files[sourcePath];
			_ = _files.Remove(sourcePath);
		}

		public void Copy(string sourcePath, string destinationPath, bool overwrite)
		{
			CopyCalls++;
			ApplyRace(sourcePath);
			_files[destinationPath] = _files[sourcePath];
			_ = overwrite;
		}

		public byte[] ReadAllBytes(string sourcePath)
		{
			ReadAllBytesCalls++;
			byte[] captured = [.. _files[sourcePath]];
			ApplyRace(sourcePath);
			return captured;
		}

		public void WriteBytesAtomically(
			string destinationPath,
			byte[] bytes,
			ExpectedDocumentGeneration expectedGeneration,
			TimeSpan writeLockTimeout)
		{
			if (expectedGeneration.ExpectsDocument)
			{
				if (!_files.TryGetValue(destinationPath, out byte[]? current))
				{
					throw new IOException("destination missing for CAS");
				}

				long currentGeneration = ReadGeneration(current);
				if (currentGeneration != expectedGeneration.Value)
				{
					throw new IOException("generation conflict");
				}
			}
			else if (_files.ContainsKey(destinationPath))
			{
				throw new IOException("destination unexpectedly exists for absent-token CAS");
			}

			_files[destinationPath] = [.. bytes];
		}

		public void Write(string destinationPath, ConfigDocument document, TimeSpan writeLockTimeout)
		{
			_files[destinationPath] = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":1}");
		}

		private static long ReadGeneration(byte[] bytes)
		{
			try
			{
				using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(bytes);
				if (json.RootElement.TryGetProperty("generation", out System.Text.Json.JsonElement generation)
					&& generation.ValueKind == System.Text.Json.JsonValueKind.Number
					&& generation.TryGetInt64(out long value))
				{
					return value;
				}
			}
			catch (System.Text.Json.JsonException)
			{
			}

			return 0;
		}

		private void ApplyRace(string sourcePath)
		{
			switch (raceMutation)
			{
				case BackupRaceMutation.Replace:
					_files[sourcePath] = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":999,\"fromRace\":\"replace\"}");
					break;
				case BackupRaceMutation.Truncate:
					_files[sourcePath] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1");
					break;
				case BackupRaceMutation.Mutate:
					{
						byte[] mutated = [.. _files[sourcePath]];
						mutated[^2] = (byte)'X';
						_files[sourcePath] = mutated;
						break;
					}
				case BackupRaceMutation.Delete:
					_ = _files.Remove(sourcePath);
					throw new IOException("backup removed after validation");
				case BackupRaceMutation.None:
				default:
					break;
			}
		}
	}
}
