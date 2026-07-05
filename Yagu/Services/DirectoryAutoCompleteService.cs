using System.Diagnostics;
using Yagu.Native;
using System.Text;

namespace Yagu.Services;

/// <summary>
/// Provides directory path auto-complete suggestions using Everything SDK/es.exe when available,
/// falling back to .NET directory enumeration.
/// </summary>
internal sealed class DirectoryAutoCompleteService
{
    private readonly string? _esExePath;
    private readonly Func<string, string, Process>? _processFactory;
    private readonly IEverythingSdkOps? _sdkOps;

    public DirectoryAutoCompleteService() : this(FileLister.FindEsExe(), null, RealEverythingSdkOps.Instance) { }

    internal DirectoryAutoCompleteService(
        string? esExePath,
        Func<string, string, Process>? processFactory = null,
        IEverythingSdkOps? sdkOps = null)
    {
        _esExePath = esExePath;
        _processFactory = processFactory;
        _sdkOps = sdkOps;
    }

    public bool IsEverythingAvailable => _sdkOps != null || _esExePath != null;

    /// <summary>
    /// Returns subdirectory suggestions for the given partial path.
    /// </summary>
    public async Task<List<string>> GetSuggestionsAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Determine the parent directory to search under.
        // If text ends with '\' or '/', list immediate subdirectories of that directory.
        // Otherwise, treat the last segment as a prefix filter.
        string parentDir;
        string prefix;

        if (text.EndsWith('\\') || text.EndsWith('/'))
        {
            parentDir = text;
            prefix = string.Empty;
        }
        else
        {
            parentDir = Path.GetDirectoryName(text) ?? string.Empty;
            prefix = Path.GetFileName(text);
        }

        if (string.IsNullOrEmpty(parentDir))
            return [];

        // Normalize trailing separator
        if (!parentDir.EndsWith('\\') && !parentDir.EndsWith('/'))
            parentDir += '\\';

