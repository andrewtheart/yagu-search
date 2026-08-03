using System.Threading;

using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Owns the optional <c>FileSystemWatcher</c> latency hints for the indexed roots (plan §11.4, Phase 3):
/// one <see cref="ContentIndexRootWatcher"/> per root feeding a shared <see cref="RootChangeDebouncer"/>, and
/// a timer that fires a debounced <c>onRootSettled</c> callback once a root goes quiet. The callback (wired
/// by the app to an incremental refresh) runs on a threadpool thread with a per-root re-entrancy guard so a
/// slow refresh never stacks. This is <b>only a hint layer</b> — USN continuity stays authoritative, so a
/// registration failure, a dropped event, or a disabled watcher simply means the next scheduled/manual pass
/// picks the change up. Nothing here throws to the caller; watcher registration is expected to run off the
/// UI thread (deep-tree registration can be slow).
/// </summary>
public sealed class ContentIndexWatcherHintService : IDisposable
{
    /// <summary>Default poll cadence for draining settled roots from the debouncer.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Default quiet window a root must be idle before a single refresh hint fires.</summary>
    public static readonly TimeSpan DefaultQuietWindow = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time a continuously busy root can defer one batched refresh.</summary>
    public static readonly TimeSpan DefaultMaximumBatchWindow = TimeSpan.FromMinutes(2);

    private readonly List<ContentIndexRootWatcher> _watchers = new();
    private readonly RootChangeDebouncer _debouncer;
    private readonly Action<string> _onRootSettled;
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inFlightLock = new();
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>Number of roots with a live OS watch (the rest degraded to USN/manual). For diagnostics/tests.</summary>
    public int ActiveWatchCount { get; private set; }

    public ContentIndexWatcherHintService(
        IReadOnlyList<string> roots,
        Action<string> onRootSettled,
        string? excludedStorageRoot = null,
        TimeSpan? quietWindow = null,
        TimeSpan? maximumBatchWindow = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _onRootSettled = onRootSettled ?? throw new ArgumentNullException(nameof(onRootSettled));
        _debouncer = new RootChangeDebouncer(
            quietWindow ?? DefaultQuietWindow,
            maximumBatchWindow ?? DefaultMaximumBatchWindow);

        foreach (string root in roots)
        {
            var watcher = new ContentIndexRootWatcher(root, excludedStorageRoot);
            watcher.Changed += OnRootSignaled;
            _watchers.Add(watcher);
            if (watcher.IsWatching)
                ActiveWatchCount++;
        }

        TimeSpan interval = pollInterval ?? DefaultPollInterval;
        _timer = new Timer(_ => Drain(), null, interval, interval);
        YaguLog.For("ContentIndex").LogInformation("Watcher hint service started for {WatcherCount} root(s); {ActiveWatchCount} live OS watch(es) (the rest degrade to USN/manual).", _watchers.Count, ActiveWatchCount);
    }

    private void OnRootSignaled(string root)
    {
        if (_disposed)
            return;
        _debouncer.Signal(root, DateTimeOffset.UtcNow);
    }

    private void Drain()
    {
        if (_disposed)
            return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<string> due = _debouncer.TakeDue(now);
        foreach (string root in due)
        {
            lock (_inFlightLock)
            {
                if (!_inFlight.Add(root))
                {
                    // TakeDue removed this pending root. Reinsert it rather than losing changes that
                    // arrived during the active refresh, then give the follow-up batch a fresh quiet window.
                    _debouncer.Signal(root, now);
                    continue;
                }
            }

            YaguLog.For("ContentIndex").LogDebug("Root '{Root}' settled → hinting an incremental refresh.", root);
            _ = Task.Run(() =>
            {
                try { _onRootSettled(root); }
                catch { /* a hint-driven refresh must never crash the app */ }
                finally
                {
                    // Signals that arrived during this callback remain pending, but should batch from the
                    // completion point instead of immediately cascading into another micro-refresh.
                    _debouncer.PostponePending(root, DateTimeOffset.UtcNow);
                    lock (_inFlightLock)
                        _inFlight.Remove(root);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.Changed -= OnRootSignaled;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
