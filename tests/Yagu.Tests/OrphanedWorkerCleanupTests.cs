using System;
using System.IO;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Tests for <see cref="OrphanedWorkerCleanup"/> — startup termination of leftover semantic/OCR worker
/// processes. The pure install-path gate is unit-tested; the Process/P-Invoke behavior and the Program.cs
/// wiring (primary-instance only, before workers spawn) are source-pinned.
/// </summary>
public sealed class OrphanedWorkerCleanupTests
{
    [Fact]
    public void WorkerProcessNames_CoverBothOutOfProcessWorkers()
    {
        Assert.Contains("Yagu.SemanticWorker", OrphanedWorkerCleanup.WorkerProcessNames);
        Assert.Contains("Yagu.OcrWorker", OrphanedWorkerCleanup.WorkerProcessNames);
        Assert.Equal(2, OrphanedWorkerCleanup.WorkerProcessNames.Length);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Yagu\semantic-worker\Yagu.SemanticWorker.exe", @"C:\Program Files\Yagu\", true)]
    [InlineData(@"C:\Program Files\Yagu\ocr-worker\Yagu.OcrWorker.exe", @"C:\Program Files\Yagu", true)]
    [InlineData(@"C:\Program Files\Yagu\ocr-worker\Yagu.OcrWorker.exe", @"c:\program files\yagu\", true)] // case-insensitive
    [InlineData(@"C:\Other\Yagu\semantic-worker\Yagu.SemanticWorker.exe", @"C:\Program Files\Yagu\", false)] // different install
    [InlineData(@"C:\Temp\Yagu.OcrWorker.exe", @"C:\Program Files\Yagu\", false)] // planted elsewhere
    public void IsWorkerFromInstall_MatchesOnlyOwnInstallDirectory(string modulePath, string baseDir, bool expected)
    {
        Assert.Equal(expected, OrphanedWorkerCleanup.IsWorkerFromInstall(modulePath, baseDir));
    }

    [Theory]
    [InlineData(null, @"C:\Program Files\Yagu\")]
    [InlineData("", @"C:\Program Files\Yagu\")]
    [InlineData(@"C:\Program Files\Yagu\ocr-worker\Yagu.OcrWorker.exe", null)]
    [InlineData(@"C:\Program Files\Yagu\ocr-worker\Yagu.OcrWorker.exe", "")]
    public void IsWorkerFromInstall_UnknownPathOrBase_IsNotOurs(string? modulePath, string? baseDir)
    {
        // A process whose path can't be read (null) is left alone rather than killed.
        Assert.False(OrphanedWorkerCleanup.IsWorkerFromInstall(modulePath, baseDir));
    }

    [Fact]
    public void Startup_KillsOrphanedWorkers_OnlyForThePrimaryInstance_BeforeSpawningWorkers()
    {
        string program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Program.cs"));

        // The cleanup call exists and is placed AFTER the single-instance handoff (so a non-primary launch
        // that activates the existing window and returns never runs it) and BEFORE Application.Start.
        int handoff = program.IndexOf("ActivateExistingInstance();", StringComparison.Ordinal);
        int cleanup = program.IndexOf("Services.OrphanedWorkerCleanup.KillOrphanedWorkers();", StringComparison.Ordinal);
        int appStart = program.IndexOf("Application.Start(", StringComparison.Ordinal);
        Assert.True(handoff >= 0 && cleanup > handoff, "Cleanup must run only after the primary instance wins the mutex.");
        Assert.True(appStart < 0 || cleanup < appStart, "Cleanup must run before the WinUI app starts (before workers can spawn).");
    }

    [Fact]
    public void KillOrphanedWorkers_GatesOnInstallDirAndSparesLiveYaguChildren()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "OrphanedWorkerCleanup.cs"));

        // Only kills workers from our own install directory.
        Assert.Contains("IsWorkerFromInstall(TryGetModulePath(proc), baseDir)", src);
        // Spares a worker whose parent is a currently-running Yagu.exe (e.g. a concurrent --cli run).
        Assert.Contains("GetParentProcessId(proc.Id)", src);
        Assert.Contains("liveYaguPids.Contains(parentPid)", src);
        Assert.Contains("Process.GetProcessesByName(\"Yagu\")", src);
        // Kills the whole worker subtree.
        Assert.Contains("proc.Kill(entireProcessTree: true)", src);
        // Parent PID is read via the ntdll basic-information query (AOT-safe P/Invoke).
        Assert.Contains("NtQueryInformationProcess", src);
        Assert.Contains("InheritedFromUniqueProcessId", src);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Yagu.sln).");
    }
}
