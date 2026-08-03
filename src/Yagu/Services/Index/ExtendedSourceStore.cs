using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Persists and loads per-scope extended-source namespaces (archive / PDF-text / OCR) under the scope's
/// index directory (plan §7 Phase 4). Layout: <c>&lt;scopeDir&gt;/extended/&lt;kind&gt;/</c> holding the
/// <see cref="ExtendedSourceNamespaceSerializer"/> files. Because it lives inside the scope directory, the
/// existing <see cref="ContentIndexStore.DeleteScope"/> / <c>ContentIndexManager.ClearAll</c> already remove
/// it. Every namespace file is self-checked with a trailing SHA-256, so a torn or corrupt namespace loads as
/// <c>null</c> and its source kind falls back to live extraction — never a silent missed match.
/// </summary>
public sealed class ExtendedSourceStore
{
    internal const string ExtendedSubdir = "extended";
    internal const string DisabledMarkerSuffix = ".disabled";
    internal const string DisabledMarkerTempSuffix = ".tmp";
    internal const string ReplacementReadyMarkerFile = ".replacement-ready";
    internal const string PublishTempPrefix = ".publish-";
    internal const string BackupPrefix = ".backup-";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeDir;

    internal Action<string>? BeforeValidation { get; set; }
    internal Action? AfterInstall { get; set; }
    internal Action<string, string> MoveDirectory { get; set; } = Directory.Move;

