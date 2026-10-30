using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace BroChoos.App.Logging;

// LOCAL PROBLEM LOG (owner decision "option A"): errors, failures and crashes plus a short
// allowlist of context steps, written to ONE local file. Nothing leaves the machine.
//
// PRIVACY IS ENFORCED HERE, NOT AT THE CALL SITES (D-075/D-097, PRIVACY.md): a line carries
// a UTC timestamp, the level, the short category, the event (an EventId name or the message
// TEMPLATE with URL/path/host-shaped tokens redacted), the exception TYPE name only, and an
// allowlisted set of id/code fields whose values must be plain tokens. The rendered message,
// the exception message and the stack are never written, so a URL, a path, a host or a user
// name cannot reach the file through them.
internal sealed record ProblemLogOptions(string FilePath)
{
	public const long DefaultMaxBytes = 10L * 1024 * 1024;

	public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(14);

	public long MaxBytes { get; init; } = DefaultMaxBytes;

	public TimeSpan Retention { get; init; } = DefaultRetention;

	public const int DefaultQueueCapacity = 1024;

	// Bounded in-memory queue between the callers and the one drain worker. When full, the
	// OLDEST line is dropped and counted; the count is written as a "problemlog.dropped" line.
	public int QueueCapacity { get; init; } = DefaultQueueCapacity;

	// Test seam: false keeps queued lines in memory until Drain/WriteNow, so ordering and
	// overflow can be asserted deterministically. The product always drains in the background.
	public bool AutoDrain { get; init; } = true;

	public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

	// Same root convention as system_changes.log and cleanup_events.jsonl: always under
	// %LocalAppData%\BroChoos\logs, also in portable mode (TrackingCleanupAuditWriter.cs).
	public static string DefaultFilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"BroChoos",
		"logs",
		"problems.log");
}

internal sealed record ProblemLogEntry(
	LogLevel Level,
	string Category,
	string Event,
	string? ExceptionType,
	IReadOnlyList<KeyValuePair<string, string>> Data);

