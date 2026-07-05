using System.Diagnostics;
using Yagu.Native;
using System.Text;

namespace Yagu.Services;

internal enum SessionFileDiscoveryBackend
{
    None,
    EverythingSdk,
    EsExe,
}

internal sealed record SessionFileCandidate(string Path, long? SizeBytes, DateTimeOffset? ModifiedUtc, DateTimeOffset? CreatedUtc);

internal sealed record SessionFileDiscoveryResult(
    bool FastSearchAvailable,
    SessionFileDiscoveryBackend Backend,
    IReadOnlyList<SessionFileCandidate> Files,
    string? Error)
{
    public static SessionFileDiscoveryResult Available(
        SessionFileDiscoveryBackend backend,
        IReadOnlyList<SessionFileCandidate> files,
        string? error = null) =>
        new(true, backend, files, error);

    public static SessionFileDiscoveryResult Unavailable(string? error) =>
        new(false, SessionFileDiscoveryBackend.None, Array.Empty<SessionFileCandidate>(), error);
}

internal sealed class SessionFileDiscoveryService
{
    private const uint SdkPageSize = 5_000;
    private const string EverythingQuery = "file: ext:yagu-session";

    private readonly IEverythingSdkOps _sdkOps;
    private readonly Func<string?> _findEsExe;
    private readonly Func<string, ProcessStartInfo, FileLister.IProcess> _processFactory;

    public SessionFileDiscoveryService()
        : this(
            RealEverythingSdkOps.Instance,
            FileLister.FindEsExe,
            CreateRealProcess)
    {
    }

    internal SessionFileDiscoveryService(
        IEverythingSdkOps? sdkOps,
        Func<string?>? findEsExe,
        Func<string, ProcessStartInfo, FileLister.IProcess>? processFactory)
    {
        _sdkOps = sdkOps ?? RealEverythingSdkOps.Instance;
        _findEsExe = findEsExe ?? FileLister.FindEsExe;
        _processFactory = processFactory ?? CreateRealProcess;
    }

    private static FileLister.IProcess CreateRealProcess(string _, ProcessStartInfo psi)
        => new FileLister.RealProcess(psi);

    public async Task<SessionFileDiscoveryResult> FindSessionFilesAsync(CancellationToken cancellationToken = default)
    {
        var sdkResult = await TryFindWithSdkAsync(cancellationToken).ConfigureAwait(false);
        if (sdkResult.FastSearchAvailable)
            return sdkResult;

        string? esPath = _findEsExe();
        if (string.IsNullOrWhiteSpace(esPath))
            return SessionFileDiscoveryResult.Unavailable(sdkResult.Error ?? "Everything SDK and es.exe are not available.");

        var esResult = await TryFindWithEsAsync(esPath, cancellationToken).ConfigureAwait(false);
        if (esResult.FastSearchAvailable)
            return esResult;

        return SessionFileDiscoveryResult.Unavailable(esResult.Error ?? sdkResult.Error ?? "es.exe is not usable.");
    }

    private Task<SessionFileDiscoveryResult> TryFindWithSdkAsync(CancellationToken cancellationToken)
    {
        if (!FileLister.SdkAvailable)
            return Task.FromResult(SessionFileDiscoveryResult.Unavailable("Everything SDK is not available."));

        return Task.Run(() => TryFindWithSdkCore(cancellationToken), cancellationToken);
    }

    private SessionFileDiscoveryResult TryFindWithSdkCore(CancellationToken cancellationToken)
    {
        var candidates = new List<SessionFileCandidate>();
        string? error = null;

        lock (_sdkOps.SyncLock)
        {
            try
            {
                if (!_sdkOps.IsDBLoaded())
                    return SessionFileDiscoveryResult.Unavailable("Everything database is not loaded.");

                _sdkOps.Reset();
                _sdkOps.SetSearch(EverythingQuery);
                _sdkOps.SetMatchCase(false);
                _sdkOps.SetMatchPath(false);
                _sdkOps.SetRequestFlags(
                    EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME |
                    EverythingSdk.EVERYTHING_REQUEST_SIZE |
                    EverythingSdk.EVERYTHING_REQUEST_DATE_MODIFIED);

                uint offset = 0;
                uint totalMatches = 0;
                var buffer = new char[1024];

                while (!cancellationToken.IsCancellationRequested)
                {
                    _sdkOps.SetOffset(offset);
                    _sdkOps.SetMax(SdkPageSize);

                    if (!_sdkOps.Query(bWait: true))
                    {
                        uint err = _sdkOps.GetLastError();
                        error = err == EverythingSdk.EVERYTHING_ERROR_IPC
                            ? "Everything is not running."
                            : $"Everything SDK query failed: {_sdkOps.ErrorMessage(err)}";
                        break;
                    }

                    uint count = _sdkOps.GetNumResults();
                    if (offset == 0)
                        totalMatches = _sdkOps.GetTotResults();

                    if (count == 0)
                        break;

                    for (uint i = 0; i < count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = ReadSdkFullPath(_sdkOps, i, ref buffer);
                        if (!IsSessionFilePath(path))
                            continue;

                        long? sizeBytes = _sdkOps.GetResultSize(i, out long size) && size >= 0 ? size : null;
                        DateTimeOffset? modifiedUtc = _sdkOps.GetResultDateModified(i, out long modifiedFileTime) && modifiedFileTime > 0
                            ? new DateTimeOffset(DateTime.FromFileTimeUtc(modifiedFileTime))
                            : null;
                        DateTimeOffset? createdUtc = GetCreatedUtc(path);

                        candidates.Add(new SessionFileCandidate(path, sizeBytes, modifiedUtc, createdUtc));
                    }

                    offset += count;
                    if (count < SdkPageSize || offset >= totalMatches)
                        break;
                }
            }
            catch (DllNotFoundException)
            {
                FileLister.SdkAvailable = false;
                error = "Everything64.dll not found.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"Everything SDK query failed: {ex.Message}";
            }
            finally
            {
                try { _sdkOps.Reset(); } catch { }
            }
        }

        return error is null
            ? SessionFileDiscoveryResult.Available(SessionFileDiscoveryBackend.EverythingSdk, NormalizeCandidates(candidates))
            : SessionFileDiscoveryResult.Unavailable(error);
    }

