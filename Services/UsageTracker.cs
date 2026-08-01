using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebViewHub.Services;

/// <summary>
/// Append-only usage event log. Writes one JSON object per line
/// (JSONL) to <c>Data/usage.jsonl</c> so the stats page can stream-parse it
/// without loading the whole file into memory.
///
/// <para>Events captured:</para>
/// <list type="bullet">
///   <item><c>open</c>  — service window first shown (launch count).</item>
///   <item><c>focus</c> — window was active for <c>ms</c> milliseconds
///         (sum across a day = active usage time).</item>
///   <item><c>close</c> — user explicitly closed the window (not hide-to-tray).</item>
/// </list>
///
/// <para>Why JSONL not a database:</para> the log is append-only, never
/// rewritten, never queried mid-write. SQLite would buy us nothing here
/// and would lock the file against a side-by-side script reading stats.
/// Crash recovery is trivial — if the last line is truncated, the parser
/// drops it and keeps every prior event.
/// </summary>
public sealed class UsageTracker
{
    private readonly string _logFile;
    private readonly object _writeLock = new();

    /// <summary>Tracked focus-start timestamps keyed by service id. A
    /// missing entry means the window is currently not focused (the
    /// matching <c>Deactivated</c> already flushed the delta).</summary>
    private readonly Dictionary<string, DateTime> _focusStart = new();
    private readonly object _focusLock = new();

    /// <summary>Minimum focus duration to record. Filters out spurious
    /// focus-blur pairs caused by Alt+Tab cycling or modal dialogs that
    /// steal focus for a few hundred milliseconds.</summary>
    private const long MinFocusMs = 250;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public UsageTracker(string logFile)
    {
        _logFile = logFile;
        var dir = Path.GetDirectoryName(logFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string LogFile => _logFile;

    /// <summary>Record that the service window was opened (launched / shown).</summary>
    public void TrackOpened(string id, string name)
    {
        Write(new UsageEvent { Type = "open", Id = id, Name = name });
    }

    /// <summary>Record that the user explicitly closed the window (not hide-to-tray).</summary>
    public void TrackClosed(string id, string name)
    {
        // Flush any pending focus interval first — closing while focused
        // would otherwise drop the final active span.
        TrackBlurred(id, name);
        Write(new UsageEvent { Type = "close", Id = id, Name = name });
    }

    /// <summary>Window became active (focus gained). Starts the focus
    /// timer; the matching <see cref="TrackBlurred"/> writes the delta.</summary>
    public void TrackFocused(string id)
    {
        lock (_focusLock)
        {
            // Already tracking — leave the original start time so a stray
            // re-entry (rare but possible during DPI changes / reparents)
            // doesn't reset the clock.
            if (!_focusStart.ContainsKey(id))
                _focusStart[id] = DateTime.UtcNow;
        }
    }

    /// <summary>Window lost focus. Flushes the elapsed focus interval as
    /// a <c>focus</c> event. No-op if there's no matching prior focus.</summary>
    public void TrackBlurred(string id, string name)
    {
        DateTime startUtc;
        lock (_focusLock)
        {
            if (!_focusStart.TryGetValue(id, out startUtc)) return;
            _focusStart.Remove(id);
        }

        var ms = (long)(DateTime.UtcNow - startUtc).TotalMilliseconds;
        if (ms < MinFocusMs) return;
        Write(new UsageEvent { Type = "focus", Id = id, Name = name, Ms = ms, StartedAtUtc = startUtc });
    }

    /// <summary>Called on app shutdown to flush any still-focused windows
    /// so we don't lose the last interval.</summary>
    public void FlushAll(Func<string, string?> nameResolver)
    {
        List<KeyValuePair<string, DateTime>> snapshot;
        lock (_focusLock)
        {
            snapshot = new List<KeyValuePair<string, DateTime>>(_focusStart);
            _focusStart.Clear();
        }
        foreach (var kv in snapshot)
        {
            var ms = (long)(DateTime.UtcNow - kv.Value).TotalMilliseconds;
            if (ms < MinFocusMs) continue;
            var name = nameResolver(kv.Key) ?? kv.Key;
            Write(new UsageEvent { Type = "focus", Id = kv.Key, Name = name, Ms = ms, StartedAtUtc = kv.Value });
        }
    }

    private void Write(UsageEvent ev)
    {
        ev.At = DateTime.UtcNow;
        try
        {
            var json = JsonSerializer.Serialize(ev, JsonOpts);
            lock (_writeLock)
            {
                File.AppendAllText(_logFile, json + "\n");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"UsageTracker.Write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the full event log. Used by the stats page to build aggregates.
    /// Tolerant of partial trailing line (crash recovery) and corrupted
    /// lines (skipped with a debug log). Returns events in file order.
    /// </summary>
    public async Task<List<UsageEvent>> ReadAllAsync()
    {
        var result = new List<UsageEvent>();
        if (!File.Exists(_logFile)) return result;

        try
        {
            await using var fs = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ev = JsonSerializer.Deserialize<UsageEvent>(line);
                    if (ev != null) result.Add(ev);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"UsageTracker.ReadAll skipped malformed line: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"UsageTracker.ReadAll failed: {ex.Message}");
        }
        return result;
    }
}

/// <summary>
/// One usage event row. Property names are short (single letter where
/// possible) because the file grows fast — a heavy-use day produces
/// hundreds of focus/blur pairs.
/// </summary>
public sealed class UsageEvent
{
    /// <summary>UTC timestamp the event was written.</summary>
    [JsonPropertyName("t")] public DateTime At { get; set; }

    /// <summary>open | focus | close</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    /// <summary>Service GUID — stable key across renames.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Service display name at time of write — denormalized so
    /// stats survive a later rename without joining against config.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Duration in ms (only set on <c>focus</c> events).</summary>
    [JsonPropertyName("ms")] public long Ms { get; set; }

    /// <summary>UTC start of the focus interval (only set on <c>focus</c>).
    /// Lets the stats page place the interval in its hour-of-day bucket
    /// instead of using the end time, which can fall in the next hour.</summary>
    [JsonPropertyName("s")] public DateTime? StartedAtUtc { get; set; }
}
