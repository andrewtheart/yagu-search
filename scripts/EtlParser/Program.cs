// ETL Parser — extracts key performance data from VS Diagnostics Hub ETL files.
// Usage: dotnet run -- <path-to-etl> [--out <output-dir>] [--process <name>]
//
// Produces a summary covering:
//   - CPU sampling (hot modules, hot methods, Rust symbol demangling)
//   - CPU timeline by module (10s buckets — shows native vs managed activity over time)
//   - GC events (collections, pause times, heap sizes)
//   - ThreadPool events (worker thread count, throughput, starvation)
//   - File I/O events (opens, reads, total bytes, latency)
//   - Disk I/O events (read/write, latency)
//   - Process lifetime (start/end, duration)
//   - JIT compilation (method compile times, R2R vs JIT)
//   - Assembly loading (load order, timing)
//   - Context switches (wait reasons, thread scheduling latency)
//   - Thread lifecycle (creation/destruction rate, distinct threads)
//   - Native image/DLL loads (load order, sizes, native vs managed CPU split)
//   - Page faults (hard faults with latency — important for mmap'd I/O)
//   - VirtualAlloc (native heap growth, size histogram — Rust allocator patterns)
//   - DPC/interrupt overhead (system-level latency sources)

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

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

// File I/O — enhanced per-file tracking
// Per-file record: (openCount, readCount, totalBytesRead, firstOpenTimeMs, firstReadTimeMs)
var perFileStats = new Dictionary<string, FileStats>(StringComparer.OrdinalIgnoreCase);
// File I/O timeline (10s buckets): (opens, reads, bytesRead)
var fileIoTimeline = new Dictionary<int, (long opens, long reads, long bytesRead)>();
// Opens by file extension
var opensByExtension = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
// File open timestamps for open-to-first-read gap calculation
var fileOpenTimestamps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
// Open-to-first-read latencies (ms)
var openToFirstReadMs = new List<double>();

// Disk I/O
var diskReads = 0L;
var diskBytesRead = 0L;
var diskWrites = 0L;
var diskBytesWritten = 0L;
var diskReadLatencyMs = new List<double>();
var diskWriteLatencyMs = new List<double>();
// Disk I/O timeline (10s buckets)
var diskIoTimeline = new Dictionary<int, (long reads, long bytesRead, long writes, long bytesWritten)>();

// Process info
var processInfo = new Dictionary<int, (string Name, double StartMs, double EndMs)>();
double traceEndMs = 0;

// Contention
var contentionEvents = 0L;
var contentionDurations = new List<double>();

// Exception
var exceptionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

// JIT Compilation
var jitEvents = new List<JitRecord>();
var jitMethodStarts = new Dictionary<long, (double TimeMs, string MethodName)>(); // keyed by MethodID

// Assembly Loading
var assemblyLoads = new List<AssemblyLoadRecord>();

// Context Switches
long contextSwitchCount = 0;
var contextSwitchWaitMs = new List<double>();

// Context switch wait-reason analysis
var waitReasonCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
var waitTimeMsByThread = new Dictionary<int, List<double>>(); // ThreadID → list of wait durations

// Thread lifecycle
var threadStarts = new List<(double TimeMs, int ThreadID, int ProcessID)>();
var threadStops = new List<(double TimeMs, int ThreadID, int ProcessID)>();

// CPU timeline by module (10s buckets)
var cpuTimelineByModule = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
// module → { bucket_index → sample_count }

// Heap size over time (from GCHeapStats)
var heapTimeline = new List<(double TimeMs, long TotalHeapBytes, long Gen0, long Gen1, long Gen2, long Loh, long Poh)>();

// Native image/DLL loads
var imageLoads = new List<ImageLoadRecord>();

// Page faults (hard faults — backed by disk I/O)
long hardFaultCount = 0;
var hardFaultLatencyMs = new List<double>();
var hardFaultsByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

// VirtualAlloc — native memory allocations (Rust allocator, mmap, etc.)
long virtualAllocCount = 0;
long virtualAllocTotalBytes = 0;
long virtualFreeCount = 0;
long virtualFreeTotalBytes = 0;
var virtualAllocTimeline = new List<(double TimeMs, long Bytes, bool IsAlloc)>();

