using Yagu.Services;

namespace Yagu.Tests;

public sealed class DeviceVolumeChangeTests
{
    [Theory]
    [InlineData(0u, new string[0])]
    [InlineData(1u, new[] { @"A:\" })]
    [InlineData(5u, new[] { @"A:\", @"C:\" })]
    public void ExpandVolumeUnitMask_MapsBitsToDriveRoots(uint mask, string[] expected)
        => Assert.Equal(expected, DeviceVolumeChange.ExpandVolumeUnitMask(mask));

    [Fact]
    public void IntersectsAnyRoot_IsBoundarySafe()
    {
        Assert.True(DeviceVolumeChange.IntersectsAnyRoot(@"E:\folder\file.txt", new[] { @"E:\" }));
        Assert.False(DeviceVolumeChange.IntersectsAnyRoot(@"C:\folder\file.txt", new[] { @"E:\" }));
    }
}