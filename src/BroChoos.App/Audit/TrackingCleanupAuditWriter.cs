using System.Text.Json;
using System.Security.Cryptography;
using BroChoos.Core.Routing;

namespace BroChoos.App.Audit;

internal sealed record TrackingCleanupAuditEvent(
	DateTimeOffset Timestamp,
	string Host,
	bool Changed,
	string ReasonCode,
	IReadOnlyList<string> RemovedKeys,
	string? AppliedPackId,
	string? RawUrl = null)
{
	public string ToJsonLine()
	{
		return JsonSerializer.Serialize(new
		{
			ts = Timestamp.UtcDateTime,
			host = Host,
			changed = Changed,
			reason = ReasonCode,
			removedKeys = RemovedKeys,
			pack = AppliedPackId,
		});
	}
}

internal interface ITrackingCleanupAuditWriter
{
	void Write(TrackingCleanupAuditEvent cleanupEvent);
}

// The one call AvaloniaPicker makes at start-up; an interface only so the wiring and its
// once-per-process guard can be tested without a real file.
internal interface ICleanupAuditDiscarder
{
	CleanupAuditDiscardOutcome DiscardWhenDisabled(bool cleanupEnabled, out string? failureType);
}

// What DiscardWhenDisabled did with a leftover cleanup_events.jsonl.
internal enum CleanupAuditDiscardOutcome
{
	CleanupEnabled,
	NotPresent,
	Deleted,
	SkippedLink,
	SkippedUnexpectedPath,
	Failed,
}

internal sealed class LocalTrackingCleanupAuditWriter : ITrackingCleanupAuditWriter, ICleanupAuditDiscarder
{
	private static readonly TimeSpan MutexAcquireTimeout = TimeSpan.FromSeconds(5);
	private static readonly int[] RetryBackoffMs = [40, 80, 160, 320];

	private readonly string _cleanupEventsPath;

	public LocalTrackingCleanupAuditWriter()
		: this(Path.Combine(
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BroChoos", "logs"),
			TrackingCleanupAuditPolicy.CleanupEventsFileName))
	{
	}

