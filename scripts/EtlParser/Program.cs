// ETL Parser — extracts key performance data from VS Diagnostics Hub ETL files.
// Usage: dotnet run -- <path-to-etl> [--out <output-dir>] [--process <name>]
//
// Produces a summary covering:
//   - CPU sampling (hot modules, hot methods)
//   - GC events (collections, pause times, heap sizes)
//   - ThreadPool events (worker thread count, throughput, starvation)
//   - File I/O events (opens, reads, total bytes, latency)
//   - Disk I/O events (read/write, latency)
//   - Process lifetime (start/end, duration)

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;

// ─── Argument parsing ───────────────────────────────────────────────────────
string? etlPath = null;
string? outDir = null;
string? processFilter = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length) { outDir = args[++i]; continue; }
    if (args[i] == "--process" && i + 1 < args.Length) { processFilter = args[++i]; continue; }
    if (args[i].StartsWith('-')) continue;
    etlPath ??= args[i];
}

if (etlPath is null)
{
    Console.Error.WriteLine("Usage: EtlParser <path-to-etl> [--out <dir>] [--process <name>]");
    Console.Error.WriteLine("  --out       Output directory for reports (default: same as ETL)");
    Console.Error.WriteLine("  --process   Filter to a specific process name (e.g. Yagu)");
    return 1;
}

if (!File.Exists(etlPath))
{
    Console.Error.WriteLine($"ETL file not found: {etlPath}");
    return 1;
}

outDir ??= Path.GetDirectoryName(etlPath) ?? ".";
Directory.CreateDirectory(outDir);

Console.WriteLine($"Parsing: {etlPath}");
Console.WriteLine($"Output:  {outDir}");
Console.WriteLine($"ETL size: {new FileInfo(etlPath).Length / (1024.0 * 1024):F1} MB");
Console.WriteLine();

var sw = Stopwatch.StartNew();

// ─── Accumulators ────────────────────────────────────────────────────────────

// CPU Sampling
long totalCpuSamples = 0;
var cpuByModule = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
var cpuByMethod = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
long cpuSamplesForProcess = 0;

// GC
var gcEvents = new List<GcRecord>();
var gcAllocTicks = new List<(double TimeMs, long Bytes, string TypeName)>();

// ThreadPool
var tpWorkerChanges = new List<(double TimeMs, int Active, int Retired)>();
var tpWorkItems = 0L;

// File I/O
var fileOpens = 0L;
var fileReads = 0L;
var fileBytesRead = 0L;
var fileWrites = 0L;
var fileBytesWritten = 0L;
var topFilesByReads = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
var topFilesByBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

// Disk I/O
var diskReads = 0L;
var diskBytesRead = 0L;
var diskWrites = 0L;
var diskBytesWritten = 0L;
var diskReadLatencyMs = new List<double>();
var diskWriteLatencyMs = new List<double>();

// Process info
var processInfo = new Dictionary<int, (string Name, double StartMs, double EndMs)>();
double traceEndMs = 0;

// Contention
var contentionEvents = 0L;
var contentionDurations = new List<double>();

// Exception
var exceptionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

// ─── Phase 1: Parse ETL ──────────────────────────────────────────────────────

Console.Write("Opening ETL (this may take a while for large files)... ");

