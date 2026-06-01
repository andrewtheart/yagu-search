using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Yagu.Native;

namespace Yagu.Services;

/// <summary>
/// A sink that writes ripgrep-compatible UTF-8 output directly from the raw
/// byte pointers delivered by the Rust scanner — zero SearchResult allocation,
/// zero managed string creation in the hot path.
/// </summary>
internal sealed class DirectOutputSink : NativeSearcher.IParallelSink, IDisposable
{
    private readonly Stream _output;
    private readonly bool _color;
    private readonly List<string> _paths;
    private readonly int _maxResults;
    private readonly unsafe int* _cancelPtr;
    private readonly unsafe int* _filesScannedPtr;

    // Per-file tracking (arrays grow dynamically like StreamingScanSink)
    private int[] _statuses;
    private long[] _fileLength;
    private int _capacity;
    private readonly object _resizeLock = new();

    // State for ripgrep-format output
    private string? _currentFile;
    private int _lastLineWritten;
    private bool _wroteMatchInFile;
    private int _runningTotal;
    private bool _stopped;

    // Scratch buffer for formatting line numbers etc.
    private readonly byte[] _scratchBuffer = new byte[64];

    // ANSI color codes
    private static ReadOnlySpan<byte> BoldMagenta => "\x1b[1;35m"u8;
    private static ReadOnlySpan<byte> BoldGreen => "\x1b[1;32m"u8;
    private static ReadOnlySpan<byte> BoldBlue => "\x1b[1;34m"u8;
    private static ReadOnlySpan<byte> BoldRed => "\x1b[1;31m"u8;
    private static ReadOnlySpan<byte> Reset => "\x1b[0m"u8;
    private static ReadOnlySpan<byte> Newline => "\n"u8;
    private static ReadOnlySpan<byte> Separator => "--\n"u8;
    private static ReadOnlySpan<byte> SeparatorColor => "\x1b[1;34m--\x1b[0m\n"u8;

    public bool Truncated { get; private set; }
    public int TotalMatches => _runningTotal;
    public int FilesWithMatches { get; private set; }
    public long BytesScanned { get; private set; }
    public int FilesSkipped { get; private set; }
    public int SkipBinary { get; private set; }
    public int SkipAccessDenied { get; private set; }
    public int SkipTooLarge { get; private set; }
    public int SkipNotFound { get; private set; }
    public int SkipOther { get; private set; }

    public Exception? CapturedException { get; set; }
    public string? ErrorMessage { get; set; }

    public unsafe DirectOutputSink(
        Stream output,
        bool color,
        List<string> paths,
        int maxResults,
        int currentTotalMatches,
        IntPtr cancelPtr,
        int* filesScannedPtr,
        int initialCapacity = 4096)
    {
        _output = output;
        _color = color;
        _paths = paths;
        _maxResults = maxResults;
        _runningTotal = currentTotalMatches;
        _cancelPtr = (int*)cancelPtr;
        _filesScannedPtr = filesScannedPtr;
        _capacity = initialCapacity;
        _statuses = new int[initialCapacity];
        _fileLength = new long[initialCapacity];
    }

    public void Dispose() { }

    private void EnsureCapacity(int index)
    {
        if (index < _capacity) return;
        lock (_resizeLock)
        {
            if (index < _capacity) return;
            int newCap = Math.Max(_capacity * 2, index + 1);
            var newStatuses = new int[newCap];
            var newFileLength = new long[newCap];
            Array.Copy(_statuses, newStatuses, _capacity);
            Array.Copy(_fileLength, newFileLength, _capacity);
            _statuses = newStatuses;
            _fileLength = newFileLength;
            _capacity = newCap;
        }
    }

    public int GetStatus(int i) => i < _capacity ? _statuses[i] : 0;
    public long GetFileLength(int i) => i < _capacity ? _fileLength[i] : 0;

    public unsafe int OnMatch(NativeSearcher.QgMatchView* m) => 1;

    public unsafe int OnMatchForFile(uint fileIndex, NativeSearcher.QgMatchView* m)
    {
        if (_stopped) return 1;

        int idx = (int)fileIndex;
        string filePath = idx < _paths.Count ? _paths[idx] : string.Empty;

        var view = *m;
        int lineNum = view.LineNumber > int.MaxValue ? int.MaxValue : (int)view.LineNumber;

        // ---- File header (only on file change) ----
        if (!string.Equals(_currentFile, filePath, StringComparison.OrdinalIgnoreCase))
        {
            if (_currentFile is not null)
                _output.Write(Newline); // blank line between files

            if (_color)
            {
                _output.Write(BoldMagenta);
                WriteUtf8String(filePath);
                _output.Write(Reset);
            }
            else
            {
                WriteUtf8String(filePath);
            }
            _output.Write(Newline);

            _currentFile = filePath;
            _lastLineWritten = 0;
            _wroteMatchInFile = false;
            FilesWithMatches++;
        }

        // ---- Context before ----
        if (view.CtxBeforeCount > 0 && view.CtxBeforePtr != null && view.CtxBeforeBytes > 0)
        {
            WriteContextLines(view.CtxBeforePtr, (int)view.CtxBeforeBytes, view.CtxBeforeCount,
                lineNum - (int)view.CtxBeforeCount, isAfter: false);
        }
        else if (_wroteMatchInFile && lineNum > _lastLineWritten + 1)
        {
            _output.Write(_color ? SeparatorColor : Separator);
        }

        // ---- Match line ----
        WriteMatchLine(lineNum, view.LinePtr, (int)view.LineLen,
            (int)view.MatchStart, (int)view.MatchLen);
        _lastLineWritten = lineNum;
        _wroteMatchInFile = true;

        // ---- Context after ----
        if (view.CtxAfterCount > 0 && view.CtxAfterPtr != null && view.CtxAfterBytes > 0)
        {
            WriteContextLines(view.CtxAfterPtr, (int)view.CtxAfterBytes, view.CtxAfterCount,
                lineNum + 1, isAfter: true);
        }

        _runningTotal++;
        if (_maxResults > 0 && _runningTotal >= _maxResults)
        {
            Truncated = true;
            _stopped = true;
            return 1;
        }
        return 0;
    }

