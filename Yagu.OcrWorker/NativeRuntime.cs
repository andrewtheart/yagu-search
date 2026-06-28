using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Yagu.OcrWorker;

/// <summary>
/// Lazily downloads and stages the native OCR runtime (PaddlePaddle inference + OpenCvSharp native
/// binding) into a local folder, then makes the OS DLL loader search that folder.
/// <para>
/// The native binaries are pulled directly from the public NuGet flat-container feed (a .nupkg is a
/// zip), and only the <c>runtimes/win-x64/native/</c> payload is extracted. Nothing here touches the
/// main Yagu process; it all happens inside this isolated, non-AOT worker.
/// </para>
/// </summary>
internal static class NativeRuntime
{
    // PaddlePaddle native inference runtime (MKL build). The 3.0.x line pairs with PaddleSharp 3.0.1.
    private const string PaddleRuntimeId = "sdcb.paddleinference.runtime.win64.mkl";
    private const string PaddleRuntimePreferredVersion = "3.0.0.51";
    private const string PaddleProbeFile = "paddle_inference_c.dll";

    // OpenCvSharp native binding. MUST match the managed OpenCvSharp4 version referenced by PaddleOCR.
    private const string OpenCvRuntimeId = "opencvsharp4.runtime.win";
    private const string OpenCvRuntimeVersion = "4.11.0.20250507";
    private const string OpenCvProbeFile = "OpenCvSharpExtern.dll";

    private const string NativeFolderInNupkg = "runtimes/win-x64/native/";

    /// <summary>
    /// Ensures the native runtime DLLs are present in <paramref name="runtimeDir"/>, downloading and
    /// extracting them on first use. Safe to call repeatedly (skips work when already staged).
    /// </summary>
    public static async Task EnsureAsync(string runtimeDir, Action<string> log, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(runtimeDir);

        using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };

        if (!File.Exists(Path.Combine(runtimeDir, OpenCvProbeFile)))
        {
            await DownloadAndExtractNativeAsync(http, OpenCvRuntimeId, OpenCvRuntimeVersion, runtimeDir, log, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log($"opencv native already staged ({OpenCvProbeFile})");
        }

        if (!File.Exists(Path.Combine(runtimeDir, PaddleProbeFile)))
        {
            string version = await ResolvePaddleRuntimeVersionAsync(http, log, cancellationToken).ConfigureAwait(false);
            await DownloadAndExtractNativeAsync(http, PaddleRuntimeId, version, runtimeDir, log, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log($"paddle native already staged ({PaddleProbeFile})");
        }
    }

    /// <summary>
    /// Makes the Win32 loader search <paramref name="runtimeDir"/> for native DLLs (and their
    /// inter-dependencies). Must be called before any PaddleSharp/OpenCvSharp P/Invoke.
    /// </summary>
    public static void AddNativeSearchDirectory(string runtimeDir)
    {
        // Prepend to PATH so dependent DLLs (mkldnn, mklml, etc.) resolve from the same folder, then
        // point the loader's per-process directory at runtimeDir. We deliberately do NOT call
        // SetDefaultDllDirectories (which would disable PATH-based dependency resolution).
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        bool alreadyOnPath = path
            .Split(Path.PathSeparator)
            .Any(p => string.Equals(p.TrimEnd('\\'), runtimeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (!alreadyOnPath)
        {
            Environment.SetEnvironmentVariable("PATH", runtimeDir + Path.PathSeparator + path);
        }

        SetDllDirectory(runtimeDir);
    }

    private static async Task<string> ResolvePaddleRuntimeVersionAsync(HttpClient http, Action<string> log, CancellationToken cancellationToken)
    {
        try
        {
            string url = $"https://api.nuget.org/v3-flatcontainer/{PaddleRuntimeId}/index.json";
            string json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            List<string> versions = ParseVersions(json);

            if (versions.Contains(PaddleRuntimePreferredVersion, StringComparer.OrdinalIgnoreCase))
            {
                return PaddleRuntimePreferredVersion;
            }

            // Otherwise pick the highest stable 3.0.0.x (Paddle 3.0.0 build line that PaddleSharp 3.0.1 targets).
            string? best = null;
            Version? bestVersion = null;
            foreach (string v in versions)
            {
                if (v.Contains('-', StringComparison.Ordinal))
                {
                    continue; // skip prerelease
                }

                if (Version.TryParse(v, out Version? parsed) && parsed.Major == 3 && parsed.Minor == 0 && parsed.Build == 0)
                {
                    if (bestVersion is null || parsed > bestVersion)
                    {
                        bestVersion = parsed;
                        best = v;
                    }
                }
            }

            return best ?? PaddleRuntimePreferredVersion;
        }
        catch (Exception ex)
        {
            log($"paddle runtime version resolve failed, falling back to {PaddleRuntimePreferredVersion}: {ex.Message}");
            return PaddleRuntimePreferredVersion;
        }
    }

    private static List<string> ParseVersions(string indexJson)
    {
        List<string> result = new();
        using JsonDocument doc = JsonDocument.Parse(indexJson);
        if (doc.RootElement.TryGetProperty("versions", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in arr.EnumerateArray())
            {
                string? value = element.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }

    private static async Task DownloadAndExtractNativeAsync(HttpClient http, string id, string version, string runtimeDir, Action<string> log, CancellationToken cancellationToken)
    {
        string idLower = id.ToLowerInvariant();
        string versionLower = version.ToLowerInvariant();
        string url = $"https://api.nuget.org/v3-flatcontainer/{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg";
        string tempPath = Path.Combine(Path.GetTempPath(), $"{idLower}.{versionLower}.{Guid.NewGuid():N}.nupkg");

        log($"downloading {id} {version} ...");
        try
        {
            using (Stream networkStream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
            using (FileStream fileStream = File.Create(tempPath))
            {
                await networkStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            int extracted = 0;
            using (ZipArchive archive = ZipFile.OpenRead(tempPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string normalized = entry.FullName.Replace('\\', '/');
                    if (normalized.IndexOf(NativeFolderInNupkg, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (normalized.EndsWith('/'))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(normalized);
                    if (fileName.Length == 0)
                    {
                        continue;
                    }

                    string destination = Path.Combine(runtimeDir, fileName);
                    entry.ExtractToFile(destination, overwrite: true);
                    extracted++;
                }
            }

            log($"extracted {extracted} native file(s) from {id} {version}");
            if (extracted == 0)
            {
                throw new InvalidOperationException($"No '{NativeFolderInNupkg}' payload found in {id} {version}.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);
}
