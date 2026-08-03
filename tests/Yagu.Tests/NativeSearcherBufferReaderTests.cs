using System.Text;
using Yagu.Native;

namespace Yagu.Tests;

public sealed class NativeSearcherBufferReaderTests
{
    [Theory]
    [InlineData(8u, true)]
    [InlineData(7u, false)]
    public void TryReadAbiVersion_RequiresCurrentAbi(uint abiVersion, bool expected)
        => Assert.Equal(expected, NativeSearcher.TryReadAbiVersion(() => abiVersion));

    [Fact]
    public void TryReadAbiVersion_NativeLoadFailuresReturnFalse()
    {
        Assert.False(NativeSearcher.TryReadAbiVersion(() => throw new DllNotFoundException()));
        Assert.False(NativeSearcher.TryReadAbiVersion(() => throw new BadImageFormatException()));
        Assert.False(NativeSearcher.TryReadAbiVersion(() => throw new EntryPointNotFoundException()));
    }

    [Fact]
    public void TryReadU32_ValidData_ReadsCorrectly()
    {
        var data = new byte[4];
        BitConverter.GetBytes((uint)42).CopyTo(data, 0);
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.True(reader.TryReadU32(out uint value));
        Assert.Equal(42u, value);
    }

    [Fact]
    public void TryReadU32_InsufficientData_ReturnsFalse()
    {
        var data = new byte[2];
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.False(reader.TryReadU32(out _));
    }

    [Fact]
    public void TryReadU64_ValidData_ReadsCorrectly()
    {
        var data = new byte[8];
        BitConverter.GetBytes((ulong)123456789L).CopyTo(data, 0);
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.True(reader.TryReadU64(out ulong value));
        Assert.Equal(123456789UL, value);
    }

    [Fact]
    public void TryReadU64_InsufficientData_ReturnsFalse()
    {
        var data = new byte[4];
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.False(reader.TryReadU64(out _));
    }

    [Fact]
    public void TryReadUtf8String_EmptyString_Succeeds()
    {
        var data = new byte[4]; // doesn't matter what's here
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.True(reader.TryReadUtf8String(0, out string? value));
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TryReadUtf8String_ValidData_Succeeds()
    {
        var str = Encoding.UTF8.GetBytes("hello");
        var reader = new NativeSearchOutcome.BufferReader(str);
        Assert.True(reader.TryReadUtf8String((uint)str.Length, out string? value));
        Assert.Equal("hello", value);
    }

    [Fact]
    public void Sequential_Reads_AdvancePosition()
    {
        // u32 (4 bytes) + u64 (8 bytes) = 12 bytes total
        var data = new byte[12];
        BitConverter.GetBytes((uint)7).CopyTo(data, 0);
        BitConverter.GetBytes((ulong)99).CopyTo(data, 4);
        var reader = new NativeSearchOutcome.BufferReader(data);

        Assert.True(reader.TryReadU32(out uint v1));
        Assert.Equal(7u, v1);
        Assert.True(reader.TryReadU64(out ulong v2));
        Assert.Equal(99UL, v2);
        // Now at end, next read should fail
        Assert.False(reader.TryReadU32(out _));
    }

    [Fact]
    public void TryReadUtf8String_IntMaxLengthAfterRead_ReturnsFalseWithoutOverflow()
    {
        var data = new byte[4];
        var reader = new NativeSearchOutcome.BufferReader(data);

        Assert.True(reader.TryReadU32(out _));
        Assert.False(reader.TryReadUtf8String(int.MaxValue, out _));
    }

    [Fact]
    public void TryReadUtf8String_ValidDataAfterRead_UsesRemainingLength()
    {
        byte[] data = [0, 0, 0, 0, (byte)'x'];
        var reader = new NativeSearchOutcome.BufferReader(data);

        Assert.True(reader.TryReadU32(out _));
        Assert.True(reader.TryReadUtf8String(1, out string value));
        Assert.Equal("x", value);
    }

    [Fact]
    public void TryReadContext_EmptyAndPopulatedListsSucceed()
    {
        var emptyReader = new NativeSearchOutcome.BufferReader([]);
        Assert.True(NativeSearchOutcome.TryReadContext(ref emptyReader, 0, out List<string> empty));
        Assert.Empty(empty);

        byte[] data = [1, 0, 0, 0, (byte)'a', 1, 0, 0, 0, (byte)'b'];
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.True(NativeSearchOutcome.TryReadContext(ref reader, 2, out List<string> lines));
        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public void TryReadContext_TruncatedLengthAndTextReturnFalse()
    {
        var missingLengthReader = new NativeSearchOutcome.BufferReader([]);
        Assert.False(NativeSearchOutcome.TryReadContext(ref missingLengthReader, 1, out List<string> missingLength));
        Assert.Empty(missingLength);

        byte[] data = [2, 0, 0, 0, (byte)'a'];
        var missingTextReader = new NativeSearchOutcome.BufferReader(data);
        Assert.False(NativeSearchOutcome.TryReadContext(ref missingTextReader, 1, out List<string> missingText));
        Assert.Empty(missingText);
    }
}
