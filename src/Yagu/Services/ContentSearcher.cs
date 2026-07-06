using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;
using static Yagu.Helpers.LineTruncator;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yagu.Services;

/// <summary>
/// Searches the contents of a single file for matches according to <see cref="SearchOptions"/>.
/// </summary>
public sealed class ContentSearcher
{
    public const long MemoryMapThresholdBytes = 8 * 1024 * 1024;

    private static int s_mmfLimit = 16;
    private static int s_nativeLimit = Math.Min(64, Environment.ProcessorCount * 2);

    /// <summary>
    /// Limits the number of concurrent memory-mapped file views to prevent
    /// runaway virtual address space / private bytes growth. With 24+ parallel
    /// workers, uncapped MMF views were contributing to 13+ GB unmanaged memory.
    /// Raised from 4 to 16 since the threshold now only targets genuinely large
    /// files (≥ 8 MB), and the Rust side's mmap doesn't gate at all.
    /// </summary>
    private static SemaphoreSlim s_mmfGate = new(s_mmfLimit, s_mmfLimit);

    /// <summary>
    /// Limits concurrent native (Rust) scans. The native engine also memory-maps
    /// large files, so it must be gated like the managed MMF path. Without this,
    /// 24-way parallel scans of 50 MB-cap files drove process working set above
    /// 22 GB while the managed heap stayed under 3 GB (i.e. unmanaged blow-up).
    /// Higher than <see cref="s_mmfGate"/> because the native path streams
    /// matches incrementally without holding all decoded text, and its mmaps
    /// are promptly munmap'd. NVMe drives saturate at 16–64 outstanding reads,
    /// so the old cap of 8 was an I/O ceiling on fast storage.
    /// Raised to 64 to match Rust MAX_WORKERS — ETL profiling showed only
    /// 1,744 opens/sec on NVMe capable of 100K+ IOPS, confirming the cap
    /// was the bottleneck, not the storage.
    /// </summary>
    private static SemaphoreSlim s_nativeGate = new(s_nativeLimit, s_nativeLimit);

    /// <summary>
    /// Reconfigure the MMF and native concurrency gates. Call before starting a search.
    /// Values ≤ 0 reset to defaults (16 for MMF, min(64, ProcessorCount×2) for native).
    /// </summary>
    public static void ConfigureGates(int mmfLimit, int nativeLimit)
    {
        int newMmf = mmfLimit > 0 ? mmfLimit : 16;
        int newNative = nativeLimit > 0 ? nativeLimit : Math.Min(64, Environment.ProcessorCount * 2);

        if (newMmf != s_mmfLimit)
        {
            s_mmfLimit = newMmf;
            s_mmfGate = new SemaphoreSlim(newMmf, newMmf);
        }
        if (newNative != s_nativeLimit)
        {
            s_nativeLimit = newNative;
            s_nativeGate = new SemaphoreSlim(newNative, newNative);
        }
    }

    // Skip-reason codes (all negative so they are distinguishable from match counts).
    public const int SkipBinary     = -1;
    public const int SkipAccessDenied = -2;
    public const int SkipIOError     = -3;
    public const int SkipTooLarge    = -4;
    public const int SkipTooSmall    = -9;
    public const int SkipNotFound    = -5;
    public const int SkipEncoding    = -6;
    public const int SkipOther       = -7;
    public const int SkipByExtension = -8;
    public const int SkipCloudOnly   = -10;

    /// <summary>File exceeded <see cref="SearchOptions.MaxMultilineBytes"/> and was skipped (multiline mode).</summary>
    public const int SkipMultilineTooLarge = -11;

    /// <summary>A per-file multiline scan aborted on <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>.</summary>
    public const int SkipMultilineTimeout = -12;

    private static int LogBinaryAndReturn(string filePath)
    {
        LogService.Instance.Verbose("ContentSearcher", $"Binary detected (native): {filePath}");
        return SkipBinary;
    }