	internal LocalTrackingCleanupAuditWriter(string cleanupEventsPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cleanupEventsPath);
		_cleanupEventsPath = cleanupEventsPath;
	}

	public void Write(TrackingCleanupAuditEvent cleanupEvent)
	{
		ArgumentNullException.ThrowIfNull(cleanupEvent);

		using FileWriteMutexScope mutexScope = FileWriteMutexScope.Acquire(_cleanupEventsPath, MutexAcquireTimeout);
		WriteWithRetry(cleanupEvent);
	}

	// PRIVACY CLEANUP. Earlier internal builds wrote the host of every session here even with cleanup
	// off. With cleanup off Write never runs, so the file is never trimmed and those hosts would stay
	// on disk for good. When cleanup is disabled this removes that ONE file, and nothing else:
	//   - only a path whose file name is exactly cleanup_events.jsonl;
	//   - never through a reparse point: if the file, its logs folder or that folder's parent (the
	//     folders BroChoos owns) is a symlink or junction, nothing is deleted;
	//   - a directory with that name is left alone.
	// Fail-open: it never throws. A failure is reported by exception TYPE only, never the message,
	// which can contain the path. With cleanup enabled the file is not touched.
	public CleanupAuditDiscardOutcome DiscardWhenDisabled(bool cleanupEnabled, out string? failureType)
	{
		failureType = null;
		if (cleanupEnabled)
		{
			return CleanupAuditDiscardOutcome.CleanupEnabled;
		}

		try
		{
			string fullPath = Path.GetFullPath(_cleanupEventsPath);
			if (!string.Equals(Path.GetFileName(fullPath), TrackingCleanupAuditPolicy.CleanupEventsFileName, StringComparison.OrdinalIgnoreCase))
			{
				return CleanupAuditDiscardOutcome.SkippedUnexpectedPath;
			}

			string? logsDirectory = Path.GetDirectoryName(fullPath);
			string? appDirectory = logsDirectory is null ? null : Path.GetDirectoryName(logsDirectory);
			foreach (string? owned in new[] { appDirectory, logsDirectory })
			{
				if (owned is null)
				{
					continue;
				}

				if (!TryGetAttributes(owned, out FileAttributes ownedAttributes))
				{
					return CleanupAuditDiscardOutcome.NotPresent;
				}

				if ((ownedAttributes & FileAttributes.ReparsePoint) != 0)
				{
					return CleanupAuditDiscardOutcome.SkippedLink;
				}
			}

			// GetAttributes reads the entry itself; a symlink reports ReparsePoint and is not followed.
			if (!TryGetAttributes(fullPath, out FileAttributes attributes))
			{
				return CleanupAuditDiscardOutcome.NotPresent;
			}

			if ((attributes & FileAttributes.ReparsePoint) != 0)
			{
				return CleanupAuditDiscardOutcome.SkippedLink;
			}

			if ((attributes & FileAttributes.Directory) != 0)
			{
				return CleanupAuditDiscardOutcome.SkippedUnexpectedPath;
			}

			File.Delete(fullPath);
			return CleanupAuditDiscardOutcome.Deleted;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			failureType = ex.GetType().Name;
			return CleanupAuditDiscardOutcome.Failed;
		}
	}

	private static bool TryGetAttributes(string path, out FileAttributes attributes)
	{
		try
		{
			attributes = File.GetAttributes(path);
			return true;
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			attributes = default;
			return false;
		}
	}

	private void WriteWithRetry(TrackingCleanupAuditEvent cleanupEvent)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				WriteOnce(cleanupEvent);
				return;
			}
			catch (IOException ex) when (IsTransientFileLock(ex) && attempt < RetryBackoffMs.Length)
			{
				Thread.Sleep(RetryBackoffMs[attempt]);
			}
		}
	}

	private void WriteOnce(TrackingCleanupAuditEvent cleanupEvent)
	{

		string? directory = Path.GetDirectoryName(_cleanupEventsPath);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			_ = Directory.CreateDirectory(directory);
		}

		TrimRetentionIfNeeded();

		File.AppendAllLines(_cleanupEventsPath, [cleanupEvent.ToJsonLine()]);
	}

	private static bool IsTransientFileLock(IOException ex)
	{
		const int SharingViolation = 32;
		const int LockViolation = 33;
		int nativeCode = ex.HResult & 0xFFFF;
		return nativeCode is SharingViolation or LockViolation;
	}

	private void TrimRetentionIfNeeded()
	{
		if (!File.Exists(_cleanupEventsPath))
		{
			return;
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		DateTimeOffset cutoff = now - TrackingCleanupAuditPolicy.RetentionWindow;
		List<string> kept = [];

		foreach (string line in File.ReadLines(_cleanupEventsPath))
		{
			if (TryGetTimestamp(line, out DateTimeOffset ts) && ts >= cutoff)
			{
				kept.Add(line);
			}
		}

		// THE CAP IS A CAP ON THE FILE, AND THIS METHOD DOES NOT PRODUCE THE FILE. WriteOnce
		// appends one line after this returns, so trimming to the full MaxEntries would land the
		// file on MaxEntries + 1 (the off-by-one this bound exists to close). Reserving the slot
		// the append is about to occupy keeps the post-write steady state at exactly MaxEntries,
		// which is what "2000-line cap" means to the reader of the file.
		int retainedLimit = TrackingCleanupAuditPolicy.MaxEntries - 1;
		if (kept.Count > retainedLimit)
		{
			int skip = kept.Count - retainedLimit;
			kept = [.. kept.Skip(skip)];
		}

		File.WriteAllLines(_cleanupEventsPath, kept);
	}

	private static bool TryGetTimestamp(string line, out DateTimeOffset timestamp)
	{
		try
		{
			using JsonDocument json = JsonDocument.Parse(line);

			// An element that is not an object has no properties at all, and asking it for one
			// throws - so the kind is tested rather than assumed. Probe covers the value kinds
			// (null, number, array, string) a hand-appended line plausibly carries.
			if (json.RootElement.ValueKind is not JsonValueKind.Object)
			{
				timestamp = default;
				return false;
			}

			if (!json.RootElement.TryGetProperty("ts", out JsonElement tsElement))
			{
				timestamp = default;
				return false;
			}

			if (tsElement.ValueKind is JsonValueKind.String &&
				tsElement.GetDateTimeOffset() is DateTimeOffset value)
			{
				timestamp = value;
				return true;
			}
		}
		catch (JsonException)
		{
			// Ignore malformed historical lines; they drop out on rewrite.
		}
		catch (Exception unreadable) when (unreadable is not OutOfMemoryException and not StackOverflowException)
		{
			// NOTHING ABOVE IS THE ONLY WAY A LINE CAN BE UNREADABLE. A `ts` that is valid JSON
			// but not RFC3339 - "n/a", "2026-13-45", "2026-02-30T00:00:00Z", "" - makes
			// GetDateTimeOffset throw FormatException, which is NOT a JsonException and used to
			// escape this method, TrimRetentionIfNeeded and WriteOnce with it: a single bad
			// historical line permanently cost EVERY later cleanup event its audit record. The
			// line is dropped here exactly like a malformed one, because the intent is the same
			// - an unreadable line drops out on rewrite, it does not abort the write.
		}

		timestamp = default;
		return false;
	}

	private sealed class FileWriteMutexScope : IDisposable
	{
		private readonly Mutex _mutex;
		private readonly bool _ownsMutex;

		private FileWriteMutexScope(Mutex mutex, bool ownsMutex)
		{
			_mutex = mutex;
			_ownsMutex = ownsMutex;
		}

		public static FileWriteMutexScope Acquire(string cleanupEventsPath, TimeSpan timeout)
		{
			string mutexName = $@"Local\BroChoos.TrackingCleanupAuditWriter.{CreatePathHash(cleanupEventsPath)}";
			Mutex mutex = new(false, mutexName);
			bool ownsMutex;

			try
			{
				ownsMutex = mutex.WaitOne(timeout);
			}
			catch (AbandonedMutexException)
			{
				ownsMutex = true;
			}

			if (!ownsMutex)
			{
				mutex.Dispose();
				throw new TimeoutException($"Timed out waiting for cleanup audit write mutex: {cleanupEventsPath}");
			}

			return new FileWriteMutexScope(mutex, ownsMutex: true);
		}

		private static string CreatePathHash(string path)
		{
			byte[] input = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant());
			byte[] hash = SHA256.HashData(input);
			return Convert.ToHexString(hash);
		}

		public void Dispose()
		{
			if (_ownsMutex)
			{
				_mutex.ReleaseMutex();
			}

			_mutex.Dispose();
		}
	}
}
