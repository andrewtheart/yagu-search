using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Xunit;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Targets 100% branch/line coverage for preview-related methods:
/// - SearchResult.CreateShortPreview (line 241 — match near end of long line)
/// - ContentSearcher.NativeMatchDecoder.DecodeAndTruncate (lines 912-914 — secondary truncation)
/// - SearchResult.CompleteReservedEvictionWith (line 127 — not evicting; lines 143-146 — writer throws)
/// - FileGroup.AppendPreviewSnapshotFromEvictedStubs (line 763 — skip filename-only match)
/// </summary>
public sealed class PreviewCoverage100Tests
{
    // ───────── CreateShortPreview: match near end → end - start < ShortPreviewLength ─────────

    [Fact]
    public void CreateShortPreview_MatchNearEnd_AdjustsStartBackward()
    {
        // Line of 130 chars, match at position 125 (length 3).
        // contextChars = (120-3)/2 = 58
        // start = max(0, 125-58) = 67
        // end = min(130, 67+120) = 130
        // end - start = 63 < 120 → enters the adjustment branch (line 241)
        // start = max(0, 130-120) = 10
        string line = new string('a', 125) + "XYZ" + new string('b', 2); // 130 chars total
        int matchStart = 125;
        int matchLength = 3;

        var result = new SearchResult(
            FilePath: "test.txt",
            LineNumber: 1,
            MatchLine: line,
            MatchStartColumn: matchStart,
            MatchLength: matchLength,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());

        string preview = result.ShortPreview;

        // After adjustment: start=10, end=130, prefix=true (start>0), suffix=false (end==line.Length)
        Assert.StartsWith("\u2026", preview); // has prefix ellipsis
        Assert.DoesNotContain("XYZ" + "bb\u2026", preview); // no suffix since end == line.Length
        Assert.Contains("XYZ", preview);
    }

    [Fact]
    public void CreateShortPreview_MatchAtVeryEnd_NoSuffix()
    {
        // Another variant: match at the last char of a 150-char line
        string line = new string('z', 147) + "END"; // 150 chars
        int matchStart = 147;
        int matchLength = 3;

        var result = new SearchResult(
            FilePath: "test.txt",
            LineNumber: 1,
            MatchLine: line,
            MatchStartColumn: matchStart,
            MatchLength: matchLength,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());

        string preview = result.ShortPreview;

        // end = min(150, start+120) = 150 for some start > 0
        // end - start < 120 → adjusts start = max(0, 150-120) = 30
        // hasSuffix = 150 < 150 → false
        Assert.Contains("END", preview);
        Assert.StartsWith("\u2026", preview); // has prefix
        Assert.False(preview.EndsWith("\u2026")); // no suffix
    }

    // ───────── DecodeAndTruncate: all continuation bytes → backoff to 0 → secondary truncation ─────────

    [Fact]
    public unsafe void DecodeAndTruncate_AllContinuationBytes_HitsSecondaryTruncation()
    {
        // All 0x80 bytes (invalid continuation bytes) cause the backoff loop to go to 0.
        // s = "" → s.Length (0) <= MaxDisplayLength → enters lines 912-914.
        int maxBytes = (LineTruncator.MaxDisplayLength + 1) * 4;
        int len = maxBytes + 100; // exceeds maxBytes → bytesTruncated = true
        byte[] data = new byte[len];
        Array.Fill(data, (byte)0x80);

        fixed (byte* ptr = data)
        {
            string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(ptr, len);
            // After backoff to 0: s="", keep=min(0,500)=0, returns "…"
            Assert.Equal("\u2026", result);
        }
    }

    [Fact]
    public unsafe void DecodeAndTruncate_PartialContinuationAtBoundary_HitsSecondaryTruncation()
    {
        // Fill first few bytes with valid ASCII, then continuation bytes up to and past maxBytes.
        // The backoff will go past the continuation bytes back to the start of the ASCII prefix.
        int maxBytes = (LineTruncator.MaxDisplayLength + 1) * 4;
        int len = maxBytes + 50;
        byte[] data = new byte[len];

        // First 5 valid ASCII chars, rest all continuation bytes
        for (int i = 0; i < 5; i++) data[i] = (byte)('A' + i);
        for (int i = 5; i < len; i++) data[i] = 0x80;

        fixed (byte* ptr = data)
        {
            string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(ptr, len);
            // Backoff hits byte 5 = 0x80 (continuation) ... backs up through all 0x80 to byte 4 which is 'E' (0x45)
            // Wait: bytes 0-4 are ASCII (0x41-0x45), byte 5+ are 0x80.
            // ptr[maxBytes] = 0x80 → backoff: goes through all continuation bytes to byte 5 (0x80)...
            // Actually bytes 5 through maxBytes are all 0x80, so backoff goes to byte 4 (0x45, not continuation).
            // decodeBytes = 4 (stops when ptr[4] = 'E' = 0x45, (0x45 & 0xC0) = 0x40 ≠ 0x80)
            // Wait: ptr[decodeBytes] check. decodeBytes starts at maxBytes. ptr[maxBytes] = 0x80 → decrement.
            // It will keep decrementing until it finds a byte where (byte & 0xC0) != 0x80.
            // byte 4 = 'E' = 0x45: 0x45 & 0xC0 = 0x40 → stops. decodeBytes = 4.
            // Actually the while loop decrements THEN checks ptr[decodeBytes]:
            // while (decodeBytes > 0 && (ptr[decodeBytes] & 0xC0) == 0x80) decodeBytes--;
            // Start: decodeBytes = maxBytes. ptr[maxBytes] is beyond 5 ASCII bytes, so it's 0x80.
            // Decrements through all 0x80 bytes until decodeBytes = 5 (ptr[5] = 0x80 → decrements)
            // → decodeBytes = 4, ptr[4] = 0x45, (0x45 & 0xC0) = 0x40 ≠ 0x80 → stops.
            // s = UTF8.GetString(ptr, 4) = "ABCD" (4 chars)
            // 4 <= MaxDisplayLength → TRUE → enters lines 912-914
            // keep = min(4, TruncatedLength=500) = 4
            // returns "ABCD…"
            Assert.Equal("ABCD\u2026", result);
        }
    }

