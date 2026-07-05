using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Tests for the OOM-safe memory guard introduced in MainWindow.MemoryGuard.cs,
/// and its integration with the results-materialization paths in
/// MainWindow.ResultsSelection.cs and MainWindow.PreviewCommands.cs.
/// Uses the same source-scraping methodology as PreviewCoreRegressionTests.
/// </summary>
public sealed class MemoryGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string MainWindowSource = ReadMainWindowSources();
    private static readonly string MainViewModelSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));

    // ══════════════════════════════════════════════════════════════════
    // MemoryGuard file existence and structure
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void MemoryGuardFile_Exists()
    {
        string path = Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.MemoryGuard.cs");
        Assert.True(File.Exists(path), "MainWindow.MemoryGuard.cs should exist.");
    }

    [Fact]
    public void MemoryGuard_DefinesCriticalMemoryLoadThreshold()
    {
        Assert.Contains("private const uint ResultsCriticalMemoryLoadPercent = 94;", MainWindowSource);
    }

    [Fact]
    public void MemoryGuard_DefinesNoticeThrottleInterval()
    {
        Assert.Contains("ResultsMemoryGuardNoticeInterval = TimeSpan.FromSeconds(5);", MainWindowSource);
    }

    [Fact]
    public void MemoryGuard_TryEnsureResultsMemoryHeadroom_MethodExists()
    {
        string method = ExtractMethodWindow("TryEnsureResultsMemoryHeadroom", window: 2200);
        Assert.Contains("TryGetSystemMemoryLoadPercent(out uint load)", method);
        Assert.Contains("load < ResultsCriticalMemoryLoadPercent", method);
        Assert.Contains("TrimProcessWorkingSet()", method);
        Assert.Contains("GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true)", method);
        Assert.Contains("GC.WaitForPendingFinalizers()", method);
        Assert.Contains("ShowResultsMemoryGuardNotice(context, group, loadAfter)", method);
        Assert.Contains("return false;", method);
    }

    [Fact]
    public void MemoryGuard_TryEnsureHeadroom_ReturnsTrueWhenMemoryIsAvailable()
    {
        // When TryGetSystemMemoryLoadPercent fails (returns false), method should return true (safe to proceed)
        string method = ExtractMethodWindow("TryEnsureResultsMemoryHeadroom", window: 2200);
        Assert.Contains("!SearchService.TryGetSystemMemoryLoadPercent(out uint load)", method);
        Assert.Contains("return true;", method);
    }

    [Fact]
    public void MemoryGuard_TryEnsureHeadroom_AttemptsRecoveryBeforeRefusing()
    {
        string method = ExtractMethodWindow("TryEnsureResultsMemoryHeadroom", window: 2200);
        // Verify recovery is attempted BEFORE the second memory check
        int trimIdx = method.IndexOf("TrimProcessWorkingSet()", StringComparison.Ordinal);
        int gcIdx = method.IndexOf("GC.Collect(", StringComparison.Ordinal);
        int secondCheck = method.IndexOf("loadAfter < ResultsCriticalMemoryLoadPercent", StringComparison.Ordinal);

        Assert.True(trimIdx > 0, "TrimProcessWorkingSet should be called");
        Assert.True(gcIdx > trimIdx, "GC.Collect should come after TrimProcessWorkingSet");
        Assert.True(secondCheck > gcIdx, "Second memory check should come after recovery attempt");
    }

    [Fact]
    public void MemoryGuard_TryEnsureHeadroom_LogsRecoverySuccess()
    {
        string method = ExtractMethodWindow("TryEnsureResultsMemoryHeadroom", window: 2200);
        Assert.Contains("Memory guard recovered before", method);
        Assert.Contains("GetMemoryDiagnostics()", method);
    }

    [Fact]
    public void MemoryGuard_HandleResultsOutOfMemory_MethodExists()
    {
        string method = ExtractMethodWindow("HandleResultsOutOfMemory", window: 2000);
        Assert.Contains("OutOfMemoryException ex", method);
        Assert.Contains("TrimProcessWorkingSet()", method);
        Assert.Contains("GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true)", method);
        Assert.Contains("ShowResultsMemoryGuardNotice(context, group,", method);
    }

    [Fact]
    public void MemoryGuard_HandleOutOfMemory_LogsWarningWithDiagnostics()
    {
        string method = ExtractMethodWindow("HandleResultsOutOfMemory", window: 2000);
        Assert.Contains("Out of memory during", method);
        Assert.Contains("GetMemoryDiagnostics()", method);
    }

    [Fact]
    public void MemoryGuard_HandleOutOfMemory_SwallowsRecoveryExceptions()
    {
        string method = ExtractMethodWindow("HandleResultsOutOfMemory", window: 2000);
        Assert.Contains("catch { /* best-effort recovery */ }", method);
    }

    [Fact]
    public void MemoryGuard_ShowNotice_ThrottlesRepeatedNotifications()
    {
        string method = ExtractMethodWindow("ShowResultsMemoryGuardNotice", window: 2000);
        Assert.Contains("ResultsMemoryGuardNoticeInterval", method);
        Assert.Contains("if (throttled)", method);
        Assert.Contains("_lastResultsMemoryGuardNoticeUtc = now;", method);
    }

    [Fact]
    public void MemoryGuard_ShowNotice_IncludesFilePathAndMatchCountWhenGroupProvided()
    {
        string method = ExtractMethodWindow("ShowResultsMemoryGuardNotice", window: 2000);
        Assert.Contains("group.FilePath", method);
        Assert.Contains("group.Count:N0", method);
        Assert.Contains("Low memory: paused loading more matches for", method);
    }

    [Fact]
    public void MemoryGuard_ShowNotice_ShowsGenericMessageWhenGroupIsNull()
    {
        string method = ExtractMethodWindow("ShowResultsMemoryGuardNotice", window: 2000);
        Assert.Contains("Low memory: paused loading more results. Narrow your search or collapse some file groups to free memory.", method);
    }

    [Fact]
    public void MemoryGuard_ShowNotice_IncludesSystemLoadPercentInLog()
    {
        string method = ExtractMethodWindow("ShowResultsMemoryGuardNotice", window: 2000);
        Assert.Contains("at system load", method);
    }

    // ══════════════════════════════════════════════════════════════════
    // Integration: guard wired into EnsureVisible
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void EnsureVisibleResultsForExpandedGroupAsync_HasMemoryGuardPreCheck()
    {
        string method = ExtractMethodWindow("EnsureVisibleResultsForExpandedGroupAsync", window: 2800);
        Assert.Contains("TryEnsureResultsMemoryHeadroom(", method);
        Assert.Contains("\"expanding file group\"", method);

        // Verify the guard is BEFORE the materialization work
        int guardIdx = method.IndexOf("TryEnsureResultsMemoryHeadroom(", StringComparison.Ordinal);
        int showMoreIdx = method.IndexOf("ShowMoreVisibleResultsIncrementalAsync(", StringComparison.Ordinal);
        int hydrateIdx = method.IndexOf("HydrateVisibleResultsAsync(", StringComparison.Ordinal);

        Assert.True(guardIdx > 0);
        Assert.True(showMoreIdx > guardIdx, "Memory guard should precede ShowMore");
        Assert.True(hydrateIdx > guardIdx, "Memory guard should precede Hydrate");
    }

    [Fact]
    public void EnsureVisibleResultsForExpandedGroupAsync_HasOomCatch()
    {
        string method = ExtractMethodWindow("EnsureVisibleResultsForExpandedGroupAsync", window: 2800);
        Assert.Contains("catch (OutOfMemoryException ex)", method);
        Assert.Contains("HandleResultsOutOfMemory(\"expanding file group\", group, ex)", method);
    }

    // ══════════════════════════════════════════════════════════════════
    // Integration: guard wired into ShowMore chunk loop
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ShowMoreVisibleResultsIncrementalAsync_HasMemoryGuardInLoop()
    {
        string method = ExtractMethodWindow("ShowMoreVisibleResultsIncrementalAsync", window: 2800);
        Assert.Contains("TryEnsureResultsMemoryHeadroom(", method);
        Assert.Contains("\"showing more matches\"", method);

        // Verify the guard is inside the loop, BEFORE chunk materialization
        int whileIdx = method.IndexOf("while (remainingToShow > 0 && group.HasMore)", StringComparison.Ordinal);
        int guardIdx = method.IndexOf("TryEnsureResultsMemoryHeadroom(", StringComparison.Ordinal);
        int hydrateRangeIdx = method.IndexOf("HydrateRangeAsync(", StringComparison.Ordinal);

        Assert.True(whileIdx >= 0);
        Assert.True(guardIdx > whileIdx, "Memory guard should be inside while loop");
        Assert.True(hydrateRangeIdx > guardIdx, "HydrateRangeAsync should come after guard check");
    }

    [Fact]
    public void ShowMoreVisibleResultsIncrementalAsync_GuardBreaksLoopOnLowMemory()
    {
        string method = ExtractMethodWindow("ShowMoreVisibleResultsIncrementalAsync", window: 2800);
        // After guard returns false, the loop should break
        int guardIdx = method.IndexOf("TryEnsureResultsMemoryHeadroom(", StringComparison.Ordinal);
        string afterGuard = method[(guardIdx + 10)..Math.Min(method.Length, guardIdx + 200)];
        Assert.Contains("break;", afterGuard);
    }

    // ══════════════════════════════════════════════════════════════════
    // Integration: OOM catch in OnShowMoreClicked
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void OnShowMoreClicked_HasOomCatch()
    {
        string method = ExtractMethodWindow("OnShowMoreClicked", window: 2200);
        Assert.Contains("catch (OutOfMemoryException ex)", method);
        Assert.Contains("HandleResultsOutOfMemory(\"showing more matches\", g, ex)", method);
    }

    [Fact]
    public void OnShowMoreClicked_OomCatchResetsRestoreFlag()
    {
        string method = ExtractMethodWindow("OnShowMoreClicked", window: 2200);
        int oomIdx = method.IndexOf("catch (OutOfMemoryException", StringComparison.Ordinal);
        Assert.True(oomIdx > 0);
        string afterOom = method[oomIdx..Math.Min(method.Length, oomIdx + 300)];
        Assert.Contains("_resultsListShowMoreRestoreInProgress = false;", afterOom);
    }

    [Fact]
    public void OnShowMoreClicked_OomCatchPrecedesGeneralCatch()
    {
        string method = ExtractMethodWindow("OnShowMoreClicked", window: 2200);
        int oomIdx = method.IndexOf("catch (OutOfMemoryException", StringComparison.Ordinal);
        int generalIdx = method.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(oomIdx > 0, "OOM catch should exist");
        Assert.True(generalIdx > oomIdx, "OOM catch should come before general Exception catch");
    }

    // ══════════════════════════════════════════════════════════════════
    // EffectiveExcludeGlobsText (MainViewModel)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void MainViewModel_EffectiveExcludeGlobsText_EmptyMeansNoExcludes()
    {
        // The placeholder (e.g. "node_modules;bin;obj;.git") is ONLY an example and must NOT be
        // applied when the box is empty. An empty exclude box means "no excludes" — excludes apply
        // only when the user explicitly types them.
        Assert.Contains("private string EffectiveExcludeGlobsText => ExcludeGlobs ?? string.Empty;", MainViewModelSource);
        // The old silent-defaulting ternary must be gone.
        Assert.DoesNotContain("? AppSettings.DefaultExcludeGlobs", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_ExcludeGlobsDefaultsToEmpty()
    {
        // Verify the VM maps the settings default → empty string in constructor
        Assert.Contains("IsDefaultExcludeGlobs(_settings.ExcludeGlobs) ? string.Empty : _settings.ExcludeGlobs", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_ExcludeGlobsPropertyDefaultIsEmptyString()
    {
        Assert.Contains("public partial string ExcludeGlobs { get; set; } = string.Empty;", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_ExcludeFilterPlaceholder_IncludesEllipsis()
    {
        // Both placeholders should use the Unicode ellipsis character
        string ellipsis = "\u2026";
        Assert.Contains($@"e.g. \.(cs|xaml)${ellipsis}", MainViewModelSource);
        Assert.Contains($"AppSettings.DefaultExcludeGlobs}}{ellipsis}", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_IncludeFilterPlaceholder_IncludesEllipsis()
    {
        string ellipsis = "\u2026";
        Assert.Contains($"e.g. ts,js,py or *.cs{ellipsis}", MainViewModelSource);
    }

    // ══════════════════════════════════════════════════════════════════
    // SearchService TryGetSystemMemoryLoadPercent and GetMemoryDiagnostics
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void SearchService_TryGetSystemMemoryLoadPercent_ReturnsValidPercentage()
    {
        bool ok = SearchService.TryGetSystemMemoryLoadPercent(out uint load);
        // On any Windows machine, GlobalMemoryStatusEx should succeed
        Assert.True(ok);
        Assert.InRange(load, (uint)0, (uint)100);
    }

    [Fact]
    public void SearchService_GetMemoryDiagnostics_ContainsProcessWS()
    {
        string diag = SearchService.GetMemoryDiagnostics();
        Assert.Contains("process WS=", diag);
    }

    [Fact]
    public void SearchService_GetMemoryDiagnostics_ContainsSystemPercentAndAutoCap()
    {
        string diag = SearchService.GetMemoryDiagnostics();
        Assert.Contains("system=", diag);
        Assert.Contains("autoCap=", diag);
    }

    // ══════════════════════════════════════════════════════════════════
    // SessionFileDiscoveryService NormalizeCandidates logic
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeCandidates_DeduplicatesByPathCaseInsensitive()
    {
        // NormalizeCandidates should keep the entry with the most metadata
        var source = MainWindowSource; // Loaded for the method window pattern
        string svcSource = File.ReadAllText(
            Path.Combine(RepoRoot, "src", "Yagu", "Services", "SessionFileDiscoveryService.cs"));
        Assert.Contains("StringComparer.OrdinalIgnoreCase", svcSource);
        Assert.Contains("byPath.TryGetValue(candidate.Path", svcSource);
    }

    [Fact]
    public void NormalizeCandidates_SortsByModifiedDescending_ThenByFileName()
    {
        string svcSource = File.ReadAllText(
            Path.Combine(RepoRoot, "src", "Yagu", "Services", "SessionFileDiscoveryService.cs"));
        Assert.Contains(".OrderByDescending(candidate => candidate.ModifiedUtc ?? DateTimeOffset.MinValue)", svcSource);
        Assert.Contains(".ThenBy(candidate => Path.GetFileName(candidate.Path), StringComparer.OrdinalIgnoreCase)", svcSource);
    }

    [Fact]
    public void IsSessionFilePath_RequiresYaguSessionExtension()
    {
        string svcSource = File.ReadAllText(
            Path.Combine(RepoRoot, "src", "Yagu", "Services", "SessionFileDiscoveryService.cs"));
        Assert.Contains("SessionFileService.FileExtension", svcSource);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", svcSource);
    }

    // ══════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════

    private static string ReadMainWindowSources()
    {
        string yaguRoot = Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow");
        var sources = Directory.GetFiles(yaguRoot, "MainWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText);
        return string.Join(Environment.NewLine, sources);
    }

    private static string ExtractMethodWindow(string methodName, int window = 12000)
    {
        string needle = methodName + "(";
        int search = 0;
        while (true)
        {
            int index = MainWindowSource.IndexOf(needle, search, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Method definition '{methodName}' not found in MainWindow sources");

            int lineStart = MainWindowSource.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = MainWindowSource.IndexOf('\n', index);
            lineEnd = lineEnd < 0 ? MainWindowSource.Length : lineEnd;
            string line = MainWindowSource[lineStart..lineEnd];
            if (line.Contains("private ", StringComparison.Ordinal)
                || line.Contains("public ", StringComparison.Ordinal)
                || line.Contains("internal ", StringComparison.Ordinal)
                || line.Contains("protected ", StringComparison.Ordinal))
            {
                int end = Math.Min(MainWindowSource.Length, index + window);
                return MainWindowSource[index..end];
            }

            search = index + needle.Length;
        }
    }
}
