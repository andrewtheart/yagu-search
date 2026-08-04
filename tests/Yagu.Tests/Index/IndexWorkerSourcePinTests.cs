using System;
using System.IO;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>
/// Source-pin tests for the out-of-process content-index worker and its in-app proxy. The worker
/// <c>Program.cs</c>, the process-launching <c>IndexWorkerClient</c>, the P/Invoke
/// <see cref="WindowsJobObject"/> and <c>NativeIndexEngine</c>, and the isolated build/bundle wiring are
/// validated by asserting on their source text (they launch processes / call native code and so cannot get
/// runtime coverage in the unit suite — the same source-pin discipline used for the OCR / semantic workers).
/// The <see cref="WindowsJobObject"/> runtime behavior is additionally exercised where possible.
/// </summary>
public sealed class IndexWorkerSourcePinTests
{
    [Fact]
    public void Worker_Program_HandshakesReady_AfterAbiCheck_AndDispatchesOps()
    {
        string src = ReadSource("src", "Yagu.IndexWorker", "Program.cs");

        // ABI is verified against the dedicated index ABI before signaling ready.
        Assert.Contains("NativeIndexEngine.AbiVersion()", src);
        Assert.Contains("IndexWorkerProtocol.RequiredIndexAbiVersion", src);

        int abiCheck = src.IndexOf("RequiredIndexAbiVersion", StringComparison.Ordinal);
        int ready = src.IndexOf("MessageTypes.Ready", StringComparison.Ordinal);
        Assert.True(abiCheck >= 0 && ready > abiCheck, "ABI mismatch must be reported before signaling ready.");

        // Every op is dispatched.
        Assert.Contains("IndexWorkerProtocol.Ops.Ping", src);
        Assert.Contains("IndexWorkerProtocol.Ops.Extract", src);
        Assert.Contains("IndexWorkerProtocol.Ops.QueryContentBin", src);
        Assert.Contains("IndexWorkerProtocol.Ops.BuildScope", src);
        Assert.Contains("IndexWorkerProtocol.Ops.RefreshAuto", src);
        Assert.Contains("IndexWorkerProtocol.Ops.ValidateScope", src);
        Assert.Contains("IndexWorkerProtocol.Ops.CancelBuild", src);
        Assert.Contains("IndexWorkerProtocol.Ops.Shutdown", src);
    }

    [Fact]
    public void Worker_Program_KeepsProtocolStreamClean_AndExitsOnEof()
    {
        string src = ReadSource("src", "Yagu.IndexWorker", "Program.cs");

        // Library writes are redirected to stderr so they never corrupt the protocol stdout stream.
        Assert.Contains("Console.SetOut(Console.Error)", src);
        // Diagnostics go to stderr.
        Assert.Contains("Console.Error.WriteLine(\"[indexworker] \"", src);
        // BOM guard on the first stdin line.
        Assert.Contains("Trim('\\uFEFF', '\\u200B')", src);
        // Exits on stdin EOF (ReadLine returns null) as well as an explicit shutdown op.
        Assert.Contains("stdin.ReadLineAsync().ConfigureAwait(false)) is not null", src);
    }

    [Fact]
    public void Worker_Program_FailsRequestsGracefully_WithoutThrowing()
    {
        string src = ReadSource("src", "Yagu.IndexWorker", "Program.cs");

        // A missing file yields a failure result, not an exception.
        Assert.Contains("File.Exists(request.Path)", src);
        Assert.Contains("\"file not found\"", src);
        Assert.Contains("\"content.bin not found\"", src);
        // Any handler exception is turned into a failure result.
        Assert.Contains("catch (Exception ex)", src);
        Assert.Contains("IndexWorkerBuildHost.MapFailure(request.Id, ex)", src);
    }

