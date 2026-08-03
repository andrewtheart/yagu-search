using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Yagu.Services;

public sealed record MultiTermDownloadResult(bool Succeeded, string? FilePath, string? Error);

/// <summary>
/// Opens a dedicated PowerShell pane in MultiTerm and runs the visible update download there. Completion
/// is accepted only from the exact generated terminal id and a cryptographically random marker; the caller
/// independently verifies size/hash (and later Authenticode) before offering execution.
/// </summary>
public static class MultiTermUpdateDownloader
{
    private const int MaxBridgeMessageBytes = 1024 * 1024;
    internal static Func<IEnumerable<string>> InstallRootProvider { get; set; } = GetDefaultInstallRoots;
    internal static Func<IEnumerable<string>> RegistryInstallLocationProvider { get; set; } = GetRegistryInstallLocations;

    public static Task<MultiTermDownloadResult> DownloadAsync(
        AppReleaseInfo release,
        string destinationPath,
        CancellationToken cancellationToken = default)
        => DownloadAsync(release, destinationPath, dependencies: null, cancellationToken);

    internal static async Task<MultiTermDownloadResult> DownloadAsync(
        AppReleaseInfo release,
        string destinationPath,
        MultiTermUpdateDownloaderDependencies? dependencies,
        CancellationToken cancellationToken = default)
    {
        MultiTermUpdateDownloaderDependencies deps = dependencies ?? MultiTermUpdateDownloaderDependencies.Default;
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        MultiTermDownloadResult result;
        try
        {
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                return new(false, null, "The update destination path must include a directory.");
            deps.CreateDirectory(destinationDirectory);
            if (!await EnsureBridgeAsync(deps, cancellationToken).ConfigureAwait(false))
                return new(false, null, "MultiTerm Workbench is not running and its installed launcher could not be found.");

            await using IMultiTermBridgeSocket socket = deps.CreateBridgeSocket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            await socket.ConnectAsync(deps.WebSocketUri, timeout.Token).ConfigureAwait(false);

            string id = "yagu-update-" + deps.CreateToken();
            string successMarker = "__YAGU_UPDATE_OK_" + deps.CreateToken() + "__";
            string failureMarker = "__YAGU_UPDATE_FAIL_" + deps.CreateToken() + "__";
            var create = new MultiTermBridgeMessage
            {
                Type = "create", Id = id, Cwd = Path.GetDirectoryName(destinationPath),
                Title = $"Yagu update {release.Version}", Shell = "powershell", Cols = 120, Rows = 30,
            };
            await SendAsync(socket, create, timeout.Token).ConfigureAwait(false);
            await WaitForCreatedAsync(socket, id, timeout.Token).ConfigureAwait(false);

            string command = BuildDownloadCommand(release.Installer, destinationPath, successMarker, failureMarker);
            await SendAsync(socket, new MultiTermBridgeMessage { Type = "input", Id = id, Data = command + "\r" }, timeout.Token).ConfigureAwait(false);
            result = await WaitForCompletionAsync(
                socket, id, destinationPath, successMarker, failureMarker, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, "The MultiTerm update download timed out.");
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or InvalidDataException)
        {
            return new(false, null, ex.Message);
        }
        return result;
    }