// WRITE PATH: callers (the UI thread included) only enqueue into a bounded in-memory queue and
// return; ONE dedicated background drain thread at a time takes the cross-process mutex, prunes once per
// process, rotates and appends. Crash handlers use WriteNow, which drains whatever is still
// queued and then writes the crash line synchronously, flushed to disk, on the calling thread.
internal sealed class ProblemLogWriter
{
	private static readonly TimeSpan MutexAcquireTimeout = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan FileGateTimeout = TimeSpan.FromSeconds(3);
	private static readonly object CrashSeen = new();
	private static readonly int[] RetryBackoffMs = [20, 40, 80];
	private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);

	private readonly ProblemLogOptions _options;
	private readonly string _rotatedPath;
	private readonly string _mutexName;
	private readonly int _capacity;

	// _queueGate guards the queue, the drop counters and _draining; _fileGate serializes the
	// background drain and a crash write inside this process (the mutex does it across processes).
	private readonly object _queueGate = new();
	private readonly object _fileGate = new();
	private readonly Queue<PendingLine> _queue = new();
	private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, object> _recordedCrashes = [];

	private bool _draining;
	private long _pendingDrops;
	private long _droppedTotal;
	private int _prunedThisProcess;
	private int _pruneAttempts;

	public ProblemLogWriter(ProblemLogOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
		_capacity = Math.Max(1, options.QueueCapacity);
		_rotatedPath = Path.Combine(
			Path.GetDirectoryName(options.FilePath) ?? string.Empty,
			Path.GetFileNameWithoutExtension(options.FilePath) + ".1" + Path.GetExtension(options.FilePath));
		_mutexName = BuildMutexName(options.FilePath);
	}

	// Lines lost to queue overflow, mutex timeout, the hard size bound or an I/O failure.
	public long DroppedCount => Interlocked.Read(ref _droppedTotal);

	// Test seam: how many times this process attempted the startup prune (at most once).
	internal int PruneAttempts => Volatile.Read(ref _pruneAttempts);

	// Test seam: runs on the drain thread before each line is appended (a "slow disk").
	internal Action? BeforeAppend { get; init; }

	// Test seam: starts the drain thread for the given loop. A test can make it throw to exercise
	// the failed-thread-start path. The product always uses StartBackgroundDrainThread.
	internal Action<ThreadStart> DrainThreadStarter { get; init; } = StartBackgroundDrainThread;

	// Test seam: whether a drain is marked as running (read under _queueGate).
	internal bool IsDrainingForTests
	{
		get
		{
			lock (_queueGate)
			{
				return _draining;
			}
		}
	}

	// Never touches the disk and never blocks on it: the line is queued with its timestamp and
	// the drain thread is started if none is running. When the queue is full the OLDEST line is
	// dropped and counted.
	//
	// Returns true when the line is queued and a drain is running or was started for it. Returns
	// false in two cases: queuing itself failed (the line is lost), or the line WAS queued but the
	// drain thread could not be started. In the second case the line stays queued and is written
	// by the next drain, which the next Write or Flush starts, so false means "not promised", not
	// "lost". A failed thread start never throws out of here; an OutOfMemoryException while
	// queuing (allocating the line or growing the queue) is not swallowed and does escape.
	public bool Write(ProblemLogEntry entry)
	{
		try
		{
			PendingLine line = new(entry, _options.Clock());
			bool start = false;
			lock (_queueGate)
			{
				if (_queue.Count >= _capacity)
				{
					_ = _queue.Dequeue();
					_pendingDrops++;
					_ = Interlocked.Increment(ref _droppedTotal);
				}

				_queue.Enqueue(line);
				if (_options.AutoDrain && !_draining)
				{
					_draining = true;
					start = true;
				}
			}

			// A drain that could not be started is reported as false instead of escaping: the line
			// stays queued for the next Write or Flush, but this call cannot promise it is written.
			return !start || TryStartDrainThread();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return false;
		}
	}

	// Crash path: synchronous on the caller, drains everything still queued first (so the lines
	// leading up to the crash land before it), then writes the entry and flushes to disk.
	public bool WriteNow(ProblemLogEntry entry)
	{
		try
		{
			return DrainOnce(flushToDisk: true, new PendingLine(entry, _options.Clock()));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return false;
		}
	}

	// Waits up to the timeout for the queue to be written. With AutoDrain off (tests) it drains
	// on the caller. Returns true when nothing is left queued.
	//
	// Lines can be queued with NO drain running: a Write whose drain thread failed to start left
	// them there. Flush then starts the drain itself instead of waiting out its whole timeout for
	// a drain nobody started, so the shutdown flush (ProcessExit, provider Dispose) still writes
	// them. If that start fails too, Flush returns false at once.
	public bool Flush(TimeSpan timeout)
	{
		try
		{
			if (!_options.AutoDrain)
			{
				return DrainOnce(flushToDisk: false, tail: null);
			}

			long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
			while (true)
			{
				lock (_queueGate)
				{
					if (!_draining && _queue.Count == 0)
					{
						return true;
					}

					if (_draining)
					{
						long remaining = deadline - Environment.TickCount64;
						if (remaining <= 0)
						{
							return false;
						}

						_ = Monitor.Wait(_queueGate, (int)Math.Min(remaining, int.MaxValue));
						continue;
					}

					// Queued lines and no drain: claim the drain under the lock, start it outside.
					_draining = true;
				}

				if (!TryStartDrainThread())
				{
					return false;
				}
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return false;
		}
	}

	// The same exception object reported more than once (Avalonia's dispatcher handler and then
	// AppDomain.UnhandledException for one crash) is recorded once, by reference alone, with no
	// time window. A different exception object of the same type is still recorded.
	internal bool IsDuplicateCrash(Exception? exception)
	{
		if (exception is null)
		{
			return false;
		}

		// Weak keys: a recorded exception is not kept alive by this table.
		return !_recordedCrashes.TryAdd(exception, CrashSeen);
	}

	// Session-scoped "Local\" name, for the reason recorded at AtomicJsonFileStore.BuildMutexName:
	// under "Global\" another account could create the name first and make every write time out.
	// The path is already per-user (%LocalAppData%), and the cost of the residual gap (the same
	// user writing from two sessions at once) is at most interleaved or lost log lines.
	internal static string BuildMutexName(string filePath)
	{
		return $@"Local\BroChoos.ProblemLog.{CreatePathHash(filePath)}";
	}

	public static string Format(ProblemLogEntry entry, DateTimeOffset timestamp)
	{
		using MemoryStream buffer = new();
		using (Utf8JsonWriter json = new(buffer))
		{
			json.WriteStartObject();
			json.WriteString("ts", timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
			json.WriteString("level", entry.Level.ToString());
			json.WriteString("category", entry.Category);
			json.WriteString("event", entry.Event);
			if (entry.ExceptionType is null)
			{
				json.WriteNull("exType");
			}
			else
			{
				json.WriteString("exType", entry.ExceptionType);
			}

			json.WriteStartObject("data");
			foreach (KeyValuePair<string, string> pair in entry.Data)
			{
				json.WriteString(pair.Key, pair.Value);
			}

			json.WriteEndObject();
			json.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.ToArray());
	}

	// The drain runs on its OWN background thread, not on the thread pool. With Task.Run a
	// starved pool (a loaded 2-vCPU CI runner, every test project in parallel) started the drain
	// seconds late, so Flush(5 s) timed out with 600 ms of work not yet begun (CI run
	// 41027998726; ProblemLogStarvationTests). The thread lives for one burst: DrainLoop exits
	// when it finds the queue empty under _queueGate, and the next Write, which checks _draining
	// under the same lock, starts a new one; if starting it fails, the next Write or Flush
	// starts it, so no line is left without a drain once either is called again. IsBackground:
	// process exit never waits for it (ProcessExit gives it the bounded ShutdownDrainTimeout).
	//
	// EVERY exception is caught here, OutOfMemoryException included: when the OS cannot create
	// the thread, Thread.Start throws OutOfMemoryException, and letting it escape would leave
	// _draining stuck at true (no later Write would ever start a drain) and throw out of Write.
	// No thread was created, so nothing else can be half-done and the flag must be cleared.
	private bool TryStartDrainThread()
	{
		try
		{
			DrainThreadStarter(DrainLoop);
			return true;
		}
		catch (Exception)
		{
			// No thread: clear the flag so the next Write or Flush starts one. The lines stay
			// queued, so they are not reported written until a drain has run.
			lock (_queueGate)
			{
				_draining = false;
				Monitor.PulseAll(_queueGate);
			}

			return false;
		}
	}

	private static void StartBackgroundDrainThread(ThreadStart drainLoop)
	{
		new Thread(drainLoop) { IsBackground = true, Name = "BroChoos.ProblemLog.Drain" }.Start();
	}

	private void DrainLoop()
	{
		try
		{
			while (true)
			{
				_ = DrainOnce(flushToDisk: false, tail: null);
				lock (_queueGate)
				{
					if (_queue.Count == 0)
					{
						_draining = false;
						Monitor.PulseAll(_queueGate);
						return;
					}
				}
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			lock (_queueGate)
			{
				_draining = false;
				Monitor.PulseAll(_queueGate);
			}
		}
	}

	// Takes everything queued (plus an optional synchronous tail line) and writes it under the
	// cross-process mutex. A batch that cannot be written is counted as dropped, and the count
	// is carried into the next successful write as a "problemlog.dropped" line.
	private bool DrainOnce(bool flushToDisk, PendingLine? tail)
	{
		if (!Monitor.TryEnter(_fileGate, FileGateTimeout))
		{
			if (tail is not null)
			{
				CountLost(1);
			}

			return false;
		}

		try
		{
			List<PendingLine> batch;
			long drops;
			lock (_queueGate)
			{
				batch = [.. _queue];
				_queue.Clear();
				drops = _pendingDrops;
				_pendingDrops = 0;
			}

			int realLines = batch.Count + (tail is null ? 0 : 1);
			if (drops > 0)
			{
				DateTimeOffset at = batch.Count > 0 ? batch[0].Timestamp : tail?.Timestamp ?? _options.Clock();
				batch.Insert(0, new PendingLine(
					new ProblemLogEntry(LogLevel.Warning, "problemlog", "problemlog.dropped", null, [new("Count", drops.ToString(CultureInfo.InvariantCulture))]),
					at));
			}

			if (tail is not null)
			{
				batch.Add(tail);
			}

			if (batch.Count == 0)
			{
				return true;
			}

			bool ok;
			try
			{
				ok = WriteBatch(batch, flushToDisk);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				ok = false;
			}

			if (!ok)
			{
				// The earlier drop count is not lost either: it is re-queued with this batch.
				lock (_queueGate)
				{
					_pendingDrops += drops;
				}

				CountLost(realLines);
			}

			return ok;
		}
		finally
		{
			Monitor.Exit(_fileGate);
		}
	}

	private void CountLost(long lines)
	{
		lock (_queueGate)
		{
			_pendingDrops += lines;
		}

		_ = Interlocked.Add(ref _droppedTotal, lines);
	}

	private bool WriteBatch(List<PendingLine> batch, bool flushToDisk)
	{
		using Mutex mutex = new(false, _mutexName);
		bool owns;
		bool abandoned = false;
		try
		{
			owns = mutex.WaitOne(MutexAcquireTimeout);
		}
		catch (AbandonedMutexException)
		{
			owns = true;
			abandoned = true;
		}

		if (!owns)
		{
			return false;
		}

		try
		{
			WriteLocked(batch, abandoned, flushToDisk);
			return true;
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	private void WriteLocked(List<PendingLine> batch, bool abandoned, bool flushToDisk)
	{
		string? directory = Path.GetDirectoryName(_options.FilePath);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			_ = Directory.CreateDirectory(directory);
		}

		// The previous owner died mid-write: end its partial line so ours starts on a fresh one.
		if (abandoned)
		{
			EnsureTrailingNewline(_options.FilePath);
		}

		// Retention runs once per process, on this worker, never on the caller's thread. The flag
		// is set BEFORE the attempt, so a prune that keeps failing is not retried on every write,
		// and a failed prune never blocks the append.
		if (Interlocked.Exchange(ref _prunedThisProcess, 1) == 0)
		{
			_ = Interlocked.Increment(ref _pruneAttempts);
			DeleteStalePruneTemps(directory);
			TryPrune(_options.FilePath, _options.Clock());
			TryPrune(_rotatedPath, _options.Clock());
		}

		foreach (PendingLine pending in batch)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(Format(pending.Entry, pending.Timestamp) + Environment.NewLine);
			if (!MakeRoom(bytes.Length))
			{
				CountLost(1);
				continue;
			}

			BeforeAppend?.Invoke();
			AppendWithRetry(bytes, flushToDisk);
		}
	}

	// Rotates at the cap. If the rotation move fails (a viewer holds the file without
	// FILE_SHARE_DELETE) the line is still appended up to a hard bound of 2x the cap; past
	// that it is dropped (false) and counted by the caller.
	private bool MakeRoom(int length)
	{
		FileInfo current = new(_options.FilePath);
		if (!current.Exists || current.Length + length <= _options.MaxBytes)
		{
			return true;
		}

		try
		{
			File.Move(_options.FilePath, _rotatedPath, overwrite: true);
			TryPrune(_rotatedPath, _options.Clock());
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return current.Length + length <= _options.MaxBytes * 2;
		}
	}

	private void AppendWithRetry(byte[] bytes, bool flushToDisk)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				using FileStream stream = new(_options.FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
				stream.Write(bytes);
				stream.Flush(flushToDisk);
				return;
			}
			catch (IOException) when (attempt < RetryBackoffMs.Length)
			{
				Thread.Sleep(RetryBackoffMs[attempt]);
			}
		}
	}

	private static void EnsureTrailingNewline(string path)
	{
		if (!File.Exists(path))
		{
			return;
		}

		using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
		if (stream.Length == 0)
		{
			return;
		}

		_ = stream.Seek(-1, SeekOrigin.End);
		if (stream.ReadByte() != '\n')
		{
			_ = stream.Seek(0, SeekOrigin.End);
			stream.Write(NewLineBytes);
			stream.Flush(flushToDisk: true);
		}
	}

	// A prune interrupted by a crash leaves its "<file>.prune-<guid>.tmp" behind. These are the
	// writer's own files, removed best-effort under the mutex, so no live prune is using them.
	private void DeleteStalePruneTemps(string? directory)
	{
		if (string.IsNullOrWhiteSpace(directory))
		{
			return;
		}

		foreach (string owner in new[] { _options.FilePath, _rotatedPath })
		{
			try
			{
				foreach (string stale in Directory.EnumerateFiles(directory, Path.GetFileName(owner) + ".prune-*.tmp"))
				{
					try
					{
						File.Delete(stale);
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						// Best-effort: the next start tries again.
					}
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best-effort.
			}
		}
	}

	private void TryPrune(string path, DateTimeOffset now)
	{
		try
		{
			Prune(path, now);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best-effort: an unpruned file is still capped by rotation.
		}
	}

	// The rewrite is atomic: the kept lines go to a temp file in the SAME folder, which then
	// replaces the original in one File.Replace, so a crash mid-prune leaves the old file intact.
	private void Prune(string path, DateTimeOffset now)
	{
		if (!File.Exists(path))
		{
			return;
		}

		DateTimeOffset cutoff = now - _options.Retention;
		string[] lines = File.ReadAllLines(path);
		string[] kept = [.. lines.Where(l => TryGetTimestamp(l, out DateTimeOffset ts) && ts >= cutoff)];
		if (kept.Length == lines.Length)
		{
			return;
		}

		if (kept.Length == 0)
		{
			File.Delete(path);
			return;
		}

		string temp = path + ".prune-" + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllLines(temp, kept);
			File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
		}
		finally
		{
			if (File.Exists(temp))
			{
				File.Delete(temp);
			}
		}
	}

	private sealed record PendingLine(ProblemLogEntry Entry, DateTimeOffset Timestamp);

	private static bool TryGetTimestamp(string line, out DateTimeOffset timestamp)
	{
		timestamp = default;
		try
		{
			using JsonDocument json = JsonDocument.Parse(line);
			return json.RootElement.ValueKind is JsonValueKind.Object
				&& json.RootElement.TryGetProperty("ts", out JsonElement ts)
				&& ts.ValueKind is JsonValueKind.String
				&& DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);
		}
		catch (JsonException)
		{
			// An unreadable line drops out on rewrite.
			return false;
		}
	}

	private static string CreatePathHash(string path)
	{
		byte[] input = Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant());
		return Convert.ToHexString(SHA256.HashData(input));
	}
}

