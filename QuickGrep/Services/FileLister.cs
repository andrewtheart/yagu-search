using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using QuickGrep.Native;
using QuickGrep.Services;

namespace QuickGrep.Services;

public interface IFileLister
{
    /// <summary>
    /// Yields full paths of every file under <paramref name="directory"/>.
    /// Implementations should respect <paramref name="cancellationToken"/>.
    /// </summary>
    IAsyncEnumerable<string> ListFilesAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        CancellationToken cancellationToken);

    /// <summary>Reason fallback enumeration was used (null means es.exe was used).</summary>
    string? FallbackReason { get; }

    /// <summary>Directories that were discovered but could not be enumerated.</summary>
    int SkippedDirectories { get; }

    /// <summary>Skipped directories whose failure reason was access denied.</summary>
    int AccessDeniedDirectories { get; }

    /// <summary>Known total file count from the listing backend, or 0 when it cannot be known up front.</summary>
    int KnownTotalFiles { get; }
}

/// <summary>Selects which file-listing backend to use.</summary>
public enum FileListerBackend
{
    /// <summary>SDK → es.exe → .NET fallback (default).</summary>
    Auto = 0,
    /// <summary>Force the in-process Everything SDK only.</summary>
    EverythingSdk = 1,
    /// <summary>Force es.exe (voidtools Everything CLI) only.</summary>
    EsExe = 2,
    /// <summary>Force the built-in .NET recursive enumeration (no Everything).</summary>
    Managed = 3,
}

/// <summary>
/// Lists files under a directory using <c>es.exe</c> (voidtools Everything) when available,
/// and falls back to .NET recursive enumeration with cycle protection otherwise.
/// </summary>
public sealed class FileLister : IFileLister
{
    private readonly Func<string, ProcessStartInfo, IProcess>? _processFactory;
    private int _skippedDirectories;
    private int _accessDeniedDirectories;
    private int _knownTotalFiles;
    public string? FallbackReason { get; private set; }
    public int SkippedDirectories => Volatile.Read(ref _skippedDirectories);
    public int AccessDeniedDirectories => Volatile.Read(ref _accessDeniedDirectories);
    public int KnownTotalFiles => Volatile.Read(ref _knownTotalFiles);

    /// <summary>
    /// Forced backend selection. <c>Auto</c> tries SDK → es.exe → .NET in order.
    /// Other values restrict to a single backend.
    /// </summary>
    public static FileListerBackend Backend { get; set; } = FileListerBackend.Auto;

    public FileLister() { }

    // Test seam.
    internal FileLister(Func<string, ProcessStartInfo, IProcess>? processFactory) => _processFactory = processFactory;

    public async IAsyncEnumerable<string> ListFilesAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FallbackReason = null;
        Volatile.Write(ref _skippedDirectories, 0);
        Volatile.Write(ref _accessDeniedDirectories, 0);
        Volatile.Write(ref _knownTotalFiles, 0);
        if (string.IsNullOrWhiteSpace(directory)) yield break;