    internal static string BuildDownloadCommand(
        AppReleaseAsset asset,
        string destinationPath,
        string successMarker,
        string failureMarker)
    {
        string partial = destinationPath + ".partial";
        string successMarkerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(successMarker));
        string failureMarkerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(failureMarker));
        static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "$ErrorActionPreference='Stop'; try { "
            + $"$u={Q(asset.DownloadUri.AbsoluteUri)}; $p={Q(destinationPath)}; $partial={Q(partial)}; "
            + $"$ok=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String({Q(successMarkerBase64)})); "
            + $"$fail=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String({Q(failureMarkerBase64)})); "
            + "Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue; "
            + "& curl.exe --location --fail --progress-bar --output $partial $u; "
            + "if ($LASTEXITCODE -ne 0) { throw \"curl failed with exit code $LASTEXITCODE\" }; "
            + $"if ((Get-Item -LiteralPath $partial).Length -ne {asset.Size}) {{ throw 'Downloaded size mismatch' }}; "
            + $"if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne {Q(asset.Sha256)}) {{ throw 'Downloaded SHA-256 mismatch' }}; "
            + "Move-Item -LiteralPath $partial -Destination $p -Force; "
            + "Write-Host $ok "
            + $"}} catch {{ Remove-Item -LiteralPath {Q(partial)} -Force -ErrorAction SilentlyContinue; Write-Host $fail; Write-Error $_ }}";
    }

    private static async Task<bool> EnsureBridgeAsync(MultiTermUpdateDownloaderDependencies dependencies, CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync(dependencies, cancellationToken).ConfigureAwait(false)) return true;
        string? launcher = dependencies.ResolveLauncher();
        if (launcher is null) return false;
        var start = new ProcessStartInfo
        {
            FileName = dependencies.PowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(launcher)!,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(launcher);
        _ = dependencies.StartProcess(start);

        for (int i = 0; i < 60; i++)
        {
            await dependencies.DelayAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            if (await IsHealthyAsync(dependencies, cancellationToken).ConfigureAwait(false)) return true;
        }
        return false;
    }

    private static async Task<bool> IsHealthyAsync(MultiTermUpdateDownloaderDependencies dependencies, CancellationToken cancellationToken)
    {
        if (dependencies.IsHealthyOverride is { } isHealthyOverride)
            return await isHealthyOverride(cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpClient client = dependencies.CreateHealthClient();
            using HttpResponseMessage response = await client.GetAsync(dependencies.HealthUri, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    internal static string? ResolveLauncher()
    {
        string? overridden = Environment.GetEnvironmentVariable("MULTITERM_LAUNCHER");

        return ResolveLauncher(
            overridden,
            InstallRootProvider(),
            RegistryInstallLocationProvider(),
            File.Exists);
    }

    private static IEnumerable<string> GetDefaultInstallRoots()
    {
        string localAppDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MultiTerm Workbench");
        string programFilesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MultiTerm Workbench");
        string programFilesX86Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MultiTerm Workbench");
        return [localAppDataRoot, programFilesRoot, programFilesX86Root];
    }

    private static IEnumerable<string> GetRegistryInstallLocations()
        => GetRegistryInstallLocations(static (hive, path) =>
        {
            using RegistryKey? key = hive.OpenSubKey(path);
            return key?.GetValue("InstallLocation") as string;
        });

    internal static IReadOnlyList<string> GetRegistryInstallLocations(
        Func<RegistryKey, string, string?> readInstallLocation)
    {
        const string appId = "{2A8AE21C-CA11-4B78-8E6E-348A0EBB0E83}_is1";
        var registryInstallLocations = new List<string>();
        foreach ((RegistryKey hive, string prefix) in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\\"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\\"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\\"),
        })
        {
            try
            {
                if (readInstallLocation(hive, prefix + appId) is string install)
                    registryInstallLocations.Add(install);
            }
            catch { }
        }

        return registryInstallLocations;
    }

    internal static string? ResolveLauncher(
        string? overridden,
        IEnumerable<string> installRoots,
        IEnumerable<string> registryInstallLocations,
        Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(overridden) && fileExists(overridden)) return overridden;

        foreach (string root in installRoots)
        {
            string candidate = Path.Combine(root, "Start-MultiTerm.ps1");
            if (fileExists(candidate)) return candidate;
        }

        foreach (string install in registryInstallLocations)
        {
            string candidate = Path.Combine(install, "Start-MultiTerm.ps1");
            if (fileExists(candidate)) return candidate;
        }

        return null;
    }

    private static async Task SendAsync(IMultiTermBridgeSocket socket, MultiTermBridgeMessage message, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, MultiTermBridgeJsonContext.Default.MultiTermBridgeMessage);
        await socket.SendAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForCreatedAsync(IMultiTermBridgeSocket socket, string id, CancellationToken cancellationToken)
    {
        while (true)
        {
            using JsonDocument message = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
            string messageId = root.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? string.Empty : string.Empty;
            if (messageId == id && type == "created") return;
            if (messageId == id && (type == "createFailed" || type == "error"))
                throw new InvalidDataException(root.TryGetProperty("message", out JsonElement e) ? e.GetString() : "MultiTerm could not create the update terminal.");
        }
    }

    private static async Task<MultiTermDownloadResult> WaitForCompletionAsync(
        IMultiTermBridgeSocket socket,
        string id,
        string destinationPath,
        string successMarker,
        string failureMarker,
        CancellationToken cancellationToken)
    {
        var tail = new StringBuilder();
        while (true)
        {
            using JsonDocument message = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
            string messageId = root.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? string.Empty : string.Empty;
            if (messageId != id) continue;
            if (type == "exited") return new(false, null, "The MultiTerm update terminal exited before the download completed.");
            if (type != "output" || !root.TryGetProperty("data", out JsonElement d)) continue;
            tail.Append(d.GetString());
            if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
            string text = tail.ToString();
            if (text.Contains(failureMarker, StringComparison.Ordinal)) return new(false, null, "The download command reported a failure. See the Yagu update terminal in MultiTerm.");
            if (text.Contains(successMarker, StringComparison.Ordinal)) return new(true, destinationPath, null);
        }
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(IMultiTermBridgeSocket socket, CancellationToken cancellationToken)
    {
        MultiTermBridgeReceiveMessage message = await socket.ReceiveAsync(MaxBridgeMessageBytes, cancellationToken).ConfigureAwait(false);
        if (message.MessageType == WebSocketMessageType.Close) throw new WebSocketException("MultiTerm closed the bridge connection.");
        return JsonDocument.Parse(message.Payload);
    }
}

internal sealed class MultiTermUpdateDownloaderDependencies
{
    internal static MultiTermUpdateDownloaderDependencies Default { get; } = new();

    internal Uri HealthUri { get; init; } = MultiTermUpdateDownloaderDefaultUris.HealthUri;
    internal Uri WebSocketUri { get; init; } = MultiTermUpdateDownloaderDefaultUris.WebSocketUri;
    internal Func<string?, DirectoryInfo> CreateDirectory { get; init; } = path => Directory.CreateDirectory(path!);
    internal Func<string?> ResolveLauncher { get; init; } = MultiTermUpdateDownloader.ResolveLauncher;
    internal Func<string> PowerShellPath { get; init; }
        = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    internal Func<ProcessStartInfo, Process?> StartProcess { get; init; } = Process.Start;
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; }
        = static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);
    internal Func<HttpClient> CreateHealthClient { get; init; }
        = static () => new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    internal Func<IMultiTermBridgeSocket> CreateBridgeSocket { get; init; } = () => new ClientWebSocketBridgeSocket();
    internal Func<CancellationToken, Task<bool>>? IsHealthyOverride { get; init; }
    internal Func<string> CreateToken { get; init; } = () => Guid.NewGuid().ToString("N");
}