internal sealed class ProblemLogLoggerProvider(ProblemLogWriter writer) : ILoggerProvider
{
	private readonly ProblemLogWriter _writer = writer;

	public ILogger CreateLogger(string categoryName)
	{
		return new ProblemLogLogger(_writer, ProblemLog.ShortCategory(categoryName));
	}

	public void Dispose()
	{
		// The writer is shared and outlives the provider; give its queue the same short,
		// bounded drain as process exit (ProblemLog.Install), and never block longer.
		_ = _writer.Flush(ProblemLog.ShutdownDrainTimeout);
	}

	private sealed class ProblemLogLogger(ProblemLogWriter writer, string category) : ILogger
	{
		private readonly ProblemLogWriter _writer = writer;
		private readonly string _category = category;

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return logLevel is >= LogLevel.Information and not LogLevel.None;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			try
			{
				if (!IsEnabled(logLevel))
				{
					return;
				}

				bool named = !string.IsNullOrEmpty(eventId.Name);

				// Information is written ONLY for the allowlisted context steps.
				if (logLevel == LogLevel.Information && !(named && ProblemLog.IsAllowlistedContextEvent(eventId.Name!)))
				{
					return;
				}

				List<KeyValuePair<string, string>> data = [];
				string template = string.Empty;
				if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
				{
					foreach (KeyValuePair<string, object?> pair in values)
					{
						if (pair.Key == "{OriginalFormat}")
						{
							template = pair.Value as string ?? string.Empty;
						}
						else if (ProblemLog.TrySanitizeField(pair.Key, pair.Value, out string? safe))
						{
							data.Add(new(pair.Key, safe!));
						}
					}
				}

				string eventText = named ? ProblemLog.SafeEventName(eventId.Name!) : ProblemLog.RedactTemplate(template);
				_ = _writer.Write(new ProblemLogEntry(logLevel, _category, eventText, ProblemLog.ExceptionTypeName(exception), data));
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				// Best-effort: a logging failure must never surface in the app.
			}
		}
	}
}