using (var source = new ETWTraceEventSource(etlPath))
{
    Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F1}s");

    bool MatchProcess(TraceEvent e) =>
        processFilter is null ||
        (e.ProcessName?.Contains(processFilter, StringComparison.OrdinalIgnoreCase) ?? false);

    // ── CPU Sampling ─────────────────────────────────────────────────────
    var kernelParser = new KernelTraceEventParser(source);

    kernelParser.PerfInfoSample += e =>
    {
        totalCpuSamples++;
        if (MatchProcess(e))
            Interlocked.Increment(ref cpuSamplesForProcess);
    };

    // ── GC Events ────────────────────────────────────────────────────────
    var clrParser = new ClrTraceEventParser(source);

    clrParser.GCStart += e =>
    {
        if (!MatchProcess(e)) return;
        gcEvents.Add(new GcRecord
        {
            TimeMs = e.TimeStampRelativeMSec,
            Generation = e.Depth,
            Reason = e.Reason.ToString(),
            Type = e.Type.ToString(),
            IsBackground = e.Type == GCType.BackgroundGC,
        });
    };

    clrParser.GCHeapStats += e =>
    {
        if (!MatchProcess(e)) return;
        if (gcEvents.Count > 0)
        {
            var last = gcEvents[^1];
            last.Gen0SizeBytes = e.GenerationSize0;
            last.Gen1SizeBytes = e.GenerationSize1;
            last.Gen2SizeBytes = e.GenerationSize2;
            last.LohSizeBytes = e.GenerationSize3;
            last.PohSizeBytes = e.GenerationSize4;
            last.TotalHeapBytes = e.TotalHeapSize;
            gcEvents[^1] = last;
        }
    };

    clrParser.GCSuspendEEStart += e =>
    {
        if (!MatchProcess(e)) return;
        if (gcEvents.Count > 0)
        {
            var last = gcEvents[^1];
            last.SuspendStartMs = e.TimeStampRelativeMSec;
            gcEvents[^1] = last;
        }
    };

    clrParser.GCRestartEEStop += e =>
    {
        if (!MatchProcess(e)) return;
        if (gcEvents.Count > 0)
        {
            var last = gcEvents[^1];
            if (last.SuspendStartMs > 0)
            {
                last.PauseMs = e.TimeStampRelativeMSec - last.SuspendStartMs;
                gcEvents[^1] = last;
            }
        }
    };

    clrParser.GCAllocationTick += e =>
    {
        if (!MatchProcess(e)) return;
        gcAllocTicks.Add((e.TimeStampRelativeMSec, e.AllocationAmount64, e.TypeName ?? "?"));
    };

    // ── ThreadPool ───────────────────────────────────────────────────────
    clrParser.ThreadPoolWorkerThreadAdjustmentAdjustment += e =>
    {
        if (!MatchProcess(e)) return;
        tpWorkerChanges.Add((e.TimeStampRelativeMSec, (int)e.NewWorkerThreadCount, 0));
    };

    clrParser.ThreadPoolWorkerThreadStart += e =>
    {
        if (!MatchProcess(e)) return;
        tpWorkerChanges.Add((e.TimeStampRelativeMSec, e.ActiveWorkerThreadCount, e.RetiredWorkerThreadCount));
    };

    clrParser.ThreadPoolEnqueue += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref tpWorkItems);
    };

    // ── Contention ───────────────────────────────────────────────────────
    clrParser.ContentionStop += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref contentionEvents);
        contentionDurations.Add(e.DurationNs / 1_000_000.0);
    };

    // ── Exceptions ───────────────────────────────────────────────────────
    clrParser.ExceptionStart += e =>
    {
        if (!MatchProcess(e)) return;
        var name = e.ExceptionType ?? "Unknown";
        exceptionCounts.TryGetValue(name, out int c);
        exceptionCounts[name] = c + 1;
    };

    // ── File I/O (kernel) ────────────────────────────────────────────────
    kernelParser.FileIOCreate += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref fileOpens);
    };

    kernelParser.FileIORead += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref fileReads);
        Interlocked.Add(ref fileBytesRead, e.IoSize);
        var fn = e.FileName ?? "?";
        lock (topFilesByReads)
        {
            topFilesByReads.TryGetValue(fn, out long cnt);
            topFilesByReads[fn] = cnt + 1;
        }
        lock (topFilesByBytes)
        {
            topFilesByBytes.TryGetValue(fn, out long b);
            topFilesByBytes[fn] = b + e.IoSize;
        }
    };

    kernelParser.FileIOWrite += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref fileWrites);
        Interlocked.Add(ref fileBytesWritten, e.IoSize);
    };

    // ── Disk I/O (kernel) ────────────────────────────────────────────────
    kernelParser.DiskIORead += e =>
    {
        Interlocked.Increment(ref diskReads);
        Interlocked.Add(ref diskBytesRead, e.TransferSize);
        if (e.ElapsedTimeMSec > 0)
            lock (diskReadLatencyMs) diskReadLatencyMs.Add(e.ElapsedTimeMSec);
    };

    kernelParser.DiskIOWrite += e =>
    {
        Interlocked.Increment(ref diskWrites);
        Interlocked.Add(ref diskBytesWritten, e.TransferSize);
        if (e.ElapsedTimeMSec > 0)
            lock (diskWriteLatencyMs) diskWriteLatencyMs.Add(e.ElapsedTimeMSec);
    };

    // ── Process ──────────────────────────────────────────────────────────
    kernelParser.ProcessStart += e =>
    {
        processInfo[e.ProcessID] = (e.ProcessName ?? "?", e.TimeStampRelativeMSec, 0);
    };

    kernelParser.ProcessStop += e =>
    {
        if (processInfo.TryGetValue(e.ProcessID, out var info))
            processInfo[e.ProcessID] = (info.Name, info.StartMs, e.TimeStampRelativeMSec);
    };

    // ── Progress indicator ───────────────────────────────────────────────
    long eventCount = 0;
    long lastReport = 0;
    source.AllEvents += e =>
    {
        var c = Interlocked.Increment(ref eventCount);
        if (c - Interlocked.Read(ref lastReport) >= 5_000_000)
        {
            Interlocked.Exchange(ref lastReport, c);
            Console.Write($"\r  {c / 1_000_000.0:F1}M events processed...");
        }
        traceEndMs = Math.Max(traceEndMs, e.TimeStampRelativeMSec);
    };

    // ── Process the ETL ──────────────────────────────────────────────────
    Console.WriteLine("Processing events...");
    source.Process();
    Console.WriteLine($"\r  {eventCount / 1_000_000.0:F1}M events processed in {sw.Elapsed.TotalSeconds:F1}s");
}

