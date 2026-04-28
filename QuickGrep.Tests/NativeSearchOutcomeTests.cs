using System.Buffers.Binary;
using System.Text;
using QuickGrep.Native;
using QuickGrep.Models;

namespace QuickGrep.Tests;

public class NativeSearchOutcomeTests
{
    [Fact]
    public void Unavailable_HasCorrectKind()
    {
        var outcome = NativeSearchOutcome.Unavailable;
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Unavailable, outcome.Kind);
        Assert.Empty(outcome.Results);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void Unavailable_IsSingleton()
    {
        Assert.Same(NativeSearchOutcome.Unavailable, NativeSearchOutcome.Unavailable);
    }

    [Fact]
    public void Skipped_HasCorrectKindAndReason()
    {
        var outcome = NativeSearchOutcome.Skipped("binary");
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Skipped, outcome.Kind);
        Assert.Equal("binary", outcome.Reason);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public void Error_HasCorrectKindAndReason()
    {
        var outcome = NativeSearchOutcome.Error("invalid regex");
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Error, outcome.Kind);
        Assert.Equal("invalid regex", outcome.Reason);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public void Cancelled_HasCorrectKind()
    {
        var outcome = NativeSearchOutcome.Cancelled();
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Cancelled, outcome.Kind);
        Assert.Null(outcome.Reason);
        Assert.Empty(outcome.Results);
    }
}

// ─── NativeSearchOutcome: coverage gaps ─────────────────────────────────

public class NativeSearchOutcomeCoverageTests
{
    [Fact]
    public void Unavailable_HasEmptyResults()
    {
        var outcome = NativeSearchOutcome.Unavailable;
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public void Unavailable_IsSingleton()
    {
        var a = NativeSearchOutcome.Unavailable;
        var b = NativeSearchOutcome.Unavailable;
        Assert.Same(a, b);
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Unavailable, a.Kind);
    }

    [Fact]
    public void Skipped_HasCorrectKindAndReason()
    {
        var outcome = NativeSearchOutcome.Skipped("too many files");
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Skipped, outcome.Kind);
        Assert.Equal("too many files", outcome.Reason);
    }

    [Fact]
    public void Error_HasCorrectReason()
    {
        var outcome = NativeSearchOutcome.Error("bad regex");
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Error, outcome.Kind);
        Assert.Equal("bad regex", outcome.Reason);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public void Cancelled_HasNullReason()
    {
        var outcome = NativeSearchOutcome.Cancelled();
        Assert.Null(outcome.Reason);
    }
}

public class EverythingSdkCoverageTests
{
    [Theory]
    [InlineData(0u, "OK")]
    [InlineData(1u, "Out of memory")]
    [InlineData(2u, "Everything is not running")]
    [InlineData(3u, "Unable to register window class")]
    [InlineData(4u, "Unable to create listening window")]
    [InlineData(5u, "Unable to create listening thread")]
    [InlineData(6u, "Invalid index")]
    [InlineData(7u, "Invalid call")]
    [InlineData(8u, "Invalid request data")]
    [InlineData(9u, "Invalid parameter")]
    public void ErrorMessage_KnownCodes(uint code, string expected)
    {
        Assert.Equal(expected, EverythingSdk.ErrorMessage(code));
    }

    [Theory]
    [InlineData(10u)]
    [InlineData(99u)]
    [InlineData(uint.MaxValue)]
    public void ErrorMessage_UnknownCode(uint code)
    {
        var msg = EverythingSdk.ErrorMessage(code);
        Assert.Contains("Unknown error", msg);
        Assert.Contains(code.ToString(), msg);
    }
}

// ─── NativeSearchOutcome.FromBuffer ─────────────────────────────────

public class NativeSearchOutcomeFromBufferTests
{
    private static void WriteU32(List<byte> buf, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        buf.AddRange(b);
    }

    private static void WriteU64(List<byte> buf, ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        buf.AddRange(b);
    }