    [Fact]
    public void Worker_MaintenanceRole_DoesNotLoadNativeEngine_AndKeepsCancelResponsive()
    {
        string src = ReadSource("src", "Yagu.IndexWorker", "Program.cs");

        Assert.Contains("--maintenance", src);
        Assert.Contains("if (!_maintenanceRole)", src);
        Assert.Contains("NativeIndexEngine.Install()", src);
        Assert.Contains("ConcurrentDictionary<int, CancellationTokenSource> InFlight", src);
        Assert.Contains("WorkLock.WaitAsync(0)", src);
        Assert.Contains("cancel.Cancel()", src);
        Assert.Contains("MessageTypes.Accepted", src);
        Assert.Contains("IndexWorkerBuildHost.Execute", src);
        Assert.Contains("lock (OutLock)", src);
    }

    [Fact]
    public void Worker_Parallelism_KeepsWriterAndSessionOrderingBoundaries()
    {
        string program = ReadSource("src", "Yagu.IndexWorker", "Program.cs");
        string queryHost = ReadSource("src", "Yagu.IndexWorker", "IndexQueryScopeHost.cs");
        string manager = ReadSource("src", "Yagu", "Services", "Index", "ContentIndexManager.cs");

        // Publication remains one-writer and query open/classify/reconcile/close remain ordered.
        Assert.Contains("SemaphoreSlim WorkLock = new(1, 1)", program);
        Assert.Contains("SemaphoreSlim QueryLock = new(1, 1)", program);
        Assert.Contains("await QueryLock.WaitAsync()", program);

        // Inner query lanes perform read-only classification; provisional mutation is applied afterward.
        Assert.Contains("scope.Session.ClassifyBatch(", queryHost);
        Assert.Contains("recordPruning: scope.PruningEnabled", queryHost);

        // Full builds classify a bounded window concurrently, then commit Task.WhenAll's ordered output
        // only on the writer thread.
        Assert.Contains("var readWindow = new List<", manager);
        Assert.Contains("Task.WhenAll(tasks).GetAwaiter().GetResult()", manager);
        Assert.Contains("CommitReadOutcome(items[i]", manager);
    }

    [Fact]
    public void NativeIndexEngine_IsWorkerOnly_AndResolvesEngineFromParentAppDir()
    {
        string engine = ReadSource("src", "Yagu.IndexWorker", "NativeIndexEngine.cs");

        // The index FFI is P/Invoked ONLY in the worker (crash isolation): the entry points live here.
        Assert.Contains("qg_index_abi_version", engine);
        Assert.Contains("qg_index_extract_trigrams", engine);
        Assert.Contains("qg_index_query_content_bin", engine);
        Assert.Contains("CallingConvention.Cdecl", engine);

        // Resolver probes the worker dir then the parent app dir (where yagu_core.dll ships beside Yagu.exe).
        Assert.Contains("SetDllImportResolver", engine);
        Assert.Contains("Path.GetDirectoryName(baseDir)", engine);
        Assert.Contains("yagu_core.dll", engine);

        // The main app must NOT P/Invoke the index FFI (that would defeat crash isolation).
        string appDir = Path.Combine(FindRepoRoot(), "src", "Yagu");
        foreach (string file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("qg_index_extract_trigrams", text);
            Assert.DoesNotContain("qg_index_query_content_bin", text);
        }
    }

    [Fact]
    public void Client_LaunchesUnderKillOnCloseJob_VerifiesSignature_AndDegradesGracefully()
    {
        string src = ReadSource("src", "Yagu", "Services", "Index", "IndexWorkerClient.cs");

        // Kill-on-close job created and the worker assigned to it right after start.
        Assert.Contains("WindowsJobObject.CreateKillOnClose()", src);
        Assert.Contains("static (job, handle) => job.Assign(handle)", src);
        Assert.Contains("_jobAssigner(job, process.Handle)", src);

        // Authenticode trust gate in signed builds (skipped only for the internal path-override test seam).
        Assert.Contains("AuthenticodeVerifier.IsWorkerTrustedForHost,", src);
        Assert.Contains("!_trustVerifier(workerPath, out string trustFailure)", src);
        Assert.Contains("!_hasWorkerPathOverride", src);

        // BOM-less stdin so the first JSON line is not corrupted.
        Assert.Contains("encoderShouldEmitUTF8Identifier: false", src);

        // Missing / not-running worker degrades to a failure result rather than throwing.
        Assert.Contains("LogMissingWorkerOnce()", src);
        Assert.Contains("IndexWorkerExtractResult.Fail(\"index worker is not running.\")", src);
        Assert.Contains("IndexWorkerQueryResult.Fail(\"index worker is not running.\")", src);

        // Probes the env override then <app>\index-worker\Yagu.IndexWorker.exe.
        Assert.Contains("YAGU_INDEX_WORKER", src);
        Assert.Contains("Path.Combine(AppContext.BaseDirectory, \"index-worker\", \"Yagu.IndexWorker.exe\")", src);
    }

