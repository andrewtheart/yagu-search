using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

internal readonly record struct IndexRecoveryResult(
    int DeletedBuildWorkspaces,
    int RecoveredScopes,
    int RestoredPdfNamespaces,
    int DeletedPdfBackups,
    int Failures);

internal sealed class IndexRecoveryFileSystem
{
    public Func<string, string[]> GetDirectories { get; init; } = Directory.GetDirectories;
    public Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    public Action<string> DeleteDirectory { get; init; } = static path => Directory.Delete(path, recursive: true);
    public Action<string> DeleteFile { get; init; } = File.Delete;
    public Action<string, string> MoveDirectory { get; init; } = Directory.Move;
    public Func<string, DateTime> GetLastWriteTimeUtc { get; init; } = Directory.GetLastWriteTimeUtc;
    public Action<IContentIndexPathProvider, string, int, IndexMutationContext> RecoverScope { get; init; }
        = static (paths, scopeId, retained, mutation) =>
            new ContentIndexStore(paths, scopeId, retained).RecoverOrphansUnderLease(mutation);
}

/// <summary>
/// Repairs crash residue whenever a process obtains the global index mutation lease. Because an acquired
/// lease proves that no build/refresh/validation process is active for this storage root, every root-level
/// <c>.build-*</c> workspace is abandoned and safe to remove immediately. It also reconciles PDF namespace
/// backups left by a hard process exit and invokes each scope store's normal retention pass to remove
/// orphaned generation/segment/temp directories. Failures are isolated per artifact and never prevent the
/// requested index operation from proceeding.
/// </summary>
internal static class IndexStorageRecovery
{
    private const string BuildWorkspacePrefix = ".build-";
    private const string PdfBackupPrefix = ".pdf-backup-";

    public static IndexRecoveryResult RecoverUnderLease(
        IndexMutationContext mutation,
        IContentIndexPathProvider paths,
        int retainedGenerations = IndexBuildDefaults.RetainedGenerations,
        IndexRecoveryFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(paths);
        mutation.EnsureOwns(paths);
        fileSystem ??= new IndexRecoveryFileSystem();

        int deletedBuilds = 0;
        int recoveredScopes = 0;
        int restoredPdfs = 0;
        int deletedBackups = 0;
        int failures = 0;

        foreach (string directory in SafeGetDirectories(paths.IndexRoot, fileSystem, ref failures))
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith(BuildWorkspacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (TryDelete(directory, fileSystem))
                {
                    deletedBuilds++;
                    IndexMutationFaults.Hit(IndexMutationFaults.RecoveryBuildWorkspaceDeleted);
                }
                else
                    failures++;
                continue;
            }

            // Dot-prefixed root metadata/directories are not scope ids.
            if (name.StartsWith(".", StringComparison.Ordinal))
                continue;

            try
            {
                fileSystem.RecoverScope(paths, name, retainedGenerations, mutation);
                recoveredScopes++;
                IndexMutationFaults.Hit(IndexMutationFaults.RecoveryScopeReconciled);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failures++;
                YaguLog.For("ContentIndex").LogWarning(ex,
                    "Crash recovery could not reconcile index scope '{ScopeDir}'; leaving it fail-safe for a later pass.", directory);
            }

            RecoverPdfBackups(directory, fileSystem, ref restoredPdfs, ref deletedBackups, ref failures);
        }

        IndexMutationFaults.Hit(IndexMutationFaults.RecoveryCompleted);

