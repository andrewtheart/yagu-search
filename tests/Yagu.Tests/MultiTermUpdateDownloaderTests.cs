using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class MultiTermUpdateDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_PublicWrapper_InvalidDestinationPath_ReturnsValidationError()
    {
        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            "YaguSetup.exe",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("The update destination path must include a directory.", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_SuccessMarker_ReturnsSuccessWithDestinationPath()
    {
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{"type":"created","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token","data":"ok __YAGU_UPDATE_OK_ok-token__"}""")
        ]);

        string? createdDirectory = null;
        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateDirectory = path =>
            {
                createdDirectory = path;
                return new DirectoryInfo(path!);
            },
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token"),
            WebSocketUri = new Uri("ws://127.0.0.1:12345/ws")
        };

        string destination = Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe");
        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            destination,
            dependencies,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(destination, result.FilePath);
        Assert.Null(result.Error);
        Assert.Equal(Path.GetDirectoryName(destination), createdDirectory);
        Assert.Equal(new Uri("ws://127.0.0.1:12345/ws"), socket.ConnectedUri);
        Assert.Equal(2, socket.SentMessages.Count);
        Assert.Contains("\"type\":\"create\"", socket.SentMessages[0]);
        Assert.Contains("\"type\":\"input\"", socket.SentMessages[1]);
    }

    [Fact]
    public async Task DownloadAsync_CreateFailedMessage_ReturnsMessageAsError()
    {
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{"type":"createFailed","id":"yagu-update-id-token","message":"boom"}""")
        ]);

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.FilePath);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_CreateErrorWithoutMessage_UsesFallbackCreateError()
    {
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{"type":"error","id":"yagu-update-id-token"}""")
        ]);

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("MultiTerm could not create the update terminal.", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_ExitedBeforeSuccess_ReturnsExitedError()
    {
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{"type":"created","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"type":"exited","id":"yagu-update-id-token"}""")
        ]);

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("exited before the download completed", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_FailureMarker_ReturnsFailureMessage()
    {
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{"type":"created","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token","data":"__YAGU_UPDATE_FAIL_fail-token__"}""")
        ]);

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("reported a failure", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_IgnoresUnrelatedMessagesAndLargeTailBeforeSuccess()
    {
        string large = new string('x', 9000);
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{"type":"created","id":"other"}"""),
            _ => JsonMessage("""{"type":"created","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"type":"output","id":"other","data":"ignored"}"""),
            _ => JsonMessage("""{"type":"created","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token"}"""),
            _ => JsonMessage($"{{\"type\":\"output\",\"id\":\"yagu-update-id-token\",\"data\":\"{large}\"}}"),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token","data":"__YAGU_UPDATE_OK_ok-token__"}""")
        ]);

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task DownloadAsync_WaitForCreatedAndCompletion_IgnoreMessagesMissingFields()
    {
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{}"""),
            _ => JsonMessage("""{"type":null,"id":null}"""),
            _ => JsonMessage("""{"type":"created"}"""),
            _ => JsonMessage("""{"id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"type":"created","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{}"""),
            _ => JsonMessage("""{"type":null,"id":null}"""),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"id":"yagu-update-id-token","data":"ignored"}"""),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token","data":null}"""),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token","data":"__YAGU_UPDATE_OK_ok-token__"}""")
        ]);

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task DownloadAsync_ClosedBridgeConnection_ReturnsWebSocketError()
    {
        ScriptedBridgeSocket socket = new([
            _ => new MultiTermBridgeReceiveMessage(WebSocketMessageType.Close, [])
        ]);

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("MultiTerm closed the bridge connection.", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_WhenBridgeNeverHealthyAndNoLauncher_ReturnsUnavailableError()
    {
        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(false),
            ResolveLauncher = () => null,
            DelayAsync = static (_, _) => Task.CompletedTask
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("MultiTerm Workbench is not running and its installed launcher could not be found.", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_HealthProbeFailureWithoutOverride_FallsBackToLauncherMissingMessage()
    {
        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            HealthUri = new Uri("http://127.0.0.1:1/health"),
            ResolveLauncher = () => null,
            DelayAsync = static (_, _) => Task.CompletedTask,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("MultiTerm Workbench is not running and its installed launcher could not be found.", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_HealthProbeSuccessWithoutOverride_UsesHttpResponse()
    {
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{"type":"created","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token","data":"__YAGU_UPDATE_OK_ok-token__"}""")
        ]);
        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            CreateHealthClient = () => new HttpClient(new StaticHandler(HttpStatusCode.OK)),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task DownloadAsync_WhenLauncherExists_StartsProcessAndWaitsUntilHealthy()
    {
        ScriptedBridgeSocket socket = new([
            _ => JsonMessage("""{"type":"created","id":"yagu-update-id-token"}"""),
            _ => JsonMessage("""{"type":"output","id":"yagu-update-id-token","data":"__YAGU_UPDATE_OK_ok-token__"}""")
        ]);

        ProcessStartInfo? started = null;
        int healthChecks = 0;
        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(Interlocked.Increment(ref healthChecks) >= 3),
            ResolveLauncher = () => @"C:\Tools\MultiTerm\Start-MultiTerm.ps1",
            PowerShellPath = () => @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            StartProcess = info =>
            {
                started = info;
                return Process.GetCurrentProcess();
            },
            DelayAsync = static (_, _) => Task.CompletedTask,
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(started);
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", started!.FileName);
        Assert.Equal(@"C:\Tools\MultiTerm", started.WorkingDirectory);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", @"C:\Tools\MultiTerm\Start-MultiTerm.ps1"], started.ArgumentList.ToArray());
    }

    [Fact]
    public async Task DownloadAsync_WhenLauncherStartsButBridgeNeverBecomesHealthy_ReturnsUnavailableError()
    {
        int checks = 0;
        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ =>
            {
                checks++;
                return Task.FromResult(false);
            },
            ResolveLauncher = () => @"C:\Tools\MultiTerm\Start-MultiTerm.ps1",
            StartProcess = _ => Process.GetCurrentProcess(),
            DelayAsync = static (_, _) => Task.CompletedTask,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(checks >= 2);
        Assert.Equal("MultiTerm Workbench is not running and its installed launcher could not be found.", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_OperationCanceledWithoutCallerCancellation_ReturnsTimeoutError()
    {
        ScriptedBridgeSocket socket = new([])
        {
            ConnectException = new OperationCanceledException()
        };

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        MultiTermDownloadResult result = await MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("The MultiTerm update download timed out.", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_CallerCancellation_Propagates()
    {
        ScriptedBridgeSocket socket = new([])
        {
            ConnectCallback = (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };

        var dependencies = new MultiTermUpdateDownloaderDependencies
        {
            IsHealthyOverride = _ => Task.FromResult(true),
            CreateBridgeSocket = () => socket,
            CreateToken = TokenSequence("id-token", "ok-token", "fail-token")
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => MultiTermUpdateDownloader.DownloadAsync(
            CreateReleaseInfo(),
            Path.Combine(Path.GetTempPath(), "yagu-update-test", "YaguSetup.exe"),
            dependencies,
            cts.Token));
    }

    [Fact]
    public void ResolveLauncher_UsesOverrideInstallRootsRegistryAndMissingFallback()
    {
        string overridePath = @"C:\a\Start-MultiTerm.ps1";
        string rootPath = @"C:\root\Start-MultiTerm.ps1";
        string regPath = @"D:\reg\Start-MultiTerm.ps1";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            overridePath,
            rootPath,
            regPath
        };

        string? fromOverride = MultiTermUpdateDownloader.ResolveLauncher(
            overridePath,
            [@"C:\root"],
            [@"D:\reg"],
            existing.Contains);
        Assert.Equal(overridePath, fromOverride);

        string? fromRoot = MultiTermUpdateDownloader.ResolveLauncher(
            null,
            [@"C:\root"],
            [@"D:\reg"],
            existing.Contains);
        Assert.Equal(rootPath, fromRoot);

        string? fromRegistry = MultiTermUpdateDownloader.ResolveLauncher(
            null,
            [@"E:\none"],
            [@"D:\reg"],
            existing.Contains);
        Assert.Equal(regPath, fromRegistry);

        string? missing = MultiTermUpdateDownloader.ResolveLauncher(
            null,
            [@"E:\none"],
            [@"Z:\none"],
            existing.Contains);
        Assert.Null(missing);
    }

    [Fact]
    public void ResolveLauncher_Parameterless_UsesEnvironmentOverrideWhenPresent()
    {
        string original = Environment.GetEnvironmentVariable("MULTITERM_LAUNCHER") ?? string.Empty;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "yagu-multiterm-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string launcher = Path.Combine(tempDirectory, "Start-MultiTerm.ps1");
        File.WriteAllText(launcher, "# test");

        try
        {
            Environment.SetEnvironmentVariable("MULTITERM_LAUNCHER", launcher);
            Assert.Equal(launcher, MultiTermUpdateDownloader.ResolveLauncher());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MULTITERM_LAUNCHER", string.IsNullOrEmpty(original) ? null : original);
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveLauncher_Parameterless_UsesRegistryProviderWhenOverrideAndDefaultRootsDoNotExist()
    {
        string original = Environment.GetEnvironmentVariable("MULTITERM_LAUNCHER") ?? string.Empty;
        Func<IEnumerable<string>> originalInstallRoots = MultiTermUpdateDownloader.InstallRootProvider;
        Func<IEnumerable<string>> originalProvider = MultiTermUpdateDownloader.RegistryInstallLocationProvider;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "yagu-multiterm-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string launcher = Path.Combine(tempDirectory, "Start-MultiTerm.ps1");
        File.WriteAllText(launcher, "# registry path");

        try
        {
            Environment.SetEnvironmentVariable("MULTITERM_LAUNCHER", " ");
            MultiTermUpdateDownloader.InstallRootProvider = () => [Path.Combine(tempDirectory, "missing")];
            MultiTermUpdateDownloader.RegistryInstallLocationProvider = () => [tempDirectory];

            Assert.Equal(launcher, MultiTermUpdateDownloader.ResolveLauncher());
        }
        finally
        {
            MultiTermUpdateDownloader.InstallRootProvider = originalInstallRoots;
            MultiTermUpdateDownloader.RegistryInstallLocationProvider = originalProvider;
            Environment.SetEnvironmentVariable("MULTITERM_LAUNCHER", string.IsNullOrEmpty(original) ? null : original);
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetRegistryInstallLocations_SkipsReadFailuresAndMissingValues()
    {
        int calls = 0;

        IReadOnlyList<string> locations = MultiTermUpdateDownloader.GetRegistryInstallLocations((_, _) =>
            ++calls switch
            {
                1 => throw new IOException("registry unavailable"),
                2 => @"C:\Tools\MultiTerm",
                _ => null,
            });

        Assert.Equal(3, calls);
        Assert.Equal([@"C:\Tools\MultiTerm"], locations);
    }

    [Fact]
    public void DefaultUris_AreInitializedToLoopbackBridgeEndpoints()
    {
        Assert.Equal(new Uri("http://127.0.0.1:3177/health"), MultiTermUpdateDownloaderDefaultUris.HealthUri);
        Assert.Equal(new Uri("ws://127.0.0.1:3177/ws"), MultiTermUpdateDownloaderDefaultUris.WebSocketUri);
    }

    [Fact]
    public async Task ClientWebSocketBridgeSocket_Methods_ExecuteOnUnconnectedSocketPaths()
    {
        await using var socket = new ClientWebSocketBridgeSocket();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => socket.ConnectAsync(
            new Uri("ws://127.0.0.1:3177/ws"),
            canceled.Token));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => socket.SendAsync([1, 2, 3], CancellationToken.None));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => socket.ReceiveAsync(1024, CancellationToken.None));
    }

    [Fact]
    public async Task ClientWebSocketBridgeSocket_ReceiveAsync_CombinesFrames()
    {
        Queue<(byte[] Payload, WebSocketMessageType Type, bool End)> frames = new([
            (Encoding.UTF8.GetBytes("hel"), WebSocketMessageType.Text, false),
            (Encoding.UTF8.GetBytes("lo"), WebSocketMessageType.Text, true),
        ]);
        await using var socket = new ClientWebSocketBridgeSocket((buffer, _) =>
        {
            (byte[] payload, WebSocketMessageType type, bool end) = frames.Dequeue();
            payload.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(payload.Length, type, end));
        });

        MultiTermBridgeReceiveMessage message = await socket.ReceiveAsync(10, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        Assert.Equal("hello", Encoding.UTF8.GetString(message.Payload));
    }

    [Fact]
    public async Task ClientWebSocketBridgeSocket_ReceiveAsync_ReturnsCloseFrame()
    {
        await using var socket = new ClientWebSocketBridgeSocket((_, _) => Task.FromResult(
            new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true)));

        MultiTermBridgeReceiveMessage message = await socket.ReceiveAsync(10, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Close, message.MessageType);
        Assert.Empty(message.Payload);
    }

    [Fact]
    public async Task ClientWebSocketBridgeSocket_ReceiveAsync_RejectsOversizedMessage()
    {
        await using var socket = new ClientWebSocketBridgeSocket((buffer, _) =>
        {
            byte[] payload = [1, 2, 3];
            payload.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(
                payload.Length, WebSocketMessageType.Binary, endOfMessage: true));
        });

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => socket.ReceiveAsync(2, CancellationToken.None));

        Assert.Equal("MultiTerm sent an oversized bridge message.", exception.Message);
    }

    private static AppReleaseInfo CreateReleaseInfo()
    {
        var installer = new AppReleaseAsset(
            "YaguSetup-1.0.0.9-x64.exe",
            new Uri("https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.9/YaguSetup-1.0.0.9-x64.exe"),
            128,
            new string('A', 64));
        return new AppReleaseInfo(
            new Version(1, 0, 0, 9),
            "v1.0.0.9",
            "Yagu 1.0.0.9",
            "notes",
            new Uri("https://github.com/andrewtheart/yagu-search/releases/tag/v1.0.0.9"),
            DateTimeOffset.UtcNow,
            installer);
    }

    private static Func<string> TokenSequence(params string[] values)
    {
        Queue<string> queue = new(values);
        return () => queue.Dequeue();
    }

    private static MultiTermBridgeReceiveMessage JsonMessage(string json)
        => new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));

    private sealed class ScriptedBridgeSocket : IMultiTermBridgeSocket
    {
        private readonly Queue<Func<ScriptedBridgeSocket, MultiTermBridgeReceiveMessage>> _receiveSteps;

        public ScriptedBridgeSocket(IEnumerable<Func<ScriptedBridgeSocket, MultiTermBridgeReceiveMessage>> receiveSteps)
        {
            _receiveSteps = new Queue<Func<ScriptedBridgeSocket, MultiTermBridgeReceiveMessage>>(receiveSteps);
        }

        public List<string> SentMessages { get; } = [];
        public Uri? ConnectedUri { get; private set; }
        public Exception? ConnectException { get; set; }
        public Func<Uri, CancellationToken, Task>? ConnectCallback { get; set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken)
        {
            ConnectedUri = webSocketUri;
            if (ConnectCallback is not null)
            {
                await ConnectCallback(webSocketUri, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (ConnectException is not null)
                throw ConnectException;
        }

        public Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            SentMessages.Add(Encoding.UTF8.GetString(payload));
            return Task.CompletedTask;
        }

        public Task<MultiTermBridgeReceiveMessage> ReceiveAsync(int maximumMessageBytes, CancellationToken cancellationToken)
        {
            if (_receiveSteps.Count == 0)
                throw new InvalidOperationException("No scripted bridge messages remain.");
            return Task.FromResult(_receiveSteps.Dequeue()(this));
        }
    }

    private sealed class StaticHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