    public void OnFileDone(uint fileIndex, int status, ulong fileLength, ulong lastModifiedFileTime)
    {
        int idx = (int)fileIndex;
        EnsureCapacity(idx);
        _statuses[idx] = status;
        _fileLength[idx] = fileLength > long.MaxValue ? long.MaxValue : (long)fileLength;

        unsafe
        {
            if (_filesScannedPtr != null)
                Interlocked.Increment(ref *_filesScannedPtr);
        }

        // Track stats
        if (status == NativeSearcher.StatusOk)
        {
            BytesScanned += (long)Math.Min(fileLength, (ulong)long.MaxValue);
        }
        else
        {
            FilesSkipped++;
            switch (status)
            {
                case NativeSearcher.StatusBinarySkipped: SkipBinary++; break;
                case NativeSearcher.StatusOpenFailed: SkipAccessDenied++; break;
                case NativeSearcher.StatusTooLarge: SkipTooLarge++; break;
                case NativeSearcher.StatusInvalidPath: SkipNotFound++; break;
                default: SkipOther++; break;
            }
        }
    }

    // ---- Direct UTF-8 writing methods (zero allocation hot path) ----

    private unsafe void WriteMatchLine(int lineNum, byte* linePtr, int lineLen, int matchStart, int matchLen)
    {
        if (_color)
        {
            _output.Write(BoldGreen);
            WriteLineNumber(lineNum);
            _output.Write(Reset);
            _output.Write(":"u8);

            // Write line with highlighted match
            if (matchStart >= 0 && matchLen > 0 && matchStart < lineLen)
            {
                int matchEnd = Math.Min(matchStart + matchLen, lineLen);
                // Before match
                if (matchStart > 0)
                    _output.Write(new ReadOnlySpan<byte>(linePtr, matchStart));
                // Match (highlighted)
                _output.Write(BoldRed);
                _output.Write(new ReadOnlySpan<byte>(linePtr + matchStart, matchEnd - matchStart));
                _output.Write(Reset);
                // After match
                if (matchEnd < lineLen)
                    _output.Write(new ReadOnlySpan<byte>(linePtr + matchEnd, lineLen - matchEnd));
            }
            else
            {
                _output.Write(new ReadOnlySpan<byte>(linePtr, lineLen));
            }
        }
        else
        {
            WriteLineNumber(lineNum);
            _output.Write(":"u8);
            _output.Write(new ReadOnlySpan<byte>(linePtr, lineLen));
        }
        _output.Write(Newline);
    }

    private unsafe void WriteContextLines(byte* ptr, int totalBytes, uint count, int startLineNum, bool isAfter)
    {
        if (ptr == null || totalBytes == 0 || count == 0) return;

        // Check if we need a separator before context-before lines
        if (!isAfter && _wroteMatchInFile && startLineNum > _lastLineWritten + 1)
            _output.Write(_color ? SeparatorColor : Separator);

        int pos = 0;
        for (uint i = 0; i < count && pos + 4 <= totalBytes; i++)
        {
            uint len = (uint)(ptr[pos] | (ptr[pos + 1] << 8) | (ptr[pos + 2] << 16) | (ptr[pos + 3] << 24));
            pos += 4;
            if (len > (uint)(totalBytes - pos)) break;

            int ctxLineNum = startLineNum + (int)i;

            if (_color)
            {
                _output.Write(BoldBlue);
                WriteLineNumber(ctxLineNum);
                _output.Write(Reset);
                _output.Write("-"u8);
            }
            else
            {
                WriteLineNumber(ctxLineNum);
                _output.Write("-"u8);
            }
            _output.Write(new ReadOnlySpan<byte>(ptr + pos, (int)len));
            _output.Write(Newline);

            _lastLineWritten = ctxLineNum;
            pos += (int)len;
        }
    }

    private void WriteLineNumber(int lineNum)
    {
        // Format line number directly to UTF-8 bytes (no string allocation)
        if (lineNum.TryFormat(_scratchBuffer, out int written, provider: System.Globalization.CultureInfo.InvariantCulture))
        {
            _output.Write(_scratchBuffer.AsSpan(0, written));
        }
        else
        {
            // Fallback (shouldn't happen for int)
            WriteUtf8String(lineNum.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void WriteUtf8String(string s)
    {
        // Encode string to UTF-8 and write — uses stackalloc for small strings
        int maxBytes = Encoding.UTF8.GetMaxByteCount(s.Length);
        if (maxBytes <= 1024)
        {
            Span<byte> buf = stackalloc byte[maxBytes];
            int written = Encoding.UTF8.GetBytes(s, buf);
            _output.Write(buf[..written]);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                int written = Encoding.UTF8.GetBytes(s, rented);
                _output.Write(rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public void Flush() => _output.Flush();
}