        if (deletedBuilds > 0 || restoredPdfs > 0 || deletedBackups > 0 || failures > 0)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Index crash recovery: removed {BuildCount} abandoned build workspace(s), reconciled {ScopeCount} scope(s), restored {RestoredPdfCount} PDF namespace(s), removed {BackupCount} stale PDF backup(s), failures={FailureCount}.",
                deletedBuilds, recoveredScopes, restoredPdfs, deletedBackups, failures);
        }

        return new IndexRecoveryResult(deletedBuilds, recoveredScopes, restoredPdfs, deletedBackups, failures);
    }

    private static void RecoverPdfBackups(
        string scopeDirectory,
        IndexRecoveryFileSystem fileSystem,
        ref int restoredPdfs,
        ref int deletedBackups,
        ref int failures)
    {
        RecoverExtendedSourceBackups(scopeDirectory, "pdf", fileSystem, ref restoredPdfs, ref deletedBackups, ref failures);
        RecoverExtendedSourceBackups(scopeDirectory, "ocr", fileSystem, ref restoredPdfs, ref deletedBackups, ref failures);
    }

    private static void RecoverExtendedSourceBackups(
        string scopeDirectory,
        string kindFolder,
        IndexRecoveryFileSystem fileSystem,
        ref int restoredPdfs,
        ref int deletedBackups,
        ref int failures)
    {
        string extendedDirectory = Path.Combine(scopeDirectory, "extended");
        string[] directories = SafeGetDirectories(extendedDirectory, fileSystem, ref failures);
        string publishTempPrefix = ExtendedSourceStore.PublishTempPrefix + kindFolder + "-";
        foreach (string temp in directories.Where(path =>
                     Path.GetFileName(path).StartsWith(publishTempPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryDelete(temp, fileSystem))
                failures++;
        }

        string currentBackupPrefix = ExtendedSourceStore.BackupPrefix + kindFolder + "-";
        string[] backups = directories
            .Where(path => (kindFolder == "pdf" && Path.GetFileName(path).StartsWith(PdfBackupPrefix, StringComparison.OrdinalIgnoreCase))
                || Path.GetFileName(path).StartsWith(currentBackupPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => SafeLastWriteTime(path, fileSystem))
            .ToArray();

        string livePdf = Path.Combine(extendedDirectory, kindFolder);
        string disabledMarker = Path.Combine(
            extendedDirectory,
            kindFolder + ExtendedSourceStore.DisabledMarkerSuffix);
        string disabledTemp = disabledMarker + ExtendedSourceStore.DisabledMarkerTempSuffix;
        string replacementReady = Path.Combine(livePdf, ExtendedSourceStore.ReplacementReadyMarkerFile);
        if (fileSystem.FileExists(disabledTemp) && !TryDeleteFile(disabledTemp, fileSystem))
            failures++;

        bool disabled = fileSystem.FileExists(disabledMarker);
        bool liveExists = fileSystem.DirectoryExists(livePdf);
        bool replacementInstalled = liveExists && fileSystem.FileExists(replacementReady);

        if (disabled && !replacementInstalled)
        {
            // A committed disable state always wins. Never restore an old namespace whose determinism or
            // fingerprint proof failed; absence/disabled means every PDF is extracted live.
            if (liveExists && !TryDelete(livePdf, fileSystem))
                failures++;
            DeleteBackups(backups, fileSystem, ref deletedBackups, ref failures);
            return;
        }

        if (disabled && replacementInstalled)
        {
            // A complete replacement carries its own durable ready marker. Finish enabling it; if marker
            // deletion fails, TryLoad still observes disabled and remains fail-safe until a later retry.
            if (TryDeleteFile(disabledMarker, fileSystem))
            {
                disabled = false;
                if (!TryDeleteFile(replacementReady, fileSystem))
                    failures++;
            }
            else
            {
                failures++;
            }
        }
        else if (replacementInstalled && !TryDeleteFile(replacementReady, fileSystem))
        {
            failures++;
        }

        if (backups.Length == 0)
            return;

        int firstBackupToDelete = 0;
        if (!fileSystem.DirectoryExists(livePdf) && !disabled)
        {
            try
            {
                fileSystem.MoveDirectory(backups[0], livePdf);
                restoredPdfs++;
                firstBackupToDelete = 1;
                IndexMutationFaults.Hit(IndexMutationFaults.RecoveryPdfRestored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                return; // keep every backup; a later pass can retry safely
            }
        }

        for (int i = firstBackupToDelete; i < backups.Length; i++)
        {
            if (TryDelete(backups[i], fileSystem))
            {
                deletedBackups++;
                IndexMutationFaults.Hit(IndexMutationFaults.RecoveryPdfBackupDeleted);
            }
            else
                failures++;
        }
    }

    private static void DeleteBackups(
        IReadOnlyList<string> backups,
        IndexRecoveryFileSystem fileSystem,
        ref int deletedBackups,
        ref int failures)
    {
        foreach (string backup in backups)
        {
            if (TryDelete(backup, fileSystem))
            {
                deletedBackups++;
                IndexMutationFaults.Hit(IndexMutationFaults.RecoveryPdfBackupDeleted);
            }
            else
            {
                failures++;
            }
        }
    }

    private static string[] SafeGetDirectories(
        string directory,
        IndexRecoveryFileSystem fileSystem,
        ref int failures)
    {
        try
        {
            return fileSystem.DirectoryExists(directory)
                ? fileSystem.GetDirectories(directory)
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures++;
            return Array.Empty<string>();
        }
    }

    internal static DateTime SafeLastWriteTime(string directory, IndexRecoveryFileSystem fileSystem)
    {
        try { return fileSystem.GetLastWriteTimeUtc(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static bool TryDelete(string directory, IndexRecoveryFileSystem fileSystem)
    {
        try
        {
            fileSystem.DeleteDirectory(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path, IndexRecoveryFileSystem fileSystem)
    {
        try
        {
            fileSystem.DeleteFile(path);
            return !fileSystem.FileExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
