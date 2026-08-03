namespace Yagu.Services.Index;

/// <summary>
/// Pure decision + coalescing helpers for the optional <c>FileSystemWatcher</c> latency hint (plan §11.4,
/// Phase 3). A watcher is <b>only a hint</b>: it lets an incremental refresh react to a change sooner than
/// the next scheduled pass, but it never establishes freshness — USN continuity stays authoritative and the
/// refresher still validates the journal before trusting any postings. Kept pure/side-effect-free so the
/// gate and the debounce are unit-tested without a real watcher or timer; the actual
/// <c>FileSystemWatcher</c> plumbing lives in <see cref="ContentIndexRootWatcher"/>.
/// </summary>
public static class ContentIndexWatcherHints
{
    /// <summary>
    /// Whether watcher hints should run: the master feature is on, the user opted into
    /// <c>IndexUseWatcherHints</c>, and the update mode is automatic (a watcher would do nothing useful
    /// under <c>ManualFullRebuild</c>). Off by default — the reserved setting defaults to false.
    /// </summary>
    public static bool ShouldEnable(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EnableContentIndex || !settings.IndexUseWatcherHints)
            return false;
        string mode = AppSettings.NormalizeIndexUpdateMode(settings.IndexUpdateMode);
        return string.Equals(mode, AppSettings.IndexUpdateModeAutomaticIncremental, StringComparison.Ordinal)
            || string.Equals(mode, AppSettings.IndexUpdateModeAutomaticFullRebuildWhenDirty, StringComparison.Ordinal);
    }
}

/// <summary>
/// Coalesces a burst of raw watcher change signals per root into a single "root changed" hint once the root
/// has been quiet for <c>quietWindow</c>. A file save can fire several notifications in a few milliseconds,
/// and a large copy fires continuously; debouncing turns that noise into one refresh trigger and avoids
/// starting a refresh mid-write. Pure and clock-injected so it is unit-tested deterministically; the
/// production caller ticks it from a timer with <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public sealed class RootChangeDebouncer
{
    private readonly TimeSpan _quietWindow;
    private readonly TimeSpan _maximumBatchWindow;
    private readonly Dictionary<string, PendingRoot> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private sealed record PendingRoot(DateTimeOffset FirstSignal, DateTimeOffset LastSignal, long SignalCount);

    public RootChangeDebouncer(TimeSpan quietWindow, TimeSpan? maximumBatchWindow = null)
    {
        if (quietWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietWindow), "The quiet window must be positive.");
        _quietWindow = quietWindow;
        _maximumBatchWindow = maximumBatchWindow ?? TimeSpan.MaxValue;
        if (_maximumBatchWindow < _quietWindow)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchWindow), "The maximum batch window must not be shorter than the quiet window.");
    }

    /// <summary>Records a raw change under <paramref name="root"/> at <paramref name="now"/> (resets its quiet timer).</summary>
    public void Signal(string root, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        lock (_lock)
        {
            if (_pending.TryGetValue(root, out PendingRoot? pending))
                _pending[root] = pending with { LastSignal = now, SignalCount = pending.SignalCount + 1 };
            else
                _pending[root] = new PendingRoot(now, now, 1);
        }
    }

    /// <summary>
    /// Returns and clears the roots whose most recent signal is at least <c>quietWindow</c> old at
    /// <paramref name="now"/> — the roots that have settled and are due for a single refresh hint. Roots
    /// still receiving changes are left pending until they go quiet.
    /// </summary>
    public IReadOnlyList<string> TakeDue(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_pending.Count == 0)
                return Array.Empty<string>();

            List<string>? due = null;
            foreach (var kvp in _pending)
            {
                PendingRoot pending = kvp.Value;
                if (now - pending.LastSignal >= _quietWindow
                    || now - pending.FirstSignal >= _maximumBatchWindow)
                {
                    (due ??= new List<string>()).Add(kvp.Key);
                }
            }
            if (due is null)
                return Array.Empty<string>();
            foreach (string root in due)
                _pending.Remove(root);
            return due;
        }
    }

    /// <summary>
    /// If signals accumulated while a refresh was running, starts their quiet/max-age windows at
    /// <paramref name="now"/>. No signal is discarded; this merely prevents an old in-flight timestamp from
    /// causing an immediate chain of tiny follow-up refreshes as soon as the prior callback returns.
    /// </summary>
    public bool PostponePending(string root, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        lock (_lock)
        {
            if (!_pending.TryGetValue(root, out PendingRoot? pending))
                return false;
            _pending[root] = pending with { FirstSignal = now, LastSignal = now };
            return true;
        }
    }

    /// <summary>True when at least one root is waiting to settle.</summary>
    public bool HasPending
    {
        get { lock (_lock) return _pending.Count > 0; }
    }
}