    [Fact]
    public void Client_Dispose_RequestsShutdown_ThenKillsAndDisposesJob()
    {
        string src = ReadSource("src", "Yagu", "Services", "Index", "IndexWorkerClient.cs");
        src = src[src.IndexOf("public void Dispose()", StringComparison.Ordinal)..];

        int shutdown = src.IndexOf("IndexWorkerProtocol.Ops.Shutdown", StringComparison.Ordinal);
        int kill = src.IndexOf("process.Kill();", StringComparison.Ordinal);
        int processDispose = src.IndexOf("_process?.Dispose()", StringComparison.Ordinal);
        int jobDispose = src.IndexOf("_job?.Dispose()", StringComparison.Ordinal);
        Assert.True(shutdown >= 0 && kill > shutdown, "Dispose must ask the worker to exit before force-killing.");
        Assert.True(processDispose > kill, "Dispose must release the worker process after force-killing it.");
        Assert.True(jobDispose > processDispose, "The kill-on-close job is disposed last, as the final backstop.");
    }

    [Fact]
    public void QueryClient_ValidatesProtocolAndRestartsAfterFatalChannelFailure()
    {
        string src = ReadSource("src", "Yagu", "Services", "Index", "IndexWorkerClient.cs");

        Assert.Contains("ready.ControlProtocolVersion != IndexWorkerProtocol.ControlProtocolVersion", src);
        Assert.Contains("FailProtocolChannel(\"index worker emitted malformed JSON:", src);
        Assert.Contains("index worker emitted unknown message type", src);
        Assert.Contains("private int _sessionId;", src);
        Assert.Contains("IsCurrentSession(process, sessionId)", src);
        // A fatal channel failure tears the worker down (kill + dispose) before a fresh init is scheduled.
        Assert.Contains("CleanupProcessUnderGate();", src);
        Assert.Contains("liveProcess.Kill();", src);
        Assert.Contains("_initTask = null;", src);
    }

    [Fact]
    public void JobObject_UsesKillOnCloseFlag_AndIsNeverThrowing()
    {
        string src = ReadSource("src", "Yagu", "Services", "Index", "WindowsJobObject.cs");

        Assert.Contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000", src);
        Assert.Contains("JobObjectExtendedLimitInformation", src);
        Assert.Contains("AssignProcessToJobObject", src);
        // Non-Windows / failure paths return an invalid (handle-less) instance instead of throwing. The OS
        // and native calls are injected for testability, so the handle-less instance carries the delegates.
        Assert.Contains("OperatingSystem.IsWindows()", src);
        Assert.Contains("new WindowsJobObject(IntPtr.Zero, assignProcess, closeHandle)", src);
    }

    [Fact]
    public void JobObject_CreateKillOnClose_OnWindows_IsValid_AndDisposeIsIdempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowsJobObject job = WindowsJobObject.CreateKillOnClose();
        Assert.False(job.IsInvalid);