    /// <summary>
    /// Search a single file and write matches to <paramref name="writer"/>.
    /// Returns the number of matches written, or a negative SkipXxx code if skipped.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept instance-shaped for injected test and service call sites.")]
    public Task<int> SearchFileAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken)
        => SearchFileAsync(filePath, regex, literal, literalComparison, options, writer, session: null, cancellationToken);

    /// <summary>
    /// Search a single file using an optional pre-compiled native session for
    /// faster regex reuse across files. Falls back to the sessionless path
    /// when <paramref name="session"/> is null.
    /// </summary>
    internal static async Task<int> SearchFileAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        Native.NativeSession? session,
        CancellationToken cancellationToken)
        => (await SearchFileWithStatsAsync(filePath, regex, literal, literalComparison, options, writer, session, cancellationToken).ConfigureAwait(false)).MatchCount;

    internal static async Task<FileSearchOutcome> SearchFileWithStatsAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        Native.NativeSession? session,
        CancellationToken cancellationToken)
    {
        bool watched = FileWatchDiagnostics.IsWatched(filePath);
        var totalSw = watched ? Stopwatch.StartNew() : null;
        if (watched) FileWatchDiagnostics.Checkpoint(filePath, "ENTER");

        FileInfo fi;
        long fileLength;
        FileMetadata metadata;
        if (FileMetadataCache.TryGet(filePath, out var cached))
        {
            fileLength = cached.Length;
            metadata = cached;
            fi = null!; // avoid stat; only used for Exists check below
            if (fileLength == 0 && !File.Exists(filePath)) return new FileSearchOutcome(SkipNotFound, 0);
        }
        else
        {
            try { fi = new FileInfo(filePath); }
            catch (Exception ex) { LogService.Instance.Verbose("ContentSearcher", $"Cannot stat file: {filePath}", ex); return new FileSearchOutcome(SkipOther, 0); }
            if (!fi.Exists) return new FileSearchOutcome(SkipNotFound, 0);

            // Cloud-only placeholder guard. Opening a dehydrated OneDrive/Google
            // Drive online-only file triggers a provider hydration that blocks
            // forever when no provider is connected. Manual discovery filters these
            // earlier; this covers Everything-backend / uncached paths. Reading the
            // attribute never hydrates.
            if (CloudFileHelper.IsCloudOnlyPlaceholder(fi.Attributes)
                && CloudFileHelper.ShouldSkipPlaceholder(filePath, fi.Attributes, options.SearchOnlineOnlyFiles))
            {
                LogService.Instance.Verbose("ContentSearcher", $"Cloud-only placeholder skipped: {filePath}");
                return new FileSearchOutcome(SkipCloudOnly, 0);
            }

            fileLength = fi.Length;
            metadata = new FileMetadata(fileLength, fi.LastWriteTime, fi.CreationTime);
        }

        if (options.MinFileSizeBytes > 0 && fileLength < options.MinFileSizeBytes) return new FileSearchOutcome(SkipTooSmall, 0);
        if (options.MaxFileSizeBytes > 0 && fileLength > options.MaxFileSizeBytes) return new FileSearchOutcome(SkipTooLarge, 0);

        // Extension-based skip — no binary sniff, no content read.
        // When SearchInsideArchives is enabled, defer the skip decision to
        // the ZIP header check below so archive files with skippable
        // extensions (e.g. .jar, .war) are still searched.
        bool hasSkippableExtension = false;
        if (options.SkipExtensions.Count > 0)
        {
            var ext = Path.GetExtension(filePath);
            if (ext.Length > 1 && options.SkipExtensions.Contains(ext.AsSpan(1).ToString()))
            {
                if (!options.SearchInsideArchives)
                    return new FileSearchOutcome(SkipByExtension, 0);
                hasSkippableExtension = true; // resolve after ZIP header check
            }
        }

        // Archive search: open the file once and reuse the stream for
        // both the header check and the archive searcher, eliminating the
        // 2–3 redundant CreateFile round-trips the old code path incurred.
        if (options.SearchInsideArchives)
        {
            try
            {
                using var archiveFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                var headerBuf = new byte[4];
                int headerRead = 0;
                while (headerRead < headerBuf.Length)
                {
                    int n = await archiveFs.ReadAsync(headerBuf.AsMemory(headerRead), cancellationToken).ConfigureAwait(false);
                    if (n <= 0) break;
                    headerRead += n;
                }
                if (headerRead >= 4 && BinaryDetector.IsZipMagic(headerBuf.AsSpan(0, headerRead)))
                {
                    archiveFs.Position = 0; // rewind — ZipArchive needs the full stream from the start
                    var archiveResult = await ZipArchiveSearcher.SearchArchiveStreamAsync(
                        archiveFs, filePath, regex, literal, literalComparison, options, writer, 0, cancellationToken).ConfigureAwait(false);
                    if (watched) FileWatchDiagnostics.Checkpoint(filePath, "EXIT-ZIP", totalSw!.ElapsedMilliseconds, $"produced={archiveResult.MatchCount} entries={archiveResult.EntriesScanned}");
                    return new FileSearchOutcome(archiveResult.MatchCount, archiveResult.MatchCount >= 0 ? fileLength : 0, archiveResult.EntriesScanned);
                }
                // Not a supported archive — if the extension was skippable, skip now.
                if (hasSkippableExtension)
                    return new FileSearchOutcome(SkipByExtension, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Instance.Verbose("ContentSearcher", $"Archive header check failed for {filePath}, falling through to normal scan", ex);
            }
        }

        // Multiline (cross-line) search runs the regex over the whole file buffer, so it cannot use
        // the per-line native or managed scanners nor the single-literal fast path. It enforces the
        // dedicated size cap first and runs the native whole-file engine (Phase 2), with the managed
        // engine as the lookaround/error fallback. A bare literal was promoted to an escaped regex
        // upstream, so `regex` is always non-null here.
        if (options.Multiline)
        {
            // Phase 2: try the native whole-buffer multiline engine first (non-lookaround patterns).
            // It compiles the identical resolved pattern (options.Query / UseRegex are the promoted
            // regex form here), enforces the same MaxMultilineBytes cap, and falls back to the managed
            // engine only on invalid-regex (lookaround) or a native error (NativeFellThrough).
            if (PreferNative && Native.NativeSearcher.IsAvailable)
            {
                int nativeMl = await TryNativeMultilineAsync(filePath, options, writer, metadata, cancellationToken).ConfigureAwait(false);
                if (nativeMl != NativeFellThrough)
                {
                    if (watched) FileWatchDiagnostics.Checkpoint(filePath, "EXIT-MULTILINE-NATIVE", totalSw!.ElapsedMilliseconds, $"produced={nativeMl}");
                    return new FileSearchOutcome(nativeMl, nativeMl >= 0 ? fileLength : 0);
                }
                if (watched) FileWatchDiagnostics.Checkpoint(filePath, "MULTILINE-NATIVE-FELLTHROUGH", totalSw!.ElapsedMilliseconds);
            }

            int mlResult;
            try
            {
                mlResult = await SearchMultilineAsync(filePath, fileLength, regex, options, writer, metadata, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex) { LogService.Instance.Verbose("ContentSearcher", $"Access denied (multiline): {filePath}", ex); return new FileSearchOutcome(SkipAccessDenied, 0); }
            catch (IOException ex) { LogService.Instance.Verbose("ContentSearcher", $"IO error (multiline): {filePath}", ex); return new FileSearchOutcome(SkipIOError, 0); }
            catch (DecoderFallbackException ex) { LogService.Instance.Verbose("ContentSearcher", $"Encoding error (multiline): {filePath}", ex); return new FileSearchOutcome(SkipEncoding, 0); }
            if (watched) FileWatchDiagnostics.Checkpoint(filePath, "EXIT-MULTILINE", totalSw!.ElapsedMilliseconds, $"produced={mlResult}");
            return new FileSearchOutcome(mlResult, mlResult >= 0 ? fileLength : 0);
        }

        // Native (Rust) fast path. Falls back to the managed path on any failure
        // mode the native engine doesn't handle (encoding-aware decoding,
        // unusual streams, etc.).
        if (PreferNative && Native.NativeSearcher.IsAvailable)
        {
            int produced = await TryNativeAsync(filePath, options, writer, session, metadata, cancellationToken).ConfigureAwait(false);
            if (produced != NativeFellThrough)
            {
                if (watched) FileWatchDiagnostics.Checkpoint(filePath, "EXIT-NATIVE", totalSw!.ElapsedMilliseconds, $"produced={produced}");
                return new FileSearchOutcome(produced, produced >= 0 ? fileLength : 0);
            }
            if (watched) FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-FELLTHROUGH", totalSw!.ElapsedMilliseconds);
        }

        try
        {
            int result;
            if (fileLength >= MemoryMapThresholdBytes)
            {
                // Large files: binary sniff still happens via the MMF view's first 8 KB.
                result = await SearchMappedAsync(filePath, fileLength, regex, literal, literalComparison, options, writer, metadata, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Single-peek open: one FileStream, one 8 KB read for binary + encoding
                // detection, then reuse the same stream for scanning. This eliminates the
                // 2–3 CreateFile round-trips per file the old code path incurred.
                result = await SearchStreamSinglePeekAsync(filePath, regex, literal, literalComparison, options, writer, metadata, cancellationToken).ConfigureAwait(false);
            }
            if (watched) FileWatchDiagnostics.Checkpoint(filePath, "EXIT-MANAGED", totalSw!.ElapsedMilliseconds, $"produced={result} size={fileLength}");
            return new FileSearchOutcome(result, result >= 0 ? fileLength : 0);
        }
        catch (UnauthorizedAccessException ex) { LogService.Instance.Verbose("ContentSearcher", $"Access denied searching: {filePath}", ex); return new FileSearchOutcome(SkipAccessDenied, 0); }
        catch (IOException ex) { LogService.Instance.Verbose("ContentSearcher", $"IO error searching: {filePath}", ex); return new FileSearchOutcome(SkipIOError, 0); }
        catch (DecoderFallbackException ex) { LogService.Instance.Verbose("ContentSearcher", $"Encoding error searching: {filePath}", ex); return new FileSearchOutcome(SkipEncoding, 0); }
    }


    internal static async Task<int> SearchStreamAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        FileMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var encoding = EncodingDetector.DetectEncoding(fs);
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        return await SearchLinesAsync(filePath, reader, regex, literal, literalComparison, options, writer, metadata, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-peek stream path: opens the file once, reads 8 KB into a buffer,
    /// runs binary detection AND encoding detection on that buffer, then seeks
    /// back and scans. Eliminates the 2–3 separate CreateFile + ReadFile calls
    /// the old code path made per file (~0.2 ms each on HDD).
    /// </summary>
    private static async Task<int> SearchStreamSinglePeekAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        FileMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);

        // Single 8 KB peek: shared by binary detection + encoding detection.
        var peek = new byte[BinaryDetector.SampleBytes];
        int peekRead = 0;
        while (peekRead < peek.Length)
        {
            int n = await fs.ReadAsync(peek.AsMemory(peekRead), cancellationToken).ConfigureAwait(false);
            if (n <= 0) break;
            peekRead += n;
        }

        if (options.SkipBinary && peekRead > 0 && BinaryDetector.IsBinary(peek.AsSpan(0, peekRead)))
        {
            LogService.Instance.Verbose("ContentSearcher", $"Binary detected (content sniff): {filePath}");
            return SkipBinary;
        }

        var encoding = EncodingDetector.DetectEncoding(peek.AsSpan(0, Math.Min(peekRead, 4)));

        // Seek back to the start so StreamReader reads the full file.
        fs.Position = 0;
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        return await SearchLinesAsync(filePath, reader, regex, literal, literalComparison, options, writer, metadata, cancellationToken).ConfigureAwait(false);
    }


    internal static async Task<int> SearchMappedAsync(
        string filePath,
        long length,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        FileMetadata metadata,
        CancellationToken cancellationToken)
    {
        await s_mmfGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            using var stream = mmf.CreateViewStream(0, length, MemoryMappedFileAccess.Read);

            // Peek the first 8 KB of the view for binary + encoding detection
            // so we don't need a separate file open for sniffing.
            Span<byte> peek = stackalloc byte[BinaryDetector.SampleBytes];
            int peekRead = 0;
            while (peekRead < peek.Length)
            {
                int n = stream.Read(peek[peekRead..]);
                if (n <= 0) break;
                peekRead += n;
            }

            if (options.SkipBinary && peekRead > 0 && BinaryDetector.IsBinary(peek[..peekRead]))
            {
                LogService.Instance.Verbose("ContentSearcher", $"Binary detected (content sniff, MMF): {filePath}");
                return SkipBinary;
            }

            var encoding = EncodingDetector.DetectEncoding(peek[..Math.Min(peekRead, 4)]);
            stream.Position = 0;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            return await SearchLinesAsync(filePath, reader, regex, literal, literalComparison, options, writer, metadata, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_mmfGate.Release();
        }
    }

    /// <summary>
    /// Cross-line (multiline) whole-file search: runs the regex over an LF-normalized shadow of the
    /// whole file so a single match can span line breaks (§6 of the multiline plan). Enforces the
    /// dedicated size cap first, binary-sniffs before decoding, buffers per-file results and publishes
    /// only on success (timeout atomicity), and emits span-aware <see cref="SearchResult"/> records
    /// (start line/col + end line/col). Returns the match count, or a negative Skip code.
    /// </summary>
    internal static async Task<int> SearchMultilineAsync(
        string filePath,
        long fileLength,
        Regex? regex,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        FileMetadata metadata,
        CancellationToken cancellationToken)
    {
        // Enforce the dedicated cap FIRST — never read multi-GB into a contiguous string.
        if (options.MaxMultilineBytes > 0 && fileLength > options.MaxMultilineBytes)
            return SkipMultilineTooLarge;

        // A bare literal is promoted to an escaped regex upstream, so regex is expected non-null.
        if (regex is null)
            return 0;

        cancellationToken.ThrowIfCancellationRequested();

        // ── Read: single 8 KB peek for binary + encoding detection BEFORE decoding, then read fully. ──
        string original;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
        {
            var peek = new byte[BinaryDetector.SampleBytes];
            int peekRead = 0;
            while (peekRead < peek.Length)
            {
                int n = await fs.ReadAsync(peek.AsMemory(peekRead), cancellationToken).ConfigureAwait(false);
                if (n <= 0) break;
                peekRead += n;
            }

            // Binary-sniff BEFORE decoding a (potentially 50 MB) buffer into a UTF-16 string,
            // so multiline never scans binaries the line path skips.
            if (options.SkipBinary && peekRead > 0 && BinaryDetector.IsBinary(peek.AsSpan(0, peekRead)))
            {
                LogService.Instance.Verbose("ContentSearcher", $"Binary detected (multiline sniff): {filePath}");
                return SkipBinary;
            }

            var encoding = EncodingDetector.DetectEncoding(peek.AsSpan(0, Math.Min(peekRead, 4)));
            fs.Position = 0;
            using var streamReader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
            original = await streamReader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        int contextLines = Math.Max(0, options.ContextLines);
        int matchCap = options.MaxMatchesPerFile > 0 ? options.MaxMatchesPerFile : int.MaxValue;
        if (options.MaxResults > 0 && options.MaxResults < matchCap)
            matchCap = options.MaxResults; // bound per-file buffering by min(MaxMatchesPerFile, MaxResults)

        // ── Build the LF shadow (parity-first: scan LF-only bytes) and a line-start table. ──
        var shadow = MultilineTextShadow.Build(original);
        string lf = shadow.Lf;
        var lineStarts = BuildLineStarts(lf);

        // Buffer results and publish only on success. If the regex times out mid-file, discard the
        // partial results and record a per-file multiline-timeout skip (atomicity).
        var buffered = new List<SearchResult>();
        try
        {
            foreach (Match m in regex.Matches(lf))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (m.Length == 0) continue; // drop zero-width, matching the line path

                int startOffset = m.Index;
                int endOffset = m.Index + m.Length;
                int startLineIdx = LineIndexOf(lineStarts, startOffset);
                int endLineIdx = LineIndexOf(lineStarts, endOffset);

                int startLineBegin = lineStarts[startLineIdx];
                int startLineContentEnd = startLineIdx + 1 < lineStarts.Count ? lineStarts[startLineIdx + 1] - 1 : lf.Length;
                int sourceCol = startOffset - startLineBegin;
                int firstLineVisibleEnd = Math.Min(endOffset, startLineContentEnd);
                int firstLineVisibleLen = Math.Max(0, firstLineVisibleEnd - startOffset);

                string startLineRaw = lf.Substring(startLineBegin, startLineContentEnd - startLineBegin);
                var display = TruncateAroundMatch(startLineRaw, sourceCol, firstLineVisibleLen);

                var before = BuildContextBefore(lf, lineStarts, startLineIdx, contextLines);
                var after = BuildContextAfter(lf, lineStarts, endLineIdx, contextLines);

                var result = new SearchResult(
                    FilePath: filePath,
                    LineNumber: startLineIdx + 1,
                    MatchLine: display.Text,
                    MatchStartColumn: display.MatchStart,
                    MatchLength: firstLineVisibleLen,
                    ContextBefore: before,
                    ContextAfter: after)
                { SourceMatchStartColumn = sourceCol };

                if (endLineIdx > startLineIdx)
                {
                    // True cross-line span: carry the end line/col; single-line hits (even during a
                    // multiline search) leave these null so they render exactly like line mode.
                    result.MatchEndLineNumber = endLineIdx + 1;
                    result.MatchEndColumn = endOffset - lineStarts[endLineIdx];
                }

                buffered.Add(result);
                if (buffered.Count >= matchCap) break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            LogService.Instance.Warning("ContentSearcher", $"Multiline regex timed out; skipping file: {filePath}");
            return SkipMultilineTimeout;
        }

        if (buffered.Count > 0)
        {
            FileMetadataCache.Set(filePath, metadata);
            foreach (var r in buffered)
                await writer.WriteAsync(r, cancellationToken).ConfigureAwait(false);
        }

        return buffered.Count;
    }

    /// <summary>Builds the sorted line-start offsets for an LF-only buffer (index i = start of line i+1).</summary>
    internal static List<int> BuildLineStarts(string lf)
    {
        var starts = new List<int>(Math.Max(1, lf.Length / 40)) { 0 };
        for (int i = 0; i < lf.Length; i++)
        {
            if (lf[i] == '\n')
                starts.Add(i + 1);
        }
        return starts;
    }

    /// <summary>Returns the 0-based line index containing <paramref name="offset"/> (greatest start ≤ offset).</summary>
    internal static int LineIndexOf(List<int> lineStarts, int offset)
    {
        int lo = 0, hi = lineStarts.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lineStarts[mid] <= offset) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    private static string LineTextAt(string lf, List<int> lineStarts, int lineIdx)
    {
        int begin = lineStarts[lineIdx];
        int end = lineIdx + 1 < lineStarts.Count ? lineStarts[lineIdx + 1] - 1 : lf.Length;
        return lf.Substring(begin, Math.Max(0, end - begin));
    }

    private static IReadOnlyList<string> BuildContextBefore(string lf, List<int> lineStarts, int startLineIdx, int contextLines)
    {
        if (contextLines == 0) return Array.Empty<string>();
        int from = Math.Max(0, startLineIdx - contextLines);
        if (from >= startLineIdx) return Array.Empty<string>();
        var list = new List<string>(startLineIdx - from);
        for (int li = from; li < startLineIdx; li++)
            list.Add(Truncate(LineTextAt(lf, lineStarts, li)));
        return list;
    }

    private static IReadOnlyList<string> BuildContextAfter(string lf, List<int> lineStarts, int endLineIdx, int contextLines)
    {
        if (contextLines == 0) return Array.Empty<string>();
        int lastLineIdx = lineStarts.Count - 1;
        int to = Math.Min(lastLineIdx, endLineIdx + contextLines);
        if (to <= endLineIdx) return Array.Empty<string>();
        var list = new List<string>(to - endLineIdx);
        for (int li = endLineIdx + 1; li <= to; li++)
            list.Add(Truncate(LineTextAt(lf, lineStarts, li)));
        return list;
    }

    private static async Task<int> SearchLinesAsync(
        string filePath,
        StreamReader reader,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        FileMetadata metadata,
        CancellationToken cancellationToken)
    {
        int contextLines = Math.Max(0, options.ContextLines);
        int perFileCap = options.MaxMatchesPerFile > 0 ? options.MaxMatchesPerFile : int.MaxValue;
        var ring = new RingBuffer<string>(contextLines);
        // Each pending entry accumulates context-after lines in a mutable list,
        // building the final SearchResult only once when the context is complete.
        var pendingAfter = new Queue<(SearchResult Partial, List<string> AfterLines, int Remaining)>();
        // Per-file freelist of context-after List<string> buffers. Match-dense files
        // (thousands of hits) used to allocate one List+backing array per match; we
        // now copy into a sized string[] on emit and recycle the List for reuse.
        Stack<List<string>>? afterListPool = contextLines > 0 ? new Stack<List<string>>() : null;
        int lineNumber = 0;
        int matchCount = 0;
        bool metadataCached = false;

        var buffer = ArrayPool<char>.Shared.Rent(16 * 1024);
        var lineBuilder = new StringBuilder();
        bool stoppedEarly = false;

        try
        {
            while (!stoppedEarly)
            {
                int charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (charsRead == 0)
                    break;

                for (int i = 0; i < charsRead; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    char ch = buffer[i];
                    if (ch == '\n')
                    {
                        if (lineBuilder.Length > 0 && lineBuilder[^1] == '\r')
                            lineBuilder.Length--;

                        string line = lineBuilder.ToString();
                        lineBuilder.Clear();
                        if (!await ProcessLineAsync(line).ConfigureAwait(false))
                        {
                            stoppedEarly = true;
                            break;
                        }
                    }
                    else
                    {
                        lineBuilder.Append(ch);
                    }
                }
            }

            if (!stoppedEarly && lineBuilder.Length > 0)
            {
                if (lineBuilder[^1] == '\r')
                    lineBuilder.Length--;

                await ProcessLineAsync(lineBuilder.ToString()).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        // Flush any partials that didn't get a full after-context.
        while (pendingAfter.Count > 0)
        {
            var (partial, afterLines, _) = pendingAfter.Dequeue();
            var finalAfter = afterLines.Count == 0 ? Array.Empty<string>() : afterLines.ToArray();
            await writer.WriteAsync(partial with { ContextAfter = finalAfter }, cancellationToken).ConfigureAwait(false);
        }

        return matchCount;

        async Task<bool> ProcessLineAsync(string line)
        {
            lineNumber++;

            // Fill out queued context-after slots.
            if (pendingAfter.Count > 0)
            {
                int remaining = pendingAfter.Count;
                for (int i = 0; i < remaining; i++)
                {
                    var (partial, afterLines, left) = pendingAfter.Dequeue();
                    afterLines.Add(Truncate(line));
                    int newLeft = left - 1;
                    if (newLeft <= 0)
                    {
                        var finalAfter = afterLines.Count == 0 ? Array.Empty<string>() : afterLines.ToArray();
                        afterLines.Clear();
                        afterListPool?.Push(afterLines);
                        await writer.WriteAsync(partial with { ContextAfter = finalAfter }, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        pendingAfter.Enqueue((partial, afterLines, newLeft));
                    }
                }
            }

            // Find matches in this line.
            var matches = FindMatches(line, regex, literal, literalComparison);
            if (matches.Count > 0)
            {
                if (!metadataCached)
                {
                    FileMetadataCache.Set(filePath, metadata);
                    metadataCached = true;
                }
                matchCount += matches.Count;
                var before = ring.Snapshot();
                foreach (var (start, length) in matches)
                {
                    var displayLine = TruncateAroundMatch(line, start, length);
                    var partial = new SearchResult(
                        FilePath: filePath,
                        LineNumber: lineNumber,
                        MatchLine: displayLine.Text,
                        MatchStartColumn: displayLine.MatchStart,
                        MatchLength: length,
                        ContextBefore: before,
                        ContextAfter: Array.Empty<string>())
                    { SourceMatchStartColumn = start };
                    if (contextLines == 0)
                    {
                        await writer.WriteAsync(partial, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var bucket = afterListPool is { Count: > 0 } pool ? pool.Pop() : new List<string>(contextLines);
                        pendingAfter.Enqueue((partial, bucket, contextLines));
                    }
                }
                if (matchCount >= perFileCap) return false;
            }

            ring.Add(Truncate(line));
            return true;
        }
    }

    [ThreadStatic] private static List<(int Start, int Length)>? t_hits;

    internal static List<(int Start, int Length)> FindMatches(string line, Regex? regex, string? literal, StringComparison cmp)
    {
        var hits = t_hits ??= new List<(int, int)>();
        hits.Clear();
        if (regex is not null)
        {
            foreach (Match m in regex.Matches(line))
            {
                if (m.Length == 0) continue; // avoid zero-width loop
                hits.Add((m.Index, m.Length));
            }
        }
        else if (!string.IsNullOrEmpty(literal))
        {
            int idx = 0;
            while (idx <= line.Length)
            {
                int found = line.IndexOf(literal, idx, cmp);
                if (found < 0) break;
                hits.Add((found, literal.Length));
                idx = found + literal.Length;
            }
        }
        return hits;
    }

    private sealed class RingBuffer<T>
    {
        private readonly T[] _data;
        private int _count;
        private int _head;
        // Cached snapshot: reused across consecutive matches until the ring mutates.
        // Without this, a file with N matches in a row allocated N copies of the same
        // before-context array — a major LOH/Gen2 contributor on dense-match files.
        private IReadOnlyList<T>? _cachedSnapshot;
        public RingBuffer(int capacity) { _data = new T[Math.Max(0, capacity)]; }
        public void Add(T item)
        {
            if (_data.Length == 0) return;
            _data[_head] = item;
            _head = (_head + 1) % _data.Length;
            if (_count < _data.Length) _count++;
            _cachedSnapshot = null; // invalidate
        }
        public IReadOnlyList<T> Snapshot()
        {
            if (_count == 0) return Array.Empty<T>();
            if (_cachedSnapshot is not null) return _cachedSnapshot;
            var result = new T[_count];
            int start = (_head - _count + _data.Length) % _data.Length;
            for (int i = 0; i < _count; i++) result[i] = _data[(start + i) % _data.Length];
            _cachedSnapshot = result;
            return result;
        }
    }

    /// <summary>When true, attempt the native (Rust) scan path before falling back to managed.</summary>
    public static bool PreferNative { get; set; } = true;

    /// <summary>
    /// Extensions that are known ZIP-like containers. Populated from <see cref="SearchOptions.ArchiveExtensions"/>
    /// before each search. Used to bypass extension-based skip at the file-lister layer.
    /// </summary>
    internal IReadOnlySet<string> ZipLikeExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static bool IsZipLikeExtension(IReadOnlySet<string> archiveExts, string ext) => archiveExts.Contains(ext);

    /// <summary>Sentinel returned by <see cref="TryNativeAsync"/> when the native path cannot be used and the caller should run the managed path.</summary>
    private const int NativeFellThrough = int.MinValue;

    /// <summary>
    /// Per-thread cancel-flag pointer. Eliminates per-file AllocHGlobal/FreeHGlobal
    /// + CancellationToken.Register allocations — the same pinned int* is reused for
    /// every file scanned on a given thread.
    /// </summary>
    private static readonly ThreadLocal<IntPtr> t_cancelPtr = new(
        () => Marshal.AllocHGlobal(sizeof(int)),
        trackAllValues: false);


    internal static async Task<int> TryNativeAsync(
        string filePath,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        Native.NativeSession? session,
        FileMetadata metadata,
        CancellationToken cancellationToken)
    {
        bool watched = FileWatchDiagnostics.IsWatched(filePath);
        var gateSw = watched ? Stopwatch.StartNew() : null;
        // Gate native scans to bound concurrent mmap views. See s_nativeGate.
        await s_nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (watched)
        {
            FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-GATE-ACQUIRED", gateSw!.ElapsedMilliseconds,
                $"gateAvail={s_nativeGate.CurrentCount}");
        }
        try
        {
            return await Task.Run(() =>
            {
                // Reuse the thread-local cancel-int — reset to 0 before each file.
                IntPtr cancelPtr = t_cancelPtr.Value!;
                unsafe { *(int*)cancelPtr = 0; }
                using var ctr = cancellationToken.Register(static state =>
                {
                    unsafe { Interlocked.Exchange(ref *(int*)(IntPtr)state!, 1); }
                }, cancelPtr);

                var sink = new StreamingSink(filePath, writer, options.ContextLines, metadata, cancellationToken);
                var scanSw = watched ? Stopwatch.StartNew() : null;
                if (watched) FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-SCAN-START");
                int status;
                try
                {
                    unsafe
                    {
                        if (session != null)
                        {
                            // Session path: uses pre-compiled regex — no per-file compilation.
                            status = Native.NativeSearcher.SearchFileStreamWithSession(
                                session, filePath, (int*)cancelPtr, sink);
                        }
                        else
                        {
                            status = Native.NativeSearcher.SearchFileStream(
                                filePath, options.Query, options, (int*)cancelPtr, sink);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("ContentSearcher", $"Native streaming scan threw for {filePath}; falling back to managed", ex);
                    if (watched) FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-SCAN-THREW", scanSw!.ElapsedMilliseconds, ex.GetType().Name);
                    return NativeFellThrough;
                }

                if (watched) FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-SCAN-DONE", scanSw!.ElapsedMilliseconds, $"status={status} emitted={sink.Emitted}");

                return status switch
                {
                    Native.NativeSearcher.StatusOk => sink.Emitted,
                    Native.NativeSearcher.StatusBinarySkipped => LogBinaryAndReturn(filePath),
                    Native.NativeSearcher.StatusTooLarge => SkipTooLarge,
                    Native.NativeSearcher.StatusOpenFailed => SkipAccessDenied,
                    Native.NativeSearcher.StatusCancelled => Cancel(sink.Emitted, cancellationToken),
                    Native.NativeSearcher.StatusInvalidRegex => NativeFellThrough,
                    _ => NativeFellThrough,
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_nativeGate.Release();
        }
    }

    /// <summary>
    /// Phase 2: native whole-buffer multiline scan of a single file. Returns
    /// <see cref="NativeFellThrough"/> when the native path can't be used (native
    /// unavailable, or lookaround/invalid-regex) so the caller runs the managed
    /// <see cref="SearchMultilineAsync"/>. Over-cap / binary / open skips map to
    /// the same skip codes the managed path returns (parity, plan §9).
    /// </summary>
    internal static async Task<int> TryNativeMultilineAsync(
        string filePath,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        FileMetadata metadata,
        CancellationToken cancellationToken)
    {
        await s_nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                IntPtr cancelPtr = t_cancelPtr.Value!;
                unsafe { *(int*)cancelPtr = 0; }
                using var ctr = cancellationToken.Register(static state =>
                {
                    unsafe { Interlocked.Exchange(ref *(int*)(IntPtr)state!, 1); }
                }, cancelPtr);

                var sink = new MultilineStreamingSink(filePath, writer, metadata, cancellationToken);
                int status;
                try
                {
                    unsafe
                    {
                        status = Native.NativeSearcher.SearchFileStreamMultiline(
                            filePath, options.Query, options, (int*)cancelPtr, sink);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("ContentSearcher", $"Native multiline scan threw for {filePath}; falling back to managed", ex);
                    return NativeFellThrough;
                }

                return status switch
                {
                    Native.NativeSearcher.StatusOk => sink.Emitted,
                    Native.NativeSearcher.StatusBinarySkipped => LogBinaryAndReturn(filePath),
                    Native.NativeSearcher.StatusTooLarge => SkipMultilineTooLarge,
                    Native.NativeSearcher.StatusOpenFailed => SkipAccessDenied,
                    Native.NativeSearcher.StatusCancelled => Cancel(sink.Emitted, cancellationToken),
                    // Lookaround / invalid regex: run the managed whole-file engine instead.
                    Native.NativeSearcher.StatusInvalidRegex => NativeFellThrough,
                    _ => NativeFellThrough,
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_nativeGate.Release();
        }
    }

    internal static int Cancel(int emitted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return emitted;
    }

    /// <summary>
    /// Sink that receives per-match callbacks from the native engine, copies
    /// pointer-backed data into managed strings, and writes <see cref="SearchResult"/>
    /// items into the channel.
    /// </summary>

    internal sealed class StreamingSink : Native.IStreamingSink
    {
        private readonly string _filePath;
        private readonly ChannelWriter<SearchResult> _writer;
        private readonly CancellationToken _ct;
        private readonly int _contextLines;
        private readonly FileMetadata _metadata;
        private bool _metadataCached;
        public int Emitted;
        public Exception? CapturedException { get; set; }
        public string? ErrorMessage { get; set; }

        public StreamingSink(string filePath, ChannelWriter<SearchResult> writer, int contextLines, FileMetadata metadata, CancellationToken ct)
        {
            _filePath = filePath;
            _writer = writer;
            _ct = ct;
            _contextLines = contextLines;
            _metadata = metadata;
        }

        public unsafe int OnMatch(Native.NativeSearcher.QgMatchView* m)
        {
            if (_ct.IsCancellationRequested) return 1;

            var view = *m;
            int lineBytes = view.LineLen > (nuint)int.MaxValue ? int.MaxValue : (int)view.LineLen;
            int matchStartBytes = view.MatchStart > int.MaxValue ? lineBytes : (int)view.MatchStart;
            int? sourceMatchStartBytes = view.SourceMatchStart > int.MaxValue ? (int?)null : (int)view.SourceMatchStart;
            int matchLenBytes = view.MatchLen > int.MaxValue ? 0 : (int)view.MatchLen;
            var matchLine = DecodeMatchLine(view.LinePtr, lineBytes, matchStartBytes, matchLenBytes, sourceMatchStartBytes);

            var before = UnpackLinesTruncated(view.CtxBeforePtr, view.CtxBeforeBytes, view.CtxBeforeCount);
            var after = UnpackLinesTruncated(view.CtxAfterPtr, view.CtxAfterBytes, view.CtxAfterCount);

            int lineNum = view.LineNumber > int.MaxValue ? int.MaxValue : (int)view.LineNumber;

            var result = new SearchResult(
                FilePath: _filePath,
                LineNumber: lineNum,
                MatchLine: matchLine.Line,
                MatchStartColumn: matchLine.MatchStart,
                MatchLength: matchLine.MatchLength,
                ContextBefore: before,
                ContextAfter: after)
            { SourceMatchStartColumn = matchLine.SourceMatchStart };

            if (!_metadataCached)
            {
                FileMetadataCache.Set(_filePath, _metadata);
                _metadataCached = true;
            }

            if (!TryWriteWithBackpressure(result)) return 1;
            Emitted++;
            return 0;
        }

        private bool TryWriteWithBackpressure(SearchResult result)
        {
            if (_writer.TryWrite(result))
                return true;

            while (!_ct.IsCancellationRequested)
            {
                try
                {
                    if (!_writer.WaitToWriteAsync(_ct).AsTask().GetAwaiter().GetResult())
                        return false;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                if (_writer.TryWrite(result))
                    return true;
            }

            return false;
        }

        private static unsafe NativeMatchDecoder.DecodedMatchLine DecodeMatchLine(byte* ptr, int len, int matchStartBytes, int matchLenBytes, int? sourceMatchStartBytes = null)
            => NativeMatchDecoder.DecodeMatchLine(ptr, len, matchStartBytes, matchLenBytes, sourceMatchStartBytes);

        private static unsafe IReadOnlyList<string> UnpackLinesTruncated(byte* ptr, nuint totalBytes, uint count)
            => NativeMatchDecoder.UnpackLinesTruncated(ptr, totalBytes, count);
    }

    /// <summary>
    /// Sink for the native multiline engine: decodes each span-carrying
    /// <see cref="Native.NativeSearcher.QgMultilineMatchView"/> into a span-aware
    /// <see cref="SearchResult"/>. The START line is decoded exactly like the
    /// single-line <see cref="StreamingSink"/>; the true span end (end line/col)
    /// is carried on the result only when the match actually crosses lines
    /// (single-line hits during a multiline search stay null and render like line
    /// mode).
    /// </summary>
    internal sealed class MultilineStreamingSink : Native.IMultilineStreamingSink
    {
        private readonly string _filePath;
        private readonly ChannelWriter<SearchResult> _writer;
        private readonly CancellationToken _ct;
        private readonly FileMetadata _metadata;
        private bool _metadataCached;
        public int Emitted;
        public Exception? CapturedException { get; set; }
        public string? ErrorMessage { get; set; }

        public MultilineStreamingSink(string filePath, ChannelWriter<SearchResult> writer, FileMetadata metadata, CancellationToken ct)
        {
            _filePath = filePath;
            _writer = writer;
            _ct = ct;
            _metadata = metadata;
        }

        public unsafe int OnMultilineMatch(Native.NativeSearcher.QgMultilineMatchView* m)
        {
            if (_ct.IsCancellationRequested) return 1;

            var view = *m;
            int lineBytes = view.LineLen > (nuint)int.MaxValue ? int.MaxValue : (int)view.LineLen;
            int matchStartBytes = view.MatchStart > int.MaxValue ? lineBytes : (int)view.MatchStart;
            int? sourceMatchStartBytes = view.SourceMatchStart > int.MaxValue ? (int?)null : (int)view.SourceMatchStart;
            int matchLenBytes = view.MatchLen > int.MaxValue ? 0 : (int)view.MatchLen;
            var matchLine = NativeMatchDecoder.DecodeMatchLine(view.LinePtr, lineBytes, matchStartBytes, matchLenBytes, sourceMatchStartBytes);

            var before = NativeMatchDecoder.UnpackLinesTruncated(view.CtxBeforePtr, view.CtxBeforeBytes, view.CtxBeforeCount);
            var after = NativeMatchDecoder.UnpackLinesTruncated(view.CtxAfterPtr, view.CtxAfterBytes, view.CtxAfterCount);

            int lineNum = view.LineNumber > int.MaxValue ? int.MaxValue : (int)view.LineNumber;
            int endLine = view.EndLine > int.MaxValue ? int.MaxValue : (int)view.EndLine;

            var result = new SearchResult(
                FilePath: _filePath,
                LineNumber: lineNum,
                MatchLine: matchLine.Line,
                MatchStartColumn: matchLine.MatchStart,
                MatchLength: matchLine.MatchLength,
                ContextBefore: before,
                ContextAfter: after)
            { SourceMatchStartColumn = matchLine.SourceMatchStart };

            // Carry the true span only when the match crosses lines (mirrors the managed engine —
            // single-line hits leave the end fields null so they render exactly like line mode).
            if (endLine > lineNum)
            {
                result.MatchEndLineNumber = endLine;
                result.MatchEndColumn = view.EndCol > int.MaxValue ? int.MaxValue : (int)view.EndCol;
            }

            if (!_metadataCached)
            {
                FileMetadataCache.Set(_filePath, _metadata);
                _metadataCached = true;
            }

            if (!TryWriteWithBackpressure(result)) return 1;
            Emitted++;
            return 0;
        }

        private bool TryWriteWithBackpressure(SearchResult result)
        {
            if (_writer.TryWrite(result))
                return true;

            while (!_ct.IsCancellationRequested)
            {
                try
                {
                    if (!_writer.WaitToWriteAsync(_ct).AsTask().GetAwaiter().GetResult())
                        return false;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                if (_writer.TryWrite(result))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Shared helpers for decoding native match data into managed SearchResult fields.
    /// Used by both the per-file <see cref="StreamingSink"/> and the batch
    /// <see cref="SearchService"/> parallel scan sink.
    /// </summary>

    internal static class NativeMatchDecoder
    {
        internal readonly record struct DecodedMatchLine(string Line, int MatchStart, int MatchLength, int SourceMatchStart)
        {
            public void Deconstruct(out string line, out int matchStart, out int matchLength)
            {
                line = Line;
                matchStart = MatchStart;
                matchLength = MatchLength;
            }
        }

        // Decode one native match view into a managed line + display/source columns.
        //
        // This is the DELIBERATE home for full-line column work. `sourceMatchStartBytes`
        // is null when the native scanner left the column unresolved (the common
        // case — see `source_match_start` in yagu-core/src/scan.rs). Computing the
        // UTF-16 column here is correct and cheap because it runs LAZILY, only when
        // a match is materialized into a managed result, not once per match in the
        // native scan hot loop. Do NOT push this work back into the native loop:
        // doing so regressed full-`C:\` "test" ~4x (SOURCE_MATCH_START note in repo
        // memory `yagu-profiling.md`).
        internal static unsafe DecodedMatchLine DecodeMatchLine(byte* ptr, int len, int matchStartBytes, int matchLenBytes, int? sourceMatchStartBytes = null)
        {
            if (ptr == null || len <= 0) return new DecodedMatchLine(string.Empty, 0, 0, 0);

            int safeStartBytes = Math.Clamp(matchStartBytes, 0, len);
            int safeLengthBytes = Math.Clamp(matchLenBytes, 0, len - safeStartBytes);
            int sourceMatchStart = -1;
            if (sourceMatchStartBytes.HasValue)
            {
                sourceMatchStart = Math.Max(0, sourceMatchStartBytes.Value);
            }

            if (LineTruncator.TruncatedLength == 0 || len <= LineTruncator.MaxDisplayLength * 4)
            {
                int fullMatchStart;
                int fullMatchLength;
                if (IsAsciiRegion(ptr, safeStartBytes + safeLengthBytes))
                {
                    fullMatchStart = safeStartBytes;
                    fullMatchLength = safeLengthBytes;
                }
                else
                {
                    fullMatchStart = Encoding.UTF8.GetCharCount(ptr, safeStartBytes);
                    fullMatchLength = Encoding.UTF8.GetCharCount(ptr + safeStartBytes, safeLengthBytes);
                }

                var fullLine = Encoding.UTF8.GetString(ptr, len);
                var fullDisplayLine = LineTruncator.TruncateAroundMatch(fullLine, fullMatchStart, fullMatchLength);
                return new DecodedMatchLine(fullDisplayLine.Text, fullDisplayLine.MatchStart, fullMatchLength, sourceMatchStart >= 0 ? sourceMatchStart : fullMatchStart);
            }

            if (sourceMatchStart < 0)
            {
                sourceMatchStart = IsAsciiRegion(ptr, safeStartBytes)
                ? safeStartBytes
                : Encoding.UTF8.GetCharCount(ptr, safeStartBytes);
            }

            int windowBytes = Math.Max(safeLengthBytes, LineTruncator.TruncatedLength + 2);
            int contextBytes = Math.Max(0, (windowBytes - safeLengthBytes) / 2);
            int windowStart = Math.Max(0, safeStartBytes - contextBytes);
            int windowEnd = Math.Min(len, windowStart + windowBytes);

            int minEnd = Math.Min(len, safeStartBytes + safeLengthBytes);
            if (windowEnd < minEnd)
                windowEnd = minEnd;

            if (windowEnd - windowStart < windowBytes)
                windowStart = Math.Max(0, windowEnd - windowBytes);

            windowStart = AlignUtf8Start(ptr, windowStart, len);
            windowEnd = AlignUtf8End(ptr, windowStart, windowEnd, len);

            // Fast path: if all relevant bytes are ASCII, char offsets = byte offsets.
            // Source code is overwhelmingly ASCII, so this avoids two GetCharCount calls.
            int matchStart, matchLength;
            int matchBytesFromWindowStart = Math.Max(0, safeStartBytes - windowStart);
            if (IsAsciiRegion(ptr + windowStart, Math.Min(windowEnd - windowStart, matchBytesFromWindowStart + safeLengthBytes)))
            {
                matchStart = matchBytesFromWindowStart;
                matchLength = safeLengthBytes;
            }
            else
            {
                matchStart = Encoding.UTF8.GetCharCount(ptr + windowStart, matchBytesFromWindowStart);
                matchLength = Encoding.UTF8.GetCharCount(ptr + safeStartBytes, safeLengthBytes);
            }

            var window = Encoding.UTF8.GetString(ptr + windowStart, windowEnd - windowStart);
            var prefix = windowStart > 0 ? LineTruncator.Ellipsis : string.Empty;
            var suffix = windowEnd < len ? LineTruncator.Ellipsis : string.Empty;
            return new DecodedMatchLine(string.Concat(prefix, window, suffix), matchStart + prefix.Length, matchLength, sourceMatchStart);
        }

        private static unsafe int AlignUtf8Start(byte* ptr, int start, int totalLength)
        {
            while (start < totalLength && IsUtf8ContinuationByte(ptr[start]))
                start++;
            return start;
        }

        private static unsafe int AlignUtf8End(byte* ptr, int start, int end, int totalLength)
        {
            end = Math.Clamp(end, start, totalLength);
            while (end > start && end < totalLength && IsUtf8ContinuationByte(ptr[end]))
                end--;
            return end;
        }

        private static bool IsUtf8ContinuationByte(byte value) => (value & 0xC0) == 0x80;

        /// <summary>
        /// Returns true if all bytes in [ptr, ptr+len) are ASCII (high bit clear).
        /// Uses vectorized check when possible.
        /// </summary>
        private static unsafe bool IsAsciiRegion(byte* ptr, int len)
        {
            if (len <= 0) return true;
            return Ascii.IsValid(new ReadOnlySpan<byte>(ptr, len));
        }

        internal static unsafe IReadOnlyList<string> UnpackLinesTruncated(byte* ptr, nuint totalBytes, uint count)
        {
            if (count == 0 || ptr == null || totalBytes == 0) return Array.Empty<string>();
            var list = new List<string>((int)count);
            int pos = 0;
            int total = (int)totalBytes;
            for (uint i = 0; i < count && pos + 4 <= total; i++)
            {
                uint len = (uint)(ptr[pos] | (ptr[pos + 1] << 8) | (ptr[pos + 2] << 16) | (ptr[pos + 3] << 24));
                pos += 4;
                if (len > int.MaxValue || pos + (int)len > total) break;
                list.Add(DecodeAndTruncate(ptr + pos, (int)len));
                pos += (int)len;
            }
            return list;
        }

        /// <summary>
        /// Decode a UTF-8 byte buffer to a managed string, hard-capped to keep huge
        /// lines off the LOH.
        /// </summary>

        internal static unsafe string DecodeAndTruncate(byte* ptr, int len)
        {
            if (ptr == null || len <= 0) return string.Empty;
            int maxBytes = (LineTruncator.MaxDisplayLength + 1) * 4;
            bool bytesTruncated = len > maxBytes;
            int decodeBytes = bytesTruncated ? maxBytes : len;
            if (bytesTruncated)
            {
                while (decodeBytes > 0 && (ptr[decodeBytes] & 0xC0) == 0x80) decodeBytes--;
            }
            var s = Encoding.UTF8.GetString(ptr, decodeBytes);
            if (LineTruncator.TruncatedLength > 0 && bytesTruncated && s.Length <= LineTruncator.MaxDisplayLength)
            {
                int keep = Math.Min(s.Length, LineTruncator.TruncatedLength);
                return string.Concat(s.AsSpan(0, keep), LineTruncator.Ellipsis);
            }
            return Truncate(s);
        }
    }
}