        return await GetSuggestionsForParentAsync(parentDir, prefix, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns immediate child directories for an already selected directory.
    /// </summary>
    public Task<List<string>> GetChildDirectorySuggestionsAsync(string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return Task.FromResult(new List<string>());

        string parentDir = directory;
        if (!parentDir.EndsWith('\\') && !parentDir.EndsWith('/'))
            parentDir += '\\';

        return GetSuggestionsForParentAsync(parentDir, string.Empty, ct);
    }

    private async Task<List<string>> GetSuggestionsForParentAsync(string parentDir, string prefix, CancellationToken ct)
    {
        if (_sdkOps != null && FileLister.SdkAvailable)
        {
            var sdkResults = await QueryEverythingSdkAsync(parentDir, prefix, ct).ConfigureAwait(false);
            if (sdkResults.Count > 0)
                return sdkResults;
        }

        if (_esExePath != null)
        {
            var results = await QueryEverythingAsync(parentDir, prefix, ct).ConfigureAwait(false);
            if (results.Count > 0)
                return results;
        }

        return await Task.Run(() => EnumerateDirectories(parentDir, prefix), ct).ConfigureAwait(false);
    }

    internal Task<List<string>> QueryEverythingSdkAsync(string parentDir, string prefix, CancellationToken ct)
    {
        if (_sdkOps == null || !FileLister.SdkAvailable)
            return Task.FromResult(new List<string>());

        return Task.Run(() => QueryEverythingSdkCore(parentDir, prefix, ct), ct);
    }

    private List<string> QueryEverythingSdkCore(string parentDir, string prefix, CancellationToken ct)
    {
        var results = new List<string>();

        try
        {
            lock (_sdkOps!.SyncLock)
            {
                ct.ThrowIfCancellationRequested();
                if (!_sdkOps.IsDBLoaded())
                    return results;

                _sdkOps.Reset();
                _sdkOps.SetSearch(BuildEverythingDirectoryQuery(parentDir, prefix));
                _sdkOps.SetMatchCase(false);
                _sdkOps.SetMatchPath(false);
                _sdkOps.SetOffset(0);
                _sdkOps.SetMax(30);
                _sdkOps.SetRequestFlags(EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME);

                if (!_sdkOps.Query(bWait: true))
                    return results;

                uint count = _sdkOps.GetNumResults();
                var buffer = new char[1024];
                for (uint index = 0; index < count && results.Count < 30; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = ReadSdkFullPath(_sdkOps, index, ref buffer);
                    if (!string.IsNullOrWhiteSpace(path))
                        results.Add(path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException)
        {
            FileLister.SdkAvailable = false;
        }
        catch
        {
            // Everything SDK is unavailable or failed; caller will fall back to es.exe/.NET.
        }

        return results;
    }

    internal async Task<List<string>> QueryEverythingAsync(string parentDir, string prefix, CancellationToken ct)
    {
        // es.exe query: search for folders directly under parentDir. The query embeds the user-typed
        // prefix and parent path, so the production process is built with ArgumentList (see
        // CreateDefaultProcess) which passes the query as a single, self-contained argument. That way
        // a prefix containing a double-quote cannot break out of the query string and inject extra
        // es.exe switches (argument injection), and paths containing spaces stay grouped — matching
        // the single-string query handed to the Everything SDK.
        var query = BuildEverythingDirectoryQuery(parentDir, prefix);

        var results = new List<string>();

        try
        {
            using var proc = _processFactory != null
                ? _processFactory(_esExePath!, $"-max-results 30 {query}")
                : CreateDefaultProcess(_esExePath!, query);

            proc.Start();

            using var reader = proc.StandardOutput;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    results.Add(line);
                if (results.Count >= 30)
                    break;
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // es.exe not running or failed — will fall back to .NET
        }

        return results;
    }

    private static string BuildEverythingDirectoryQuery(string parentDir, string prefix)
        => string.IsNullOrEmpty(prefix)
            ? $"parent:\"{parentDir.TrimEnd('\\', '/')}\" folder:"
            : $"parent:\"{parentDir.TrimEnd('\\', '/')}\" folder: \"{prefix}\"";

    private static string ReadSdkFullPath(IEverythingSdkOps sdk, uint index, ref char[] buffer)
    {
        Array.Clear(buffer);
        uint length = sdk.GetResultFullPathName(index, buffer, (uint)buffer.Length);
        if (length == 0) return string.Empty;

        if (length >= buffer.Length)
        {
            buffer = new char[(int)length + 1];
            Array.Clear(buffer);
            length = sdk.GetResultFullPathName(index, buffer, (uint)buffer.Length);
            if (length == 0) return string.Empty;
        }

        int charCount = (int)Math.Min(length, (uint)buffer.Length);
        if (charCount > 0 && buffer[charCount - 1] == '\0') charCount--;
        return new string(buffer, 0, charCount);
    }

    private static Process CreateDefaultProcess(string esExePath, string query)
    {
        var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = esExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        // Each argument is added individually so the runtime escapes it: the caller-influenced query
        // is passed as ONE argument and can never be split into additional es.exe switches.
        proc.StartInfo.ArgumentList.Add("-max-results");
        proc.StartInfo.ArgumentList.Add("30");
        proc.StartInfo.ArgumentList.Add(query);
        return proc;
    }

    internal static List<string> EnumerateDirectories(string parentDir, string prefix)
    {
        var results = new List<string>();
        try
        {
            if (!Directory.Exists(parentDir))
                return results;

            var pattern = string.IsNullOrEmpty(prefix) ? "*" : $"{prefix}*";
            foreach (var dir in Directory.EnumerateDirectories(parentDir, pattern))
            {
                results.Add(dir);
                if (results.Count >= 30)
                    break;
            }
        }
        catch
        {
            // Access denied or invalid path — return empty
        }
        return results;
    }
}
