using System.Buffers.Binary;

namespace Yagu.Services.Index;

/// <summary>
/// How one record type is compared and framed on a spool file. Implementations must be pure and
/// deterministic: the same record always encodes to the same bytes and compares the same way, so a
/// merge produces byte-identical output for identical input.
/// </summary>
internal interface IIndexRecordCodec<TRecord>
{
    /// <summary>Upper bound on the bytes <see cref="Encode"/> writes for any record.</summary>
    int MaxPayloadBytes { get; }

    /// <summary>Writes <paramref name="record"/> into <paramref name="destination"/> and returns the byte count.</summary>
    int Encode(TRecord record, Span<byte> destination);

    /// <summary>Rebuilds a record from exactly the bytes <see cref="Encode"/> produced.</summary>
    bool TryDecode(ReadOnlySpan<byte> payload, out TRecord record);
}

/// <summary>A record codec that also supplies the ordering and memory estimate required for external sorting.</summary>
internal interface IIndexSpoolCodec<TRecord> : IIndexRecordCodec<TRecord>, IComparer<TRecord>
{

    /// <summary>Approximate in-memory cost of holding one record, used to bound sorted-run size.</summary>
    long EstimateInMemoryBytes(TRecord record);
}

/// <summary>
/// Appends records to a private spool file using the same length-delimited framing as a sorted run, for
/// stages that already produce records in the required order and only need to re-read them later without
/// holding them in memory.
/// </summary>
internal sealed class IndexSpoolWriter<TRecord> : IDisposable
{
    private readonly IIndexRecordCodec<TRecord> _codec;
    private readonly IndexCompactionDiskGuard? _diskGuard;
    private readonly FileStream _stream;
    private readonly byte[] _payload;

    public IndexSpoolWriter(string path, IIndexRecordCodec<TRecord> codec, IndexCompactionDiskGuard? diskGuard = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _codec = codec;
        _diskGuard = diskGuard;
        _payload = new byte[codec.MaxPayloadBytes];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
    }

    /// <summary>Records written so far.</summary>
    public long Count { get; private set; }

    public void Write(TRecord record)
    {
        int length = _codec.Encode(record, _payload);
        if (length < 0 || length > _payload.Length)
            throw new InvalidDataException("Spool codec produced a payload outside its declared bound.");
        _diskGuard?.EnsureHeadroomFor(length + 4);
        Span<byte> header = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, length);
        _stream.Write(header);
        _stream.Write(_payload, 0, length);
        _diskGuard?.RecordCreated(length + 4);
        Count++;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Sequentially re-reads a spool file written by <see cref="IndexSpoolWriter{TRecord}"/>.</summary>
internal sealed class IndexSpoolReader<TRecord> : IDisposable
{
    private readonly IIndexRecordCodec<TRecord> _codec;
    private readonly FileStream _stream;
    private readonly byte[] _payload;

    public IndexSpoolReader(string path, IIndexRecordCodec<TRecord> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
        _payload = new byte[codec.MaxPayloadBytes];
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
    }

    public bool TryReadNext(out TRecord record)
    {
        record = default!;
        Span<byte> header = stackalloc byte[4];
        if (!ReadExact(header, allowEof: true))
            return false;
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > _payload.Length)
            throw new InvalidDataException("Spool contains a record length outside the codec bound.");
        ReadExact(_payload.AsSpan(0, length), allowEof: false);
        if (!_codec.TryDecode(_payload.AsSpan(0, length), out record))
            throw new InvalidDataException("Spool contains a record the codec could not decode.");
        return true;
    }

    private bool ReadExact(Span<byte> destination, bool allowEof)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = _stream.Read(destination[total..]);
            if (read <= 0)
            {
                if (total == 0 && allowEof)
                    return false;
                throw new InvalidDataException("Spool is truncated.");
            }
            total += read;
        }
        return true;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// A bounded-memory external sort: records are accumulated until they reach the configured memory
/// budget, each full chunk is stably sorted and spilled to a private spool file, and the sorted output
/// is produced by a k-way merge that holds only one buffered record per run.
/// <para>
/// Peak managed memory is therefore the configured budget plus one fixed framing buffer, regardless of
/// how many records pass through — the property that makes compacting a 35 GB index possible without a
/// 35 GB heap. Ordering is total and deterministic: equal keys keep insertion order (stable chunk sort
/// plus run-index tie-breaking in the merge). Spool files live under a caller-owned workspace and are
/// deleted on <see cref="Dispose"/>, including after an abort.
/// </para>
/// </summary>
internal sealed class IndexExternalMergeSorter<TRecord> : IDisposable
{
    private readonly IIndexSpoolCodec<TRecord> _codec;
    private readonly string _spoolDirectory;
    private readonly long _memoryBudgetBytes;
    private readonly IndexCompactionDiskGuard? _diskGuard;
    private readonly List<TRecord> _buffer = [];
    private readonly List<string> _runPaths = [];
    private long _bufferBytes;
    private bool _sealed;
    private bool _disposed;

    public IndexExternalMergeSorter(
        IIndexSpoolCodec<TRecord> codec,
        string spoolDirectory,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
        _codec = codec;
        _spoolDirectory = spoolDirectory;
        // Records are appended before the budget is checked, so a run always holds at least one record
        // however small the budget is. No implicit ceiling is added on top of the caller's budget.
        _memoryBudgetBytes = Math.Max(1, memoryBudgetBytes);
        _diskGuard = diskGuard;
    }

