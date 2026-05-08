using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Yagu.Services;

/// <summary>
/// Provides directory path auto-complete suggestions using Everything's es.exe when available,
/// falling back to .NET directory enumeration.
/// </summary>
internal sealed class DirectoryAutoCompleteService
{
    private readonly string? _esExePath;
    private readonly Func<string, string, Process>? _processFactory;

    public DirectoryAutoCompleteService() : this(FileLister.FindEsExe(), null) { }

    internal DirectoryAutoCompleteService(string? esExePath, Func<string, string, Process>? processFactory = null)
    {
        _esExePath = esExePath;
        _processFactory = processFactory;
    }

    public bool IsEverythingAvailable => _esExePath != null;

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

        if (_esExePath != null)
        {
            var results = await QueryEverythingAsync(parentDir, prefix, ct).ConfigureAwait(false);
            if (results.Count > 0)
                return results;
        }

        // Fallback: .NET enumeration
        return await Task.Run(() => EnumerateDirectories(parentDir, prefix), ct).ConfigureAwait(false);
    }

    internal async Task<List<string>> QueryEverythingAsync(string parentDir, string prefix, CancellationToken ct)
    {
        // es.exe query: search for folders directly under parentDir
        var query = string.IsNullOrEmpty(prefix)
            ? $"parent:\"{parentDir.TrimEnd('\\')}\" folder:"
            : $"parent:\"{parentDir.TrimEnd('\\')}\" folder: \"{prefix}\"";

        var arguments = $"-max-results 30 {query}";
        var results = new List<string>();

        try
        {
            using var proc = _processFactory != null
                ? _processFactory(_esExePath!, arguments)
                : CreateDefaultProcess(_esExePath!, arguments);

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

    private static Process CreateDefaultProcess(string esExePath, string arguments)
    {
        var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = esExePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
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