        // Idempotent dispose (must not throw or double-close).
        job.Dispose();
        job.Dispose();
    }

    [Fact]
    public void JobObject_KillOnClose_TerminatesAssignedChild_OnDispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A throwaway child that would otherwise run for ~30s. It must NOT be the test host — closing a
        // kill-on-close job that the test process is assigned to would kill the test runner.
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(psi)!;
        try
        {
            WindowsJobObject job = WindowsJobObject.CreateKillOnClose();
            Assert.True(job.Assign(child.SafeHandle.DangerousGetHandle()));

            // Closing the last job handle triggers kill-on-close, terminating the assigned child.
            job.Dispose();
            Assert.True(child.WaitForExit(5000), "kill-on-close job should terminate the assigned child.");
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch { /* ignore */ }
        }
    }

    [Fact]
    public void JobObject_Assign_WhenInvalid_ReturnsFalse()
    {
        // An invalid (handle-less) job never assigns and never throws.
        using WindowsJobObject job = MakeInvalidJob();
        Assert.True(job.IsInvalid);
        Assert.False(job.Assign(new IntPtr(1234)));
        Assert.False(job.Assign(IntPtr.Zero));
    }

    [Fact]
    public void Csproj_BuildsAndBundlesWorker_WithArchMatchedRid()
    {
        string csproj = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Yagu.csproj"));

        // Skippable, isolated child publish (not a ProjectReference — would pollute the AOT graph).
        Assert.Contains("BuildIndexWorker", csproj);
        Assert.Contains("dotnet publish &quot;$(IndexWorkerProject)&quot;", csproj);
        Assert.Contains("--self-contained true", csproj);

        // RID tracks the app arch (the native engine is arch-specific), falling back to win-x64.
        Assert.Contains("<IndexWorkerRid Condition=\"'$(IndexWorkerRid)' == '' And '$(RuntimeIdentifier)' != ''\">$(RuntimeIdentifier)</IndexWorkerRid>", csproj);
        Assert.Contains("<IndexWorkerRid Condition=\"'$(IndexWorkerRid)' == ''\">win-x64</IndexWorkerRid>", csproj);

        // Bundled into <app>\index-worker\ for both plain build and self-contained publish (installer source).
        Assert.Contains("index-worker\\%(RecursiveDir)%(Filename)%(Extension)", csproj);
        Assert.Contains("AfterTargets=\"Build\"", csproj);
        Assert.Contains("AfterTargets=\"Publish\"", csproj);
    }

    [Fact]
    public void WorkerCsproj_IsSelfContainedMultiArch_AndSourceLinksTheSharedProtocol()
    {
        string csproj = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu.IndexWorker", "Yagu.IndexWorker.csproj"));

        Assert.Contains("<OutputType>Exe</OutputType>", csproj);
        Assert.Contains("win-x64;win-x86;win-arm64", csproj);
        Assert.Contains("<PublishAot>false</PublishAot>", csproj);
        Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", csproj);
        // Single source of truth for the wire protocol (also compiled into Yagu.dll + Yagu.Tests).
        Assert.Contains("..\\Yagu\\Services\\Index\\IndexWorkerProtocol.cs", csproj);
        Assert.Contains("..\\Yagu\\Services\\Index\\IndexBuildExecutor.cs", csproj);
        Assert.Contains("..\\Yagu\\Services\\Index\\ContentIndexManager.cs", csproj);
        Assert.Contains("..\\Yagu\\Services\\Index\\ContentIndexIncrementalRefresher.cs", csproj);
        Assert.Contains("..\\Yagu\\Services\\Index\\PdfExtendedSourcePopulator.cs", csproj);
        Assert.DoesNotContain("IndexWorkerClient.cs", csproj);
        Assert.DoesNotContain("IndexBuildCoordinator.cs", csproj);
        Assert.DoesNotContain("SettingsService.cs", csproj);
        Assert.DoesNotContain("IndexTelemetry.cs", csproj);
    }

    /// <summary>Builds an invalid <see cref="WindowsJobObject"/> for the failure-path test by driving the
    /// injectable factory down its non-Windows branch, which is exactly how a real failure surfaces (a
    /// handle-less instance). This works identically on every host, so no reflection is needed.</summary>
    private static WindowsJobObject MakeInvalidJob()
        => WindowsJobObject.CreateKillOnClose(
            isWindows: false,
            createJob: static (_, _) => IntPtr.Zero,
            setInformation: static (_, _, _, _) => false,
            assignProcess: static (_, _) => false,
            closeHandle: static _ => true);

    private static string ReadSource(params string[] parts)
    {
        string[] full = new string[parts.Length + 1];
        full[0] = FindRepoRoot();
        Array.Copy(parts, 0, full, 1, parts.Length);
        return File.ReadAllText(Path.Combine(full));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Yagu.slnx).");
    }
}