    /// <summary>Number of sorted runs spilled so far (0 when everything still fits in memory).</summary>
    public int SpilledRunCount => _runPaths.Count;

    /// <summary>Total records added.</summary>
    public long RecordCount { get; private set; }

    public void Add(TRecord record, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
            throw new InvalidOperationException("Records cannot be added after the sorted output has been requested.");
        cancellationToken.ThrowIfCancellationRequested();

        _buffer.Add(record);
        _bufferBytes += Math.Max(1, _codec.EstimateInMemoryBytes(record));
        RecordCount++;
        if (_bufferBytes >= _memoryBudgetBytes)
            SpillRun(cancellationToken);
    }

    /// <summary>
    /// Every added record in total order. Enumerating seals the sorter; enumerate once.
    /// </summary>
    public IEnumerable<TRecord> SortedRecords(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
            throw new InvalidOperationException("The sorted output has already been requested.");
        _sealed = true;

        if (_runPaths.Count == 0)
        {
            StableSortBuffer();
            return DrainBuffer(cancellationToken);
        }

        if (_buffer.Count > 0)
            SpillRun(cancellationToken);
        return MergeRuns(cancellationToken);
    }

    private IEnumerable<TRecord> DrainBuffer(CancellationToken cancellationToken)
    {
        foreach (TRecord record in _buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }
        _buffer.Clear();
    }

    private IEnumerable<TRecord> MergeRuns(CancellationToken cancellationToken)
    {
        var readers = new List<SpoolRunReader>(_runPaths.Count);
        try
        {
            foreach (string path in _runPaths)
                readers.Add(new SpoolRunReader(path, _codec));

            var queue = new PriorityQueue<int, (TRecord Record, int Run)>(new RunOrder(_codec));
            for (int i = 0; i < readers.Count; i++)
            {
                if (readers[i].TryReadNext(out TRecord first))
                    queue.Enqueue(i, (first, i));
            }

            while (queue.TryDequeue(out int runIndex, out (TRecord Record, int Run) element))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return element.Record;
                if (readers[runIndex].TryReadNext(out TRecord next))
                    queue.Enqueue(runIndex, (next, runIndex));
            }
        }
        finally
        {
            foreach (SpoolRunReader reader in readers)
                reader.Dispose();
        }
    }

    private void SpillRun(CancellationToken cancellationToken)
    {
        StableSortBuffer();

        Directory.CreateDirectory(_spoolDirectory);
        string path = Path.Combine(
            _spoolDirectory,
            $"run-{_runPaths.Count.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)}.spool");

        byte[] payload = new byte[_codec.MaxPayloadBytes];
        Span<byte> header = stackalloc byte[4];
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            foreach (TRecord record in _buffer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = _codec.Encode(record, payload);
                if (length < 0 || length > payload.Length)
                    throw new InvalidDataException("Spool codec produced a payload outside its declared bound.");
                _diskGuard?.EnsureHeadroomFor(length + 4);
                BinaryPrimitives.WriteInt32LittleEndian(header, length);
                fs.Write(header);
                fs.Write(payload, 0, length);
                _diskGuard?.RecordCreated(length + 4);
            }
            fs.Flush();
        }
        catch
        {
            DeleteFileSafe(path);
            throw;
        }

        _runPaths.Add(path);
        _buffer.Clear();
        _bufferBytes = 0;
    }

    private void StableSortBuffer()
    {
        if (_buffer.Count < 2)
            return;
        TRecord[] items = _buffer.ToArray();
        int[] order = new int[items.Length];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int comparison = _codec.Compare(items[a], items[b]);
            return comparison != 0 ? comparison : a.CompareTo(b);
        });
        for (int i = 0; i < order.Length; i++)
            _buffer[i] = items[order[i]];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _buffer.Clear();
        foreach (string path in _runPaths)
            DeleteFileSafe(path);
        _runPaths.Clear();
    }

    private static void DeleteFileSafe(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* workspace cleanup is best effort */ }
    }

    private sealed class RunOrder(IIndexSpoolCodec<TRecord> codec) : IComparer<(TRecord Record, int Run)>
    {
        public int Compare((TRecord Record, int Run) x, (TRecord Record, int Run) y)
        {
            int comparison = codec.Compare(x.Record, y.Record);
            return comparison != 0 ? comparison : x.Run.CompareTo(y.Run);
        }
    }

    private sealed class SpoolRunReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly IIndexSpoolCodec<TRecord> _codec;
        private readonly byte[] _payload;

        public SpoolRunReader(string path, IIndexSpoolCodec<TRecord> codec)
        {
            _codec = codec;
            _payload = new byte[codec.MaxPayloadBytes];
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        }

        public bool TryReadNext(out TRecord record)
        {
            record = default!;
            Span<byte> header = stackalloc byte[4];
            if (!ReadExact(header))
                return false;
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0 || length > _payload.Length)
                throw new InvalidDataException("Spool run contains a record length outside the codec bound.");
            if (!ReadExact(_payload.AsSpan(0, length)))
                throw new InvalidDataException("Spool run ended in the middle of a record.");
            if (!_codec.TryDecode(_payload.AsSpan(0, length), out record))
                throw new InvalidDataException("Spool run contains a record the codec could not decode.");
            return true;
        }

        private bool ReadExact(Span<byte> destination)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int read = _stream.Read(destination[total..]);
                if (read <= 0)
                    return total == 0 ? false : throw new InvalidDataException("Spool run is truncated.");
                total += read;
            }
            return true;
        }

        public void Dispose() => _stream.Dispose();
    }
}
