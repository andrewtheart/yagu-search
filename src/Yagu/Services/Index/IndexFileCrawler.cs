using System.IO.Enumeration;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// A file yielded by the index crawl (plan §5.4): its path plus the length and attributes carried straight
/// from the directory enumeration record, so the build loop needs neither a per-file
/// <c>File.GetAttributes</c> nor a <c>new FileInfo(path)</c> restat.
/// </summary>
internal readonly record struct IndexCrawlEntry(string Path, long Length, FileAttributes Attributes);

/// <summary>Completion seal for a full crawl. Access-denied subtrees and directories that vanish mid-crawl
/// remain ordinary skips, but a genuine directory I/O fault means the root was not completely enumerated and
/// must not replace a prior index.</summary>
internal sealed class IndexCrawlCompletion
{
    public bool IsComplete { get; private set; } = true;
    public string? FailedDirectory { get; private set; }
    public string? Failure { get; private set; }

    public void RecordFailure(string directory, Exception exception)
    {
        IsComplete = false;
        FailedDirectory ??= directory;
        Failure ??= exception.Message;
    }
}

internal sealed class IndexCrawlerFileSystem
{
    /// <summary>
    /// Enumerates a directory's immediate children as <see cref="IndexCrawlEntry"/> records — path, length,
    /// and attributes all read from the single native enumeration record (<c>WIN32_FIND_DATA</c>), never a
    /// per-file stat. Injectable so vanished/denied-directory, junction, cycle, and fault tests stay
    /// deterministic.
    /// </summary>
    public Func<string, IEnumerable<IndexCrawlEntry>> EnumerateEntries { get; init; } = DefaultEnumerate;

    public Func<string, string?> ResolveDirectoryTarget { get; init; } = static directory =>
    {
        FileSystemInfo? target = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true);
        return target?.FullName;
    };

    private static IEnumerable<IndexCrawlEntry> DefaultEnumerate(string directory)
        => new FileSystemEnumerable<IndexCrawlEntry>(
            directory,
            static (ref FileSystemEntry entry) => new IndexCrawlEntry(entry.ToFullPath(), entry.Length, entry.Attributes),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            });
}

/// <summary>
/// Fault-isolating directory crawler for full index builds. A directory that disappears, becomes denied,
/// or returns a transient I/O error is skipped without aborting the entire root. Directory reparse points
/// obey the existing ingestion setting: they are skipped by default; when enabled, only targets on the same
/// volume and under the indexed root are traversed, and a resolved-target visited set prevents junction
/// cycles. The index storage subtree is pruned before enumeration rather than walked and filtered file by file.
/// Each yielded <see cref="IndexCrawlEntry"/> carries the length and attributes from the enumeration record.
/// </summary>
internal static class IndexFileCrawler
{
    public static IEnumerable<IndexCrawlEntry> EnumerateFiles(
        string normalizedRoot,
        IndexIngestionPolicy policy,
        string excludedStorageRoot,
        CancellationToken cancellationToken,
        IndexCrawlerFileSystem? fileSystem = null,
        IndexCrawlCompletion? completion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRoot);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(excludedStorageRoot);
        fileSystem ??= new IndexCrawlerFileSystem();

        string root = IndexScopeIdentity.NormalizePath(normalizedRoot);
        string excluded = IndexScopeIdentity.NormalizePath(excludedStorageRoot);
        string rootVolume = Path.GetPathRoot(root)!;
        var pending = new Stack<(string Directory, string Identity)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push((root, root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string directory, string identity) = pending.Pop();
            if (!visited.Add(identity) || IsUnder(directory, excluded))
                continue;

            IEnumerator<IndexCrawlEntry>? enumerator = null;
            try
            {
                enumerator = TryOpenEnumerator(fileSystem, directory, completion);
                if (enumerator is null)
                    continue;
                while (MoveNextSafe(enumerator, directory, completion))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IndexCrawlEntry entry = enumerator.Current;
                    FileAttributes attributes = entry.Attributes;

                    if (!attributes.HasFlag(FileAttributes.Directory))
                    {
                        yield return entry;
                        continue;
                    }

                    string normalizedEntry = IndexScopeIdentity.NormalizePath(entry.Path);
                    if (IsUnder(normalizedEntry, excluded))
                        continue;

                    if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push((normalizedEntry, normalizedEntry));
                        continue;
                    }

                    if (!policy.FollowReparsePoints)
                        continue;

                    string? target;
                    try { target = fileSystem.ResolveDirectoryTarget(entry.Path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(target))
                        continue;
                    string normalizedTarget = IndexScopeIdentity.NormalizePath(target);
                    if (!string.Equals(Path.GetPathRoot(normalizedTarget), rootVolume, StringComparison.OrdinalIgnoreCase)
                        || !IsUnder(normalizedTarget, root))
                        continue;
                    pending.Push((normalizedEntry, normalizedTarget));
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }
    }

    private static bool MoveNextSafe(IEnumerator<IndexCrawlEntry> enumerator, string directory, IndexCrawlCompletion? completion)
    {
        try { return enumerator.MoveNext(); }
        catch (UnauthorizedAccessException ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Index crawl skipped inaccessible directory '{Directory}'; the remaining root continues.", directory);
            return false;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            // A directory (or its parent) vanished mid-crawl — extremely common on a live volume (temp/MUI
            // locale dirs). It genuinely no longer exists, so there is nothing to miss: skip it and keep the
            // build committable, exactly like a vanished file. Only a real I/O fault fails the crawl closed.
            YaguLog.For("ContentIndex").LogDebug(ex,
                "Index crawl skipped vanished directory '{Directory}'; the remaining root continues.", directory);
            return false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            completion?.RecordFailure(directory, ex);
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Index crawl became incomplete at directory '{Directory}'; the staged build will not be committed.", directory);
            return false;
        }
    }

    private static IEnumerator<IndexCrawlEntry>? TryOpenEnumerator(
        IndexCrawlerFileSystem fileSystem,
        string directory,
        IndexCrawlCompletion? completion)
    {
        try { return fileSystem.EnumerateEntries(directory).GetEnumerator(); }
        catch (UnauthorizedAccessException ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Index crawl could not access directory '{Directory}'; the remaining root continues.", directory);
            return null;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            // The directory vanished between being listed by its parent and being opened (a TOCTOU race that
            // is routine on a live volume). It no longer exists, so nothing is missed: skip it and keep the
            // build committable, like a vanished file. Only a genuine I/O fault fails the crawl closed.
            YaguLog.For("ContentIndex").LogDebug(ex,
                "Index crawl skipped vanished directory '{Directory}'; the remaining root continues.", directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            completion?.RecordFailure(directory, ex);
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Index crawl could not open directory '{Directory}'; the staged build will not be committed.", directory);
            return null;
        }
    }

    private static bool IsUnder(string path, string ancestor)
    {
        if (path.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = ancestor.EndsWith('\\') ? ancestor : ancestor + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
