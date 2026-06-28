using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Yagu.Services.Ocr;

/// <summary>
/// File-backed cache of OCR-recognized text — one small text file per source image, keyed by the
/// image path plus its size, last-write time, and the OCR engine id. This lets repeated searches
/// of the same folder reuse OCR output instead of re-running the (slow) recognizer, and gives the
/// preview drawer a stable place to read an image's recognized text from.
/// </summary>
public sealed class OcrTextCache
{
    private const string HeaderMagic = "YAGUOCR1";
    private readonly string _baseDir;

    // The owning process id is embedded in every cache file name so that a run which exited in the
    // middle of an image search leaves identifiable leftovers (purged by Cleanup on the next launch /
    // before the next search) and so a process only ever reads back the OCR text it wrote itself — a
    // secondary sanity check on top of the size/mtime/engine header validation.
    private static readonly string PidToken =
        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string PidFileSuffix = ".p" + PidToken + ".txt";

    public OcrTextCache(string? baseDirectory = null)
    {
        _baseDir = baseDirectory ?? DefaultBaseDirectory();
    }

    /// <summary>Default cache directory: <c>%LOCALAPPDATA%\Yagu\ocr-cache</c>.</summary>
    public static string DefaultBaseDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Yagu", "ocr-cache");

    /// <summary>Absolute path of the cache file that would hold the OCR text for the image.</summary>
    public string GetCacheFilePath(string imagePath, string engineId)
    {
        string key = (engineId ?? string.Empty) + "|" + (imagePath ?? string.Empty).ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string name = Convert.ToHexString(hash, 0, 16); // 32 hex chars — collision-resistant enough for a cache
        return Path.Combine(_baseDir, name + PidFileSuffix);
    }

    /// <summary>
    /// Returns the cached OCR text for <paramref name="imagePath"/> if a fresh entry exists for the
    /// given engine. A cache entry is stale (ignored) once the image's size or last-write time change.
    /// </summary>
    public bool TryGet(string imagePath, string engineId, out string text)
    {
        text = string.Empty;
        FileInfo info;
        try
        {
            info = new FileInfo(imagePath);
            if (!info.Exists) return false;
        }
        catch
        {
            return false;
        }

        string cacheFile = GetCacheFilePath(imagePath, engineId);
        try
        {
            if (!File.Exists(cacheFile)) return false;
            using var reader = new StreamReader(cacheFile, Encoding.UTF8);
            string? header = reader.ReadLine();
            if (header is null) return false;
            string expected = BuildHeader(info.Length, info.LastWriteTimeUtc.Ticks, engineId);
            if (!string.Equals(header, expected, StringComparison.Ordinal)) return false;
            text = reader.ReadToEnd();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes (or refreshes) the cached OCR text for the image. Best-effort: failures are swallowed.</summary>
    public void Set(string imagePath, string engineId, string text)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(imagePath);
            if (!info.Exists) return;
        }
        catch
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_baseDir);
            string cacheFile = GetCacheFilePath(imagePath, engineId);
            string header = BuildHeader(info.Length, info.LastWriteTimeUtc.Ticks, engineId);
            string tmp = cacheFile + ".tmp" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(tmp, header + "\n" + (text ?? string.Empty), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, cacheFile, overwrite: true);
        }
        catch
        {
            // Cache writes are advisory; a failure just means the next search re-OCRs the image.
        }
    }

    /// <summary>
    /// Deletes OCR text files left behind by Yagu processes that are no longer running (for example a
    /// run that exited in the middle of an image search). Every cache file is tagged with the owning
    /// process id (see <see cref="GetCacheFilePath"/>); any file whose process id is not a live Yagu
    /// instance — including legacy untagged files — is removed. Best-effort: failures are logged at
    /// Verbose and otherwise ignored. Call on launch and before each image search so stale OCR text
    /// never accumulates or gets mistaken for the current run's output.
    /// </summary>
    public static void Cleanup(string? baseDirectory = null)
    {
        string dir = baseDirectory ?? DefaultBaseDirectory();

        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return;
            files = Directory.GetFiles(dir);
        }
        catch
        {
            return;
        }
        if (files.Length == 0) return;

        HashSet<int> liveProcessIds = GetLiveYaguProcessIds();

        int deleted = 0;
        foreach (string file in files)
        {
            int pid = ExtractProcessId(Path.GetFileName(file));
            if (pid >= 0 && liveProcessIds.Contains(pid))
                continue; // Belongs to a live Yagu instance (including this one) — keep it.

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                LogService.Instance.Verbose("OcrCache", $"Could not delete stale OCR text file '{file}': {ex.Message}");
            }
        }

        if (deleted > 0)
            LogService.Instance.Info("OcrCache", $"Cleaned up {deleted} stale OCR text file(s) from '{dir}'.");
    }

    private static HashSet<int> GetLiveYaguProcessIds()
        => GetLiveYaguProcessIds("Yagu");

    // Split out (with the process name as a parameter) so the enumeration loop can be exercised by
    // tests against a process name that is actually running, without depending on a live "Yagu".
    internal static HashSet<int> GetLiveYaguProcessIds(string processName)
    {
        var ids = new HashSet<int> { Environment.ProcessId };
        try
        {
            foreach (Process proc in Process.GetProcessesByName(processName))
            {
                try { ids.Add(proc.Id); }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch
        {
            // Process enumeration can fail under tight permissions; the current process id alone is a safe floor.
        }
        return ids;
    }

    /// <summary>
    /// Parses the owning process id out of a cache file name shaped like <c>&lt;hash&gt;.p&lt;pid&gt;.txt</c>
    /// (optionally followed by a <c>.tmp…</c> suffix while writing). Returns -1 for an untagged name.
    /// </summary>
    internal static int ExtractProcessId(string fileName)
    {
        int marker = fileName.IndexOf(".p", StringComparison.Ordinal);
        if (marker < 0) return -1;
        int start = marker + 2;
        int end = start;
        while (end < fileName.Length && char.IsAsciiDigit(fileName[end]))
            end++;
        if (end == start) return -1;
        return int.TryParse(fileName.AsSpan(start, end - start), out int pid) ? pid : -1;
    }

    private static string BuildHeader(long length, long mtimeTicks, string? engineId)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{HeaderMagic}|{length}|{mtimeTicks}|{engineId}");
}
