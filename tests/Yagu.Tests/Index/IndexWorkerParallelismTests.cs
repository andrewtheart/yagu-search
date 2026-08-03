using System.Runtime.InteropServices;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexWorkerParallelismTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(999, IndexWorkerParallelism.Maximum)]
    public void NormalizeSetting_PreservesAutomaticAndClampsExplicitValues(int input, int expected)
        => Assert.Equal(expected, IndexWorkerParallelism.NormalizeSetting(input));

    [Fact]
    public void ResolveBuildDegree_AutomaticUsesPhysicalCoresAndMemoryBound()
    {
        Assert.Equal(3, IndexWorkerParallelism.ResolveBuildDegree(
            configured: 0,
            logicalProcessorCount: 24,
            physicalCoreCount: 12,
            buildMemoryBudgetMB: 384,
            limitParallelismOnHardDisks: false,
            isHardDisk: false));

        Assert.Equal(1, IndexWorkerParallelism.ResolveBuildDegree(
            configured: 12,
            logicalProcessorCount: 24,
            physicalCoreCount: 12,
            buildMemoryBudgetMB: 128,
            limitParallelismOnHardDisks: false,
            isHardDisk: false));
    }

    [Fact]
    public void ResolveBuildDegree_FallsBackWhenPhysicalCoreProbeIsUnavailable()
    {
        Assert.Equal(4, IndexWorkerParallelism.ResolveBuildDegree(
            configured: 0,
            logicalProcessorCount: 8,
            physicalCoreCount: 0,
            buildMemoryBudgetMB: 1024,
            limitParallelismOnHardDisks: false,
            isHardDisk: false));
    }

    [Fact]
    public void ResolveQueryDegree_AutomaticUsesLogicalProcessorsAndCapsIt()
    {
        Assert.Equal(12, IndexWorkerParallelism.ResolveQueryDegree(
            configured: 0, logicalProcessorCount: 12,
            limitParallelismOnHardDisks: false, isHardDisk: false));
        Assert.Equal(IndexWorkerParallelism.MaximumAutomaticQuery,
            IndexWorkerParallelism.ResolveQueryDegree(
                configured: 0, logicalProcessorCount: 128,
                limitParallelismOnHardDisks: false, isHardDisk: false));
        Assert.Equal(4, IndexWorkerParallelism.ResolveQueryDegree(
            configured: 4, logicalProcessorCount: 24,
            limitParallelismOnHardDisks: false, isHardDisk: false));
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 8)]
    [InlineData(true, false, 8)]
    public void ExistingHddSafeguard_OverridesBothWorkerDegrees(
        bool limitOnHdd,
        bool isHdd,
        int expected)
    {
        Assert.Equal(expected, IndexWorkerParallelism.ResolveBuildDegree(
            configured: 8, logicalProcessorCount: 16, physicalCoreCount: 8,
            buildMemoryBudgetMB: 1024,
            limitParallelismOnHardDisks: limitOnHdd, isHardDisk: isHdd));
        Assert.Equal(expected, IndexWorkerParallelism.ResolveQueryDegree(
            configured: 8, logicalProcessorCount: 16,
            limitParallelismOnHardDisks: limitOnHdd, isHardDisk: isHdd));
    }

    [Fact]
    public void PhysicalCoreProbe_IsBoundedAndNeverThrows()
    {
        int count = IndexWorkerParallelism.DetectedPhysicalCoreCount;
        Assert.InRange(count, 0, Math.Max(0, Environment.ProcessorCount));
    }

    [Fact]
    public void PhysicalCoreProbe_UnavailableOrFailingInputs_ReturnZero()
    {
        Assert.Equal(0, IndexWorkerParallelism.DetectPhysicalCoreCount(false, UnexpectedRead));
        Assert.Equal(0, IndexWorkerParallelism.DetectPhysicalCoreCount(true, SizeOnly(0)));
        Assert.Equal(0, IndexWorkerParallelism.DetectPhysicalCoreCount(true, SizeOnly(uint.MaxValue)));
        Assert.Equal(0, IndexWorkerParallelism.DetectPhysicalCoreCount(true, Payload(new byte[8], succeed: false)));
        Assert.Equal(0, IndexWorkerParallelism.DetectPhysicalCoreCount(true, ThrowingPayload(new byte[8])));
    }

    [Theory]
    [MemberData(nameof(PhysicalCorePayloads))]
    public void PhysicalCoreProbe_ParsesOnlyCompleteValidCoreEntries(byte[] payload, int expected)
        => Assert.Equal(expected, IndexWorkerParallelism.DetectPhysicalCoreCount(true, Payload(payload)));

    public static TheoryData<byte[], int> PhysicalCorePayloads => new()
    {
        { Entry(relationship: 0, size: 8), 1 },
        { Entry(relationship: 1, size: 8), 0 },
        { Entry(relationship: 0, size: 7), 0 },
        { Entry(relationship: 0, size: 16), 0 },
        { Entry(relationship: 0, size: 8).Concat(new byte[1]).ToArray(), 0 },
        { Entry(relationship: 0, size: 8).Concat(Entry(relationship: 0, size: 8)).ToArray(), 2 },
    };

    private static byte[] Entry(int relationship, int size)
    {
        var payload = new byte[8];
        BitConverter.GetBytes(relationship).CopyTo(payload, 0);
        BitConverter.GetBytes(size).CopyTo(payload, 4);
        return payload;
    }

    private static IndexWorkerParallelism.LogicalProcessorInformationReader SizeOnly(uint size)
        => (int _, IntPtr _, ref uint returnedLength) =>
        {
            returnedLength = size;
            return false;
        };

    private static IndexWorkerParallelism.LogicalProcessorInformationReader Payload(
        byte[] payload,
        bool succeed = true)
        => (int _, IntPtr buffer, ref uint returnedLength) =>
        {
            returnedLength = (uint)payload.Length;
            if (buffer != IntPtr.Zero && succeed)
                Marshal.Copy(payload, 0, buffer, payload.Length);
            return buffer != IntPtr.Zero && succeed;
        };

    private static IndexWorkerParallelism.LogicalProcessorInformationReader ThrowingPayload(byte[] payload)
        => (int _, IntPtr buffer, ref uint returnedLength) =>
        {
            returnedLength = (uint)payload.Length;
            if (buffer != IntPtr.Zero)
                throw new InvalidOperationException("native probe failed");
            return false;
        };

    private static bool UnexpectedRead(int _, IntPtr __, ref uint returnedLength)
    {
        returnedLength = 0;
        throw new InvalidOperationException("Probe must not run.");
    }
}