    private static void WriteUtf8String(List<byte> buf, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteU32(buf, (uint)bytes.Length);
        buf.AddRange(bytes);
    }

    [Fact]
    public unsafe void ZeroBuffer_ReturnsEmptyMatches()
    {
        var result = new NativeSearcher.QgResult
        {
            Buffer = IntPtr.Zero,
            BufferLen = 0,
        };
        var outcome = NativeSearchOutcome.FromBuffer("test.txt", result, 0);
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Matches, outcome.Kind);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public unsafe void SingleMatch_NoContext_ParsesCorrectly()
    {
        // Protocol: U32 count, per match: U64 lineNumber, U32 matchStart, U32 matchLen,
        //           U32 lineLen, UTF8[lineLen], U32 beforeCount, U32 afterCount
        var buf = new List<byte>();
        WriteU32(buf, 1);          // count = 1
        WriteU64(buf, 5);          // lineNumber = 5
        WriteU32(buf, 2);          // matchStart = 2
        WriteU32(buf, 3);          // matchLen = 3
        var lineBytes = Encoding.UTF8.GetBytes("hello world");
        WriteU32(buf, (uint)lineBytes.Length);
        buf.AddRange(lineBytes);
        WriteU32(buf, 0);          // beforeCount = 0
        WriteU32(buf, 0);          // afterCount = 0

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var result = new NativeSearcher.QgResult
            {
                Buffer = (IntPtr)ptr,
                BufferLen = (nuint)bytes.Length,
            };
            var outcome = NativeSearchOutcome.FromBuffer("test.txt", result, 0);
            Assert.Equal(NativeSearchOutcome.OutcomeKind.Matches, outcome.Kind);
            Assert.Single(outcome.Results);
            Assert.Equal(5, outcome.Results[0].LineNumber);
            Assert.Equal(2, outcome.Results[0].MatchStartColumn);
            Assert.Equal(3, outcome.Results[0].MatchLength);
            Assert.Equal("hello world", outcome.Results[0].MatchLine);
            Assert.Equal("test.txt", outcome.Results[0].FilePath);
        }
    }

    [Fact]
    public unsafe void SingleMatch_WithContext_ParsesCorrectly()
    {
        var buf = new List<byte>();
        WriteU32(buf, 1);          // count = 1
        WriteU64(buf, 10);         // lineNumber = 10
        WriteU32(buf, 0);          // matchStart = 0
        WriteU32(buf, 5);          // matchLen = 5
        var lineBytes = Encoding.UTF8.GetBytes("match");
        WriteU32(buf, (uint)lineBytes.Length);
        buf.AddRange(lineBytes);

        // before context: 1 line
        WriteU32(buf, 1);
        WriteUtf8String(buf, "before line");

        // after context: 1 line
        WriteU32(buf, 1);
        WriteUtf8String(buf, "after line");

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var result = new NativeSearcher.QgResult
            {
                Buffer = (IntPtr)ptr,
                BufferLen = (nuint)bytes.Length,
            };
            var outcome = NativeSearchOutcome.FromBuffer("ctx.txt", result, 1);
            Assert.Equal(NativeSearchOutcome.OutcomeKind.Matches, outcome.Kind);
            Assert.Single(outcome.Results);
            Assert.Equal("before line", outcome.Results[0].ContextBefore[0]);
            Assert.Equal("after line", outcome.Results[0].ContextAfter[0]);
        }
    }

    [Fact]
    public unsafe void TruncatedBuffer_CountOnly_ReturnsEmptyResults()
    {
        // Buffer has count=3 but no match data — parser breaks gracefully
        var buf = new List<byte>();
        WriteU32(buf, 3);
        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var result = new NativeSearcher.QgResult
            {
                Buffer = (IntPtr)ptr,
                BufferLen = (nuint)bytes.Length,
            };
            var outcome = NativeSearchOutcome.FromBuffer("trunc.txt", result, 0);
            Assert.Equal(NativeSearchOutcome.OutcomeKind.Matches, outcome.Kind);
            Assert.Empty(outcome.Results);
        }
    }

    [Fact]
    public unsafe void TruncatedCount_ReturnsError()
    {
        // Only 2 bytes — can't even read the count U32
        var bytes = new byte[] { 0x01, 0x00 };
        fixed (byte* ptr = bytes)
        {
            var result = new NativeSearcher.QgResult
            {
                Buffer = (IntPtr)ptr,
                BufferLen = (nuint)bytes.Length,
            };
            var outcome = NativeSearchOutcome.FromBuffer("bad.txt", result, 0);
            Assert.Equal(NativeSearchOutcome.OutcomeKind.Error, outcome.Kind);
            Assert.Contains("truncated", outcome.Reason);
        }
    }

    [Fact]
    public unsafe void MultipleMatches_ParseAll()
    {
        var buf = new List<byte>();
        WriteU32(buf, 2);          // count = 2

        for (int i = 0; i < 2; i++)
        {
            WriteU64(buf, (ulong)(i + 1));
            WriteU32(buf, 0);
            WriteU32(buf, 1);
            var lb = Encoding.UTF8.GetBytes($"line{i}");
            WriteU32(buf, (uint)lb.Length);
            buf.AddRange(lb);
            WriteU32(buf, 0); // no before
            WriteU32(buf, 0); // no after
        }

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var result = new NativeSearcher.QgResult
            {
                Buffer = (IntPtr)ptr,
                BufferLen = (nuint)bytes.Length,
            };
            var outcome = NativeSearchOutcome.FromBuffer("multi.txt", result, 0);
            Assert.Equal(2, outcome.Results.Count);
            Assert.Equal("line0", outcome.Results[0].MatchLine);
            Assert.Equal("line1", outcome.Results[1].MatchLine);
        }
    }
}