// DPC (Deferred Procedure Calls) — interrupt overhead
long dpcCount = 0;
var dpcLatencyMs = new List<double>();

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

    // ── JIT Compilation ──────────────────────────────────────────────────
    clrParser.MethodJittingStarted += e =>
    {
        if (!MatchProcess(e)) return;
        jitMethodStarts[e.MethodID] = (e.TimeStampRelativeMSec,
            $"{e.MethodNamespace}.{e.MethodName}");
    };

    clrParser.MethodLoadVerbose += e =>
    {
        if (!MatchProcess(e)) return;
        string methodName = $"{e.MethodNamespace}.{e.MethodName}";
        double jitMs = 0;
        if (jitMethodStarts.Remove(e.MethodID, out var startInfo))
        {
            jitMs = e.TimeStampRelativeMSec - startInfo.TimeMs;
            methodName = startInfo.MethodName; // prefer the name from JittingStarted
        }

        jitEvents.Add(new JitRecord
        {
            TimeMs = e.TimeStampRelativeMSec,
            MethodName = methodName,
            JitMs = jitMs,
            IsR2R = !e.IsJitted,
            NativeSize = e.MethodSize,
        });
    };

    // ── Assembly Loading ─────────────────────────────────────────────────
    clrParser.LoaderAssemblyLoad += e =>
    {
        if (!MatchProcess(e)) return;
        assemblyLoads.Add(new AssemblyLoadRecord
        {
            TimeMs = e.TimeStampRelativeMSec,
            AssemblyName = e.FullyQualifiedAssemblyName ?? "?",
        });
    };

    // ── Context Switches (kernel) ────────────────────────────────────────
    kernelParser.ThreadCSwitch += e =>
    {
        // Count context switches where the new thread belongs to the target process
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref contextSwitchCount);

        // Track why the old thread yielded
        var reason = e.OldThreadWaitReason.ToString();
        lock (waitReasonCounts)
        {
            waitReasonCounts.TryGetValue(reason, out long cnt);
            waitReasonCounts[reason] = cnt + 1;
        }

        // Track how long the NEW thread waited before being scheduled (100ns units → ms)
        if (e.NewThreadWaitTime > 0)
        {
            double waitMs = e.NewThreadWaitTime / 10_000.0; // 100ns ticks to ms
            lock (waitTimeMsByThread)
            {
                if (!waitTimeMsByThread.TryGetValue(e.NewThreadID, out var list))
                {
                    list = new List<double>();
                    waitTimeMsByThread[e.NewThreadID] = list;
                }
                list.Add(waitMs);
            }
        }
    };

    // ── Thread Lifecycle (kernel) ────────────────────────────────────────
    kernelParser.ThreadStart += e =>
    {
        if (!MatchProcess(e)) return;
        lock (threadStarts)
            threadStarts.Add((e.TimeStampRelativeMSec, e.ThreadID, e.ProcessID));
    };

    kernelParser.ThreadStop += e =>
    {
        if (!MatchProcess(e)) return;
        lock (threadStops)
            threadStops.Add((e.TimeStampRelativeMSec, e.ThreadID, e.ProcessID));
    };

    // ── GC Heap Timeline ─────────────────────────────────────────────────
    // (also hooks GCHeapStats — we already update gcEvents above, also track timeline)
    clrParser.GCHeapStats += e =>
    {
        if (!MatchProcess(e)) return;
        heapTimeline.Add((e.TimeStampRelativeMSec,
            e.TotalHeapSize,
            e.GenerationSize0, e.GenerationSize1, e.GenerationSize2,
            e.GenerationSize3, e.GenerationSize4));
    };

    // ── Native Image/DLL Loads ───────────────────────────────────────────
    kernelParser.ImageLoad += e =>
    {
        if (!MatchProcess(e)) return;
        imageLoads.Add(new ImageLoadRecord
        {
            TimeMs = e.TimeStampRelativeMSec,
            FileName = e.FileName ?? "?",
            ImageBase = e.ImageBase,
            ImageSize = e.ImageSize,
            BuildTime = e.BuildTime,
        });
    };

    // ── Hard Page Faults ─────────────────────────────────────────────────
    kernelParser.MemoryHardFault += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref hardFaultCount);
        if (e.ElapsedTimeMSec > 0)
            lock (hardFaultLatencyMs) hardFaultLatencyMs.Add(e.ElapsedTimeMSec);
        var fn = e.FileName ?? "?";
        lock (hardFaultsByFile)
        {
            hardFaultsByFile.TryGetValue(fn, out long cnt);
            hardFaultsByFile[fn] = cnt + 1;
        }
    };

    // ── VirtualAlloc / VirtualFree ───────────────────────────────────────
    kernelParser.VirtualMemAlloc += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref virtualAllocCount);
        Interlocked.Add(ref virtualAllocTotalBytes, e.Length);
        lock (virtualAllocTimeline)
            virtualAllocTimeline.Add((e.TimeStampRelativeMSec, e.Length, true));
    };

    kernelParser.VirtualMemFree += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref virtualFreeCount);
        Interlocked.Add(ref virtualFreeTotalBytes, e.Length);
        lock (virtualAllocTimeline)
            virtualAllocTimeline.Add((e.TimeStampRelativeMSec, e.Length, false));
    };

    // ── DPC (interrupt) overhead ─────────────────────────────────────────
    kernelParser.PerfInfoDPC += e =>
    {
        Interlocked.Increment(ref dpcCount);
        if (e.ElapsedTimeMSec > 0)
            lock (dpcLatencyMs) dpcLatencyMs.Add(e.ElapsedTimeMSec);
    };

    // ── File I/O (kernel) ────────────────────────────────────────────────
    kernelParser.FileIOCreate += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref fileOpens);
        var fn = e.FileName ?? "?";

        // Timeline bucket
        int bucket = (int)(e.TimeStampRelativeMSec / 10_000);
        lock (fileIoTimeline)
        {
            fileIoTimeline.TryGetValue(bucket, out var cur);
            fileIoTimeline[bucket] = (cur.opens + 1, cur.reads, cur.bytesRead);
        }

        // Extension tracking
        var ext = Path.GetExtension(fn);
        if (string.IsNullOrEmpty(ext)) ext = "(no ext)";
        lock (opensByExtension)
        {
            opensByExtension.TryGetValue(ext, out long cnt);
            opensByExtension[ext] = cnt + 1;
        }

        // Per-file stats (first open time)
        lock (perFileStats)
        {
            if (!perFileStats.TryGetValue(fn, out var stats))
            {
                stats = new FileStats { FirstOpenTimeMs = e.TimeStampRelativeMSec };
                perFileStats[fn] = stats;
            }
            stats.OpenCount++;
        }

        // Track open timestamp for open-to-first-read gap
        lock (fileOpenTimestamps)
        {
            fileOpenTimestamps[fn] = e.TimeStampRelativeMSec;
        }
    };

    kernelParser.FileIORead += e =>
    {
        if (!MatchProcess(e)) return;
        Interlocked.Increment(ref fileReads);
        Interlocked.Add(ref fileBytesRead, e.IoSize);
        var fn = e.FileName ?? "?";

        // Timeline bucket
        int bucket = (int)(e.TimeStampRelativeMSec / 10_000);
        lock (fileIoTimeline)
        {
            fileIoTimeline.TryGetValue(bucket, out var cur);
            fileIoTimeline[bucket] = (cur.opens, cur.reads + 1, cur.bytesRead + e.IoSize);
        }

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

        // Per-file stats
        lock (perFileStats)
        {
            if (!perFileStats.TryGetValue(fn, out var stats))
            {
                stats = new FileStats { FirstOpenTimeMs = e.TimeStampRelativeMSec };
                perFileStats[fn] = stats;
            }
            stats.ReadCount++;
            stats.TotalBytesRead += e.IoSize;
            if (stats.FirstReadTimeMs == 0)
                stats.FirstReadTimeMs = e.TimeStampRelativeMSec;
        }

        // Open-to-first-read gap
        lock (fileOpenTimestamps)
        {
            if (fileOpenTimestamps.Remove(fn, out double openTime))
            {
                double gap = e.TimeStampRelativeMSec - openTime;
                if (gap >= 0 && gap < 60_000) // sanity: < 60s
                    lock (openToFirstReadMs) openToFirstReadMs.Add(gap);
            }
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

        // Timeline bucket
        int bucket = (int)(e.TimeStampRelativeMSec / 10_000);
        lock (diskIoTimeline)
        {
            diskIoTimeline.TryGetValue(bucket, out var cur);
            diskIoTimeline[bucket] = (cur.reads + 1, cur.bytesRead + e.TransferSize, cur.writes, cur.bytesWritten);
        }
    };

    kernelParser.DiskIOWrite += e =>
    {
        Interlocked.Increment(ref diskWrites);
        Interlocked.Add(ref diskBytesWritten, e.TransferSize);
        if (e.ElapsedTimeMSec > 0)
            lock (diskWriteLatencyMs) diskWriteLatencyMs.Add(e.ElapsedTimeMSec);

        // Timeline bucket
        int bucket = (int)(e.TimeStampRelativeMSec / 10_000);
        lock (diskIoTimeline)
        {
            diskIoTimeline.TryGetValue(bucket, out var cur);
            diskIoTimeline[bucket] = (cur.reads, cur.bytesRead, cur.writes + 1, cur.bytesWritten + e.TransferSize);
        }
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

                    // Track CPU timeline: 10s buckets per module
                    int bucket = (int)(evt.TimeStampRelativeMSec / 10_000);
                    if (!cpuTimelineByModule.TryGetValue(moduleName, out var buckets))
                    {
                        buckets = new Dictionary<int, long>();
                        cpuTimelineByModule[moduleName] = buckets;
                    }
                    buckets.TryGetValue(bucket, out long bc);
                    buckets[bucket] = bc + 1;
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
        sb.AppendLine($"│ {count,9:N0} | {pct,8:F1}% | {DemangleRust(method)}");
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

// ── JIT Compilation ──────────────────────────────────────────────────────
sb.AppendLine("┌─ JIT COMPILATION ─────────────────────────────────────────────────");
var jittedMethods = jitEvents.Where(j => !j.IsR2R).ToList();
var r2rMethods = jitEvents.Where(j => j.IsR2R).ToList();
sb.AppendLine($"│ Methods JIT-compiled: {jittedMethods.Count:N0}");
sb.AppendLine($"│ Methods R2R (precompiled): {r2rMethods.Count:N0}");
if (jittedMethods.Count > 0)
{
    var jittedWithTime = jittedMethods.Where(j => j.JitMs > 0).ToList();
    double totalJitMs = jittedWithTime.Sum(j => j.JitMs);
    sb.AppendLine($"│ Total JIT time:     {totalJitMs:F1} ms ({totalJitMs / 1000.0:F2}s)");
    if (traceDurationSec > 0)
    {
        double jitPct = totalJitMs / (traceDurationSec * 1000) * 100;
        sb.AppendLine($"│ % of trace in JIT:  {jitPct:F1}%");
    }
    sb.AppendLine($"│ Total native size:  {jittedMethods.Sum(j => j.NativeSize) / 1024.0:F0} KB");

    if (jittedWithTime.Count > 0)
    {
        sb.AppendLine("│");
        sb.AppendLine("│ Slowest JIT compilations:");
        sb.AppendLine("│    JIT ms | Native B | Method");
        sb.AppendLine("│ ---------|----------|-------");
        var slowest = jittedWithTime.OrderByDescending(j => j.JitMs).Take(30);
        foreach (var j in slowest)
            sb.AppendLine($"│  {j.JitMs,7:F2} |   {j.NativeSize,6} | {j.MethodName}");
    }

    if (jittedMethods.Count > 20)
    {
        sb.AppendLine("│");
        sb.AppendLine("│ JIT activity over time (5s buckets):");
        var jitBuckets = jittedMethods
            .GroupBy(j => (int)(j.TimeMs / 5_000))
            .OrderBy(g => g.Key)
            .Take(30);
        foreach (var b in jitBuckets)
        {
            double tSec = b.Key * 5;
            double bucketJitMs = b.Where(j => j.JitMs > 0).Sum(j => j.JitMs);
            sb.AppendLine($"│   t={tSec,7:F0}s: {b.Count(),4} methods JIT'd, {bucketJitMs:F1}ms total JIT time");
        }
    }
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── Assembly Loading ─────────────────────────────────────────────────────
if (assemblyLoads.Count > 0)
{
    sb.AppendLine("┌─ ASSEMBLY LOADING ────────────────────────────────────────────────");
    sb.AppendLine($"│ Assemblies loaded: {assemblyLoads.Count:N0}");
    sb.AppendLine("│");
    sb.AppendLine("│ Load order:");
    sb.AppendLine("│    Time (s) | Assembly");
    sb.AppendLine("│ ------------|--------");
    foreach (var asm in assemblyLoads.OrderBy(a => a.TimeMs))
    {
        // Trim to just the assembly name for readability
        string shortName = asm.AssemblyName;
        int commaIdx = shortName.IndexOf(',');
        if (commaIdx > 0) shortName = shortName[..commaIdx];
        sb.AppendLine($"│   {asm.TimeMs / 1000.0,9:F3} | {shortName}");
    }
    sb.AppendLine("└──────────────────────────────────────────────────────────────────");
    sb.AppendLine();
}

// ── Context Switches ─────────────────────────────────────────────────────
sb.AppendLine("┌─ CONTEXT SWITCHES ────────────────────────────────────────────────");
sb.AppendLine($"│ Context switches: {contextSwitchCount:N0}");
if (traceDurationSec > 0 && contextSwitchCount > 0)
    sb.AppendLine($"│ Switches/sec:    {contextSwitchCount / traceDurationSec:F0}");
if (waitReasonCounts.Count > 0)
{
    sb.AppendLine("│");
    sb.AppendLine("│ Wait reasons (why the OLD thread yielded):");
    sb.AppendLine("│   Count | Reason");
    sb.AppendLine("│ --------|-------");
    foreach (var (reason, count) in waitReasonCounts.OrderByDescending(kv => kv.Value).Take(15))
        sb.AppendLine($"│ {count,7:N0} | {reason}");
}
if (waitTimeMsByThread.Count > 0)
{
    // Aggregate: total wait time across all context switches
    var allWaitTimes = waitTimeMsByThread.Values.SelectMany(v => v).ToList();
    allWaitTimes.Sort();
    sb.AppendLine("│");
    sb.AppendLine($"│ Thread wait time before being scheduled ({allWaitTimes.Count:N0} measured):");
    sb.AppendLine($"│   Mean:    {allWaitTimes.Average():F3} ms");
    sb.AppendLine($"│   Median:  {Percentile(allWaitTimes, 50):F3} ms");
    sb.AppendLine($"│   P95:     {Percentile(allWaitTimes, 95):F3} ms");
    sb.AppendLine($"│   P99:     {Percentile(allWaitTimes, 99):F3} ms");
    sb.AppendLine($"│   Max:     {allWaitTimes.Max():F3} ms");
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── Thread Lifecycle ─────────────────────────────────────────────────────
if (threadStarts.Count > 0 || threadStops.Count > 0)
{
    sb.AppendLine("┌─ THREAD LIFECYCLE ────────────────────────────────────────────────");
    sb.AppendLine($"│ Threads created: {threadStarts.Count:N0}");
    sb.AppendLine($"│ Threads exited:  {threadStops.Count:N0}");
    if (traceDurationSec > 0 && threadStarts.Count > 0)
        sb.AppendLine($"│ Creates/sec:     {threadStarts.Count / traceDurationSec:F1}");

    // Thread creation over time (10s buckets)
    if (threadStarts.Count > 10)
    {
        sb.AppendLine("│");
        sb.AppendLine("│ Thread creation over time (10s buckets):");
        var tBuckets = threadStarts
            .GroupBy(t => (int)(t.TimeMs / 10_000))
            .OrderBy(g => g.Key)
            .Take(20);
        foreach (var b in tBuckets)
        {
            double tSec = b.Key * 10;
            sb.AppendLine($"│   t={tSec,7:F0}s: {b.Count(),4} threads created");
        }
    }

    // Peak concurrent threads (approximate from create/destroy timeline)
    var allThreadIds = new HashSet<int>(threadStarts.Select(t => t.ThreadID));
    sb.AppendLine($"│ Distinct thread IDs: {allThreadIds.Count}");
    sb.AppendLine("└──────────────────────────────────────────────────────────────────");
    sb.AppendLine();
}

// ── CPU Timeline by Module ───────────────────────────────────────────────
if (cpuTimelineByModule.Count > 0)
{
    // Pick top 5 modules by total CPU
    var topTimelineModules = cpuTimelineByModule
        .Select(kv => (Module: kv.Key, Total: kv.Value.Values.Sum(), Buckets: kv.Value))
        .OrderByDescending(x => x.Total)
        .Take(5)
        .ToList();

    if (topTimelineModules.Count > 0)
    {
        sb.AppendLine("┌─ CPU TIMELINE BY MODULE (10s buckets, top 5 modules) ────────────");

        // Determine bucket range
        int minBucket = topTimelineModules.SelectMany(m => m.Buckets.Keys).Min();
        int maxBucket = topTimelineModules.SelectMany(m => m.Buckets.Keys).Max();

        // Header
        sb.Append("│ Time (s)  ");
        foreach (var m in topTimelineModules)
        {
            string name = m.Module.Length > 12 ? m.Module[..12] : m.Module;
            sb.Append($"| {name,-12} ");
        }
        sb.AppendLine();

        sb.Append("│ ----------");
        foreach (var _ in topTimelineModules)
            sb.Append("|──────────────");
        sb.AppendLine();

        for (int b = minBucket; b <= maxBucket; b++)
        {
            double tSec = b * 10;
            sb.Append($"│ {tSec,8:F0}  ");
            foreach (var m in topTimelineModules)
            {
                m.Buckets.TryGetValue(b, out long cnt);
                string bar = cnt > 0 ? $"{cnt,6:N0}" : "     -";
                double pct = m.Total > 0 ? cnt * 100.0 / m.Total : 0;
                sb.Append($"| {bar} {pct,5:F0}% ");
            }
            sb.AppendLine();
        }
        sb.AppendLine("└──────────────────────────────────────────────────────────────────");
        sb.AppendLine();

        // ── CPU Utilization % (estimated from sample rate) ───────────────
        // ETW CPU sampling typically fires at 1kHz per logical core. 
        // samples_in_bucket / (cores * 10s * 1000 samples/sec/core) ≈ utilization
        int coreCount = Environment.ProcessorCount;
        long expectedSamplesPerBucket = (long)coreCount * 10 * 1000; // 10s bucket, 1kHz
        sb.AppendLine("┌─ CPU UTILIZATION % TIMELINE (10s buckets, process only) ──────────");
        sb.AppendLine($"│ (Estimated from {coreCount} cores × 1kHz sample rate = {expectedSamplesPerBucket:N0} max samples/bucket)");
        sb.AppendLine("│  Time (s) | Samples | Est. CPU% | ████ (bar)");
        sb.AppendLine("│ ----------|---------|-----------|----------");

        // Total samples per bucket across all modules in the process
        var totalSamplesPerBucket = new Dictionary<int, long>();
        foreach (var mod in cpuTimelineByModule.Values)
        {
            foreach (var (bk, cnt) in mod)
            {
                totalSamplesPerBucket.TryGetValue(bk, out long existing);
                totalSamplesPerBucket[bk] = existing + cnt;
            }
        }

        foreach (var bucket in totalSamplesPerBucket.OrderBy(kv => kv.Key))
        {
            double tSec = bucket.Key * 10;
            long samples = bucket.Value;
            // Note: inclusive stacks mean a sample appears in multiple modules.
            // Use cpuSamplesForProcess to calibrate: total inclusive / bucket count ≈ actual ratio
            // But simpler: just divide by expected max. Cap at 100%.
            double utilPct = Math.Min(100.0, samples * 100.0 / expectedSamplesPerBucket);
            int barLen = (int)(utilPct / 2.5); // 40 chars = 100%
            string bar = new string('█', barLen);
            sb.AppendLine($"│ {tSec,8:F0}  | {samples,7:N0} | {utilPct,7:F1}%  | {bar}");
        }
        sb.AppendLine("│");
        sb.AppendLine("│ Note: Inclusive stacks inflate per-bucket totals (a single sample appears");
        sb.AppendLine("│ in every module on its stack). Treat as relative activity, not absolute %.");
        sb.AppendLine("└──────────────────────────────────────────────────────────────────");
        sb.AppendLine();
    }
}

// ── Native Image / DLL Loads ─────────────────────────────────────────────
if (imageLoads.Count > 0)
{
    sb.AppendLine("┌─ NATIVE IMAGE / DLL LOADS ────────────────────────────────────────");
    sb.AppendLine($"│ Images loaded: {imageLoads.Count:N0}");
    long totalImageSize = imageLoads.Sum(i => (long)i.ImageSize);
    sb.AppendLine($"│ Total image size: {totalImageSize / (1024.0 * 1024):F1} MB");
    sb.AppendLine("│");
    sb.AppendLine("│ Load order (by time):");
    sb.AppendLine("│    Time (s) |    Size KB | Image");
    sb.AppendLine("│ ------------|-----------|------");
    foreach (var img in imageLoads.OrderBy(i => i.TimeMs))
    {
        string shortName = Path.GetFileName(img.FileName);
        sb.AppendLine($"│   {img.TimeMs / 1000.0,9:F3} | {img.ImageSize / 1024.0,9:F0} | {shortName}");
    }
    sb.AppendLine("│");
    sb.AppendLine("│ Largest images:");
    sb.AppendLine("│    Size MB | Image");
    sb.AppendLine("│ ----------|------");
    var largestImages = imageLoads.OrderByDescending(i => i.ImageSize).Take(15);
    foreach (var img in largestImages)
    {
        string shortName = Path.GetFileName(img.FileName);
        sb.AppendLine($"│  {img.ImageSize / (1024.0 * 1024),8:F2} | {shortName}");
    }

    // Flag native vs managed modules in CPU
    if (cpuByModule.Count > 0)
    {
        // Build a set of native-only image names (loaded via ImageLoad but not through CLR)
        var loadedImageNames = new HashSet<string>(
            imageLoads.Select(i => Path.GetFileNameWithoutExtension(i.FileName)),
            StringComparer.OrdinalIgnoreCase);
        var managedNames = new HashSet<string>(
            assemblyLoads.Select(a =>
            {
                string name = a.AssemblyName;
                int commaIdx = name.IndexOf(',');
                return commaIdx > 0 ? name[..commaIdx] : name;
            }),
            StringComparer.OrdinalIgnoreCase);

        var nativeCpuModules = cpuByModule
            .Where(kv => loadedImageNames.Contains(kv.Key) && !managedNames.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .ToList();

        if (nativeCpuModules.Count > 0)
        {
            long nativeSamples = nativeCpuModules.Sum(kv => kv.Value);
            sb.AppendLine("│");
            sb.AppendLine("│ CPU in native (non-CLR) modules:");
            sb.AppendLine("│   Samples | % of proc | Module");
            sb.AppendLine("│ ----------|-----------|-------");
            foreach (var (mod, count) in nativeCpuModules)
            {
                double pct = cpuSamplesForProcess > 0 ? count * 100.0 / cpuSamplesForProcess : 0;
                sb.AppendLine($"│ {count,9:N0} | {pct,8:F1}% | {mod}");
            }
            double nativePct = cpuSamplesForProcess > 0 ? nativeSamples * 100.0 / cpuSamplesForProcess : 0;
            sb.AppendLine($"│           ─ Total native: {nativeSamples:N0} ({nativePct:F1}% of process CPU)");
        }
    }

    sb.AppendLine("└──────────────────────────────────────────────────────────────────");
    sb.AppendLine();
}

// ── Hard Page Faults ─────────────────────────────────────────────────────
sb.AppendLine("┌─ HARD PAGE FAULTS ────────────────────────────────────────────────");
sb.AppendLine($"│ Hard faults:  {hardFaultCount:N0}");
if (traceDurationSec > 0 && hardFaultCount > 0)
    sb.AppendLine($"│ Faults/sec:   {hardFaultCount / traceDurationSec:F0}");
if (hardFaultLatencyMs.Count > 0)
{
    hardFaultLatencyMs.Sort();
    sb.AppendLine($"│ Fault latency ({hardFaultLatencyMs.Count:N0} measured):");
    sb.AppendLine($"│   Mean:   {hardFaultLatencyMs.Average():F3} ms");
    sb.AppendLine($"│   Median: {Percentile(hardFaultLatencyMs, 50):F3} ms");
    sb.AppendLine($"│   P95:    {Percentile(hardFaultLatencyMs, 95):F3} ms");
    sb.AppendLine($"│   P99:    {Percentile(hardFaultLatencyMs, 99):F3} ms");
    sb.AppendLine($"│   Max:    {hardFaultLatencyMs.Max():F3} ms");
    sb.AppendLine($"│   Total:  {hardFaultLatencyMs.Sum():F1} ms");
}
if (hardFaultsByFile.Count > 0)
{
    sb.AppendLine("│");
    sb.AppendLine("│ Top faulted files:");
    var topFaults = hardFaultsByFile.OrderByDescending(kv => kv.Value).Take(20);
    foreach (var (file, count) in topFaults)
        sb.AppendLine($"│   {count,8:N0}  {file}");
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── Virtual Memory (native allocator) ────────────────────────────────────
sb.AppendLine("┌─ VIRTUAL MEMORY (native allocator) ───────────────────────────────");
sb.AppendLine($"│ VirtualAlloc calls: {virtualAllocCount:N0}  ({virtualAllocTotalBytes / (1024.0 * 1024):F1} MB total)");
sb.AppendLine($"│ VirtualFree calls:  {virtualFreeCount:N0}  ({virtualFreeTotalBytes / (1024.0 * 1024):F1} MB total)");
long netVirtual = virtualAllocTotalBytes - virtualFreeTotalBytes;
if (virtualAllocCount > 0)
    sb.AppendLine($"│ Net allocated:      {netVirtual / (1024.0 * 1024):F1} MB");
if (traceDurationSec > 0 && virtualAllocCount > 0)
    sb.AppendLine($"│ Alloc rate:         {virtualAllocCount / traceDurationSec:F0}/sec");
if (virtualAllocTimeline.Count > 20)
{
    sb.AppendLine("│");
    sb.AppendLine("│ VirtualAlloc over time (10s buckets):");
    var allocBuckets = virtualAllocTimeline
        .Where(v => v.IsAlloc)
        .GroupBy(v => (int)(v.TimeMs / 10_000))
        .OrderBy(g => g.Key)
        .Take(20);
    foreach (var b in allocBuckets)
    {
        double tSec = b.Key * 10;
        long bucketBytes = b.Sum(v => v.Bytes);
        sb.AppendLine($"│   t={tSec,7:F0}s: {b.Count(),5} allocs, {bucketBytes / (1024.0 * 1024):F1} MB");
    }
}
// Size distribution histogram
var allocSizes = virtualAllocTimeline.Where(v => v.IsAlloc).Select(v => v.Bytes).ToList();
if (allocSizes.Count > 10)
{
    sb.AppendLine("│");
    sb.AppendLine("│ Allocation size distribution:");
    var sizeRanges = new (string Label, long Min, long Max)[]
    {
        ("    < 4 KB", 0, 4 * 1024),
        ("  4-64 KB", 4 * 1024, 64 * 1024),
        ("64 KB-1 MB", 64 * 1024, 1024 * 1024),
        ("  1-16 MB", 1024 * 1024, 16L * 1024 * 1024),
        ("16-256 MB", 16L * 1024 * 1024, 256L * 1024 * 1024),
        ("   > 256 MB", 256L * 1024 * 1024, long.MaxValue),
    };
    sb.AppendLine("│     Size range |   Count |         Bytes | % count");
    sb.AppendLine("│ ---------------|---------|---------------|--------");
    foreach (var (label, min, max) in sizeRanges)
    {
        var inRange = allocSizes.Where(s => s >= min && s < max).ToList();
        if (inRange.Count > 0)
        {
            double pct = inRange.Count * 100.0 / allocSizes.Count;
            long totalBytes = inRange.Sum();
            sb.AppendLine($"│ {label,14} | {inRange.Count,7:N0} | {totalBytes / (1024.0 * 1024),10:F1} MB | {pct,5:F1}%");
        }
    }
}
sb.AppendLine("└──────────────────────────────────────────────────────────────────");
sb.AppendLine();

// ── DPC / Interrupt Overhead ─────────────────────────────────────────────
sb.AppendLine("┌─ DPC / INTERRUPT OVERHEAD ────────────────────────────────────────");
sb.AppendLine($"│ DPC events:  {dpcCount:N0}");
if (traceDurationSec > 0 && dpcCount > 0)
    sb.AppendLine($"│ DPCs/sec:    {dpcCount / traceDurationSec:F0}");
if (dpcLatencyMs.Count > 0)
{
    dpcLatencyMs.Sort();
    double totalDpcMs = dpcLatencyMs.Sum();
    sb.AppendLine($"│ DPC duration:");
    sb.AppendLine($"│   Mean:   {dpcLatencyMs.Average():F4} ms");
    sb.AppendLine($"│   P95:    {Percentile(dpcLatencyMs, 95):F4} ms");
    sb.AppendLine($"│   P99:    {Percentile(dpcLatencyMs, 99):F4} ms");
    sb.AppendLine($"│   Max:    {dpcLatencyMs.Max():F4} ms");
    sb.AppendLine($"│   Total:  {totalDpcMs:F1} ms");
    if (traceDurationSec > 0)
    {
        double dpcPct = totalDpcMs / (traceDurationSec * 1000) * 100;
        sb.AppendLine($"│   % time: {dpcPct:F2}% of trace in DPC");
    }
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
if (fileOpens > 0 && fileReads > 0)
{
    double avgBytesPerOpen = (double)fileBytesRead / fileOpens;
    double readsPerOpen = (double)fileReads / fileOpens;
    sb.AppendLine($"│ Avg bytes/open: {avgBytesPerOpen / 1024:F1} KB");
    sb.AppendLine($"│ Reads/open:     {readsPerOpen:F2}");
}
sb.AppendLine("│");

// ── File I/O Timeline ────────────────────────────────────────────────
if (fileIoTimeline.Count > 0)
{
    sb.AppendLine("│ ┌─ FILE I/O TIMELINE (10s buckets) ─────────────────────────────");
    sb.AppendLine("│ │  Time (s) |   Opens | Opens/s |  Reads |  Read MB | MB/s");
    sb.AppendLine("│ │ ----------|---------|---------|--------|----------|-----");
    foreach (var bucket in fileIoTimeline.OrderBy(kv => kv.Key))
    {
        double tSec = bucket.Key * 10;
        var (opens, reads, bytesRead) = bucket.Value;
        double mbRead = bytesRead / (1024.0 * 1024);
        sb.AppendLine($"│ │ {tSec,8:F0}  | {opens,7:N0} | {opens / 10.0,7:F0} | {reads,6:N0} | {mbRead,8:F1} | {mbRead / 10.0:F1}");
    }
    sb.AppendLine("│ └────────────────────────────────────────────────────────────");
    sb.AppendLine("│");
}

// ── File Size Distribution (by bytes read per file) ──────────────────
if (perFileStats.Count > 0)
{
    sb.AppendLine("│ ┌─ FILE SIZE DISTRIBUTION (by total bytes read per file) ───────");
    var fileSizeRanges = new (string Label, long Min, long Max)[]
    {
        ("       0 B (open-only)", 0, 1),
        ("    1 B - 4 KB", 1, 4 * 1024),
        ("  4 KB - 8 KB", 4 * 1024, 8 * 1024),
        (" 8 KB - 64 KB", 8 * 1024, 64 * 1024),
        ("64 KB - 1 MB", 64 * 1024, 1024 * 1024),
        (" 1 MB - 10 MB", 1024 * 1024, 10L * 1024 * 1024),
        ("     > 10 MB", 10L * 1024 * 1024, long.MaxValue),
    };
    sb.AppendLine("│ │     Size range | Files |   % | Total bytes |  % bytes");
    sb.AppendLine("│ │ ---------------|-------|-----|-------------|--------");
    long totalFilesTracked = perFileStats.Count;
    long totalBytesTracked = perFileStats.Values.Sum(f => f.TotalBytesRead);
    foreach (var (label, min, max) in fileSizeRanges)
    {
        var inRange = perFileStats.Values
            .Where(f => f.TotalBytesRead >= min && f.TotalBytesRead < max)
            .ToList();
        if (inRange.Count > 0)
        {
            double pctFiles = inRange.Count * 100.0 / totalFilesTracked;
            long rangeBytes = inRange.Sum(f => f.TotalBytesRead);
            double pctBytes = totalBytesTracked > 0 ? rangeBytes * 100.0 / totalBytesTracked : 0;
            sb.AppendLine($"│ │ {label,14} | {inRange.Count,5:N0} | {pctFiles,3:F0}% | {rangeBytes / (1024.0 * 1024),8:F1} MB | {pctBytes,5:F1}%");
        }
    }
    sb.AppendLine("│ └────────────────────────────────────────────────────────────");
    sb.AppendLine("│");
}

// ── Wasted Opens (binary probe rejects: open + ≤1 read of ≤8KB) ─────
if (perFileStats.Count > 0)
{
    var wastedOpens = perFileStats.Values
        .Where(f => f.ReadCount == 0 || (f.ReadCount == 1 && f.TotalBytesRead <= 8192))
        .ToList();
    long totalOpensTracked = perFileStats.Values.Sum(f => f.OpenCount);
    long wastedOpenCount = wastedOpens.Sum(f => f.OpenCount);
    if (wastedOpenCount > 0)
    {
        double wastedPct = wastedOpenCount * 100.0 / totalOpensTracked;
        long noReadFiles = perFileStats.Values.Count(f => f.ReadCount == 0);
        long probeOnlyFiles = perFileStats.Values.Count(f => f.ReadCount == 1 && f.TotalBytesRead <= 8192);
        sb.AppendLine("│ ┌─ WASTED OPENS (open cost paid, no useful scan) ───────────────");
        sb.AppendLine($"│ │ Total wasted opens: {wastedOpenCount:N0} of {totalOpensTracked:N0} ({wastedPct:F1}%)");
        sb.AppendLine($"│ │   Files with 0 reads (open-only):    {noReadFiles:N0}");
        sb.AppendLine($"│ │   Files with 1 read ≤8KB (probe):    {probeOnlyFiles:N0}");
        sb.AppendLine($"│ │ Each wasted open pays full filter-stack traversal cost.");
        sb.AppendLine("│ └────────────────────────────────────────────────────────────");
        sb.AppendLine("│");
    }
}

// ── Open-to-First-Read Latency ───────────────────────────────────────
if (openToFirstReadMs.Count > 10)
{
    openToFirstReadMs.Sort();
    sb.AppendLine("│ ┌─ OPEN-TO-FIRST-READ GAP ─────────────────────────────────────");
    sb.AppendLine($"│ │ Measured gaps: {openToFirstReadMs.Count:N0}");
    sb.AppendLine($"│ │   Mean:   {openToFirstReadMs.Average():F3} ms");
    sb.AppendLine($"│ │   Median: {Percentile(openToFirstReadMs, 50):F3} ms");
    sb.AppendLine($"│ │   P95:    {Percentile(openToFirstReadMs, 95):F3} ms");
    sb.AppendLine($"│ │   P99:    {Percentile(openToFirstReadMs, 99):F3} ms");
    sb.AppendLine($"│ │   Max:    {openToFirstReadMs.Max():F3} ms");
    sb.AppendLine($"│ │ (Time between FileIO/Create and first FileIO/Read on same file.)");
    sb.AppendLine($"│ │ (Large gaps indicate managed-side overhead between open and scan start.)");
    sb.AppendLine("│ └────────────────────────────────────────────────────────────");
    sb.AppendLine("│");
}

// ── Opens by Extension ───────────────────────────────────────────────
if (opensByExtension.Count > 0)
{
    sb.AppendLine("│ ┌─ OPENS BY FILE EXTENSION (top 25) ────────────────────────────");
    sb.AppendLine("│ │   Opens |   % | Extension");
    sb.AppendLine("│ │ --------|-----|----------");
    var topExts = opensByExtension.OrderByDescending(kv => kv.Value).Take(25);
    foreach (var (ext, count) in topExts)
    {
        double pct = fileOpens > 0 ? count * 100.0 / fileOpens : 0;
        sb.AppendLine($"│ │ {count,7:N0} | {pct,3:F0}% | {ext}");
    }
    sb.AppendLine("│ └────────────────────────────────────────────────────────────");
    sb.AppendLine("│");
}

if (topFilesByReads.Count > 0)
{
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
if (diskReads > 0)
{
    double avgDiskReadSize = (double)diskBytesRead / diskReads;
    sb.AppendLine($"│ Avg read size: {avgDiskReadSize / 1024:F1} KB");
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
    sb.AppendLine("│");
    // Latency histogram
    sb.AppendLine("│ Read latency histogram:");
    var latencyBuckets = new (string Label, double Min, double Max)[]
    {
        ("    < 0.01 ms", 0, 0.01),
        ("0.01-0.05 ms", 0.01, 0.05),
        ("0.05-0.1  ms", 0.05, 0.1),
        (" 0.1-0.5  ms", 0.1, 0.5),
        (" 0.5-1.0  ms", 0.5, 1.0),
        (" 1.0-5.0  ms", 1.0, 5.0),
        (" 5.0-20   ms", 5.0, 20.0),
        ("     > 20 ms", 20.0, double.MaxValue),
    };
    sb.AppendLine("│     Latency    |   Count |   % | Cumulative");
    sb.AppendLine("│ ---------------|---------|-----|----------");
    long cumulative = 0;
    foreach (var (label, min, max) in latencyBuckets)
    {
        var inRange = diskReadLatencyMs.Count(l => l >= min && l < max);
        if (inRange > 0)
        {
            cumulative += inRange;
            double pct = inRange * 100.0 / diskReadLatencyMs.Count;
            double cumPct = cumulative * 100.0 / diskReadLatencyMs.Count;
            sb.AppendLine($"│ {label,14} | {inRange,7:N0} | {pct,3:F0}% | {cumPct,5:F1}%");
        }
    }
}
if (diskWriteLatencyMs.Count > 0)
{
    diskWriteLatencyMs.Sort();
    sb.AppendLine($"│ Write latency ({diskWriteLatencyMs.Count:N0} measured):");
    sb.AppendLine($"│   Mean:   {diskWriteLatencyMs.Average():F3} ms");
    sb.AppendLine($"│   P95:    {Percentile(diskWriteLatencyMs, 95):F3} ms");
    sb.AppendLine($"│   Max:    {diskWriteLatencyMs.Max():F3} ms");
}
sb.AppendLine("│");

// ── Disk I/O Timeline ────────────────────────────────────────────────
if (diskIoTimeline.Count > 0)
{
    sb.AppendLine("│ ┌─ DISK I/O TIMELINE (10s buckets) ─────────────────────────────");
    sb.AppendLine("│ │  Time (s) |  Reads | Read MB |  MB/s | Writes | Write MB");
    sb.AppendLine("│ │ ----------|--------|---------|-------|--------|--------");
    foreach (var bucket in diskIoTimeline.OrderBy(kv => kv.Key))
    {
        double tSec = bucket.Key * 10;
        var (reads, bytesRead, writes, bytesWritten) = bucket.Value;
        double readMB = bytesRead / (1024.0 * 1024);
        double writeMB = bytesWritten / (1024.0 * 1024);
        sb.AppendLine($"│ │ {tSec,8:F0}  | {reads,6:N0} | {readMB,7:F1} | {readMB / 10.0,5:F1} | {writes,6:N0} | {writeMB,7:F1}");
    }
    sb.AppendLine("│ └────────────────────────────────────────────────────────────");
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

// JIT insights
var jittedWithTime2 = jitEvents.Where(j => !j.IsR2R && j.JitMs > 0).ToList();
double totalJitMs2 = jittedWithTime2.Sum(j => j.JitMs);
if (totalJitMs2 > 500)
{
    insightNum++;
    sb.AppendLine($"│ {insightNum}. HIGH JIT OVERHEAD: {totalJitMs2:F0}ms spent JIT-compiling {jittedWithTime2.Count:N0} methods");
    sb.AppendLine($"│    → Consider ReadyToRun (R2R) compilation: <PublishReadyToRun>true</PublishReadyToRun>");
    sb.AppendLine($"│    → Or enable tiered PGO: <TieredPGO>true</TieredPGO>");
    var first5s = jittedWithTime2.Where(j => j.TimeMs < 5_000).ToList();
    if (first5s.Count > 0)
    {
        double startupJitMs = first5s.Sum(j => j.JitMs);
        sb.AppendLine($"│    → {first5s.Count} methods ({startupJitMs:F0}ms) JIT'd in first 5s — impacts startup");
    }
}

if (jitEvents.Count(j => !j.IsR2R) > 0 && jitEvents.Count(j => j.IsR2R) == 0)
{
    insightNum++;
    sb.AppendLine($"│ {insightNum}. NO R2R METHODS: All {jitEvents.Count(j => !j.IsR2R)} methods are JIT-compiled");
    sb.AppendLine($"│    → Publish with ReadyToRun for faster startup");
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

// ThreadPool starvation detection
if (tpWorkerChanges.Count > 0)
{
    var maxThreads = tpWorkerChanges.Max(w => w.Active);
    if (maxThreads > 32)
    {
        insightNum++;
        sb.AppendLine($"│ {insightNum}. THREADPOOL GROWTH: Worker threads peaked at {maxThreads}");
        sb.AppendLine($"│    → May indicate thread starvation (blocking calls on TP threads)");
        sb.AppendLine($"│    → Audit sync-over-async patterns and blocking I/O on TP threads");
    }
}

// Context switch rate
if (contextSwitchCount > 0 && traceDurationSec > 0)
{
    double csPerSec = contextSwitchCount / traceDurationSec;
    if (csPerSec > 50_000)
    {
        insightNum++;
        sb.AppendLine($"│ {insightNum}. HIGH CONTEXT SWITCH RATE: {csPerSec:F0}/sec");
        sb.AppendLine($"│    → Excessive thread switching degrades cache locality");
        sb.AppendLine($"│    → Reduce thread count or consolidate work onto fewer threads");
    }
}

// Native-code focused insights
if (hardFaultCount > 100)
{
    insightNum++;
    sb.AppendLine($"│ {insightNum}. HARD PAGE FAULTS: {hardFaultCount:N0} faults");
    if (hardFaultLatencyMs.Count > 0)
        sb.AppendLine($"│    → Total fault latency: {hardFaultLatencyMs.Sum():F1}ms, max: {hardFaultLatencyMs.Max():F1}ms");
    sb.AppendLine($"│    → Hard faults require disk I/O — stalls native threads including Rust");
    sb.AppendLine($"│    → If file-backed: ensure working set fits in RAM; consider MAP_POPULATE");
    sb.AppendLine($"│    → If code: large native DLLs may fault in on first use");
}

// Rust DLL CPU dominance
if (cpuByModule.Count > 0 && cpuSamplesForProcess > 0)
{
    // Look for the yagu_core Rust module or any Rust-associated pattern
    var rustModules = cpuByModule
        .Where(kv => kv.Key.Contains("yagu_core", StringComparison.OrdinalIgnoreCase)
                   || kv.Key.Contains("yagu-core", StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (rustModules.Count > 0)
    {
        long rustSamples = rustModules.Sum(kv => kv.Value);
        double rustPct = rustSamples * 100.0 / cpuSamplesForProcess;
        insightNum++;
        sb.AppendLine($"│ {insightNum}. RUST CORE CPU: {rustPct:F1}% of process CPU in yagu_core ({rustSamples:N0} samples)");
        if (rustPct > 70)
            sb.AppendLine($"│    → Rust core dominates — optimize hot Rust functions for biggest gains");
        else if (rustPct < 30)
            sb.AppendLine($"│    → Rust core is NOT the bottleneck — check managed/C# overhead");
        // Check for Rust methods in the method breakdown
        var rustMethods = cpuByMethod
            .Where(kv => kv.Key.Contains("yagu_core", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .ToList();
        if (rustMethods.Count > 0)
        {
            sb.AppendLine($"│    → Top Rust functions:");
            foreach (var (method, count) in rustMethods)
            {
                double pct = count * 100.0 / cpuSamplesForProcess;
                sb.AppendLine($"│       {pct,5:F1}%  {DemangleRust(method)}");
            }
        }
    }
}

// VirtualAlloc churn
if (virtualAllocCount > 1000 && traceDurationSec > 0)
{
    double allocRate = virtualAllocCount / traceDurationSec;
    if (allocRate > 100)
    {
        insightNum++;
        sb.AppendLine($"│ {insightNum}. HIGH VIRTUAL ALLOC RATE: {allocRate:F0}/sec ({virtualAllocCount:N0} total, {virtualAllocTotalBytes / (1024.0 * 1024):F0} MB)");
        sb.AppendLine($"│    → Frequent VirtualAlloc can indicate Rust allocator churn");
        sb.AppendLine($"│    → Consider arena/bump allocation for short-lived objects");
        sb.AppendLine($"│    → Or use jemalloc/mimalloc via #[global_allocator]");
    }
}

// DPC overhead
if (dpcLatencyMs.Count > 0)
{
    double totalDpcMs2 = dpcLatencyMs.Sum();
    if (totalDpcMs2 > traceDurationSec * 10) // >1% of trace
    {
        insightNum++;
        double dpcPct = totalDpcMs2 / (traceDurationSec * 1000) * 100;
        sb.AppendLine($"│ {insightNum}. DPC OVERHEAD: {dpcPct:F1}% of trace in interrupt processing");
        sb.AppendLine($"│    → DPCs run at elevated priority, preempting all user threads");
        sb.AppendLine($"│    → High DPC can cause latency spikes in native file I/O");
    }
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

// ── Write heap timeline CSV ──────────────────────────────────────────────
if (heapTimeline.Count > 0)
{
    string heapCsv = Path.Combine(outDir, "heap-timeline.csv");
    using var csv = new StreamWriter(heapCsv, false, Encoding.UTF8);
    csv.WriteLine("TimeMs,TotalHeapMB,Gen0MB,Gen1MB,Gen2MB,LOHMB,POHMB");
    foreach (var h in heapTimeline.OrderBy(h => h.TimeMs))
    {
        csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F2},{1:F1},{2:F1},{3:F1},{4:F1},{5:F1},{6:F1}",
            h.TimeMs,
            h.TotalHeapBytes / (1024.0 * 1024),
            h.Gen0 / (1024.0 * 1024),
            h.Gen1 / (1024.0 * 1024),
            h.Gen2 / (1024.0 * 1024),
            h.Loh / (1024.0 * 1024),
            h.Poh / (1024.0 * 1024)));
    }
    Console.WriteLine($"Heap timeline:  {heapCsv}");
}

// ── Write JIT events CSV ─────────────────────────────────────────────────
if (jitEvents.Count > 0)
{
    string jitCsv = Path.Combine(outDir, "jit-events.csv");
    using var csv = new StreamWriter(jitCsv, false, Encoding.UTF8);
    csv.WriteLine("TimeMs,JitMs,NativeSize,IsR2R,Method");
    foreach (var j in jitEvents.OrderBy(j => j.TimeMs))
    {
        csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F2},{1:F3},{2},{3},{4}",
            j.TimeMs, j.JitMs, j.NativeSize,
            j.IsR2R ? "true" : "false",
            j.MethodName.Replace(",", ";")));
    }
    Console.WriteLine($"JIT events CSV: {jitCsv}");
}

// ── Write native image loads CSV ─────────────────────────────────────────
if (imageLoads.Count > 0)
{
    string imgCsv = Path.Combine(outDir, "native-image-loads.csv");
    using var csv = new StreamWriter(imgCsv, false, Encoding.UTF8);
    csv.WriteLine("TimeMs,ImageSizeKB,ImageBase,FileName");
    foreach (var img in imageLoads.OrderBy(i => i.TimeMs))
    {
        csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F2},{1:F1},0x{2:X},{3}",
            img.TimeMs, img.ImageSize / 1024.0, img.ImageBase,
            img.FileName.Replace(",", ";")));
    }
    Console.WriteLine($"Image loads CSV: {imgCsv}");
}

// ── Write VirtualAlloc timeline CSV ──────────────────────────────────────
if (virtualAllocTimeline.Count > 0)
{
    string vaCsv = Path.Combine(outDir, "virtualalloc-timeline.csv");
    using var csv = new StreamWriter(vaCsv, false, Encoding.UTF8);
    csv.WriteLine("TimeMs,Bytes,Type");
    foreach (var va in virtualAllocTimeline.OrderBy(v => v.TimeMs))
    {
        csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F2},{1},{2}",
            va.TimeMs, va.Bytes, va.IsAlloc ? "Alloc" : "Free"));
    }
    Console.WriteLine($"VirtualAlloc CSV: {vaCsv}");
}

// ── Write CPU timeline CSV ───────────────────────────────────────────────
if (cpuTimelineByModule.Count > 0)
{
    string cpuTlCsv = Path.Combine(outDir, "cpu-timeline-by-module.csv");
    using var csv = new StreamWriter(cpuTlCsv, false, Encoding.UTF8);

    // Get top 10 modules for columns
    var topModsForCsv = cpuTimelineByModule
        .OrderByDescending(kv => kv.Value.Values.Sum())
        .Take(10)
        .Select(kv => kv.Key)
        .ToList();

    csv.Write("TimeSec");
    foreach (var mod in topModsForCsv)
        csv.Write($",{mod}");
    csv.WriteLine();

    int csvMinBucket = cpuTimelineByModule.Values.SelectMany(d => d.Keys).Min();
    int csvMaxBucket = cpuTimelineByModule.Values.SelectMany(d => d.Keys).Max();
    for (int b = csvMinBucket; b <= csvMaxBucket; b++)
    {
        csv.Write(string.Format(CultureInfo.InvariantCulture, "{0:F0}", b * 10.0));
        foreach (var mod in topModsForCsv)
        {
            long cnt = 0;
            if (cpuTimelineByModule.TryGetValue(mod, out var buckets2))
                buckets2.TryGetValue(b, out cnt);
            csv.Write($",{cnt}");
        }
        csv.WriteLine();
    }
    Console.WriteLine($"CPU timeline CSV: {cpuTlCsv}");
}

// ── Write File I/O timeline CSV ──────────────────────────────────────────
if (fileIoTimeline.Count > 0)
{
    string fioTlCsv = Path.Combine(outDir, "fileio-timeline.csv");
    using var csv = new StreamWriter(fioTlCsv, false, Encoding.UTF8);
    csv.WriteLine("TimeSec,Opens,OpensPerSec,Reads,BytesRead,ReadMBPerSec");
    foreach (var bucket in fileIoTimeline.OrderBy(kv => kv.Key))
    {
        var (opens, reads, bytesRead) = bucket.Value;
        csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F0},{1},{2:F1},{3},{4},{5:F1}",
            bucket.Key * 10.0, opens, opens / 10.0, reads, bytesRead,
            bytesRead / (1024.0 * 1024) / 10.0));
    }
    Console.WriteLine($"File I/O timeline CSV: {fioTlCsv}");
}

