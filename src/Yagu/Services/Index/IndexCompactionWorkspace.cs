using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// A private scratch directory on the index volume for one streaming merge or compaction: external-sort
/// spool runs plus the prepared (not yet published) layer. Nothing inside it is ever referenced by a
/// pointer slot, so abandoning it — by crash, cancellation, or a disk-guard abort — cannot affect the
/// live index. <see cref="IndexStorageRecovery"/> removes leftovers from an interrupted run.
/// </summary>
internal sealed class IndexCompactionWorkspace : IDisposable
{
    /// <summary>Directory-name prefix that marks a workspace as disposable residue.</summary>
    internal const string Prefix = ".compact-";

    private bool _disposed;

    private IndexCompactionWorkspace(string root)
    {
        Root = root;
        SpoolDirectory = Path.Combine(root, "spool");
        PreparedDirectory = Path.Combine(root, "prepared");
    }

    /// <summary>The workspace root, a direct child of the index root so it shares the index volume.</summary>
    public string Root { get; }

    /// <summary>Where <see cref="IndexExternalMergeSorter{TRecord}"/> spills sorted runs.</summary>
    public string SpoolDirectory { get; }

    /// <summary>Where the merged layer is written before validation and publication.</summary>
    public string PreparedDirectory { get; }

    public static IndexCompactionWorkspace Create(string indexRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        string root = Path.Combine(indexRoot, Prefix + Guid.NewGuid().ToString("N"));
        var workspace = new IndexCompactionWorkspace(root);
        Directory.CreateDirectory(workspace.SpoolDirectory);
        Directory.CreateDirectory(workspace.PreparedDirectory);
        IndexMutationFaults.Hit(IndexMutationFaults.CompactionWorkspaceCreated);
        return workspace;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        TryDelete(Root);
    }

    internal static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "Could not remove index compaction workspace '{Directory}'.", directory);
        }
    }
}