internal static class MultiTermUpdateDownloaderDefaultUris
{
    internal static Uri HealthUri { get; } = new("http://127.0.0.1:3177/health");
    internal static Uri WebSocketUri { get; } = new("ws://127.0.0.1:3177/ws");
}

internal readonly record struct MultiTermBridgeReceiveMessage(WebSocketMessageType MessageType, byte[] Payload);

internal interface IMultiTermBridgeSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken);
    Task SendAsync(byte[] payload, CancellationToken cancellationToken);
    Task<MultiTermBridgeReceiveMessage> ReceiveAsync(int maximumMessageBytes, CancellationToken cancellationToken);
}

internal sealed class ClientWebSocketBridgeSocket : IMultiTermBridgeSocket
{
    private readonly ClientWebSocket _socket;
    private readonly Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>> _receiveAsync;

    public ClientWebSocketBridgeSocket()
    {
        _socket = new ClientWebSocket();
        _receiveAsync = (buffer, cancellationToken) => _socket.ReceiveAsync(buffer, cancellationToken);
    }

    internal ClientWebSocketBridgeSocket(
        Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>> receiveAsync)
    {
        _socket = new ClientWebSocket();
        _receiveAsync = receiveAsync;
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    public Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken)
        => _socket.ConnectAsync(webSocketUri, cancellationToken);

    public Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        => _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);

    public async Task<MultiTermBridgeReceiveMessage> ReceiveAsync(int maximumMessageBytes, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await _receiveAsync(
                new ArraySegment<byte>(chunk), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return new MultiTermBridgeReceiveMessage(WebSocketMessageType.Close, []);
            if (stream.Length + result.Count > maximumMessageBytes)
                throw new InvalidDataException("MultiTerm sent an oversized bridge message.");
            stream.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
                return new MultiTermBridgeReceiveMessage(result.MessageType, stream.ToArray());
        }
    }
}

internal sealed class MultiTermBridgeMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("shell")] public string? Shell { get; set; }
    [JsonPropertyName("cols")] public int? Cols { get; set; }
    [JsonPropertyName("rows")] public int? Rows { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
}

[JsonSerializable(typeof(MultiTermBridgeMessage))]
internal sealed partial class MultiTermBridgeJsonContext : JsonSerializerContext;