internal static partial class ProblemLog
{
	private static readonly HashSet<string> AllowlistedContextEvents = new(StringComparer.Ordinal)
	{
		"picker.opened",
		"card.chosen",
		"launch.result",
		"fallback.used",
		"forward.received",
		"first-run.action",
		"safe-mode.route",
	};

	private static readonly HashSet<string> CodeKeys = new(StringComparer.OrdinalIgnoreCase) { "ErrorCode", "Code", "ReasonCode" };

	private static readonly HashSet<string> CountKeys = new(StringComparer.OrdinalIgnoreCase) { "Count", "Cards" };

	private static readonly HashSet<string> TokenKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		"BrowserId", "Target", "Status", "Action", "ActionId", "Route", "Step", "ExceptionType", "Outcome",
	};

	private static int s_dispatcherAttached;

	public static ProblemLogWriter? Current { get; private set; }

	// Called once from Program.Main. Does not touch the disk: the folder and file are created
	// on the first write. Crash handlers record and never swallow (UnobservedTaskException is
	// observed, which is the documented non-crashing default since .NET 4.5 anyway).
	public static ProblemLogWriter Install(ProblemLogOptions options)
	{
		ProblemLogWriter writer = new(options);
		Current = writer;
		AppDomain.CurrentDomain.UnhandledException += (_, e) => RecordUnhandled(writer, e);
		TaskScheduler.UnobservedTaskException += (_, e) => RecordUnobserved(writer, e);

		// Normal exit: give the background drain a short, bounded chance to finish. Lines still
		// queued after it are lost (known limitation).
		AppDomain.CurrentDomain.ProcessExit += (_, _) => _ = writer.Flush(ShutdownDrainTimeout);
		return writer;
	}

	public static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromMilliseconds(500);

	// Avalonia 11.3: Dispatcher.UnhandledException. Handled is left untouched, so the crash
	// still crashes; it is only recorded first. Attached once per process.
	public static void AttachDispatcher(Dispatcher dispatcher)
	{
		ArgumentNullException.ThrowIfNull(dispatcher);
		if (Interlocked.Exchange(ref s_dispatcherAttached, 1) == 0)
		{
			dispatcher.UnhandledException += (_, e) => RecordCrash(Current, LogLevel.Critical, "crash.ui-dispatcher", e.Exception);
		}
	}

	public static void RecordUnhandled(ProblemLogWriter? writer, UnhandledExceptionEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);
		RecordCrash(writer, LogLevel.Critical, "crash.unhandled", e.ExceptionObject as Exception);
	}

	public static void RecordUnobserved(ProblemLogWriter? writer, UnobservedTaskExceptionEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);
		RecordCrash(writer, LogLevel.Error, "crash.unobserved-task", e.Exception);
		e.SetObserved();
	}

	// Routes a [picker] marker into the file. Only mapped markers are written, and only their
	// allowlisted id/code fields; everything else on the marker line is dropped.
	public static bool RecordMarker(ProblemLogWriter? writer, string message)
	{
		try
		{
			if (writer is null || string.IsNullOrWhiteSpace(message))
			{
				return false;
			}

			string[] tokens = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			string marker = tokens[0];
			List<KeyValuePair<string, string>> data = [];
			LogLevel level = LogLevel.Information;
			string eventName;

			switch (marker)
			{
				case "FIRST_FRAME_REQUESTED":
					eventName = "picker.opened";
					AddFields(tokens, data, "cards");
					break;
				case "LAUNCH_REQUESTED":
					eventName = "card.chosen";
					AddFields(tokens, data, "target");
					break;
				case "LAUNCH_OK":
				case "LAUNCH_UNKNOWN":
				case "LAUNCH_FAILED":
				case "LAUNCH_THREW":
					eventName = "launch.result";
					level = marker == "LAUNCH_OK" ? LogLevel.Information : LogLevel.Warning;
					AddFields(tokens, data, "target", "status", "code");
					if (!data.Exists(static p => p.Key == "status"))
					{
						data.Add(new("status", marker["LAUNCH_".Length..].ToLowerInvariant()));
					}

					break;
				case "FALLBACK_OK":
				case "FALLBACK_FAILED":
					eventName = "fallback.used";
					level = marker == "FALLBACK_OK" ? LogLevel.Information : LogLevel.Warning;
					data.Add(new("status", marker == "FALLBACK_OK" ? "ok" : "failed"));
					AddFields(tokens, data, "code");
					break;
				case "FWD_URL_RECEIVED":
					eventName = "forward.received";
					AddFields(tokens, data, "count");
					break;
				case "FIRST_RUN_ACTION":
				case "FIRST_RUN_ACTION_THREW":
					eventName = "first-run.action";
					level = AddFirstRunFields(marker, tokens, data);
					break;
				default:
					return false;
			}

			return writer.Write(new ProblemLogEntry(level, "picker", eventName, null, data));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return false;
		}
	}

	public static bool IsAllowlistedContextEvent(string eventName)
	{
		return AllowlistedContextEvents.Contains(eventName);
	}

	// An EventId name is a code constant ("safe-mode.route"); anything that is not a plain
	// dotted token is replaced rather than trusted.
	public static string SafeEventName(string eventName)
	{
		return CodeToken().IsMatch(eventName) && eventName.Length <= 64 ? eventName : "<redacted>";
	}

	public static string ShortCategory(string categoryName)
	{
		string name = categoryName;
		int generic = name.IndexOf('`', StringComparison.Ordinal);
		if (generic >= 0)
		{
			name = name[..generic];
		}

		int dot = name.LastIndexOf('.');
		return dot >= 0 ? name[(dot + 1)..] : name;
	}

	public static string? ExceptionTypeName(Exception? exception)
	{
		Exception? effective = exception is AggregateException { InnerExceptions.Count: 1 } aggregate
			? aggregate.InnerExceptions[0]
			: exception;
		return effective?.GetType().Name;
	}

	// A template is a code constant, but it is still scrubbed: anything shaped like a URL, an
	// absolute/UNC path or a dotted host name becomes a placeholder.
	public static string RedactTemplate(string template)
	{
		string redacted = UrlPattern().Replace(template, "<url>");
		redacted = PathPattern().Replace(redacted, "<path>");
		return HostPattern().Replace(redacted, "<host>");
	}

	// Allowlisted keys only. Codes must be on the exact code allowlist (else "unlisted"), ids
	// must be a plain token without dots, counts are digits. ':' '/' '\' '?' '@' and spaces are
	// never accepted, so no URL, path or account name can pass.
	public static bool TrySanitizeField(string key, object? value, out string? safe)
	{
		safe = null;
		string? text = value switch
		{
			null => null,
			IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => value.ToString(),
		};

		if (string.IsNullOrEmpty(text))
		{
			return false;
		}

		// Codes: an exact allowlist member is written as-is, anything else as "unlisted".
		if (CodeKeys.Contains(key))
		{
			safe = IsErrorCode(text) ? text : UnlistedCode;
			return true;
		}

		if (text.Length > 64)
		{
			return false;
		}

		bool ok = CountKeys.Contains(key) ? CountToken().IsMatch(text)
			: TokenKeys.Contains(key) && PlainToken().IsMatch(text);
		safe = ok ? text : null;
		return ok;
	}

	// Value written for an ErrorCode/Code/ReasonCode that is not on the exact allowlist: the line
	// still says "some code was here" without carrying an unknown (possibly host-shaped) string.
	public const string UnlistedCode = "unlisted";

	// EXACT allowlist: a code is written only if it is one of the codes BroChoos itself produces.
	// No shape heuristic, so "ipc.example.com.url" or "request.example.com" cannot pass by
	// resembling a code. Anything else becomes UnlistedCode.
	public static bool IsErrorCode(string text)
	{
		return KnownErrorCodes.Contains(text);
	}

	// Every ErrorCode/Code/ReasonCode literal produced in src/ (grep, 2026-10-27), including the
	// ones our own helper processes return in their JSON response.ErrorCode (LaunchHelper,
	// StorageHelper, NetworkHelper/ShortLinkResolver), plus "none" (the coordinator's placeholder
	// for a missing code). ProblemLogTests.EveryProducedCodeLiteral_IsOnTheAllowlist fails when
	// a new code literal appears in src/ without being added here.
	internal static readonly IReadOnlySet<string> KnownErrorCodes = new HashSet<string>(StringComparer.Ordinal)
	{
		"none",

		// Core IPC protocol (ActivationProtocol).
		"ack.field-count", "ack.id", "ack.kind", "ack.request-mismatch", "ack.status",
		"frame.length", "frame.oversized", "frame.trailing", "frame.truncated", "frame.utf8", "frame.version",
		"request.expired", "request.expiry", "request.field-count", "request.id", "request.kind", "request.url",

		// Tracking cleanup (TrackingCleanupEngine).
		"cleanup.changed", "cleanup.disabled", "cleanup.malformed", "cleanup.no-change", "cleanup.non-http",
		"cleanup.no-query", "cleanup.over-limit", "cleanup.sso-bypass",

		// First run and manual browser admission.
		"consent.discovery.failed", "first-run.manual-add-not-yet-implemented",
		"manual-browser.admitted", "manual-browser.empty-path", "manual-browser.missing-file", "manual-browser.not-a-file",
		"manual-browser.not-an-executable", "manual-browser.path-rejected", "manual-browser.publish-failed",
		"manual-browser.selector-unavailable", "manual-browser.self-reference",

		// Fullscreen suppression queue.
		"fullscreen.queue.dispatcher-unavailable", "fullscreen.queue.drop-oldest", "fullscreen.queue.duplicate", "fullscreen.queue.expired",

		// Single-instance guard, peer authentication and activation queue.
		"ipc.forwarded-url.rejected", "ipc.forwarded-url.subscriber-failed", "ipc.frame.length", "ipc.frame.truncated",
		"ipc.identity.ambiguous", "ipc.identity.missing", "ipc.identity.path-mismatch",
		"ipc.peer.frame.base64", "ipc.peer.frame.fields", "ipc.peer.frame.length", "ipc.peer.frame.oversized",
		"ipc.peer.frame.truncated", "ipc.peer.frame.utf8",
		"ipc.peer.image-handle-invalid", "ipc.peer.image-mismatch", "ipc.peer.image-open-failed",
		"ipc.peer.image-pid-unavailable", "ipc.peer.image-unavailable",
		"ipc.queue.full", "ipc.request.duplicate", "ipc.request.expired", "ipc.request.rejected",
		"ipc.secret.missing", "ipc.secret.replayed-or-unknown", "ipc.signer.identity-mismatch", "ipc.signer.identity-missing",
		"ipc.trust-anchor.fixture-rejected", "ipc.trust-anchor.revoked", "ipc.trust-anchor.unavailable",
		"ipc.validation.read-failed", "ipc.validation.timeout",

		// Browser launch (launcher, LaunchHelper process, helper client) and its message keys.
		"launch.error.failed", "launch.error.timeout", "launch.error.unknown",
		"launch.helper-client-unavailable", "launch.helper-empty-response", "launch.helper-invalid-request",
		"launch.helper-malformed-response", "launch.helper-nonzero-exit", "launch.helper-not-found", "launch.helper-timeout",
		"launch.internal-error", "launch.invalid-target", "launch.private-unsupported", "launch.recursion-guard",
		"launch.start-failed", "launch.target-missing",

		// NetworkHelper client and process (ShortLinkResolver).
		"network.helper-client-unavailable", "network.helper-empty-response", "network.helper-malformed-response",
		"network.helper-nonzero-exit", "network.helper-not-found",
		"shortlink.consent-required", "shortlink.dangerous-scheme", "shortlink.host-not-allowlisted", "shortlink.invalid-url",
		"shortlink.network-error", "shortlink.private-target", "shortlink.redirect-limit", "shortlink.redirect-loop",
		"shortlink.redirect-missing-location", "shortlink.timeout", "shortlink.unsafe-downgrade",

		// StorageHelper client and process.
		"storage.helper-client-unavailable", "storage.helper-empty-response", "storage.helper-execution-failed",
		"storage.helper-invalid-request", "storage.helper-malformed-response", "storage.helper-nonzero-exit",
		"storage.helper-not-found", "storage.helper-timeout",

		// Catalog persist rejections (BrowserCatalogPersistenceCodes.ToErrorCode, one per enum member).
		"storage.persist.persisted", "storage.persist.scan-empty", "storage.persist.no-recognized-browsers",
		"storage.persist.destination-open-failed", "storage.persist.destination-requires-repair",
		"storage.persist.destination-newer-revision-read-only", "storage.persist.destination-newer-schema-read-only",
		"storage.persist.destination-envelope-invalid-read-only",
		"storage.persist.destination-older-schema-migration-unavailable-read-only",
		"storage.persist.writer-failed", "storage.persist.destination-clearance-refused-read-only",
		"storage.persist.unknown",

		// System fallback launcher.
		"systemfallback.invalid-url", "systemfallback.start-failed",

		// Picker config-recovery notice (history.json / rules.json unreadable; Safe Mode start).
		"config.history.reset-defaults", "config.rules.reset-defaults",
		"config.settings.repair-failed", "config.settings.repair-incomplete",
	};

	private static void RecordCrash(ProblemLogWriter? writer, LogLevel level, string eventName, Exception? exception)
	{
		if (writer is null || writer.IsDuplicateCrash(exception))
		{
			return;
		}

		// The process may be about to die: WriteNow drains the queued lines and writes this one
		// synchronously on the crashing thread, flushed to disk, before returning.
		_ = writer.WriteNow(new ProblemLogEntry(level, "crash", eventName, ExceptionTypeName(exception) ?? "unknown", []));
	}

	private static LogLevel AddFirstRunFields(string marker, string[] tokens, List<KeyValuePair<string, string>> data)
	{
		if (marker == "FIRST_RUN_ACTION_THREW")
		{
			AddFields(tokens, data, "action");
			return LogLevel.Error;
		}

		// "FIRST_RUN_ACTION <action> [<outcome>] [key=value...]"
		if (tokens.Length > 1 && TrySanitizeField("action", tokens[1], out string? action))
		{
			data.Add(new("action", action!));
		}

		string? outcome = null;
		if (tokens.Length > 2 && !tokens[2].Contains('=', StringComparison.Ordinal) && TrySanitizeField("outcome", tokens[2], out outcome))
		{
			data.Add(new("outcome", outcome!));
		}

		AddFields(tokens, data, "code");
		bool failed = outcome is not null
			&& (outcome.EndsWith("-failed", StringComparison.Ordinal) || outcome == "refused");
		return failed ? LogLevel.Warning : LogLevel.Information;
	}

	private static void AddFields(string[] tokens, List<KeyValuePair<string, string>> data, params ReadOnlySpan<string> keys)
	{
		foreach (string token in tokens)
		{
			int eq = token.IndexOf('=', StringComparison.Ordinal);
			if (eq <= 0)
			{
				continue;
			}

			string key = token[..eq];
			if (keys.Contains(key) && TrySanitizeField(key, token[(eq + 1)..], out string? safe))
			{
				data.Add(new(key, safe!));
			}
		}
	}

	[GeneratedRegex(@"\S*://\S*", RegexOptions.CultureInvariant)]
	private static partial Regex UrlPattern();

	[GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)\S*", RegexOptions.CultureInvariant)]
	private static partial Regex PathPattern();

	[GeneratedRegex(@"\b[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+\b", RegexOptions.CultureInvariant)]
	private static partial Regex HostPattern();

	[GeneratedRegex(@"^[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*$", RegexOptions.CultureInvariant)]
	private static partial Regex CodeToken();

	[GeneratedRegex(@"^[0-9]{1,9}$", RegexOptions.CultureInvariant)]
	private static partial Regex CountToken();

	[GeneratedRegex(@"^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
	private static partial Regex PlainToken();
}