// ─── BufferReader unit tests ────────────────────────────────────────

public class BufferReaderTests
{
    [Fact]
    public void TryReadU32_EmptyBuffer_ReturnsFalse()
    {
        var reader = new NativeSearchOutcome.BufferReader(ReadOnlySpan<byte>.Empty);
        Assert.False(reader.TryReadU32(out _));
    }

    [Fact]
    public void TryReadU64_TooShort_ReturnsFalse()
    {
        Span<byte> data = stackalloc byte[4]; // need 8
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.False(reader.TryReadU64(out _));
    }

    [Fact]
    public void TryReadUtf8String_ZeroLen_ReturnsEmpty()
    {
        var reader = new NativeSearchOutcome.BufferReader(ReadOnlySpan<byte>.Empty);
        Assert.True(reader.TryReadUtf8String(0, out var value));
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TryReadUtf8String_BufferTooShort_ReturnsFalse()
    {
        Span<byte> data = stackalloc byte[2];
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.False(reader.TryReadUtf8String(10, out _));
    }

    [Fact]
    public void TryReadU32_ValidData_ReturnsCorrectValue()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEADBEEF);
        var reader = new NativeSearchOutcome.BufferReader(bytes);
        Assert.True(reader.TryReadU32(out uint val));
        Assert.Equal(0xDEADBEEF, val);
    }

    [Fact]
    public void TryReadU64_ValidData_ReturnsCorrectValue()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0x0102030405060708UL);
        var reader = new NativeSearchOutcome.BufferReader(bytes);
        Assert.True(reader.TryReadU64(out ulong val));
        Assert.Equal(0x0102030405060708UL, val);
    }

    [Fact]
    public void TryReadUtf8String_LenExceedsIntMax_ReturnsFalse()
    {
        var reader = new NativeSearchOutcome.BufferReader(new byte[8]);
        Assert.False(reader.TryReadUtf8String(uint.MaxValue, out _));
    }
}

// ─── NativeSearchOutcome.FromBuffer edge cases ──────────────────────────

public class NativeSearchOutcomeFromBufferEdgeTests
{
    private static void WriteU32(List<byte> buf, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        buf.AddRange(b);
    }