        var fullDir = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDir))
        {
            FallbackReason = "Directory does not exist";
            yield break;
        }

        var backend = Backend;

        // ── Tier 1: Everything SDK (in-process, fastest) ──
        bool sdkYielded = false;
        if (_processFactory is null && (backend == FileListerBackend.Auto || backend == FileListerBackend.EverythingSdk)) // skip SDK in test mode
        {
            IAsyncEnumerable<string>? sdkResults = null;
            try
            {
                sdkResults = RunEverythingSdkAsync(fullDir, includeExtensions, maxFiles, cancellationToken);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("FileLister", $"Everything SDK unavailable: {ex.Message}", ex);
            }

            if (sdkResults is not null)
            {
                await foreach (var p in sdkResults.WithCancellation(cancellationToken))
                {
                    sdkYielded = true;
                    yield return p;
                }
                if (sdkYielded) yield break;
            }
            if (backend == FileListerBackend.EverythingSdk)
            {
                if (FallbackReason is null) FallbackReason = "Everything SDK returned no results (forced backend)";
                yield break;
            }
        }

        // ── Tier 2: es.exe (process spawn) ──
        if (backend == FileListerBackend.Auto || backend == FileListerBackend.EsExe)
        {
            var esPath = FindEsExe();
            bool esYielded = false;
            if (esPath is not null)
            {
                IAsyncEnumerable<string>? esResults = null;
                try
                {
                    esResults = RunEverythingAsync(esPath, fullDir, includeExtensions, maxFiles, cancellationToken);
                }
                catch (Exception ex)
                {
                    FallbackReason = $"es.exe failed: {ex.Message}";
                    LogService.Instance.Warning("FileLister", FallbackReason, ex);
                }

                if (esResults is not null)
                {
                    await foreach (var p in esResults.WithCancellation(cancellationToken))
                    {
                        esYielded = true;
                        yield return p;
                    }
                    if (esYielded) yield break;
                    if (FallbackReason is null) FallbackReason = "es.exe returned no results";
                }
            }
            else
            {
                if (FallbackReason is null) FallbackReason = "es.exe not found";
            }
            if (backend == FileListerBackend.EsExe)
                yield break;
        }

        // ── Tier 3: .NET recursive enumeration ──
        await foreach (var p in EnumerateFallbackAsync(fullDir, includeExtensions, maxFiles, cancellationToken))
        {
            yield return p;
        }
    }

    // ── Everything SDK (in-process) ────────────────────────────────
    private static bool _sdkAvailable = true; // set to false on first load failure

    private async IAsyncEnumerable<string> RunEverythingSdkAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_sdkAvailable)
        {
            FallbackReason = "Everything SDK not available";
            yield break;
        }

        // Build the Everything search string.
        // "file:" modifier ensures only file results (not folders).
        // Include the directory path with trailing '\' to limit to that folder tree.
        // Paths containing spaces must be double-quoted.
        var normalizedDir = directory.TrimEnd('\\') + "\\";
        var pathPart = normalizedDir.Contains(' ')
            ? $"\"{normalizedDir}\""
            : normalizedDir;

        // Always start with "file:" to exclude folder results
        var query = $"file: {pathPart}";

        if (includeExtensions is { Count: > 0 })
        {
            var exts = string.Join(';', includeExtensions
                .Select(NormalizeExtension)
                .Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(exts))
                query += $" ext:{exts}";
        }

        LogService.Instance.Warning("FileLister", $"Everything SDK query: {query}");

        // Stream results through a bounded channel instead of collecting all
        // into a List<string>. This avoids allocating a multi-million-entry
        // List (with LOH-sized backing arrays) that caused 1.4 GB LOH spikes
        // and triggered 97%-time-in-GC storms.
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        string? error = null;

        // Producer: runs the Everything SDK query and writes paths into the channel.
        _ = Task.Run(() =>
        {
            try
            {
                lock (EverythingSdk.Lock)
                {
                    try
                    {
                        if (!EverythingSdk.IsDBLoaded())
                        {
                            error = "Everything database not loaded (is Everything running?)";
                            return;
                        }

                        EverythingSdk.Reset();
                        EverythingSdk.SetSearch(query);
                        EverythingSdk.SetMatchCase(false);
                        EverythingSdk.SetRequestFlags(EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME);
                        EverythingSdk.SetSort(EverythingSdk.EVERYTHING_SORT_PATH_ASCENDING);
                        if (maxFiles > 0)
                            EverythingSdk.SetMax((uint)maxFiles);
                        else
                            EverythingSdk.SetMax(0xFFFFFFFF);

                        if (!EverythingSdk.Query(bWait: true))
                        {
                            var err = EverythingSdk.GetLastError();
                            error = err == EverythingSdk.EVERYTHING_ERROR_IPC
                                ? "Everything is not running"
                                : $"Everything SDK query failed: {EverythingSdk.ErrorMessage(err)}";
                            return;
                        }

                        uint count = EverythingSdk.GetNumResults();
                        uint total = EverythingSdk.GetTotResults();
                        SetKnownTotalFiles(count);
                        LogService.Instance.Warning("FileLister", $"Everything SDK: {count} returned, {total} total matches, last error={EverythingSdk.GetLastError()}");
                        var buf = new System.Text.StringBuilder(1024);

                        for (uint i = 0; i < count; i++)
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            buf.Clear();
                            uint len = EverythingSdk.GetResultFullPathName(i, buf, (uint)buf.Capacity);
                            if (len == 0) continue;
                            if (len >= buf.Capacity)
                            {
                                buf.Capacity = (int)len + 1;
                                buf.Clear();
                                EverythingSdk.GetResultFullPathName(i, buf, (uint)buf.Capacity);
                            }
                            // Write into the channel. TryWrite succeeds until the 4096
                            // buffer is full; if full, use the async path with a sync wait.
                            if (!channel.Writer.TryWrite(buf.ToString()))
                            {
                                channel.Writer.WriteAsync(buf.ToString(), cancellationToken)
                                    .AsTask().GetAwaiter().GetResult();
                            }
                        }

                        EverythingSdk.Reset();
                    }
                    catch (DllNotFoundException)
                    {
                        _sdkAvailable = false;
                        error = "Everything64.dll not found";
                    }
                    catch (Exception ex)
                    {
                        error = $"Everything SDK error: {ex.Message}";
                    }
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        if (error is not null)
        {
            FallbackReason = error;
            LogService.Instance.Warning("FileLister", error);
            yield break;
        }

        bool anyYielded = false;
        await foreach (var path in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (error is not null) break;
            anyYielded = true;
            yield return path;
        }

        if (error is not null)
        {
            FallbackReason = error;
            LogService.Instance.Warning("FileLister", error);
            yield break;
        }

        if (!anyYielded)
        {
            FallbackReason = "Everything SDK returned no results";
            yield break;
        }
    }

    public static string? FindEsExe()
    {
        var candidates = new List<string>();

        // 1. PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim('"'), "es.exe"));
            }
        }
        // 2. LocalAppData
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "es.exe"));
        // 3. Program Files
        candidates.Add(@"C:\Program Files\Everything\es.exe");
        candidates.Add(@"C:\Program Files (x86)\Everything\es.exe");
        // 4. C:\tools
        candidates.Add(@"C:\tools\es.exe");

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch (Exception ex) { LogService.Instance.Verbose("FileLister", $"Cannot check es.exe path: {c}", ex); }
        }
        return null;
    }

    private async IAsyncEnumerable<string> RunEverythingAsync(
        string esPath,
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // es.exe expects a path-prefix query. Wrap in quotes and ensure trailing slash for prefix match.
        var dirArg = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        var args = new List<string>
        {
            dirArg,
            "/a-d",       // exclude directories
        };
        if (includeExtensions is { Count: > 0 })
        {
            var exts = string.Join(';', includeExtensions.Select(NormalizeExtension).Where(s => !string.IsNullOrEmpty(s))!);
            if (!string.IsNullOrEmpty(exts)) args.Add($"ext:{exts}");
        }
        if (maxFiles > 0) { args.Add("-n"); args.Add(maxFiles.ToString()); }

        int resultCount = await TryGetEverythingResultCountAsync(esPath, args, cancellationToken).ConfigureAwait(false);
        if (resultCount > 0) SetKnownTotalFiles(resultCount);

        var psi = new ProcessStartInfo
        {
            FileName = esPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        IProcess proc = _processFactory is not null ? _processFactory(esPath, psi) : new RealProcess(psi);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            FallbackReason = $"es.exe could not start: {ex.Message}";
            LogService.Instance.Warning("FileLister", FallbackReason, ex);
            yield break;
        }

        string? line;
        int yielded = 0;
        while ((line = await proc.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            yield return line;
            yielded++;
        }

        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (proc.ExitCode == 8)
        {
            FallbackReason = "Everything is not running";
            // Re-yield via fallback if we didn't get any results.
            if (yielded == 0) yield break;
        }
        else if (proc.ExitCode != 0 && yielded == 0)
        {
            FallbackReason = $"es.exe exited with code {proc.ExitCode}";
        }
    }

    private async Task<int> TryGetEverythingResultCountAsync(
        string esPath,
        IReadOnlyList<string> queryArgs,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = esPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-get-result-count");
        foreach (var argument in queryArgs) psi.ArgumentList.Add(argument);

        IProcess countProcess = _processFactory is not null ? _processFactory(esPath, psi) : new RealProcess(psi);
        try
        {
            countProcess.Start();
            string? line = await countProcess.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            await countProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (countProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(line)) return 0;
            return int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count >= 0
                ? count
                : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Instance.Verbose("FileLister", $"Could not get es.exe result count: {ex.Message}", ex);
            return 0;
        }
    }

    private void SetKnownTotalFiles(uint count) =>
        SetKnownTotalFiles(count > int.MaxValue ? int.MaxValue : (int)count);

    private void SetKnownTotalFiles(int count) =>
        Volatile.Write(ref _knownTotalFiles, Math.Max(0, count));

    private static string NormalizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return string.Empty;
        var s = ext.Trim();
        // Strip leading "*." or "*" or "."
        if (s.StartsWith("*.")) s = s[2..];
        else s = s.TrimStart('.', '*');
        return s;
    }

    private async IAsyncEnumerable<string> EnumerateFallbackAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(directory);

        var extSet = includeExtensions is { Count: > 0 }
            ? new HashSet<string>(includeExtensions.Select(e => "." + NormalizeExtension(e)).Where(s => s.Length > 1), StringComparer.OrdinalIgnoreCase)
            : null;

        int yielded = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            string canonical;
            try { canonical = Path.GetFullPath(current); }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _skippedDirectories);
                LogService.Instance.Verbose("FileLister", $"Cannot resolve path: {current}", ex);
                continue;
            }
            if (!visited.Add(canonical)) continue;

            IEnumerator<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(canonical).GetEnumerator();
            }
            catch (UnauthorizedAccessException ex)
            {
                Interlocked.Increment(ref _skippedDirectories);
                Interlocked.Increment(ref _accessDeniedDirectories);
                LogService.Instance.Verbose("FileLister", $"Access denied: {canonical}", ex);
                continue;
            }
            catch (DirectoryNotFoundException ex)
            {
                Interlocked.Increment(ref _skippedDirectories);
                LogService.Instance.Verbose("FileLister", $"Dir not found: {canonical}", ex);
                continue;
            }
            catch (IOException ex)
            {
                Interlocked.Increment(ref _skippedDirectories);
                LogService.Instance.Verbose("FileLister", $"IO error enumerating: {canonical}", ex);
                continue;
            }

            using (entries)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string entry;
                    try
                    {
                        if (!entries.MoveNext()) break;
                        entry = entries.Current;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Interlocked.Increment(ref _skippedDirectories);
                        Interlocked.Increment(ref _accessDeniedDirectories);
                        LogService.Instance.Verbose("FileLister", $"Access denied while enumerating: {canonical}", ex);
                        break;
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        Interlocked.Increment(ref _skippedDirectories);
                        LogService.Instance.Verbose("FileLister", $"Dir not found while enumerating: {canonical}", ex);
                        break;
                    }
                    catch (IOException ex)
                    {
                        Interlocked.Increment(ref _skippedDirectories);
                        LogService.Instance.Verbose("FileLister", $"IO error while enumerating: {canonical}", ex);
                        break;
                    }

                    FileAttributes attrs;
                    try { attrs = File.GetAttributes(entry); }
                    catch (Exception ex) { LogService.Instance.Verbose("FileLister", $"Cannot get attrs: {entry}", ex); continue; }

                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        // Skip reparse points we've already followed.
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                        {
                            try
                            {
                                var resolved = Path.GetFullPath(entry);
                                if (visited.Contains(resolved)) continue;
                            }
                            catch (Exception ex) { LogService.Instance.Verbose("FileLister", $"Cannot resolve reparse: {entry}", ex); continue; }
                        }
                        stack.Push(entry);
                    }
                    else
                    {
                        if (extSet is not null)
                        {
                            var ext = Path.GetExtension(entry);
                            if (ext.Length == 0 || !extSet.Contains(ext)) continue;
                        }
                        yield return entry;
                        yielded++;
                        if (maxFiles > 0 && yielded >= maxFiles) yield break;
                    }
                }
            }
            await Task.Yield();
        }
    }

    // ---- Process abstraction so tests can substitute es.exe ----
    public interface IProcess
    {
        void Start();
        Task<string?> ReadLineAsync(CancellationToken cancellationToken);
        Task WaitForExitAsync(CancellationToken cancellationToken);
        int ExitCode { get; }
    }

    private sealed class RealProcess : IProcess
    {
        private readonly Process _p;
        public RealProcess(ProcessStartInfo psi)
        {
            _p = new Process { StartInfo = psi };
        }
        public int ExitCode => _p.ExitCode;
        public void Start() => _p.Start();
        public Task<string?> ReadLineAsync(CancellationToken ct) => _p.StandardOutput.ReadLineAsync(ct).AsTask();
        public Task WaitForExitAsync(CancellationToken ct) => _p.WaitForExitAsync(ct);
    }
}
