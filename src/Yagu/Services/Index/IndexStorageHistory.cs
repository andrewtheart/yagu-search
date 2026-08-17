using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yagu.Services.Index;

/// <summary>One hourly physical index-storage snapshot, grouped by source drive.</summary>
public sealed class IndexStorageHistorySample
{
    public DateTimeOffset TimestampUtc { get; set; }
    public long TotalBytes { get; set; }
    public Dictionary<string, long> BytesByDrive { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One point rendered by the index-size history chart.</summary>
public readonly record struct IndexStorageHistoryPoint(DateTimeOffset TimestampUtc, long Bytes);

internal sealed class IndexStorageHistoryDocument
{
    public int Version { get; set; } = 1;
    public List<IndexStorageHistorySample> Samples { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(IndexStorageHistoryDocument))]
internal sealed partial class IndexStorageHistoryJsonContext : JsonSerializerContext;

/// <summary>
/// Persists a bounded history of physical index sizes. Sampling is capped at once per hour and retention
/// is controlled by the user's Indexing setting; rendering uses one polyline regardless of sample count.
/// </summary>
public sealed class IndexStorageHistoryStore
{
    public const string FileName = "index-size-history.json";
    public static readonly TimeSpan SampleInterval = TimeSpan.FromHours(1);

    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastSamples =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;

    public IndexStorageHistoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static string PathForIndexRoot(string indexRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        return Path.Combine(indexRoot, FileName);
    }

    /// <summary>
    /// Captures and stores a snapshot only when the latest point is at least one hour old. The expensive
    /// storage measurement delegate is never called for a skipped sample.
    /// </summary>
    public bool TryRecordIfDue(
        Func<IndexStorageSummary> capture,
        int retentionDays,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(capture);
        nowUtc = nowUtc.ToUniversalTime();
        if (LastSamples.TryGetValue(_path, out DateTimeOffset cached)
            && nowUtc - cached < SampleInterval)
        {
            return false;
        }

        lock (Gates.GetOrAdd(_path, static _ => new object()))
        {
            try
            {
                IndexStorageHistoryDocument document = LoadDocument();
                DateTimeOffset? latest = document.Samples.Count == 0
                    ? null
                    : document.Samples.Max(static sample => sample.TimestampUtc);
                if (latest is { } latestUtc)
                {
                    latestUtc = latestUtc.ToUniversalTime();
                    LastSamples[_path] = latestUtc;
                    if (nowUtc - latestUtc < SampleInterval)
                        return false;
                }

                IndexStorageSummary summary = capture();
                if (summary.Indexes.Count == 0 && !File.Exists(_path))
                    return false;

                document.Samples.Add(CreateSample(summary, nowUtc));
                Prune(document, retentionDays, nowUtc);
                SaveDocument(document);
                LastSamples[_path] = nowUtc;
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }
    }

    /// <summary>Reads the configured time window, returning an empty list for absent or corrupt history.</summary>
    public IReadOnlyList<IndexStorageHistorySample> Read(int retentionDays, DateTimeOffset nowUtc)
    {
        lock (Gates.GetOrAdd(_path, static _ => new object()))
        {
            try
            {
                IndexStorageHistoryDocument document = LoadDocument();
                Prune(document, retentionDays, nowUtc.ToUniversalTime());
                return document.Samples
                    .OrderBy(static sample => sample.TimestampUtc)
                    .ToArray();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Array.Empty<IndexStorageHistorySample>();
            }
        }
    }

    public static IReadOnlyList<string> AvailableDrives(IEnumerable<IndexStorageHistorySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return samples
            .SelectMany(static sample => sample.BytesByDrive.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static drive => drive, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Projects the collective total when drive is blank, otherwise one drive's size (0 if absent).</summary>
    public static IReadOnlyList<IndexStorageHistoryPoint> BuildSeries(
        IEnumerable<IndexStorageHistorySample> samples,
        string? drive)
    {
        ArgumentNullException.ThrowIfNull(samples);
        string? normalizedDrive = string.IsNullOrWhiteSpace(drive)
            ? null
            : IndexScopeIdentity.NormalizePath(drive);
        return samples
            .OrderBy(static sample => sample.TimestampUtc)
            .Select(sample => new IndexStorageHistoryPoint(
                sample.TimestampUtc,
                normalizedDrive is null
                    ? Math.Max(0, sample.TotalBytes)
                    : sample.BytesByDrive.TryGetValue(normalizedDrive, out long bytes)
                        ? Math.Max(0, bytes)
                        : 0))
            .ToArray();
    }

    private static IndexStorageHistorySample CreateSample(
        IndexStorageSummary summary,
        DateTimeOffset timestampUtc)
    {
        var bytesByDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (IndexStorageStat index in summary.Indexes)
        {
            string? drive = DriveForRoot(index.RootPath);
            if (drive is null)
                continue;
            bytesByDrive[drive] = bytesByDrive.TryGetValue(drive, out long existing)
                ? checked(existing + Math.Max(0, index.SizeBytes))
                : Math.Max(0, index.SizeBytes);
        }
        return new IndexStorageHistorySample
        {
            TimestampUtc = timestampUtc,
            TotalBytes = Math.Max(0, summary.TotalSizeBytes),
            BytesByDrive = bytesByDrive,
        };
    }

    private static string? DriveForRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        string? drive = Path.GetPathRoot(IndexScopeIdentity.NormalizePath(root));
        return string.IsNullOrWhiteSpace(drive) ? null : IndexScopeIdentity.NormalizePath(drive);
    }

    private static void Prune(
        IndexStorageHistoryDocument document,
        int retentionDays,
        DateTimeOffset nowUtc)
    {
        int days = AppSettings.NormalizeIndexStorageHistoryRetentionDays(retentionDays);
        DateTimeOffset cutoff = nowUtc.AddDays(-days);
        document.Samples = document.Samples
            .Where(sample => sample.TimestampUtc >= cutoff && sample.TimestampUtc <= nowUtc.Add(SampleInterval))
            .OrderBy(static sample => sample.TimestampUtc)
            .ToList();
    }

    private IndexStorageHistoryDocument LoadDocument()
    {
        if (!File.Exists(_path))
            return new IndexStorageHistoryDocument();
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        IndexStorageHistoryDocument? document = JsonSerializer.Deserialize(
            stream,
            IndexStorageHistoryJsonContext.Default.IndexStorageHistoryDocument);
        if (document is null || document.Version != 1)
            return new IndexStorageHistoryDocument();
        document.Samples ??= [];
        foreach (IndexStorageHistorySample sample in document.Samples)
        {
            sample.BytesByDrive = new Dictionary<string, long>(
                sample.BytesByDrive ?? new Dictionary<string, long>(),
                StringComparer.OrdinalIgnoreCase);
        }
        return document;
    }

    private void SaveDocument(IndexStorageHistoryDocument document)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    document,
                    IndexStorageHistoryJsonContext.Default.IndexStorageHistoryDocument);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}