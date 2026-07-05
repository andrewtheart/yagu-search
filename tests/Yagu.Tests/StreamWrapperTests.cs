using System.Text;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Tests for ReportExportService.WriterStream (private nested stream wrapper).
/// </summary>
public sealed class WriterStreamTests
{
    private static Stream CreateWriterStream(TextWriter writer)
    {
        // WriterStream is private, but the source is compiled into this assembly.
        // Access via reflection since it's a private nested class.
        var type = typeof(ReportExportService).GetNestedType("WriterStream", System.Reflection.BindingFlags.NonPublic)!;
        return (Stream)Activator.CreateInstance(type, writer, Encoding.UTF8)!;
    }

    [Fact]
    public void Write_WritesDecodedBytesToTextWriter()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);

        byte[] data = Encoding.UTF8.GetBytes("hello world");
        stream.Write(data, 0, data.Length);

        Assert.Equal("hello world", sw.ToString());
    }

    [Fact]
    public async Task WriteAsync_ByteArray_WritesToTextWriter()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);

        byte[] data = Encoding.UTF8.GetBytes("async content");
        await stream.WriteAsync(data, 0, data.Length);

        Assert.Equal("async content", sw.ToString());
    }

    [Fact]
    public async Task WriteAsync_ReadOnlyMemory_WritesToTextWriter()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);

        byte[] data = Encoding.UTF8.GetBytes("memory write");
        await stream.WriteAsync(data.AsMemory());

        Assert.Equal("memory write", sw.ToString());
    }

    [Fact]
    public void CanRead_ReturnsFalse()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void CanSeek_ReturnsFalse()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public void CanWrite_ReturnsTrue()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public void Length_Throws()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.Throws<NotSupportedException>(() => stream.Length);
    }

    [Fact]
    public void Position_Get_Throws()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.Throws<NotSupportedException>(() => stream.Position);
    }

    [Fact]
    public void Position_Set_Throws()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
    }

    [Fact]
    public void Read_Throws()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void Seek_Throws()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void SetLength_Throws()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public void Flush_FlushesUnderlyingWriter()
    {
        using var sw = new StringWriter();
        using var stream = CreateWriterStream(sw);

        byte[] data = Encoding.UTF8.GetBytes("flushed");
        stream.Write(data, 0, data.Length);
        stream.Flush();

        Assert.Equal("flushed", sw.ToString());
    }
}

/// <summary>
/// Tests for SessionFileService.ProgressStream (private nested stream wrapper).
/// </summary>
public sealed class ProgressStreamTests
{
    private static Stream CreateProgressStream(Stream inner, long total, Action<double> report)
    {
        var type = typeof(SessionFileService).GetNestedType("ProgressStream", System.Reflection.BindingFlags.NonPublic)!;
        return (Stream)Activator.CreateInstance(type, inner, total, report)!;
    }

    [Fact]
    public void Read_ReadsFromInnerAndReportsProgress()
    {
        byte[] data = Encoding.UTF8.GetBytes("test data for progress");
        using var inner = new MemoryStream(data);
        double lastProgress = -1;

        using var stream = CreateProgressStream(inner, data.Length, p => lastProgress = p);

        var buffer = new byte[data.Length];
        int read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(data.Length, read);
        Assert.Equal(1.0, lastProgress);
    }

    [Fact]
    public async Task ReadAsync_Memory_ReadsAndReportsProgress()
    {
        byte[] data = Encoding.UTF8.GetBytes("async progress data");
        using var inner = new MemoryStream(data);
        double lastProgress = -1;

        using var stream = CreateProgressStream(inner, data.Length, p => lastProgress = p);

        var buffer = new byte[data.Length];
        int read = await stream.ReadAsync(buffer.AsMemory());

        Assert.Equal(data.Length, read);
        Assert.Equal(1.0, lastProgress);
    }

    [Fact]
    public void CanRead_DelegatesToInner()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void CanSeek_ReturnsFalse()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public void CanWrite_ReturnsFalse()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void Length_ReturnsTotal()
    {
        using var inner = new MemoryStream(new byte[42]);
        using var stream = CreateProgressStream(inner, 42, _ => { });
        Assert.Equal(42, stream.Length);
    }

    [Fact]
    public void Position_Get_ReturnsReadCount()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });

        Assert.Equal(0, stream.Position);
        stream.Read(new byte[5], 0, 5);
        Assert.Equal(5, stream.Position);
    }

    [Fact]
    public void Position_Set_Throws()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });
        Assert.Throws<NotSupportedException>(() => stream.Position = 5);
    }

    [Fact]
    public void Seek_Throws()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void SetLength_Throws()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });
        Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
    }

    [Fact]
    public void Write_Throws()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
    }

    [Fact]
    public void Flush_DelegatesToInner()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = CreateProgressStream(inner, 10, _ => { });
        // Should not throw
        stream.Flush();
    }
}