// ─── Phase 1b: CPU stack sampling via TraceLog (enables native frame resolution) ──
Console.WriteLine();
Console.Write("Converting ETL → ETLX for stack resolution... ");
var etlxPath = Path.ChangeExtension(etlPath, ".etlx");
try
{
    TraceLog.CreateFromEventTraceLogFile(etlPath, etlxPath);
    Console.WriteLine("done.");

    using var traceLog = TraceLog.OpenOrConvert(etlxPath);

    // Find the target process
    TraceProcess? targetProcess = null;
    if (processFilter != null)
    {
        targetProcess = traceLog.Processes
            .FirstOrDefault(p => p.Name?.Contains(processFilter, StringComparison.OrdinalIgnoreCase) == true);
    }

    if (targetProcess != null || processFilter == null)
    {
        Console.Write("Walking CPU stacks... ");
        var cpuStackSw = Stopwatch.StartNew();

        var events = traceLog.Events
            .Where(e => e.EventName == "PerfInfoSample" || e.EventName == "PerfInfo/Sample")
            .Where(e => targetProcess == null || e.ProcessID == targetProcess.ProcessID);

        long stackSamples = 0;
        foreach (var evt in events)
        {
            var callStack = evt.CallStack();
            if (callStack == null) continue;
            stackSamples++;

            var seenModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var frame = callStack;
            while (frame != null)
            {
                var modFile = frame.CodeAddress?.ModuleFile;
                var moduleName = modFile?.Name;
                var methodName = frame.CodeAddress?.FullMethodName;

                if (!string.IsNullOrEmpty(moduleName) && seenModules.Add(moduleName))
                {
                    cpuByModule.TryGetValue(moduleName, out long c);
                    cpuByModule[moduleName] = c + 1;
                }

                if (!string.IsNullOrEmpty(methodName) && methodName != "?" && seenMethods.Add(methodName))
                {
                    cpuByMethod.TryGetValue(methodName, out long c);
                    cpuByMethod[methodName] = c + 1;
                }

                frame = frame.Caller;
            }
        }
        Console.WriteLine($"done ({stackSamples:N0} stacks resolved in {cpuStackSw.Elapsed.TotalSeconds:F1}s)");
    }
    else
    {
        Console.WriteLine($"Process '{processFilter}' not found in TraceLog — skipping stack resolution.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ETLX conversion failed: {ex.Message}");
    Console.WriteLine("CPU module/method breakdown will be unavailable.");
}

// ─── Phase 2: Produce reports ────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Generating reports...");

var sb = new StringBuilder();
double traceDurationSec = traceEndMs / 1000.0;

sb.AppendLine("═══════════════════════════════════════════════════════════════════");
sb.AppendLine("  ETL Performance Analysis Report");
sb.AppendLine($"  Source: {Path.GetFileName(etlPath)}");
sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
sb.AppendLine("═══════════════════════════════════════════════════════════════════");
sb.AppendLine();

// ── Trace Overview ───────────────────────────────────────────────────────
sb.AppendLine("┌─ TRACE OVERVIEW ─────────────────────────────────────────────────");
sb.AppendLine($"│ Duration:          {traceDurationSec:F1} seconds ({traceDurationSec / 60:F1} min)");
sb.AppendLine($"│ Total CPU samples: {totalCpuSamples:N0}");
sb.AppendLine($"│ Processes seen:    {processInfo.Count}");
if (processFilter != null)
    sb.AppendLine($"│ Filter:            {processFilter}");
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── CPU Sampling by Module ───────────────────────────────────────────────
sb.AppendLine("┌─ CPU SAMPLING BY MODULE (inclusive, process-filtered) ────────────");
sb.AppendLine($"│ Samples in process: {cpuSamplesForProcess:N0} of {totalCpuSamples:N0} total");
if (cpuByModule.Count > 0)
{
    sb.AppendLine("│");
    sb.AppendLine("│   Samples | % of proc | Module");
    sb.AppendLine("│ ----------|-----------|-------");
    var topModules = cpuByModule.OrderByDescending(kv => kv.Value).Take(30);
    foreach (var (mod, count) in topModules)
    {
        double pct = cpuSamplesForProcess > 0 ? count * 100.0 / cpuSamplesForProcess : 0;
        sb.AppendLine($"│ {count,9:N0} | {pct,8:F1}% | {mod}");
    }
}
else
{
    sb.AppendLine("│ No resolved stack frames (symbols may not be available).");
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── CPU Sampling by Method ───────────────────────────────────────────────
sb.AppendLine("┌─ CPU SAMPLING BY METHOD (inclusive, top 50) ──────────────────────");
if (cpuByMethod.Count > 0)
{
    sb.AppendLine("│   Samples | % of proc | Method");
    sb.AppendLine("│ ----------|-----------|-------");
    var topMethods = cpuByMethod.OrderByDescending(kv => kv.Value).Take(50);
    foreach (var (method, count) in topMethods)
    {
        double pct = cpuSamplesForProcess > 0 ? count * 100.0 / cpuSamplesForProcess : 0;
        sb.AppendLine($"│ {count,9:N0} | {pct,8:F1}% | {method}");
    }
}
else
{
    sb.AppendLine("│ No resolved method names (symbols may not be available).");
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── GC Analysis ──────────────────────────────────────────────────────────
sb.AppendLine("┌─ GARBAGE COLLECTION ─────────────────────────────────────────────");
if (gcEvents.Count > 0)
{
    var gen0 = gcEvents.Where(g => g.Generation == 0).ToList();
    var gen1 = gcEvents.Where(g => g.Generation == 1).ToList();
    var gen2 = gcEvents.Where(g => g.Generation == 2).ToList();

    sb.AppendLine($"│ Total GC events:   {gcEvents.Count:N0}");
    sb.AppendLine($"│   Gen 0:           {gen0.Count:N0}");
    sb.AppendLine($"│   Gen 1:           {gen1.Count:N0}");
    sb.AppendLine($"│   Gen 2:           {gen2.Count:N0}  (most expensive)");
    sb.AppendLine($"│   Background:      {gcEvents.Count(g => g.IsBackground):N0}");
    sb.AppendLine("│");

    var pauseTimes = gcEvents.Where(g => g.PauseMs > 0).Select(g => g.PauseMs).ToList();
    if (pauseTimes.Count > 0)
    {
        pauseTimes.Sort();
        sb.AppendLine($"│ Pause times ({pauseTimes.Count} measured):");
        sb.AppendLine($"│   Mean:    {pauseTimes.Average():F2} ms");
        sb.AppendLine($"│   Median:  {Percentile(pauseTimes, 50):F2} ms");
        sb.AppendLine($"│   P95:     {Percentile(pauseTimes, 95):F2} ms");
        sb.AppendLine($"│   P99:     {Percentile(pauseTimes, 99):F2} ms");
        sb.AppendLine($"│   Max:     {pauseTimes.Max():F2} ms");
        sb.AppendLine($"│   Total:   {pauseTimes.Sum():F1} ms ({pauseTimes.Sum() / 1000.0:F2}s)");
        double gcPercent = pauseTimes.Sum() / (traceDurationSec * 1000) * 100;
        sb.AppendLine($"│   % time:  {gcPercent:F1}% of trace duration in GC pauses");
        sb.AppendLine("│");

        var gen2Pauses = gen2.Where(g => g.PauseMs > 0).Select(g => g.PauseMs).OrderBy(p => p).ToList();
        if (gen2Pauses.Count > 0)
        {
            sb.AppendLine($"│ Gen 2 pause breakdown ({gen2Pauses.Count} collections):");
            sb.AppendLine($"│   Mean:    {gen2Pauses.Average():F2} ms");
            sb.AppendLine($"│   Max:     {gen2Pauses.Max():F2} ms");
            sb.AppendLine($"│   Total:   {gen2Pauses.Sum():F1} ms");
        }
    }

    var withHeap = gcEvents.Where(g => g.TotalHeapBytes > 0).ToList();
    if (withHeap.Count > 0)
    {
        var last = withHeap[^1];
        sb.AppendLine("│");
        sb.AppendLine($"│ Last observed heap:");
        sb.AppendLine($"│   Total:   {last.TotalHeapBytes / (1024.0 * 1024):F1} MB");
        sb.AppendLine($"│   Gen 0:   {last.Gen0SizeBytes / (1024.0 * 1024):F1} MB");
        sb.AppendLine($"│   Gen 1:   {last.Gen1SizeBytes / (1024.0 * 1024):F1} MB");
        sb.AppendLine($"│   Gen 2:   {last.Gen2SizeBytes / (1024.0 * 1024):F1} MB");
        sb.AppendLine($"│   LOH:     {last.LohSizeBytes / (1024.0 * 1024):F1} MB");
        sb.AppendLine($"│   POH:     {last.PohSizeBytes / (1024.0 * 1024):F1} MB");

        long peakHeap = withHeap.Max(g => g.TotalHeapBytes);
        sb.AppendLine($"│   Peak:    {peakHeap / (1024.0 * 1024):F1} MB");
    }

    if (gcEvents.Count > 10)
    {
        sb.AppendLine("│");
        sb.AppendLine("│ GC rate over time (10s buckets, busiest periods):");
        var buckets = gcEvents
            .GroupBy(g => (int)(g.TimeMs / 10_000))
            .OrderByDescending(g => g.Count())
            .Take(10);
        foreach (var b in buckets)
        {
            double tSec = b.Key * 10;
            sb.AppendLine($"│   t={tSec,7:F0}s: {b.Count(),4} GCs  (Gen0={b.Count(x => x.Generation == 0)} Gen1={b.Count(x => x.Generation == 1)} Gen2={b.Count(x => x.Generation == 2)})");
        }
    }
}
else
{
    sb.AppendLine("│ No GC events captured (CLR provider may not be in this ETL).");
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── Allocation hot types ─────────────────────────────────────────────────
if (gcAllocTicks.Count > 0)
{
    sb.AppendLine("┌─ ALLOCATION HOT TYPES (from GCAllocationTick) ────────────────────");
    var topTypes = gcAllocTicks
        .GroupBy(a => a.TypeName)
        .Select(g => (Type: g.Key, TotalBytes: g.Sum(a => a.Bytes), Count: g.Count()))
        .OrderByDescending(x => x.TotalBytes)
        .Take(20);
    sb.AppendLine("│  # |          Bytes |   Count | Type");
    sb.AppendLine("│ ---|----------------|---------|-----");
    int rank = 0;
    foreach (var t in topTypes)
    {
        rank++;
        sb.AppendLine($"│ {rank,2} | {t.TotalBytes / (1024.0 * 1024),11:F2} MB | {t.Count,7:N0} | {t.Type}");
    }
    sb.AppendLine("└──────────────────────────────────────────────────────────────────");
    sb.AppendLine();
}

// ── ThreadPool ───────────────────────────────────────────────────────────
sb.AppendLine("┌─ THREADPOOL ──────────────────────────────────────────────────────");
sb.AppendLine($"│ Work items enqueued: {tpWorkItems:N0}");
if (traceDurationSec > 0)
    sb.AppendLine($"│ Work items/sec:     {tpWorkItems / traceDurationSec:F1}");
if (tpWorkerChanges.Count > 0)
{
    var maxThreads = tpWorkerChanges.Max(w => w.Active);
    var minThreads = tpWorkerChanges.Min(w => w.Active);
    sb.AppendLine($"│ Worker thread range: {minThreads} – {maxThreads}");

    sb.AppendLine("│");
    sb.AppendLine("│ Thread count over time (30s buckets):");
    var threadBuckets = tpWorkerChanges
        .GroupBy(w => (int)(w.TimeMs / 30_000))
        .OrderBy(g => g.Key)
        .Take(20);
    foreach (var b in threadBuckets)
    {
        double tSec = b.Key * 30;
        var avg = b.Average(w => w.Active);
        var max = b.Max(w => w.Active);
        sb.AppendLine($"│   t={tSec,7:F0}s: avg={avg:F0} max={max}");
    }
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── Lock Contention ──────────────────────────────────────────────────────
sb.AppendLine("┌─ LOCK CONTENTION ─────────────────────────────────────────────────");
sb.AppendLine($"│ Contention events: {contentionEvents:N0}");
if (contentionDurations.Count > 0)
{
    contentionDurations.Sort();
    sb.AppendLine($"│ Duration stats:");
    sb.AppendLine($"│   Mean:   {contentionDurations.Average():F3} ms");
    sb.AppendLine($"│   Median: {Percentile(contentionDurations, 50):F3} ms");
    sb.AppendLine($"│   P95:    {Percentile(contentionDurations, 95):F3} ms");
    sb.AppendLine($"│   P99:    {Percentile(contentionDurations, 99):F3} ms");
    sb.AppendLine($"│   Max:    {contentionDurations.Max():F3} ms");
    sb.AppendLine($"│   Total:  {contentionDurations.Sum():F1} ms");
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── File I/O ─────────────────────────────────────────────────────────────
sb.AppendLine("┌─ FILE I/O ────────────────────────────────────────────────────────");
sb.AppendLine($"│ File opens:   {fileOpens:N0}");
sb.AppendLine($"│ File reads:   {fileReads:N0}  ({fileBytesRead / (1024.0 * 1024 * 1024):F2} GB)");
sb.AppendLine($"│ File writes:  {fileWrites:N0}  ({fileBytesWritten / (1024.0 * 1024):F1} MB)");
if (traceDurationSec > 0)
{
    sb.AppendLine($"│ Opens/sec:    {fileOpens / traceDurationSec:F0}");
    sb.AppendLine($"│ Reads/sec:    {fileReads / traceDurationSec:F0}");
    sb.AppendLine($"│ Read MB/sec:  {fileBytesRead / (1024.0 * 1024) / traceDurationSec:F1}");
}

if (topFilesByReads.Count > 0)
{
    sb.AppendLine("│");
    sb.AppendLine("│ Top files by read count:");
    var topReads = topFilesByReads.OrderByDescending(kv => kv.Value).Take(20);
    foreach (var (file, count) in topReads)
        sb.AppendLine($"│   {count,8:N0} reads  {file}");
}
if (topFilesByBytes.Count > 0)
{
    sb.AppendLine("│");
    sb.AppendLine("│ Top files by bytes read:");
    var topBytes = topFilesByBytes.OrderByDescending(kv => kv.Value).Take(20);
    foreach (var (file, bytes) in topBytes)
        sb.AppendLine($"│   {bytes / (1024.0 * 1024),8:F1} MB  {file}");
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── Disk I/O ─────────────────────────────────────────────────────────────
sb.AppendLine("┌─ DISK I/O ────────────────────────────────────────────────────────");
sb.AppendLine($"│ Disk reads:    {diskReads:N0}  ({diskBytesRead / (1024.0 * 1024 * 1024):F2} GB)");
sb.AppendLine($"│ Disk writes:   {diskWrites:N0}  ({diskBytesWritten / (1024.0 * 1024):F1} MB)");
if (traceDurationSec > 0)
{
    sb.AppendLine($"│ Reads/sec:     {diskReads / traceDurationSec:F0}");
    sb.AppendLine($"│ Read MB/sec:   {diskBytesRead / (1024.0 * 1024) / traceDurationSec:F1}");
}
if (diskReadLatencyMs.Count > 0)
{
    diskReadLatencyMs.Sort();
    sb.AppendLine($"│ Read latency ({diskReadLatencyMs.Count:N0} measured):");
    sb.AppendLine($"│   Mean:   {diskReadLatencyMs.Average():F3} ms");
    sb.AppendLine($"│   Median: {Percentile(diskReadLatencyMs, 50):F3} ms");
    sb.AppendLine($"│   P95:    {Percentile(diskReadLatencyMs, 95):F3} ms");
    sb.AppendLine($"│   P99:    {Percentile(diskReadLatencyMs, 99):F3} ms");
    sb.AppendLine($"│   Max:    {diskReadLatencyMs.Max():F3} ms");
}
if (diskWriteLatencyMs.Count > 0)
{
    diskWriteLatencyMs.Sort();
    sb.AppendLine($"│ Write latency ({diskWriteLatencyMs.Count:N0} measured):");
    sb.AppendLine($"│   Mean:   {diskWriteLatencyMs.Average():F3} ms");
    sb.AppendLine($"│   P95:    {Percentile(diskWriteLatencyMs, 95):F3} ms");
    sb.AppendLine($"│   Max:    {diskWriteLatencyMs.Max():F3} ms");
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── Exceptions ───────────────────────────────────────────────────────────
if (exceptionCounts.Count > 0)
{
    sb.AppendLine("┌─ EXCEPTIONS ──────────────────────────────────────────────────────");
    var sorted = exceptionCounts.OrderByDescending(kv => kv.Value);
    foreach (var (type, count) in sorted)
        sb.AppendLine($"│   {count,6:N0}  {type}");
    sb.AppendLine("└──────────────────────────────────────────────────────────────────");
    sb.AppendLine();
}

// ── Processes ────────────────────────────────────────────────────────────
if (processInfo.Count > 0)
{
    sb.AppendLine("┌─ PROCESSES ───────────────────────────────────────────────────────");
    var sorted = processInfo
        .OrderBy(p => p.Value.StartMs)
        .Take(30);
    sb.AppendLine("│   PID | Start (s) |   Duration | Name");
    sb.AppendLine("│  -----|-----------|------------|-----");
    foreach (var kv in sorted)
    {
        var (name, startMs, endMs) = kv.Value;
        double dur = endMs > startMs ? (endMs - startMs) / 1000.0 : 0;
        sb.AppendLine($"│ {kv.Key,5} | {startMs / 1000.0,9:F1} | {(dur > 0 ? $"{dur:F1}s" : "running"),10} | {name}");
    }
    sb.AppendLine("└──────────────────────────────────────────────────────────────────");
    sb.AppendLine();
}

// ── Performance Insights ─────────────────────────────────────────────────
sb.AppendLine("┌─ PERFORMANCE INSIGHTS ────────────────────────────────────────────");
int insightNum = 0;

var totalPauseMs = gcEvents.Where(g => g.PauseMs > 0).Sum(g => g.PauseMs);
if (totalPauseMs > traceDurationSec * 10)
{
    insightNum++;
    double pct = totalPauseMs / (traceDurationSec * 1000) * 100;
    sb.AppendLine($"│ {insightNum}. HIGH GC PRESSURE: {pct:F1}% of trace spent in GC pauses");
    sb.AppendLine($"│    → Enable Server GC (<ServerGarbageCollection>true</ServerGarbageCollection>)");
    sb.AppendLine($"│    → Reduce allocations of short-lived objects in hot paths");
}

if (gcEvents.Count(g => g.Generation == 2) > gcEvents.Count * 0.1 && gcEvents.Count > 10)
{
    insightNum++;
    sb.AppendLine($"│ {insightNum}. FREQUENT GEN 2 GC: {gcEvents.Count(g => g.Generation == 2)} of {gcEvents.Count} are Gen 2");
    sb.AppendLine($"│    → Objects surviving to Gen 2 cause expensive collections");
    sb.AppendLine($"│    → Pool/reuse large buffers; avoid promoting short-lived objects");
}

if (fileOpens > 0 && traceDurationSec > 0)
{
    double opensPerSec = fileOpens / traceDurationSec;
    insightNum++;
    sb.AppendLine($"│ {insightNum}. FILE I/O RATE: {opensPerSec:F0} opens/sec, {fileReads / traceDurationSec:F0} reads/sec");
    if (opensPerSec < 5000 && fileOpens > 1000)
        sb.AppendLine($"│    → Consider increasing I/O parallelism (more concurrent file opens)");
}

if (contentionEvents > 100 && contentionDurations.Count > 0)
{
    insightNum++;
    sb.AppendLine($"│ {insightNum}. LOCK CONTENTION: {contentionEvents:N0} events, total blocked {contentionDurations.Sum():F1}ms");
    if (contentionDurations.Max() > 10)
        sb.AppendLine($"│    → Max contention {contentionDurations.Max():F1}ms — check for hot locks");
}

if (insightNum == 0)
    sb.AppendLine("│ No major issues detected from available events.");

sb.AppendLine("└──────────────────────────────────────────────────────────────────");

// ── Write output ─────────────────────────────────────────────────────────

string report = sb.ToString();
Console.WriteLine();
Console.WriteLine(report);

string outFile = Path.Combine(outDir, "etl-analysis.txt");
File.WriteAllText(outFile, report, Encoding.UTF8);
Console.WriteLine($"Report saved to: {outFile}");
Console.WriteLine($"Total time: {sw.Elapsed.TotalSeconds:F1}s");

// ── Also write GC events CSV ─────────────────────────────────────────────
if (gcEvents.Count > 0)
{
    string gcCsv = Path.Combine(outDir, "gc-events.csv");
    using var csv = new StreamWriter(gcCsv, false, Encoding.UTF8);
    csv.WriteLine("TimeMs,Generation,Reason,Type,PauseMs,TotalHeapMB,Gen0MB,Gen1MB,Gen2MB,LOHMB,POHMB");
    foreach (var g in gcEvents)
    {
        csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F2},{1},{2},{3},{4:F3},{5:F1},{6:F1},{7:F1},{8:F1},{9:F1},{10:F1}",
            g.TimeMs, g.Generation, g.Reason, g.Type, g.PauseMs,
            g.TotalHeapBytes / (1024.0 * 1024),
            g.Gen0SizeBytes / (1024.0 * 1024),
            g.Gen1SizeBytes / (1024.0 * 1024),
            g.Gen2SizeBytes / (1024.0 * 1024),
            g.LohSizeBytes / (1024.0 * 1024),
            g.PohSizeBytes / (1024.0 * 1024)));
    }
    Console.WriteLine($"GC events CSV: {gcCsv}");
}

// ── Write file I/O breakdown CSV ─────────────────────────────────────────
if (topFilesByReads.Count > 0)
{
    string ioCsv = Path.Combine(outDir, "fileio-top-reads.csv");
    using var csv = new StreamWriter(ioCsv, false, Encoding.UTF8);
    csv.WriteLine("Reads,BytesRead,File");
    var joined = topFilesByReads
        .OrderByDescending(kv => kv.Value)
        .Take(500);
    foreach (var (file, reads) in joined)
    {
        topFilesByBytes.TryGetValue(file, out long bytes);
        csv.WriteLine($"{reads},{bytes},{file}");
    }
    Console.WriteLine($"File I/O CSV:   {ioCsv}");
}

return 0;

// ─── Helpers ─────────────────────────────────────────────────────────────

static double Percentile(List<double> sorted, double p)
{
    if (sorted.Count == 0) return 0;
    double idx = (p / 100.0) * (sorted.Count - 1);
    int lo = (int)Math.Floor(idx);
    int hi = (int)Math.Ceiling(idx);
    if (lo == hi) return sorted[lo];
    return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
}

// ─── Records ─────────────────────────────────────────────────────────────

struct GcRecord
{
    public double TimeMs;
    public int Generation;
    public string Reason;
    public string Type;
    public bool IsBackground;
    public double SuspendStartMs;
    public double PauseMs;
    public long Gen0SizeBytes;
    public long Gen1SizeBytes;
    public long Gen2SizeBytes;
    public long LohSizeBytes;
    public long PohSizeBytes;
    public long TotalHeapBytes;
}