// ── Write Disk I/O timeline CSV ──────────────────────────────────────────
if (diskIoTimeline.Count > 0)
{
    string dioTlCsv = Path.Combine(outDir, "diskio-timeline.csv");
    using var csv = new StreamWriter(dioTlCsv, false, Encoding.UTF8);
    csv.WriteLine("TimeSec,Reads,BytesRead,ReadMBPerSec,Writes,BytesWritten");
    foreach (var bucket in diskIoTimeline.OrderBy(kv => kv.Key))
    {
        var (reads, bytesRead, writes, bytesWritten) = bucket.Value;
        csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F0},{1},{2},{3:F1},{4},{5}",
            bucket.Key * 10.0, reads, bytesRead,
            bytesRead / (1024.0 * 1024) / 10.0, writes, bytesWritten));
    }
    Console.WriteLine($"Disk I/O timeline CSV: {dioTlCsv}");
}

// ── Write per-file stats CSV (top 500 by bytes read) ─────────────────────
if (perFileStats.Count > 0)
{
    string pfCsv = Path.Combine(outDir, "fileio-per-file-stats.csv");
    using var csv = new StreamWriter(pfCsv, false, Encoding.UTF8);
    csv.WriteLine("Opens,Reads,BytesRead,AvgBytesPerRead,FirstOpenMs,FirstReadMs,OpenToReadGapMs,File");
    var topPerFile = perFileStats
        .OrderByDescending(kv => kv.Value.TotalBytesRead)
        .Take(500);
    foreach (var (file, stats) in topPerFile)
    {
        double avgBpr = stats.ReadCount > 0 ? (double)stats.TotalBytesRead / stats.ReadCount : 0;
        double gap = stats.FirstReadTimeMs > 0 && stats.FirstOpenTimeMs > 0
            ? stats.FirstReadTimeMs - stats.FirstOpenTimeMs : -1;
        csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3:F0},{4:F2},{5:F2},{6:F3},{7}",
            stats.OpenCount, stats.ReadCount, stats.TotalBytesRead, avgBpr,
            stats.FirstOpenTimeMs, stats.FirstReadTimeMs, gap,
            file.Replace(",", ";")));
    }
    Console.WriteLine($"Per-file stats CSV: {pfCsv}");
}

