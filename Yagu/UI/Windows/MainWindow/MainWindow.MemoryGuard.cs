using System;
using Yagu.Models;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Out-of-memory safety net for the results-materialization paths (expanding a
/// file group, "show more", and hydration). A multi-million-match search can
/// drive the process to the brink of memory exhaustion; pushing yet more rows
/// into the WinUI visual tree at that point triggers a non-recoverable
/// out-of-memory <c>STATUS_STOWED_EXCEPTION</c> failfast inside
/// <c>Microsoft.UI.Xaml.dll</c> that bypasses the managed unhandled-exception
/// handler. These guards pre-check available memory before materializing rows
/// and surface a friendly message instead of letting the XAML layer crash.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// System memory-load percentage at or above which materializing more
    /// results risks an out-of-memory failfast in the XAML layer. Above this
    /// line the guard attempts recovery and, if memory is still critical,
    /// pauses loading more rows.
    /// </summary>
    private const uint ResultsCriticalMemoryLoadPercent = 94;

    /// <summary>Minimum gap between user-facing low-memory notices, so the
    /// repeatedly-firing container-recycling path cannot spam the status bar.</summary>
    private static readonly TimeSpan ResultsMemoryGuardNoticeInterval = TimeSpan.FromSeconds(5);

    private DateTime _lastResultsMemoryGuardNoticeUtc = DateTime.MinValue;

    /// <summary>
    /// Pre-flight memory check for results-materialization paths. Returns
    /// <c>true</c> when it is safe to proceed. When system memory is critically
    /// low it first attempts recovery (working-set trim + a blocking GC); if
    /// memory is still critical it logs, shows a throttled status message, and
    /// returns <c>false</c> so the caller bails gracefully instead of driving
    /// the XAML layer into an out-of-memory failfast.
    /// </summary>
    private bool TryEnsureResultsMemoryHeadroom(string context, FileGroup? group)
    {
        if (!SearchService.TryGetSystemMemoryLoadPercent(out uint load)
            || load < ResultsCriticalMemoryLoadPercent)
        {
            return true;
        }

        // Attempt recovery before refusing: release soft-faulted pages and run a
        // blocking, compacting GC. Often enough to drop back below the line.
        SearchService.TrimProcessWorkingSet();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        uint loadAfter = SearchService.TryGetSystemMemoryLoadPercent(out uint measured) ? measured : load;
        if (loadAfter < ResultsCriticalMemoryLoadPercent)
        {
            LogService.Instance.Info("Results",
                $"Memory guard recovered before {context}: load {load}% -> {loadAfter}% ({SearchService.GetMemoryDiagnostics()})");
            return true;
        }

        ShowResultsMemoryGuardNotice(context, group, loadAfter);
        return false;
    }

    /// <summary>
    /// Recovers memory after a managed <see cref="OutOfMemoryException"/> escaped
    /// a results-materialization path, and surfaces a friendly message. Used as a
    /// secondary net behind <see cref="TryEnsureResultsMemoryHeadroom"/>.
    /// </summary>
    private void HandleResultsOutOfMemory(string context, FileGroup? group, OutOfMemoryException ex)
    {
        LogService.Instance.Warning("Results",
            $"Out of memory during {context}; recovering ({SearchService.GetMemoryDiagnostics()})", ex);
        try
        {
            SearchService.TrimProcessWorkingSet();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        catch { /* best-effort recovery */ }

        if (SearchService.TryGetSystemMemoryLoadPercent(out uint load))
            ShowResultsMemoryGuardNotice(context, group, load);
        else
            ShowResultsMemoryGuardNotice(context, group, null);
    }

    private void ShowResultsMemoryGuardNotice(string context, FileGroup? group, uint? loadPercent)
    {
        var now = DateTime.UtcNow;
        bool throttled = (now - _lastResultsMemoryGuardNoticeUtc) < ResultsMemoryGuardNoticeInterval;

        LogService.Instance.Warning("Results",
            $"Memory guard paused {context}" +
            (group is null ? string.Empty : $" for '{group.FilePath}' ({group.Count:N0} matches)") +
            (loadPercent is uint p ? $" at system load {p}%" : string.Empty) +
            $" ({SearchService.GetMemoryDiagnostics()})");

        if (throttled)
            return;
        _lastResultsMemoryGuardNoticeUtc = now;

        ViewModel.StatusText = group is null
            ? "Low memory: paused loading more results. Narrow your search or collapse some file groups to free memory."
            : $"Low memory: paused loading more matches for {Path.GetFileName(group.FilePath)} ({group.Count:N0} matches). Narrow your search or collapse some file groups to free memory.";
    }
}