    public ExtendedSourceStore(IContentIndexPathProvider pathProvider, string scopeId)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        _paths = pathProvider;
        _scopeDir = pathProvider.GetScopeDirectory(scopeId);
    }

    /// <summary>The directory that holds one source kind's namespace files.</summary>
    public string NamespaceDirectory(SpecialSourceKind kind) =>
        Path.Combine(_scopeDir, ExtendedSubdir, KindFolder(kind));

    internal string DisabledMarkerPath(SpecialSourceKind kind) =>
        Path.Combine(_scopeDir, ExtendedSubdir, KindFolder(kind) + DisabledMarkerSuffix);

    /// <summary>
    /// Atomically publishes <paramref name="ns"/> for its kind: writes it to a temp directory, validates it
    /// reads back, then replaces the current namespace directory. A failed validation leaves the previous
    /// namespace (if any) untouched. Returns true on success.
    /// </summary>
    public bool Publish(ExtendedSourceNamespace ns)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return PublishUnderLease(mutation, ns);
    }

    internal bool PublishUnderLease(IndexMutationContext mutation, ExtendedSourceNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentNullException.ThrowIfNull(ns);
        string finalDir = NamespaceDirectory(ns.Kind);
        string parent = Path.GetDirectoryName(finalDir)!;
        Directory.CreateDirectory(parent);
        string kindFolder = KindFolder(ns.Kind);
        string tempDir = Path.Combine(parent, PublishTempPrefix + kindFolder + "-" + Guid.NewGuid().ToString("N"));
        string? backupDir = null;
        bool installed = false;
        bool wasDisabled = File.Exists(DisabledMarkerPath(ns.Kind));

        try
        {
            ExtendedSourceNamespaceSerializer.Write(tempDir, ns);
            BeforeValidation?.Invoke(tempDir);
            if (ExtendedSourceNamespaceSerializer.TryRead(tempDir) is null)
            {
                YaguLog.For("ContentIndex").LogWarning(
                    "ExtendedSourceStore.Publish: freshly written {Kind} namespace failed validation; keeping the previous namespace.", ns.Kind);
                DeleteDirectorySafe(tempDir);
                return false;
            }
            IndexMutationFaults.Hit(IndexMutationFaults.ExtendedValidated);

            WriteMarker(Path.Combine(tempDir, ReplacementReadyMarkerFile));

            // Keep the prior complete namespace as a recoverable backup until the replacement is installed.
            // During the short rename gap TryLoad returns null, which means live extraction (fail safe).
            if (Directory.Exists(finalDir))
            {
                backupDir = Path.Combine(parent, BackupPrefix + kindFolder + "-" + Guid.NewGuid().ToString("N"));
                MoveDirectory(finalDir, backupDir);
                IndexMutationFaults.Hit(IndexMutationFaults.ExtendedBackupMoved);
            }
            MoveDirectory(tempDir, finalDir);
            installed = true;
            IndexMutationFaults.Hit(IndexMutationFaults.ExtendedInstalled);
            AfterInstall?.Invoke();

            // A durable disabled marker always wins at query time. Clear it only after the complete new
            // namespace is installed; if deletion fails, leave the replacement marker so recovery can finish.
            if (DeleteFileSafe(DisabledMarkerPath(ns.Kind)))
            {
                DeleteFileSafe(Path.Combine(finalDir, ReplacementReadyMarkerFile));
                IndexMutationFaults.Hit(IndexMutationFaults.ExtendedEnabled);
            }
            DeleteDirectorySafe(backupDir);
            IndexMutationFaults.Hit(IndexMutationFaults.ExtendedBackupDeleted);
            YaguLog.For("ContentIndex").LogDebug(
                "Published {Kind} extended-source namespace ({SourceCount} source(s)) to '{Dir}'.", ns.Kind, ns.SourceCount, finalDir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "ExtendedSourceStore.Publish failed for {Kind}; source kind falls back to live extraction.", ns.Kind);
            DeleteDirectorySafe(tempDir);
            if (installed)
                DeleteDirectorySafe(finalDir);
            if (backupDir is not null && Directory.Exists(backupDir) && !Directory.Exists(finalDir))
                TryMoveDirectory(backupDir, finalDir, Directory.Move);
            if (wasDisabled)
                WriteDisabledMarkerSafe(ns.Kind);
            return false;
        }
    }

    /// <summary>Loads the namespace for <paramref name="kind"/>, or <c>null</c> when absent/corrupt (→ live extract).</summary>
    public ExtendedSourceNamespace? TryLoad(SpecialSourceKind kind) => File.Exists(DisabledMarkerPath(kind))
        ? null
        : ExtendedSourceNamespaceSerializer.TryRead(NamespaceDirectory(kind));

    /// <summary>Deletes the namespace for one source kind (best effort).</summary>
    public void Delete(SpecialSourceKind kind)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        DeleteUnderLease(mutation, kind);
    }

    internal void DeleteUnderLease(IndexMutationContext mutation, SpecialSourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        string finalDir = NamespaceDirectory(kind);
        string parent = Path.GetDirectoryName(finalDir)!;
        Directory.CreateDirectory(parent);

        // Persist the disabled state BEFORE moving/deleting the prior namespace. A hard crash at any later
        // point therefore cannot resurrect a namespace whose determinism/fingerprint proof just failed.
        WriteDisabledMarker(kind);
        IndexMutationFaults.Hit(IndexMutationFaults.PdfDisabled);

        string? backupDir = null;
        if (Directory.Exists(finalDir))
        {
            backupDir = Path.Combine(parent, BackupPrefix + KindFolder(kind) + "-" + Guid.NewGuid().ToString("N"));
            MoveDirectory(finalDir, backupDir);
            IndexMutationFaults.Hit(IndexMutationFaults.ExtendedBackupMoved);
        }
        DeleteDirectorySafe(backupDir);
        IndexMutationFaults.Hit(IndexMutationFaults.ExtendedBackupDeleted);
    }

    internal static string KindFolder(SpecialSourceKind kind) => kind switch
    {
        SpecialSourceKind.PdfText => "pdf",
        SpecialSourceKind.ImageOcr => "ocr",
        SpecialSourceKind.Archive => "archive",
        _ => "other",
    };

    internal static void WriteMarker(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        stream.WriteByte(1);
        stream.Flush(flushToDisk: true);
    }

    private void WriteDisabledMarker(SpecialSourceKind kind)
    {
        string marker = DisabledMarkerPath(kind);
        string temp = marker + DisabledMarkerTempSuffix;
        WriteMarker(temp);
        File.Move(temp, marker, overwrite: true);
    }

    internal void WriteDisabledMarkerSafe(SpecialSourceKind kind)
    {
        try { WriteDisabledMarker(kind); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static bool TryMoveDirectory(string source, string destination, Action<string, string> move)
    {
        ArgumentNullException.ThrowIfNull(move);
        try
        {
            move(source, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool DeleteFileSafe(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void DeleteDirectorySafe(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return;
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "ExtendedSourceStore: could not delete '{Dir}'.", dir);
        }
    }
}
