using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// A thin, fault-tolerant <see cref="FileSystemWatcher"/> wrapper for one indexed root (plan §11.4, Phase 3).
/// It raises <see cref="Changed"/> on any create/change/delete/rename anywhere under the root so an
/// incremental refresh can react sooner than the next scheduled pass. It is <b>only a latency hint</b>: on a
/// buffer overflow or an internal error the watcher signals once (so the root is re-checked) and keeps
/// running — USN continuity, not the watcher, establishes freshness, so a missed or dropped event never
/// corrupts the index. Registration can be slow for a deep tree, so callers construct instances off the UI
/// thread; construction never throws (a failed registration disposes and leaves <see cref="IsWatching"/>
/// false, degrading gracefully to USN/manual).
/// </summary>
public sealed class ContentIndexRootWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly string _root;
    private readonly string? _excludedStorageRoot;
    private readonly Action<FileSystemWatcher> _disableWatcher;
    private bool _disposed;

    /// <summary>Raised (on a threadpool thread) when something under the root may have changed. The argument is the root.</summary>
    public event Action<string>? Changed;

    public ContentIndexRootWatcher(string root, string? excludedStorageRoot = null)
        : this(
            root,
            excludedStorageRoot,
            static path => new FileSystemWatcher(path),
            static watcher => watcher.EnableRaisingEvents = true,
            static watcher => watcher.EnableRaisingEvents = false)
    {
    }

    internal ContentIndexRootWatcher(
        string root,
        string? excludedStorageRoot,
        Func<string, FileSystemWatcher> createWatcher,
        Action<FileSystemWatcher> enableWatcher,
        Action<FileSystemWatcher> disableWatcher)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _disableWatcher = disableWatcher;
        _excludedStorageRoot = string.IsNullOrWhiteSpace(excludedStorageRoot)
            ? null
            : IndexScopeIdentity.NormalizePath(excludedStorageRoot);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = createWatcher(root);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
            watcher.InternalBufferSize = 64 * 1024;
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError; // buffer overflow / internal failure → hint once, keep watching
            enableWatcher(watcher);
            _watcher = watcher;
            YaguLog.For("ContentIndex").LogInformation("Filesystem watcher registered for indexed root '{Root}'.", root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Watch-descriptor limit, denied, or a racing delete → degrade to USN/manual, never throw.
            watcher?.Dispose();
            _watcher = null;
            YaguLog.For("ContentIndex").LogWarning(ex, "Could not register a filesystem watcher for '{Root}'; degrading to USN/manual freshness.", root);
        }
    }

    /// <summary>True when a live OS watch was successfully registered.</summary>
    public bool IsWatching => _watcher is not null;

    internal void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsExcluded(e.FullPath))
            Raise();
    }

    internal void OnRenamed(object sender, RenamedEventArgs e)
    {
        // A rename wholly inside index storage is Yagu's own publication noise. A rename crossing the
        // boundary still matters to the indexed root, so suppress only when both paths are excluded.
        if (!IsExcluded(e.OldFullPath) || !IsExcluded(e.FullPath))
            Raise();
    }

    private bool IsExcluded(string path)
        => _excludedStorageRoot is not null && IndexedRootsPolicy.Covers(_excludedStorageRoot, path);

    internal void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow (we lost events) → the safe response is a single re-check hint; USN will
        // reconcile the real change set. Do NOT disable the watcher — keep providing hints.
        YaguLog.For("ContentIndex").LogWarning(e.GetException(), "Filesystem watcher error/overflow for '{Root}'; hinting a re-check (USN reconciles the real change set).", _root);
        Raise();
    }

    private void Raise()
    {
        if (_disposed)
            return;
        try { Changed?.Invoke(_root); } catch { /* a hint handler must never crash the watcher */ }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_watcher is not null)
        {
            try
            {
                _disableWatcher(_watcher);
                _watcher.Created -= OnChanged;
                _watcher.Changed -= OnChanged;
                _watcher.Deleted -= OnChanged;
                _watcher.Renamed -= OnRenamed;
                _watcher.Error -= OnError;
                _watcher.Dispose();
            }
            catch { /* best effort */ }
        }
    }
}
