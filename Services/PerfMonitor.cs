using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace WebViewHub.Services;

/// <summary>
/// Periodic process-resource sampler. Every <see cref="Interval"/> snaps
/// memory / CPU / threads / GC / WebView2-helper aggregates and appends
/// one row to <c>Data/perf/perf-yyyyMMdd.csv</c>, plus emits one
/// compact <c>[PERF]</c> line to the main log for quick eyeballing.
///
/// CSV is the right format here — across multiple sessions / days the
/// rows concatenate cleanly into a graphable time series. Use Excel /
/// Python pandas / any log viewer to spot trends: WS climbing
/// monotonically = leak; sustained GC gen2 churn = pressure; growing
/// thread count with stable service count = handle leak; growing
/// log_queue = Logger writer can't keep up.
///
/// Cost per snap: ~50-100ms (Process enumeration + Process.GetProcessesByName).
/// At a 2-minute interval that's ~50ms / 120s = 0.04% CPU — negligible.
/// </summary>
public sealed class PerfMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly string _perfDir;
    private readonly DateTime _startedAt = DateTime.Now;

    /// <summary>Interval between snapshots. 2 min is a sweet spot —
    /// fine enough to see hibernation-style transitions, coarse enough
    /// to keep daily CSV under ~30 KB and Logger noise minimal.</summary>
    public TimeSpan Interval { get; }

    public PerfMonitor(string dataDir, TimeSpan? interval = null)
    {
        Interval = interval ?? TimeSpan.FromMinutes(2);
        _perfDir = Path.Combine(dataDir, "perf");
        try { Directory.CreateDirectory(_perfDir); } catch { /* logged on first write */ }

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => SnapAndLog();
        _timer.Start();

        // Baseline snap right at startup so the CSV always has a t=0
        // row to anchor every later delta against.
        SnapAndLog();

        Logger.Info($"PerfMonitor started — interval={Interval.TotalMinutes:F1} min → {_perfDir}");
    }

    private void SnapAndLog()
    {
        try
        {
            var s = TakeSnapshot();
            WriteCsv(s);
            Logger.Info(
                $"[PERF] ws={s.MainWSMB:F0}MB priv={s.MainPrivMB:F0}MB " +
                $"threads={s.MainThreads} handles={s.MainHandles} " +
                $"gcHeap={s.GcHeapMB:F0}MB svc={s.ServiceVisible}/{s.ServiceOpen} " +
                $"wv2x{s.Wv2Count}={s.Wv2WSMB:F0}MB " +
                $"cpu/min={s.CpuPerMin:F2}s logQ={s.LogQueueDepth}");
        }
        catch (Exception ex) { Logger.Warn($"PerfMonitor snap failed: {ex.Message}"); }
    }

    private PerfSnapshot TakeSnapshot()
    {
        var proc = Process.GetCurrentProcess();
        var uptimeMin = (DateTime.Now - proc.StartTime).TotalMinutes;
        var cpuSec = proc.TotalProcessorTime.TotalSeconds;

        var snap = new PerfSnapshot
        {
            Timestamp = DateTime.UtcNow,
            UptimeMin = Math.Round(uptimeMin, 2),
            MainWSMB = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1),
            MainPrivMB = Math.Round(proc.PrivateMemorySize64 / 1024.0 / 1024.0, 1),
            MainThreads = proc.Threads.Count,
            MainHandles = proc.HandleCount,
            MainCpuSec = Math.Round(cpuSec, 2),
            CpuPerMin = uptimeMin > 0 ? Math.Round(cpuSec / uptimeMin, 3) : 0,
            GcHeapMB = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1),
            GcGen0 = GC.CollectionCount(0),
            GcGen1 = GC.CollectionCount(1),
            GcGen2 = GC.CollectionCount(2),
            LogQueueDepth = Logger.QueueDepth,
        };

        // WebView2 helpers — all msedgewebview2.exe processes on the
        // system (these are children of any WebView2 instance, ours
        // included). For a single-user dev box this is effectively
        // our count, but in shared environments where the user runs
        // Edge / Teams / another WebView2 app simultaneously, this
        // over-counts. Acceptable: we mostly care about deltas, and
        // any non-WebViewHub WebView2 will be steady.
        var helpers = Process.GetProcessesByName("msedgewebview2");
        try
        {
            snap.Wv2Count = helpers.Length;
            long wsSum = 0, privSum = 0;
            double cpuSum = 0;
            int threadSum = 0;
            foreach (var h in helpers)
            {
                try
                {
                    wsSum += h.WorkingSet64;
                    privSum += h.PrivateMemorySize64;
                    cpuSum += h.TotalProcessorTime.TotalSeconds;
                    threadSum += h.Threads.Count;
                }
                catch { /* process may have died mid-iteration */ }
            }
            snap.Wv2WSMB = Math.Round(wsSum / 1024.0 / 1024.0, 1);
            snap.Wv2PrivMB = Math.Round(privSum / 1024.0 / 1024.0, 1);
            snap.Wv2Threads = threadSum;
            snap.Wv2CpuSec = Math.Round(cpuSum, 2);
        }
        finally
        {
            foreach (var h in helpers)
            {
                try { h.Dispose(); } catch { }
            }
        }

        try
        {
            var svcWindows = System.Windows.Application.Current?.Windows
                .OfType<Windows.ServiceWindow>().ToList();
            snap.ServiceOpen = svcWindows?.Count ?? 0;
            snap.ServiceVisible = svcWindows?.Count(w => w.IsVisible) ?? 0;
            snap.HotkeysCount = App.Hotkeys?.RegisteredCount ?? 0;
        }
        catch { /* WPF Application not yet initialized — defaults to 0 */ }

        return snap;
    }

    private void WriteCsv(PerfSnapshot s)
    {
        try
        {
            var path = Path.Combine(_perfDir, $"perf-{DateTime.Now:yyyyMMdd}.csv");
            bool needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                          bufferSize: 4096);
            using var sw = new StreamWriter(fs);

            if (needsHeader)
            {
                sw.WriteLine(
                    "timestamp,uptime_min," +
                    "main_ws_mb,main_priv_mb,main_threads,main_handles,main_cpu_sec,cpu_per_min," +
                    "gc_heap_mb,gc_gen0,gc_gen1,gc_gen2," +
                    "wv2_count,wv2_ws_mb,wv2_priv_mb,wv2_threads,wv2_cpu_sec," +
                    "svc_open,svc_visible,hotkeys,log_queue");
            }

            // Use invariant culture so European decimal commas don't
            // break the CSV (which IS comma-separated).
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            sw.WriteLine(string.Format(ci,
                "{0:O},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20}",
                s.Timestamp,
                s.UptimeMin, s.MainWSMB, s.MainPrivMB, s.MainThreads, s.MainHandles, s.MainCpuSec, s.CpuPerMin,
                s.GcHeapMB, s.GcGen0, s.GcGen1, s.GcGen2,
                s.Wv2Count, s.Wv2WSMB, s.Wv2PrivMB, s.Wv2Threads, s.Wv2CpuSec,
                s.ServiceOpen, s.ServiceVisible, s.HotkeysCount, s.LogQueueDepth));
        }
        catch (Exception ex)
        {
            Logger.Warn($"PerfMonitor CSV write failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _timer.Stop(); } catch { }
    }

    private sealed class PerfSnapshot
    {
        public DateTime Timestamp { get; set; }
        public double UptimeMin { get; set; }
        public double MainWSMB { get; set; }
        public double MainPrivMB { get; set; }
        public int MainThreads { get; set; }
        public int MainHandles { get; set; }
        public double MainCpuSec { get; set; }
        public double CpuPerMin { get; set; }
        public double GcHeapMB { get; set; }
        public int GcGen0 { get; set; }
        public int GcGen1 { get; set; }
        public int GcGen2 { get; set; }
        public int Wv2Count { get; set; }
        public double Wv2WSMB { get; set; }
        public double Wv2PrivMB { get; set; }
        public int Wv2Threads { get; set; }
        public double Wv2CpuSec { get; set; }
        public int ServiceOpen { get; set; }
        public int ServiceVisible { get; set; }
        public int HotkeysCount { get; set; }
        public int LogQueueDepth { get; set; }
    }
}