    private async Task<SessionFileDiscoveryResult> TryFindWithEsAsync(string esPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = esPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("/a-d");
        psi.ArgumentList.Add("ext:yagu-session");

        FileLister.IProcess process = _processFactory(esPath, psi);
        using var processDisposer = process as IDisposable;
        var candidates = new List<SessionFileCandidate>();

        try
        {
            process.Start();

            string? line;
            while ((line = await process.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSessionFilePath(line))
                    continue;

                candidates.Add(CreateCandidateFromPath(line));
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SessionFileDiscoveryResult.Unavailable($"es.exe could not search for sessions: {ex.Message}");
        }

        if (process.ExitCode == 0)
            return SessionFileDiscoveryResult.Available(SessionFileDiscoveryBackend.EsExe, NormalizeCandidates(candidates));

        if (process.ExitCode == 8)
            return SessionFileDiscoveryResult.Unavailable("Everything is not running.");

        return candidates.Count > 0
            ? SessionFileDiscoveryResult.Available(SessionFileDiscoveryBackend.EsExe, NormalizeCandidates(candidates), $"es.exe exited with code {process.ExitCode}.")
            : SessionFileDiscoveryResult.Unavailable($"es.exe exited with code {process.ExitCode}.");
    }

    private static SessionFileCandidate CreateCandidateFromPath(string path)
    {
        long? sizeBytes = null;
        DateTimeOffset? modifiedUtc = null;
        DateTimeOffset? createdUtc = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                sizeBytes = info.Length;
                modifiedUtc = new DateTimeOffset(info.LastWriteTimeUtc);
                createdUtc = new DateTimeOffset(info.CreationTimeUtc);
            }
        }
        catch
        {
        }

        return new SessionFileCandidate(path, sizeBytes, modifiedUtc, createdUtc);
    }

    private static DateTimeOffset? GetCreatedUtc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
                return new DateTimeOffset(info.CreationTimeUtc);
        }
        catch { }
        return null;
    }

    private static SessionFileCandidate[] NormalizeCandidates(IEnumerable<SessionFileCandidate> candidates)
    {
        var byPath = new Dictionary<string, SessionFileCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!IsSessionFilePath(candidate.Path))
                continue;

            if (!byPath.TryGetValue(candidate.Path, out var existing)
                || (existing.ModifiedUtc is null && candidate.ModifiedUtc is not null)
                || (existing.SizeBytes is null && candidate.SizeBytes is not null))
            {
                byPath[candidate.Path] = candidate;
            }
        }

        return byPath.Values
            .OrderByDescending(candidate => candidate.ModifiedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => Path.GetFileName(candidate.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSessionFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return string.Equals(
            Path.GetExtension(path.Trim()),
            SessionFileService.FileExtension,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSdkFullPath(IEverythingSdkOps sdk, uint index, ref char[] buffer)
    {
        Array.Clear(buffer);
        uint length = sdk.GetResultFullPathName(index, buffer, (uint)buffer.Length);
        if (length == 0)
            return string.Empty;

        if (length >= buffer.Length)
        {
            buffer = new char[(int)length + 1];
            Array.Clear(buffer);
            length = sdk.GetResultFullPathName(index, buffer, (uint)buffer.Length);
            if (length == 0)
                return string.Empty;
        }

        int charCount = (int)Math.Min(length, (uint)buffer.Length);
        if (charCount > 0 && buffer[charCount - 1] == '\0')
            charCount--;

        return new string(buffer, 0, charCount);
    }
}
