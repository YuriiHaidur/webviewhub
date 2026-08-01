using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace WebViewHub.Services;

/// <summary>
/// Append-only file logger. Writes to Data\logs\webviewhub-yyyyMMdd.log,
/// rotated by date.
///
/// **Non-blocking by design.** Every public call enqueues a formatted line
/// and returns immediately — file I/O happens on a single dedicated
/// background thread. Callers on hot paths (low-level keyboard hooks,
/// hot rendering loops) MUST never block waiting for the disk: a
/// previous synchronous implementation made Logger calls fight a global
/// lock and bypass the OS write cache via FileOptions.WriteThrough,
/// which stalled the WH_KEYBOARD_LL callback long enough for Windows
/// to silently drop Ctrl events on its LowLevelHooksTimeout.
///
/// All public methods are safe from any thread. Lines are flushed on
/// every batch + on shutdown.
/// </summary>
public static class Logger
{
    private static string _logsDir = "";

    /// <summary>Queue of formatted log lines waiting to hit the disk.
    /// Bounded to prevent runaway memory on log-storms (the writer thread
    /// will catch up; if it can't, lines get dropped rather than OOMing).</summary>
    private static readonly BlockingCollection<string> _queue =
        new(new ConcurrentQueue<string>(), boundedCapacity: 50_000);

    private static Thread? _writerThread;
    private const long MaxFileBytes = 5 * 1024 * 1024; // 5 MB per day

    public static string LogsDir => _logsDir;

    /// <summary>Number of formatted lines waiting for the writer thread
    /// to flush. Useful for <see cref="PerfMonitor"/> to detect log
    /// backpressure — a growing queue means hot-path callers are
    /// producing faster than disk can absorb. Healthy = 0 or a few.</summary>
    public static int QueueDepth => _queue.Count;

    public static void Init(string dataDir)
    {
        try
        {
            _logsDir = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(_logsDir);

            // Background writer drains _queue. Background thread so it
            // never blocks process shutdown; CompleteAdding() on Dispose
            // gives it a chance to flush remaining entries.
            _writerThread = new Thread(WriterLoop)
            {
                Name = "WebViewHub.LogWriter",
                IsBackground = true
            };
            _writerThread.Start();

            // Header for the new session — easy to find when scrolling.
            Info(new string('=', 60));
            Info($"Session started — pid {Environment.ProcessId}");
            Info($"Process : {AppContext.BaseDirectory}");
            Info($"OS      : {Environment.OSVersion}");
            Info($".NET    : {Environment.Version}");
            Info($"Args    : {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
        }
        catch { /* logging must never throw */ }
    }

    public static void Info(string message) => Enqueue("INFO ", message, null);
    public static void Warn(string message) => Enqueue("WARN ", message, null);
    public static void Error(string message, Exception? ex = null) => Enqueue("ERROR", message, ex);
    public static void Debug(string message) => Enqueue("DEBUG", message, null);

    /// <summary>
    /// Format the line on the caller thread (cheap — no I/O) and push it
    /// into the queue. Drop on overflow so a log-storm doesn't OOM the
    /// process — we'd rather lose late entries than fall over.
    /// </summary>
    private static void Enqueue(string level, string message, Exception? ex)
    {
        if (string.IsNullOrEmpty(_logsDir)) return;

        try
        {
            var sb = new StringBuilder(128);
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            sb.Append(ts).Append(" [").Append(level).Append("] ").Append(message);
            if (ex != null)
            {
                sb.Append('\n').Append("          ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    foreach (var line in ex.StackTrace.Split('\n'))
                    {
                        sb.Append('\n').Append("          ").Append(line.TrimEnd());
                    }
                }
                var inner = ex.InnerException;
                var depth = 1;
                while (inner != null && depth < 5)
                {
                    sb.Append('\n').Append("          → inner (").Append(inner.GetType().FullName)
                      .Append("): ").Append(inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }
            }
            // TryAdd with 0 timeout → never blocks. If the queue is full
            // (writer thread can't keep up), drop the line.
            _queue.TryAdd(sb.ToString(), millisecondsTimeout: 0);
        }
        catch { /* never propagate — logging is non-essential */ }
    }

    private static void WriterLoop()
    {
        // Batch writes: pull whatever is queued, write all in one
        // FileStream session, repeat. Massively cheaper than open/close
        // per line and still keeps lines visible within a few ms of
        // their enqueue.
        var batch = new List<string>(128);
        try
        {
            while (!_queue.IsCompleted)
            {
                string? first;
                try { first = _queue.Take(); }
                catch (InvalidOperationException) { break; /* CompleteAdding */ }
                batch.Clear();
                batch.Add(first);
                // Drain anything else that's already waiting so we make
                // one disk pass per N lines instead of N disk passes.
                while (batch.Count < 256 && _queue.TryTake(out var more))
                {
                    batch.Add(more);
                }
                FlushBatch(batch);
            }
            // Final drain on shutdown.
            batch.Clear();
            while (_queue.TryTake(out var rem)) batch.Add(rem);
            if (batch.Count > 0) FlushBatch(batch);
        }
        catch { /* writer must never crash */ }
    }

    private static void FlushBatch(List<string> lines)
    {
        try
        {
            var path = Path.Combine(_logsDir, $"webviewhub-{DateTime.Now:yyyyMMdd}.log");
            // Cheap rotate: if the file exceeds limit, archive it.
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxFileBytes)
                {
                    var archived = Path.Combine(_logsDir,
                        $"webviewhub-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    File.Move(path, archived);
                }
            }
            catch { /* keep going */ }

            using var fs = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 8192);
            using var sw = new StreamWriter(fs, Encoding.UTF8);
            foreach (var line in lines) sw.WriteLine(line);
        }
        catch { /* swallow */ }
    }
}
