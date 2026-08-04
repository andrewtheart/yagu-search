using System;
using System.Linq;
using System.Text.Json;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>
/// Unit tests for <see cref="IndexWorkerProtocol"/> — the Base64 little-endian wire encoding for the index
/// worker's variable-length primitive payloads (trigram <c>u32</c> arrays and candidate <c>i32</c> arrays),
/// and the protocol constants.
/// </summary>
public sealed class IndexWorkerProtocolTests
{
    [Fact]
    public void Trigrams_RoundTrip_PreservesValuesAndOrder()
    {
        uint[] input = { 0u, 1u, 0x00FFFFFFu, 0x12345678u, uint.MaxValue };
        string encoded = IndexWorkerProtocol.EncodeTrigrams(input);
        uint[] decoded = IndexWorkerProtocol.DecodeTrigrams(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void Trigrams_LittleEndianByteLayout_IsExact()
    {
        // 0x12345678 → bytes 78 56 34 12 (LE).
        string encoded = IndexWorkerProtocol.EncodeTrigrams(new uint[] { 0x12345678u });
        byte[] bytes = Convert.FromBase64String(encoded);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DecodeTrigrams_NullOrEmpty_ReturnsEmpty(string? value)
    {
        Assert.Empty(IndexWorkerProtocol.DecodeTrigrams(value));
    }

    [Fact]
    public void DecodeTrigrams_MalformedLength_Throws()
    {
        // 3 bytes is not a multiple of 4.
        string bad = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        Assert.Throws<FormatException>(() => IndexWorkerProtocol.DecodeTrigrams(bad));
    }

    [Fact]
    public void EncodeTrigrams_Empty_ProducesEmptyBase64()
    {
        Assert.Equal(string.Empty, IndexWorkerProtocol.EncodeTrigrams(Array.Empty<uint>()));
    }

    [Fact]
    public void Candidates_RoundTrip_PreservesValuesIncludingNegativesAndOrder()
    {
        int[] input = { 0, 1, -1, int.MinValue, int.MaxValue, 42 };
        string encoded = IndexWorkerProtocol.EncodeCandidates(input);
        int[] decoded = IndexWorkerProtocol.DecodeCandidates(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void Candidates_LittleEndianByteLayout_IsExact()
    {
        // -1 → bytes FF FF FF FF.
        string encoded = IndexWorkerProtocol.EncodeCandidates(new[] { -1 });
        byte[] bytes = Convert.FromBase64String(encoded);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DecodeCandidates_NullOrEmpty_ReturnsEmpty(string? value)
    {
        Assert.Empty(IndexWorkerProtocol.DecodeCandidates(value));
    }

    [Fact]
    public void DecodeCandidates_MalformedLength_Throws()
    {
        string bad = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });
        Assert.Throws<FormatException>(() => IndexWorkerProtocol.DecodeCandidates(bad));
    }

    [Fact]
    public void EncodeCandidates_Empty_ProducesEmptyBase64()
    {
        Assert.Equal(string.Empty, IndexWorkerProtocol.EncodeCandidates(Array.Empty<int>()));
    }

    [Fact]
    public void RequiredIndexAbiVersion_MatchesNativeIndexAbi()
    {
        // Decoupled from the search qg_abi_version; must track the native qg_index_abi_version (=1).
        Assert.Equal(1, IndexWorkerProtocol.RequiredIndexAbiVersion);
    }

    [Fact]
    public void Ops_And_MessageTypes_AreStableWireStrings()
    {
        Assert.Equal("ping", IndexWorkerProtocol.Ops.Ping);
        Assert.Equal("extract", IndexWorkerProtocol.Ops.Extract);
        Assert.Equal("queryContentBin", IndexWorkerProtocol.Ops.QueryContentBin);
        Assert.Equal("buildScope", IndexWorkerProtocol.Ops.BuildScope);
        Assert.Equal("refreshAuto", IndexWorkerProtocol.Ops.RefreshAuto);
        Assert.Equal("validateScope", IndexWorkerProtocol.Ops.ValidateScope);
        Assert.Equal("cancelBuild", IndexWorkerProtocol.Ops.CancelBuild);
        Assert.Equal("shutdown", IndexWorkerProtocol.Ops.Shutdown);
        Assert.Equal("ready", IndexWorkerProtocol.MessageTypes.Ready);
        Assert.Equal("accepted", IndexWorkerProtocol.MessageTypes.Accepted);
        Assert.Equal("progress", IndexWorkerProtocol.MessageTypes.Progress);
        Assert.Equal("result", IndexWorkerProtocol.MessageTypes.Result);
        Assert.Equal("error", IndexWorkerProtocol.MessageTypes.Error);
        Assert.Equal(3, IndexWorkerProtocol.ControlProtocolVersion);
    }

    [Fact]
    public void Message_RoundTripsPostBuildCatchUpResult()
    {
        var message = new IndexWorkerMessage
        {
            PostBuildCatchUpChecked = true,
            PostBuildCatchUpThresholdChanges = 30_000,
            PostBuildCatchUpOutcome = IncrementalUpdateOutcome.SegmentAppended.ToString(),
            PostBuildCatchUpJournalChangeCount = 30_001,
            PostBuildCatchUpChangeCountComplete = true,
            PostBuildCatchUpThresholdExceeded = true,
        };

        string json = JsonSerializer.Serialize(message, IndexWorkerJsonContext.Default.IndexWorkerMessage);
        IndexWorkerMessage restored = JsonSerializer.Deserialize(
            json,
            IndexWorkerJsonContext.Default.IndexWorkerMessage)!;

        Assert.True(restored.PostBuildCatchUpChecked);
        Assert.Equal(30_000, restored.PostBuildCatchUpThresholdChanges);
        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended.ToString(), restored.PostBuildCatchUpOutcome);
        Assert.Equal(30_001, restored.PostBuildCatchUpJournalChangeCount);
        Assert.True(restored.PostBuildCatchUpChangeCountComplete);
        Assert.True(restored.PostBuildCatchUpThresholdExceeded);
    }

    [Fact]
    public void QueryOpenRequest_RoundTripsParallelism()
    {
        var request = new IndexQueryOpenRequest
        {
            SessionId = 7,
            Parallelism = 6,
            BaseDir = @"C:\index\base",
        };
        string json = JsonSerializer.Serialize(request, IndexQueryJsonContext.Default.IndexQueryOpenRequest);
        IndexQueryOpenRequest restored = JsonSerializer.Deserialize(
            json, IndexQueryJsonContext.Default.IndexQueryOpenRequest)!;
        Assert.Equal(6, restored.Parallelism);
    }

    [Fact]
    public void QueryOpenResult_RoundTripsLocalFragmentationDiagnostics()
    {
        var result = new IndexQueryOpenResult
        {
            Accelerable = true,
            CandidateCount = 17,
            Diagnostics = new IndexQueryOpenDiagnostics
            {
                LayerCount = 4,
                PathRecordCount = 8,
                TombstoneRecordCount = 2,
                DistinctRouteHashCount = 8,
                CandidatesEvaluatedInWorker = true,
                MapOpenMs = 1.25,
                CandidateEvaluationMs = 2.5,
                RoutingIndexMs = 3.75,
                WorkerOpenMs = 8,
                HostRoundTripMs = 99,
            },
        };

        string json = JsonSerializer.Serialize(result, IndexQueryJsonContext.Default.IndexQueryOpenResult);
        IndexQueryOpenResult restored = JsonSerializer.Deserialize(
            json,
            IndexQueryJsonContext.Default.IndexQueryOpenResult)!;

        Assert.DoesNotContain("hostRoundTripMs", json, StringComparison.Ordinal);
        Assert.NotNull(restored.Diagnostics);
        Assert.Equal(4, restored.Diagnostics!.LayerCount);
        Assert.Equal(10, restored.Diagnostics.RouteRecordCount);
        Assert.Equal(2, restored.Diagnostics.SupersededRouteRecordCount);
        Assert.Equal(1.25, restored.Diagnostics.RouteRecordAmplification, precision: 3);
        Assert.Equal(8, restored.Diagnostics.WorkerOpenMs);
        Assert.Equal(0, restored.Diagnostics.HostRoundTripMs);
    }

    [Fact]
    public void MaintenanceMessage_RoundTripsEveryFlatResultField()
    {
        var message = new IndexWorkerMessage
        {
            Type = IndexWorkerProtocol.MessageTypes.Result,
            Id = 42,
            Ok = true,
            ControlProtocolVersion = IndexWorkerProtocol.ControlProtocolVersion,
            OutcomeKind = "ok",
            BytesCrawled = 100,
            FilesCrawled = 3,
            Percent = 91,
            ProgressRoot = @"C:\root",
            ProgressStage = "pdf",
            ScopeId = "scope",
            IndexedCount = 7,
            SkippedCount = 2,
            Summary = "summary",
            Built = 1,
            SkippedRoots = 2,
            Failed = 3,
            DriveName = "C:",
            UsedPercent = 92.5,
            ThresholdPercent = 90,
            PdfStatus = "Published",
            PdfsSeen = 5,
            PdfAdmitted = 4,
            PdfDeterminism = "Deterministic",
            ActiveBaseGenerationId = "gen-000002",
            ActivePointerSequence = 2,
            LastPublishedArtifactId = "seg-000003",
            MaintenanceResultJson = "{}",
            Valid = true,
            FailureReason = "none",
            DocumentCount = 9,
            SegmentCount = 2,
            RootPath = @"C:\root",
        };
        string json = JsonSerializer.Serialize(message, IndexWorkerJsonContext.Default.IndexWorkerMessage);
        IndexWorkerMessage restored = JsonSerializer.Deserialize(json, IndexWorkerJsonContext.Default.IndexWorkerMessage)!;

        Assert.Equivalent(message, restored, strict: true);
    }

    [Fact]
    public void Trigrams_LargeDeterministicPayload_RoundTrips()
    {
        uint[] input = Enumerable.Range(0, 1000)
            .Select(i => unchecked((uint)i * 2_654_435_761u))
            .ToArray();
        Assert.Equal(input, IndexWorkerProtocol.DecodeTrigrams(IndexWorkerProtocol.EncodeTrigrams(input)));
    }
}
