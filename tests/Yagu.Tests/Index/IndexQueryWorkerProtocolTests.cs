using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

public sealed class IndexQueryWorkerProtocolTests
{
    [Fact]
    public void VerdictFor_MapsIndexedAndFallbackClassifications()
    {
        Assert.Equal(IndexQueryWorkerProtocol.Verdicts.Member,
            IndexQueryWorkerProtocol.VerdictFor(new IndexPathClassification.FreshIndexedMember(1, 2)));
        Assert.Equal(IndexQueryWorkerProtocol.Verdicts.Nonmember,
            IndexQueryWorkerProtocol.VerdictFor(new IndexPathClassification.FreshIndexedNonmember(1, 2)));
        Assert.Equal(IndexQueryWorkerProtocol.Verdicts.DirtyByUsn,
            IndexQueryWorkerProtocol.VerdictFor(new IndexPathClassification.DirtyByUsn(2, "changed")));
        Assert.Equal(IndexQueryWorkerProtocol.Verdicts.Unindexed,
            IndexQueryWorkerProtocol.VerdictFor(new IndexPathClassification.Unindexed("absent")));
        Assert.Equal(IndexQueryWorkerProtocol.Verdicts.Unindexed,
            IndexQueryWorkerProtocol.VerdictFor(new IndexPathClassification.SpecialSource(SpecialSourceKind.PdfText)));
        Assert.Equal(IndexQueryWorkerProtocol.Verdicts.Unindexed,
            IndexQueryWorkerProtocol.VerdictFor(new IndexPathClassification.UntrustedRoot("stale")));
    }

    [Fact]
    public void PathCodec_HandlesEmptyAndRoundTripsMultiplePaths()
    {
        Assert.Equal(string.Empty, IndexQueryWorkerProtocol.EncodePaths([]));
        Assert.Empty(IndexQueryWorkerProtocol.DecodePaths(null));
        Assert.Empty(IndexQueryWorkerProtocol.DecodePaths(string.Empty));

        string[] paths = [@"C:\one.txt", @"C:\two.txt"];
        Assert.Equal(paths, IndexQueryWorkerProtocol.DecodePaths(IndexQueryWorkerProtocol.EncodePaths(paths)));
    }

    [Fact]
    public void VerdictCodec_HandlesEmptyAndRoundTripsBytes()
    {
        Assert.Equal(string.Empty, IndexQueryWorkerProtocol.EncodeVerdicts(Array.Empty<byte>()));
        Assert.Empty(IndexQueryWorkerProtocol.DecodeVerdicts(null));
        Assert.Empty(IndexQueryWorkerProtocol.DecodeVerdicts(string.Empty));

        byte[] verdicts =
        [
            IndexQueryWorkerProtocol.Verdicts.Unindexed,
            IndexQueryWorkerProtocol.Verdicts.Member,
            IndexQueryWorkerProtocol.Verdicts.Nonmember,
        ];
        Assert.Equal(verdicts,
            IndexQueryWorkerProtocol.DecodeVerdicts(IndexQueryWorkerProtocol.EncodeVerdicts(verdicts)));
    }

    [Fact]
    public void ContentIdCodec_RejectsNull_HandlesEmpty_AndRoundTripsValues()
    {
        Assert.Throws<ArgumentNullException>(() => IndexQueryWorkerProtocol.EncodeContentIds(null!));
        Assert.Equal(string.Empty, IndexQueryWorkerProtocol.EncodeContentIds(new HashSet<long>()));

        var contentIds = new HashSet<long> { 3, 7, 11 };
        int[] decoded = IndexWorkerProtocol.DecodeCandidates(
            IndexQueryWorkerProtocol.EncodeContentIds(contentIds));

        Assert.True(contentIds.SetEquals(decoded.Select(static value => (long)value)));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(2, 0, double.PositiveInfinity)]
    [InlineData(7, 4, 1.75)]
    public void OpenDiagnostics_ComputesRouteAmplification(
        long pathRecords,
        int distinctRouteHashes,
        double expected)
    {
        var diagnostics = new IndexQueryOpenDiagnostics
        {
            PathRecordCount = pathRecords,
            DistinctRouteHashCount = distinctRouteHashes,
        };

        Assert.Equal(pathRecords, diagnostics.RouteRecordCount);
        Assert.Equal(Math.Max(0, pathRecords - distinctRouteHashes), diagnostics.SupersededRouteRecordCount);
        Assert.Equal(expected, diagnostics.RouteRecordAmplification);
    }
}