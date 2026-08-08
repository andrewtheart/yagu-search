using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yagu.Services;

public sealed record AppReleaseAsset(string Name, Uri DownloadUri, long Size, string Sha256);

public sealed record AppReleaseInfo(
    Version Version,
    string Tag,
    string Name,
    string ReleaseNotes,
    Uri ReleasePage,
    DateTimeOffset? PublishedUtc,
    AppReleaseAsset Installer);

public sealed record AppUpdateCheckResult(Version CurrentVersion, Version LatestVersion, AppReleaseInfo? Release)
{
    public bool UpdateAvailable => LatestVersion > CurrentVersion;
}

/// <summary>How Yagu checks the official GitHub Releases page for a newer version.</summary>
public enum AppUpdateCheckMode
{
    /// <summary>One-time consent not given yet; ask once on next launch (fresh-install default).</summary>
    Prompt = 0,
    /// <summary>Check automatically in the background about once a week; notify only when a newer version exists.</summary>
    Automatic = 1,
    /// <summary>Never check on its own; only when the user explicitly asks (a Settings button).</summary>
    Manual = 2,
    /// <summary>Never check for updates.</summary>
    Off = 3,
    /// <summary>Check automatically in the background about once a day; notify only when a newer version
    /// exists. The recommended choice offered by the one-time consent prompt.</summary>
    AutomaticDaily = 4,
}

/// <summary>Queries and validates Yagu's public GitHub latest-release metadata.</summary>
public static class AppUpdateChecker
{
    public const string Repository = "andrewtheart/yagu-search";
    /// <summary>Default minimum interval between automatic background checks.</summary>
    public static readonly TimeSpan DefaultAutoCheckInterval = TimeSpan.FromDays(7);
    /// <summary>Minimum interval between automatic background checks in once-per-day mode.</summary>
    public static readonly TimeSpan DailyAutoCheckInterval = TimeSpan.FromDays(1);

    /// <summary>The background-check throttle for <paramref name="mode"/>, or null when the mode never
    /// checks on its own.</summary>
    public static TimeSpan? GetAutoCheckInterval(AppUpdateCheckMode mode) => mode switch
    {
        AppUpdateCheckMode.AutomaticDaily => DailyAutoCheckInterval,
        AppUpdateCheckMode.Automatic => DefaultAutoCheckInterval,
        _ => null,
    };

    /// <summary>Whether enough time has elapsed since the last check to auto-check again. A null last-check
    /// (never checked) always returns true.</summary>
    public static bool ShouldAutoCheck(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc, TimeSpan interval)
        => lastCheckUtc is not { } last || nowUtc - last >= interval;
    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri LatestReleasePage = new($"https://github.com/{Repository}/releases/latest");
    public const int MaxReleaseMetadataBytes = 512 * 1024;
    public const int MaxReleaseNotesChars = 64 * 1024;

    public static bool TryParseVersion(string? value, out Version version)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (Version.TryParse(text, out Version? parsed)
            && parsed.Major >= 0 && parsed.Minor >= 0 && parsed.Build >= 0 && parsed.Revision >= 0)
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0, 0, 0);
        return false;
    }

    public static AppReleaseAsset? SelectInstallerAsset(
        IEnumerable<(string Name, string Url, long Size, string? Digest)> assets,
        Version releaseVersion,
        Architecture architecture)
    {
        string arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => string.Empty,
        };
        if (arch.Length == 0) return null;

        string expected = $"YaguSetup-{releaseVersion}-{arch}.exe";
        var matches = assets.Where(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Size <= 0
            || !Uri.TryCreate(matches[0].Url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        string digest = matches[0].Digest ?? string.Empty;
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = digest[7..];
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) return null;
        return new AppReleaseAsset(matches[0].Name, uri, matches[0].Size, digest.ToUpperInvariant());
    }

    public static async Task<AppUpdateCheckResult?> CheckLatestAsync(
        string currentVersion,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseVersion(currentVersion, out Version current)) return null;
        bool ownsClient = httpClient is null;
        HttpClient client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        AppUpdateCheckResult result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Yagu", current.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] json = await ReadBoundedAsync(stream, MaxReleaseMetadataBytes, cancellationToken).ConfigureAwait(false);
            GitHubReleaseDto? dto = JsonSerializer.Deserialize(json, AppUpdateJsonContext.Default.GitHubReleaseDto);
            if (dto is null || dto.Draft || dto.Prerelease || !TryParseVersion(dto.TagName, out Version latest))
                return new AppUpdateCheckResult(current, current, null);

            AppReleaseAsset? installer = SelectInstallerAsset(
                (dto.Assets ?? []).Select(a => (a.Name ?? string.Empty, a.BrowserDownloadUrl ?? string.Empty, a.Size, a.Digest)),
                latest,
                RuntimeInformation.ProcessArchitecture);
            if (installer is null) return new AppUpdateCheckResult(current, latest, null);

            Uri releasePage = Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out Uri? page)
                && page.Scheme == Uri.UriSchemeHttps
                && string.Equals(page.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                    ? page : LatestReleasePage;
            string notes = dto.Body ?? string.Empty;
            if (notes.Length > MaxReleaseNotesChars) notes = notes[..MaxReleaseNotesChars] + "\n\n[Release notes truncated by Yagu.]";
            var release = new AppReleaseInfo(
                latest,
                dto.TagName!,
                string.IsNullOrWhiteSpace(dto.Name) ? $"Yagu {latest}" : dto.Name,
                notes,
                releasePage,
                dto.PublishedAt,
                installer);
            result = new AppUpdateCheckResult(current, latest, latest > current ? release : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidDataException)
        {
            return null;
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
        return result;
    }

    public static async Task<bool> VerifyDownloadedAssetAsync(
        string path,
        AppReleaseAsset asset,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != asset.Size) return false;
            byte[] hash;
            await using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            return string.Equals(Convert.ToHexString(hash), asset.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Release metadata is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public GitHubReleaseAssetDto[]? Assets { get; set; }
}

internal sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class AppUpdateJsonContext : JsonSerializerContext;