    // ───────── CompleteReservedEvictionWith: DiskOffset != EvictingOffset → returns false ─────────

    [Fact]
    public void CompleteReservedEvictionWith_NotReserved_ReturnsFalse()
    {
        var result = new SearchResult(
            FilePath: "test.txt",
            LineNumber: 1,
            MatchLine: "hello world",
            MatchStartColumn: 0,
            MatchLength: 5,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());

        // Don't call TryBeginEviction — DiskOffset stays at InMemoryOffset (-1)
        // CompleteReservedEvictionWith checks DiskOffset != EvictingOffset → returns false
        bool completed = result.CompleteReservedEvictionWith(
            (ml, cb, ca) => 42L);

        Assert.False(completed);
    }

    // ───────── CompleteReservedEvictionWith: writer throws → catch block ─────────

    [Fact]
    public void CompleteReservedEvictionWith_WriterThrows_ResetsOffsetAndRethrows()
    {
        var result = new SearchResult(
            FilePath: "test.txt",
            LineNumber: 1,
            MatchLine: "hello world",
            MatchStartColumn: 0,
            MatchLength: 5,
            ContextBefore: new[] { "before" },
            ContextAfter: new[] { "after" });

        // Reserve for eviction
        Assert.True(result.TryBeginEviction());
        Assert.True(result.IsEvicting);

        // Writer throws — catch block should reset DiskOffset to InMemoryOffset
        var ex = Assert.Throws<InvalidOperationException>(() =>
            result.CompleteReservedEvictionWith(
                (ml, cb, ca) => throw new InvalidOperationException("disk full")));

        Assert.Equal("disk full", ex.Message);
        // After catch: _diskOffset is reset to InMemoryOffset
        Assert.False(result.IsEvicted);
        Assert.False(result.IsEvicting);
    }

    // ───────── AppendPreviewSnapshotFromEvictedStubs: skipFileNameMatches skips LineNumber==0 ─────────

    [Fact]
    public void GetPreviewSnapshot_SkipsEvictedFileNameMatchesWhenContentExists()
    {
        var group = new FileGroup("test.txt");

        // Add a content match (LineNumber > 0) so HasContentMatches becomes true
        var contentResult = new SearchResult(
            FilePath: "test.txt",
            LineNumber: 5,
            MatchLine: "content match line",
            MatchStartColumn: 0,
            MatchLength: 7,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());
        group.Add(contentResult);

        // Now add a filename-only match (LineNumber == 0) that is pre-evicted.
        // It will be stored as an evicted stub since the group is not expanded.
        var fileNameMatch = SearchResult.CreatePreEvicted(
            filePath: "test.txt",
            lineNumber: 0,
            matchStartColumn: 0,
            matchLength: 4,
            diskOffset: 100L);
        group.Add(fileNameMatch);

        // GetPreviewSnapshot should skip the evicted stub with LineNumber==0
        // because HasContentMatches is true → skipFileNameMatches = true
        var snapshot = group.GetPreviewSnapshot(100);

        // Should contain only the content match, not the filename match
        Assert.Single(snapshot);
        Assert.Equal(5, snapshot[0].LineNumber);
    }

    [Fact]
    public void GetPreviewSnapshot_IncludesEvictedFileNameMatchesWhenNoContentMatches()
    {
        var group = new FileGroup("test.txt");

        // Add only filename-only matches (LineNumber == 0), no content matches
        // HasContentMatches will be false → skipFileNameMatches = false
        var fileNameMatch = SearchResult.CreatePreEvicted(
            filePath: "test.txt",
            lineNumber: 0,
            matchStartColumn: 0,
            matchLength: 4,
            diskOffset: 200L);
        group.Add(fileNameMatch);

        var snapshot = group.GetPreviewSnapshot(100);

        // Should include the filename match since skipFileNameMatches is false
        Assert.Single(snapshot);
        Assert.Equal(0, snapshot[0].LineNumber);
    }
}
