using Yagu.Models;
using Yagu.Helpers;
using System.Text;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Re-reading evicted results from disk on demand: reading match/context payloads (including the
/// source-backed UTF-8 column estimation) and applying them back onto the result objects.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Hydrate an evicted result from disk so its full data is available.</summary>
    public void HydrateResult(SearchResult result)
    {
        if (!result.IsEvicted) return;

        if (result.IsSourceBacked)
        {
            if (ReadSourceBackedHydrationPayload(result) is { } payload)
                ApplyHydrationPayloads([payload]);
            return;
        }

        if (_resultStore is not null)
        {
            try
            {
                result.Hydrate(_resultStore);
            }
            catch (Exception ex) when (ex is EndOfStreamException or FormatException or InvalidOperationException or ObjectDisposedException)
            {
                YaguLog.For("ViewModel").LogWarning("Could not hydrate result at offset {Offset}: {Error}", result.DiskOffset, ex.Message);
            }
        }
    }

    /// <summary>
    /// Hydrate multiple evicted results in a single batched read, minimizing lock contention.
    /// </summary>
    public void HydrateResults(IReadOnlyList<SearchResult> results)
    {
        ApplyHydrationPayloads(ReadHydrationPayloads(results));
    }

    /// <summary>
    /// Read evicted result payloads from disk without mutating UI-bound SearchResult objects.
    /// Safe to call from a worker thread.
    /// </summary>
    public IReadOnlyList<HydrationPayload> ReadHydrationPayloads(IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0) return Array.Empty<HydrationPayload>();

        List<HydrationPayload>? payloads = null;

        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].IsSourceBacked && ReadSourceBackedHydrationPayload(results[i]) is { } payload)
                (payloads ??= new List<HydrationPayload>()).Add(payload);
        }

        if (_resultStore is null)
            return payloads ?? (IReadOnlyList<HydrationPayload>)Array.Empty<HydrationPayload>();

        // Collect offsets for evicted items
        long[] offsets = new long[results.Count];
        int evictedCount = 0;
        int[] evictedIndices = new int[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].DiskOffset >= 0)
            {
                offsets[evictedCount] = results[i].DiskOffset;
                evictedIndices[evictedCount] = i;
                evictedCount++;
            }
        }
        if (evictedCount == 0)
            return payloads ?? (IReadOnlyList<HydrationPayload>)Array.Empty<HydrationPayload>();

        try
        {
            var readResults = _resultStore.ReadBatch(offsets.AsSpan(0, evictedCount));
            payloads ??= new List<HydrationPayload>(evictedCount);
            for (int i = 0; i < evictedCount; i++)
            {
                var data = readResults[i];
                if (data is null) continue;
                var (ml, cb, ca) = data.Value;
                var result = results[evictedIndices[i]];
                payloads.Add(new HydrationPayload(
                    result,
                    ml,
                    cb,
                    ca,
                    result.MatchStartColumn,
                    result.MatchLength,
                    result.SourceMatchStartColumn));
            }
            return payloads;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            YaguLog.For("ViewModel").LogWarning("Batch hydration failed: {Error}", ex.Message);
            return payloads ?? (IReadOnlyList<HydrationPayload>)Array.Empty<HydrationPayload>();
        }
    }

    private HydrationPayload? ReadSourceBackedHydrationPayload(SearchResult result)
    {
        if (result.LineNumber <= 0 || string.IsNullOrWhiteSpace(result.FilePath)) return null;

        try
        {
            int contextLineCount = Math.Max(0, ContextLines);
            var before = new Queue<string>(contextLineCount);
            var after = new List<string>(contextLineCount);
            string? matchLine = null;
            int currentLineNumber = 0;

            foreach (var line in File.ReadLines(result.FilePath))
            {
                currentLineNumber++;
                if (currentLineNumber < result.LineNumber)
                {
                    if (contextLineCount > 0)
                    {
                        if (before.Count == contextLineCount)
                            before.Dequeue();
                        before.Enqueue(LineTruncator.Truncate(line));
                    }
                    continue;
                }

                if (currentLineNumber == result.LineNumber)
                {
                    matchLine = line;
                    continue;
                }

                if (after.Count < contextLineCount)
                {
                    after.Add(LineTruncator.Truncate(line));
                    if (after.Count < contextLineCount)
                        continue;
                }
                break;
            }

            if (matchLine is null) return null;

            int sourceMatchStart = EstimateUtf16ColumnFromUtf8ByteOffset(matchLine, result.SourceMatchStartColumn);
            int matchLength = EstimateUtf16LengthFromUtf8ByteLength(matchLine, sourceMatchStart, result.MatchLength);
            matchLength = Math.Min(matchLength, Math.Max(0, matchLine.Length - sourceMatchStart));
            var displayLine = LineTruncator.TruncateAroundMatch(matchLine, sourceMatchStart, matchLength);

            return new HydrationPayload(
                result,
                displayLine.Text,
                before.ToArray(),
                after,
                displayLine.MatchStart,
                matchLength,
                sourceMatchStart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            YaguLog.For("ViewModel").LogWarning("Source-backed hydration failed for '{File}': {Error}", result.FilePath, ex.Message);
            return null;
        }
    }

    private static int EstimateUtf16LengthFromUtf8ByteLength(string line, int sourceColumn, int utf8ByteLength)
    {
        if (utf8ByteLength <= 0 || sourceColumn >= line.Length) return 0;
        int consumedBytes = 0;
        int chars = 0;

        while (sourceColumn + chars < line.Length && consumedBytes < utf8ByteLength)
        {
            int charCount = 1;
            if (char.IsHighSurrogate(line[sourceColumn + chars])
                && sourceColumn + chars + 1 < line.Length
                && char.IsLowSurrogate(line[sourceColumn + chars + 1]))
            {
                charCount = 2;
            }

            int byteCount = Encoding.UTF8.GetByteCount(line.AsSpan(sourceColumn + chars, charCount));
            if (consumedBytes + byteCount > utf8ByteLength && chars > 0)
                break;

            consumedBytes += byteCount;
            chars += charCount;
        }

        return chars;
    }

    private static int EstimateUtf16ColumnFromUtf8ByteOffset(string line, int utf8ByteOffset)
    {
        if (utf8ByteOffset <= 0) return 0;

        int consumedBytes = 0;
        int column = 0;
        while (column < line.Length && consumedBytes < utf8ByteOffset)
        {
            int charCount = 1;
            if (char.IsHighSurrogate(line[column])
                && column + 1 < line.Length
                && char.IsLowSurrogate(line[column + 1]))
            {
                charCount = 2;
            }

            int byteCount = Encoding.UTF8.GetByteCount(line.AsSpan(column, charCount));
            if (consumedBytes + byteCount > utf8ByteOffset)
                break;

            consumedBytes += byteCount;
            column += charCount;
        }

        return column;
    }

    /// <summary>Apply hydrated payloads to SearchResult objects. Must run on the UI thread.</summary>
    public static void ApplyHydrationPayloads(IEnumerable<HydrationPayload> payloads)
    {
        foreach (var payload in payloads)
        {
            payload.Result.HydrateFrom(
                payload.MatchLine,
                payload.ContextBefore,
                payload.ContextAfter,
                payload.MatchStartColumn,
                payload.MatchLength,
                payload.SourceMatchStartColumn);
        }
    }
}