// ── Write disk read latency histogram CSV ────────────────────────────────
if (diskReadLatencyMs.Count > 0)
{
    string latCsv = Path.Combine(outDir, "disk-read-latency-histogram.csv");
    using var csv = new StreamWriter(latCsv, false, Encoding.UTF8);
    csv.WriteLine("BucketMinMs,BucketMaxMs,Count,Percent,CumulativePercent");
    var histBuckets = new (double Min, double Max)[]
    {
        (0, 0.01), (0.01, 0.05), (0.05, 0.1), (0.1, 0.5),
        (0.5, 1.0), (1.0, 5.0), (5.0, 20.0), (20.0, 100.0), (100.0, double.MaxValue)
    };
    long cumCount = 0;
    foreach (var (min, max) in histBuckets)
    {
        int count = diskReadLatencyMs.Count(l => l >= min && l < max);
        if (count > 0)
        {
            cumCount += count;
            double pct = count * 100.0 / diskReadLatencyMs.Count;
            double cumPct = cumCount * 100.0 / diskReadLatencyMs.Count;
            csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3:F2},{4:F2}",
                min, max == double.MaxValue ? 9999 : max, count, pct, cumPct));
        }
    }
    Console.WriteLine($"Disk latency CSV: {latCsv}");
}

// ── Write summary JSON ───────────────────────────────────────────────────
{
    var jittedOnly = jitEvents.Where(j => !j.IsR2R).ToList();
    var jittedTimed = jittedOnly.Where(j => j.JitMs > 0).ToList();
    var summary = new
    {
        trace = new
        {
            source = Path.GetFileName(etlPath),
            durationSec = Math.Round(traceDurationSec, 1),
            totalEvents = 0L, // filled below
            processFilter,
        },
        cpu = new
        {
            totalSamples = totalCpuSamples,
            processSamples = cpuSamplesForProcess,
            topModules = cpuByModule.OrderByDescending(kv => kv.Value).Take(10)
                .Select(kv => new { module = kv.Key, samples = kv.Value }),
        },
        gc = new
        {
            totalCollections = gcEvents.Count,
            gen0 = gcEvents.Count(g => g.Generation == 0),
            gen1 = gcEvents.Count(g => g.Generation == 1),
            gen2 = gcEvents.Count(g => g.Generation == 2),
            totalPauseMs = Math.Round(gcEvents.Where(g => g.PauseMs > 0).Sum(g => g.PauseMs), 1),
            peakHeapMB = gcEvents.Count > 0
                ? Math.Round(gcEvents.Max(g => g.TotalHeapBytes) / (1024.0 * 1024), 1) : 0,
        },
        jit = new
        {
            methodsJitted = jittedOnly.Count,
            methodsR2R = jitEvents.Count(j => j.IsR2R),
            totalJitMs = Math.Round(jittedTimed.Sum(j => j.JitMs), 1),
            totalNativeSizeKB = Math.Round(jittedOnly.Sum(j => j.NativeSize) / 1024.0, 0),
        },
        threadPool = new
        {
            workItemsEnqueued = tpWorkItems,
            peakWorkerThreads = tpWorkerChanges.Count > 0 ? tpWorkerChanges.Max(w => w.Active) : 0,
        },
        contention = new
        {
            events = contentionEvents,
            totalBlockedMs = contentionDurations.Count > 0
                ? Math.Round(contentionDurations.Sum(), 1) : 0,
        },
        fileIO = new
        {
            opens = fileOpens,
            reads = fileReads,
            bytesReadMB = Math.Round(fileBytesRead / (1024.0 * 1024), 1),
            writes = fileWrites,
            bytesWrittenMB = Math.Round(fileBytesWritten / (1024.0 * 1024), 1),
            opensPerSec = traceDurationSec > 0 ? Math.Round(fileOpens / traceDurationSec, 1) : 0,
            readMBPerSec = traceDurationSec > 0
                ? Math.Round(fileBytesRead / (1024.0 * 1024) / traceDurationSec, 1) : 0,
            avgBytesPerOpen = fileOpens > 0 ? Math.Round((double)fileBytesRead / fileOpens, 0) : 0,
            filesTracked = perFileStats.Count,
            wastedOpens = perFileStats.Values.Count(f => f.ReadCount == 0 || (f.ReadCount == 1 && f.TotalBytesRead <= 8192)),
            wastedOpenPercent = perFileStats.Count > 0
                ? Math.Round(perFileStats.Values.Count(f => f.ReadCount == 0 || (f.ReadCount == 1 && f.TotalBytesRead <= 8192)) * 100.0 / perFileStats.Values.Sum(f => f.OpenCount), 1) : 0,
            openToFirstReadMs = openToFirstReadMs.Count > 0
                ? new { mean = Math.Round(openToFirstReadMs.Average(), 3),
                        p50 = Math.Round(Percentile(openToFirstReadMs, 50), 3),
                        p95 = Math.Round(Percentile(openToFirstReadMs, 95), 3),
                        p99 = Math.Round(Percentile(openToFirstReadMs, 99), 3) }
                : null,
        },
        diskIO = new
        {
            reads = diskReads,
            bytesReadMB = Math.Round(diskBytesRead / (1024.0 * 1024), 1),
            writes = diskWrites,
            bytesWrittenMB = Math.Round(diskBytesWritten / (1024.0 * 1024), 1),
            readMBPerSec = traceDurationSec > 0
                ? Math.Round(diskBytesRead / (1024.0 * 1024) / traceDurationSec, 1) : 0,
            avgReadSizeKB = diskReads > 0
                ? Math.Round((double)diskBytesRead / diskReads / 1024, 1) : 0,
            readLatencyMs = diskReadLatencyMs.Count > 0
                ? new { mean = Math.Round(diskReadLatencyMs.Average(), 3),
                        p50 = Math.Round(Percentile(diskReadLatencyMs, 50), 3),
                        p95 = Math.Round(Percentile(diskReadLatencyMs, 95), 3),
                        p99 = Math.Round(Percentile(diskReadLatencyMs, 99), 3),
                        max = Math.Round(diskReadLatencyMs.Max(), 3) }
                : null,
        },
        contextSwitches = contextSwitchCount,
        assembliesLoaded = assemblyLoads.Count,
        threads = new
        {
            created = threadStarts.Count,
            exited = threadStops.Count,
            distinctIds = new HashSet<int>(threadStarts.Select(t => t.ThreadID)).Count,
        },
        nativeImages = new
        {
            loaded = imageLoads.Count,
            totalSizeMB = Math.Round(imageLoads.Sum(i => (long)i.ImageSize) / (1024.0 * 1024), 1),
        },
        pageFaults = new
        {
            hardFaults = hardFaultCount,
            totalLatencyMs = hardFaultLatencyMs.Count > 0
                ? Math.Round(hardFaultLatencyMs.Sum(), 1) : 0,
        },
        virtualMemory = new
        {
            allocCalls = virtualAllocCount,
            allocMB = Math.Round(virtualAllocTotalBytes / (1024.0 * 1024), 1),
            freeCalls = virtualFreeCount,
            freeMB = Math.Round(virtualFreeTotalBytes / (1024.0 * 1024), 1),
            netMB = Math.Round((virtualAllocTotalBytes - virtualFreeTotalBytes) / (1024.0 * 1024), 1),
        },
        dpc = new
        {
            events = dpcCount,
            totalMs = dpcLatencyMs.Count > 0 ? Math.Round(dpcLatencyMs.Sum(), 1) : 0,
        },
    };

    string jsonPath = Path.Combine(outDir, "etl-summary.json");
    var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, jsonOpts), Encoding.UTF8);
    Console.WriteLine($"Summary JSON:   {jsonPath}");
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

