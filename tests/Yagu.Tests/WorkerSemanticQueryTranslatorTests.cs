using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin tests for the out-of-process semantic worker (<c>Yagu.SemanticWorker</c>) and its in-app
/// proxy (<c>WorkerSemanticQueryTranslator</c>). The proxy spawns a real child process and the worker
/// hosts the Foundry Local SDK, so neither is unit-testable in-assembly; these pins lock in the
/// crash-ISOLATION invariants that are the whole point of the design, so a future refactor can't silently
/// re-couple Foundry to the main process.
/// </summary>
public sealed class WorkerSemanticQueryTranslatorTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    private static string Proxy() => Read("src", "Yagu", "Services", "Ai", "Worker", "WorkerSemanticQueryTranslator.cs");
    private static string WorkerProgram() => Read("src", "Yagu.SemanticWorker", "Program.cs");
    private static string WorkerCsproj() => Read("src", "Yagu.SemanticWorker", "Yagu.SemanticWorker.csproj");
    private static string MainCsproj() => Read("src", "Yagu", "Yagu.csproj");

    [Fact]
    public void Proxy_ImplementsTranslatorInterfaceAndIsAsyncDisposable()
    {
        string src = Proxy();
        Assert.Contains("public sealed class WorkerSemanticQueryTranslator : ISemanticQueryTranslator, ISemanticHostController, IAsyncDisposable", src);
        // Same 3-arg ctor shape as the in-process translator, so MainViewModel wiring is a drop-in swap.
        Assert.Contains("public WorkerSemanticQueryTranslator(bool enabled, string? modelOverrideAlias = null, string? devicePreferenceOrder = null)", src);
    }

    [Fact]
    public void Proxy_ResetHostKillsTheWorkerSoTheNextModelLoadsClean()
    {
        string src = Proxy();
        // ISemanticHostController.ResetHostAsync hard-kills the current worker (entire tree) and clears the
        // handles so the next request respawns a fresh host — the reliable way to clear the Foundry Local
        // model-switch wedge (an in-process unload leaves the stuck native thread / not-fully-freed VRAM).
        Assert.Contains("public async Task ResetHostAsync(CancellationToken cancellationToken)", src);
        // It detaches the process reference before killing (so the Exited handler doesn't race), fails any
        // in-flight requests, and kills the process tree via the shared helper.
        Assert.Contains("_proc = null;", src);
        Assert.Contains("TryKill(proc);", src);
        // The kill helper terminates the whole tree so a child (Foundry Local runtime) can't be orphaned.
        Assert.Contains("p.Kill(entireProcessTree: true)", src);
        // After killing it WAITS for full process exit and then settles, so the GPU driver reclaims the dead
        // worker's CUDA/DirectML context before the next worker re-registers execution providers — a
        // back-to-back respawn otherwise crashes the fresh worker mid-EP-init. Held under the spawn lock.
        Assert.Contains("await WaitForExitAsync(proc).ConfigureAwait(false);", src);
        Assert.Contains("await Task.Delay(HostResetGpuSettle, cancellationToken).ConfigureAwait(false);", src);
    }

    [Fact]
    public void Proxy_TranslateDegradesToFailedResult_NeverThrowsOnWorkerFault()
    {
        // A worker crash (or any non-cancellation fault) must surface as a FAILED SemanticTranslationResult,
        // not an exception that could bubble up and crash the search — that is the isolation guarantee.
        string src = Proxy();
        Assert.Contains("return SemanticTranslationResult.Fail($\"The local AI worker could not translate the query: {ex.Message}\");", src);
        // Cancellation still propagates (the caller cancelled deliberately).
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", src);
    }

    [Fact]
    public void Proxy_FaultsPendingRequestsWhenWorkerExits()
    {
        string src = Proxy();
        // On worker exit, every in-flight request is completed with an exception so awaiting callers unwind.
        Assert.Contains("proc.Exited += (_, _) => OnWorkerExited(proc);", src);
        Assert.Contains("private void OnWorkerExited(Process proc)", src);
        Assert.Contains("FaultAllPending(new SemanticWorkerException(\"the semantic worker process exited\"));", src);
        Assert.Contains("p.Terminal.TrySetException(ex)", src);
    }

    [Fact]
    public void Proxy_RespawnsAndReplaysConfigAfterCrash()
    {
        string src = Proxy();
        // EnsureWorkerAsync respawns when the process is gone, and a fresh worker gets the full config replayed
        // so a restart is transparent (enabled state, accelerators, VRAM, unload-after-use, device order, override).
        Assert.Contains("if (_stdin is not null && _proc is { HasExited: false }) return true;", src);
        Assert.Contains("await ReplayConfigAsync().ConfigureAwait(false);", src);
        Assert.Contains("Ops.SetEnabled", src);
        Assert.Contains("Ops.SetAccelerators", src);
        Assert.Contains("Ops.SetGpuMemory", src);
        Assert.Contains("Ops.SetModelOverride", src);
    }

    [Fact]
    public void Proxy_ProbesWorkerPathViaEnvThenAppFolder()
    {
        string src = Proxy();
        Assert.Contains("YAGU_SEMANTIC_WORKER", src);
        Assert.Contains("Path.Combine(AppContext.BaseDirectory, \"semantic-worker\", \"Yagu.SemanticWorker.exe\")", src);
        // BOM-less stdin so the worker's first JSON line isn't corrupted (the OCR-worker BOM-hang lesson).
        Assert.Contains("StandardInputEncoding = Utf8NoBom", src);
    }

    [Fact]
    public void Worker_RedirectsConsoleToStderrAndNeverWritesTheSharedLogFile()
    {
        string src = WorkerProgram();
        // Capture the real stdout for the protocol, then push Console.* to stderr so nothing corrupts the channel.
        Assert.Contains("_protocolOut = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom)", src);
        Assert.Contains("Console.SetOut(Console.Error);", src);
        // Two processes must not append to %APPDATA%\\Yagu\\yagu.log; the worker logs to stderr only.
        Assert.Contains("LogService.Instance.FileLevel = LogLevel.None;", src);
    }

    [Fact]
    public void MainViewModel_WiresTheOutOfProcessProxy_NotTheInProcessTranslator()
    {
        string vm = Read("src", "Yagu", "ViewModels", "MainViewModel.cs");
        Assert.Contains("new Yagu.Services.Ai.Worker.WorkerSemanticQueryTranslator(_settings.SemanticSearchEnabled, _settings.SemanticModelAlias, _settings.SemanticDevicePreferenceOrder)", vm);
    }

    [Fact]
    public void WorkerProject_SourceLinksTheClosureAndEmbedsTheSamePrompts()
    {
        string csproj = WorkerCsproj();
        // Isolated, non-AOT, self-contained win-x64 host (mirrors the OCR worker).
        Assert.Contains("<PublishAot>false</PublishAot>", csproj);
        Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>", csproj);
        Assert.Contains("Microsoft.AI.Foundry.Local.WinML", csproj);
        // The translator + its closure are source-linked (single source of truth), not duplicated.
        Assert.Contains("FoundryLocalSemanticQueryTranslator.cs", csproj);
        Assert.Contains("SemanticWorkerProtocol.cs", csproj);
        // Prompts embedded under the SAME logical names the translator loads by reflection.
        Assert.Contains("<LogicalName>Yagu.Services.Ai.Prompts.SemanticSearchSystemPrompt.prompt.md</LogicalName>", csproj);
    }

    [Fact]
    public void MainProject_BuildsAndStagesTheSemanticWorkerLikeTheOcrWorker()
    {
        string csproj = MainCsproj();
        // Built as an isolated child publish (no ProjectReference — would drag Foundry into the AOT graph),
        // then copied into <app>\semantic-worker\ where the proxy probes for it.
        Assert.Contains("Target Name=\"BuildSemanticWorker\"", csproj);
        Assert.Contains("dotnet publish &quot;$(SemanticWorkerProject)&quot; -c $(Configuration) -r win-x64 --self-contained true", csproj);
        Assert.Contains("$(OutDir)semantic-worker\\", csproj);
        Assert.DoesNotContain("<ProjectReference Include=\"..\\Yagu.SemanticWorker", csproj);
    }
}
