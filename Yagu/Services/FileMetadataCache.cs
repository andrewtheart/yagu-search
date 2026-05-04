using System.Collections.Concurrent;

namespace Yagu.Services;

internal readonly record struct FileMetadata(long Length, DateTime LastModified);

internal readonly record struct FileSearchOutcome(int MatchCount, long BytesScanned, int EntriesScanned = 0);

internal static class FileMetadataCache
{
    private static readonly ConcurrentDictionary<string, FileMetadata> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Clear() => s_cache.Clear();

    public static void Set(string path, FileMetadata metadata) => s_cache[path] = metadata;

    public static bool TryGet(string path, out FileMetadata metadata) => s_cache.TryGetValue(path, out metadata);
}