/// <summary>Basic Rust symbol demangling: strips hash suffixes and decodes common patterns.</summary>
static string DemangleRust(string symbol)
{
    // Rust v0 mangling: _R... prefix — too complex to fully decode, but strip hash
    // Legacy mangling: _ZN...E with ::h[hex] hash suffix
    // TraceEvent typically gives us partially-resolved names like:
    //   yagu_core!yagu_core::search::matcher::match_line::h1a2b3c4d5e6f7890
    // or just: yagu_core::search::matcher::match_line::h1a2b3c4d5e6f7890

    var s = symbol;

    // Strip trailing hash: ::h followed by exactly 16 hex chars at end
    int hashIdx = s.LastIndexOf("::h", StringComparison.Ordinal);
    if (hashIdx > 0 && hashIdx + 3 + 16 == s.Length)
    {
        bool allHex = true;
        for (int i = hashIdx + 3; i < s.Length; i++)
        {
            if (!Uri.IsHexDigit(s[i])) { allHex = false; break; }
        }
        if (allHex) s = s[..hashIdx];
    }

    // Strip module! prefix if duplicated in the path (e.g. "yagu_core!yagu_core::...")
    int bangIdx = s.IndexOf('!');
    if (bangIdx > 0 && bangIdx + 1 < s.Length)
    {
        string modPrefix = s[..bangIdx];
        string rest = s[(bangIdx + 1)..];
        if (rest.StartsWith(modPrefix, StringComparison.OrdinalIgnoreCase))
            s = rest;
        else
            s = rest; // still strip module! prefix for cleaner display
    }

    return s;
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

struct JitRecord
{
    public double TimeMs;
    public string MethodName;
    public double JitMs;
    public bool IsR2R;
    public int NativeSize;
}

struct AssemblyLoadRecord
{
    public double TimeMs;
    public string AssemblyName;
}

struct ImageLoadRecord
{
    public double TimeMs;
    public string FileName;
    public ulong ImageBase;
    public int ImageSize;
    public DateTime BuildTime;
}

sealed class FileStats
{
    public long OpenCount;
    public long ReadCount;
    public long TotalBytesRead;
    public double FirstOpenTimeMs;
    public double FirstReadTimeMs;
}