    private static void WriteU64(List<byte> buf, ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        buf.AddRange(b);
    }

    private static void WriteUtf8String(List<byte> buf, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteU32(buf, (uint)bytes.Length);
        buf.AddRange(bytes);
    }

    /// <summary>Build a complete match record with given values.</summary>
    private static void WriteMatch(List<byte> buf, ulong lineNum, uint matchStart, uint matchLen,
        string line, string[]? before = null, string[]? after = null)
    {
        WriteU64(buf, lineNum);
        WriteU32(buf, matchStart);
        WriteU32(buf, matchLen);
        var lb = Encoding.UTF8.GetBytes(line);
        WriteU32(buf, (uint)lb.Length);
        buf.AddRange(lb);
        before ??= [];
        after ??= [];
        WriteU32(buf, (uint)before.Length);
        foreach (var b in before) WriteUtf8String(buf, b);
        WriteU32(buf, (uint)after.Length);
        foreach (var a in after) WriteUtf8String(buf, a);
    }

    [Fact]
    public unsafe void LargeLineNumber_ClampedToIntMax()
    {
        var buf = new List<byte>();
        WriteU32(buf, 1);
        WriteMatch(buf, (ulong)int.MaxValue + 100, 0, 1, "line");

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult { Buffer = (IntPtr)ptr, BufferLen = (nuint)bytes.Length };
            var outcome = NativeSearchOutcome.FromBuffer("f.txt", r, 0);
            Assert.Single(outcome.Results);
            Assert.Equal(int.MaxValue, outcome.Results[0].LineNumber);
        }
    }

    [Fact]
    public unsafe void LargeMatchStart_ClampedToZero()
    {
        var buf = new List<byte>();
        WriteU32(buf, 1);
        WriteMatch(buf, 1, (uint)int.MaxValue + 1, 1, "line");

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult { Buffer = (IntPtr)ptr, BufferLen = (nuint)bytes.Length };
            var outcome = NativeSearchOutcome.FromBuffer("f.txt", r, 0);
            Assert.Single(outcome.Results);
            Assert.Equal(0, outcome.Results[0].MatchStartColumn);
        }
    }

    [Fact]
    public unsafe void LargeMatchLen_ClampedToZero()
    {
        var buf = new List<byte>();
        WriteU32(buf, 1);
        WriteMatch(buf, 1, 0, (uint)int.MaxValue + 1, "line");

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult { Buffer = (IntPtr)ptr, BufferLen = (nuint)bytes.Length };
            var outcome = NativeSearchOutcome.FromBuffer("f.txt", r, 0);
            Assert.Single(outcome.Results);
            Assert.Equal(0, outcome.Results[0].MatchLength);
        }
    }

    [Fact]
    public unsafe void TruncatedBeforeContext_BreaksGracefully()
    {
        // Match with beforeCount=2 but only 1 before entry followed by truncation
        var buf = new List<byte>();
        WriteU32(buf, 1);           // count
        WriteU64(buf, 1);           // lineNumber
        WriteU32(buf, 0);           // matchStart
        WriteU32(buf, 1);           // matchLen
        var lb = Encoding.UTF8.GetBytes("line");
        WriteU32(buf, (uint)lb.Length);
        buf.AddRange(lb);
        WriteU32(buf, 2);           // beforeCount = 2
        WriteUtf8String(buf, "b1"); // only 1 before entry
        // buffer ends here — truncated

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult { Buffer = (IntPtr)ptr, BufferLen = (nuint)bytes.Length };
            var outcome = NativeSearchOutcome.FromBuffer("f.txt", r, 2);
            Assert.Equal(NativeSearchOutcome.OutcomeKind.Matches, outcome.Kind);
            Assert.Empty(outcome.Results); // match dropped due to truncation
        }
    }

    [Fact]
    public unsafe void TruncatedAfterContext_BreaksGracefully()
    {
        // Match with before ok but afterCount=2 with only 1 after entry
        var buf = new List<byte>();
        WriteU32(buf, 1);           // count
        WriteU64(buf, 1);           // lineNumber
        WriteU32(buf, 0);           // matchStart
        WriteU32(buf, 1);           // matchLen
        var lb = Encoding.UTF8.GetBytes("line");
        WriteU32(buf, (uint)lb.Length);
        buf.AddRange(lb);
        WriteU32(buf, 0);           // beforeCount = 0
        WriteU32(buf, 2);           // afterCount = 2
        WriteUtf8String(buf, "a1"); // only 1 after entry
        // buffer ends — truncated

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult { Buffer = (IntPtr)ptr, BufferLen = (nuint)bytes.Length };
            var outcome = NativeSearchOutcome.FromBuffer("f.txt", r, 2);
            Assert.Equal(NativeSearchOutcome.OutcomeKind.Matches, outcome.Kind);
            Assert.Empty(outcome.Results); // match dropped due to truncation
        }
    }

    [Fact]
    public unsafe void TruncatedBeforeCount_BreaksGracefully()
    {
        // Match header complete but buffer ends before beforeCount U32
        var buf = new List<byte>();
        WriteU32(buf, 1);
        WriteU64(buf, 1);
        WriteU32(buf, 0);
        WriteU32(buf, 1);
        var lb = Encoding.UTF8.GetBytes("x");
        WriteU32(buf, (uint)lb.Length);
        buf.AddRange(lb);
        // No beforeCount — buffer ends

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult { Buffer = (IntPtr)ptr, BufferLen = (nuint)bytes.Length };
            var outcome = NativeSearchOutcome.FromBuffer("f.txt", r, 0);
            Assert.Empty(outcome.Results);
        }
    }

    [Fact]
    public unsafe void TruncatedAfterCount_BreaksGracefully()
    {
        // Match header + beforeCount=0 but buffer ends before afterCount
        var buf = new List<byte>();
        WriteU32(buf, 1);
        WriteU64(buf, 1);
        WriteU32(buf, 0);
        WriteU32(buf, 1);
        var lb = Encoding.UTF8.GetBytes("x");
        WriteU32(buf, (uint)lb.Length);
        buf.AddRange(lb);
        WriteU32(buf, 0); // beforeCount = 0
        // No afterCount — buffer ends

        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult { Buffer = (IntPtr)ptr, BufferLen = (nuint)bytes.Length };
            var outcome = NativeSearchOutcome.FromBuffer("f.txt", r, 0);
            Assert.Empty(outcome.Results);
        }
    }

    [Fact]
    public unsafe void BufferTooLarge_ReturnsError()
    {
        // A BufferLen > int.MaxValue should return an Error outcome
        var bytes = new byte[4];
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult
            {
                Buffer = (IntPtr)ptr,
                BufferLen = (nuint)int.MaxValue + 1,
            };
            var outcome = NativeSearchOutcome.FromBuffer("big.txt", r, 0);
            Assert.Equal(NativeSearchOutcome.OutcomeKind.Error, outcome.Kind);
            Assert.Contains("buffer too large", outcome.Reason);
        }
    }

    [Fact]
    public unsafe void HugeCount_CatchesOverflow_ReturnsError()
    {
        // count = 0xFFFFFFFF → (int)count = -1 → new List(-1) throws
        var buf = new List<byte>();
        WriteU32(buf, uint.MaxValue); // count overflows int cast
        var bytes = buf.ToArray();
        fixed (byte* ptr = bytes)
        {
            var r = new NativeSearcher.QgResult
            {
                Buffer = (IntPtr)ptr,
                BufferLen = (nuint)bytes.Length,
            };
            var outcome = NativeSearchOutcome.FromBuffer("overflow.txt", r, 0);
            Assert.Equal(NativeSearchOutcome.OutcomeKind.Error, outcome.Kind);
            Assert.Contains("buffer parse failed", outcome.Reason);
        }
    }
}
