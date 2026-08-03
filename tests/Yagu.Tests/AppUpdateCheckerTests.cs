using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class AppUpdateCheckerTests
{
    [Theory]
    [InlineData("v1.0.0.2382", 2382)]
    [InlineData("1.2.3.4", 4)]
    [InlineData("V2.3.4.5", 5)]
    public void TryParseVersion_FourPartRelease_Succeeds(string text, int revision)
    {
        Assert.True(AppUpdateChecker.TryParseVersion(text, out Version version));
        Assert.Equal(revision, version.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.2.3")]
    [InlineData("nightly")]
    public void TryParseVersion_MalformedOrIncomplete_Fails(string text)
        => Assert.False(AppUpdateChecker.TryParseVersion(text, out _));

    [Fact]
    public void TryParseVersion_Null_Fails()
        => Assert.False(AppUpdateChecker.TryParseVersion(null, out _));

    [Fact]
    public void ShouldAutoCheck_NeverChecked_ReturnsTrue()
        => Assert.True(AppUpdateChecker.ShouldAutoCheck(null, DateTimeOffset.UtcNow, TimeSpan.FromDays(7)));

    [Fact]
    public void ShouldAutoCheck_WithinInterval_ReturnsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(AppUpdateChecker.ShouldAutoCheck(now.AddDays(-3), now, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void ShouldAutoCheck_PastInterval_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(AppUpdateChecker.ShouldAutoCheck(now.AddDays(-8), now, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void DefaultAutoCheckInterval_IsOneWeek()
        => Assert.Equal(TimeSpan.FromDays(7), AppUpdateChecker.DefaultAutoCheckInterval);

    [Fact]
    public void SelectInstallerAsset_RequiresExactArchitectureVersionHttpsAndDigest()
    {
        var version = new Version(1, 0, 0, 9);
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "unsupported",
        };
        (string Name, string Url, long Size, string? Digest)[] assets =
        {
            ($"YaguSetup-{version}-{arch}-offline.exe", "https://github.com/x/y/a.exe", 10L, "sha256:" + new string('A', 64)),
            ($"YaguSetup-{version}-{arch}.exe", "https://github.com/x/y/b.exe", 20L, "sha256:" + new string('B', 64)),
        };

        AppReleaseAsset? selected = AppUpdateChecker.SelectInstallerAsset(assets, version, RuntimeInformation.ProcessArchitecture);

        if (arch == "unsupported") Assert.Null(selected);
        else
        {
            Assert.NotNull(selected);
            Assert.Equal(20, selected!.Size);
            Assert.Equal(new string('B', 64), selected.Sha256);
        }
    }

    [Fact]
    public void SelectInstallerAsset_InvalidInputs_ReturnNull()
    {
        var version = new Version(1, 0, 0, 9);

        Assert.Null(AppUpdateChecker.SelectInstallerAsset([], version, Architecture.Arm));

        var wrongHost = new[]
        {
            ($"YaguSetup-{version}-x64.exe", "https://example.com/y.exe", 10L, "sha256:" + new string('A', 64))
        };
        Assert.Null(AppUpdateChecker.SelectInstallerAsset(wrongHost, version, Architecture.X64));

        var badDigest = new[]
        {
            ($"YaguSetup-{version}-x64.exe", "https://github.com/o/r/y.exe", 10L, "sha256:abcd")
        };
        Assert.Null(AppUpdateChecker.SelectInstallerAsset(badDigest, version, Architecture.X64));

        var duplicate = new[]
        {
            ($"YaguSetup-{version}-x64.exe", "https://github.com/o/r/y1.exe", 10L, "sha256:" + new string('B', 64)),
            ($"YaguSetup-{version}-x64.exe", "https://github.com/o/r/y2.exe", 10L, "sha256:" + new string('C', 64))
        };
        Assert.Null(AppUpdateChecker.SelectInstallerAsset(duplicate, version, Architecture.X64));

        var badSize = new[]
        {
            ($"YaguSetup-{version}-x64.exe", "https://github.com/o/r/y.exe", 0L, "sha256:" + new string('D', 64))
        };
        Assert.Null(AppUpdateChecker.SelectInstallerAsset(badSize, version, Architecture.X64));
    }

    [Fact]
    public void SelectInstallerAsset_HttpOrRelativeUrl_ReturnsNull()
    {
        var version = new Version(1, 0, 0, 9);

        var httpAsset = new[]
        {
            ($"YaguSetup-{version}-x64.exe", "http://github.com/o/r/y.exe", 10L, "sha256:" + new string('A', 64))
        };
        Assert.Null(AppUpdateChecker.SelectInstallerAsset(httpAsset, version, Architecture.X64));

        var relativeUrlAsset = new[]
        {
            ($"YaguSetup-{version}-x64.exe", "/o/r/y.exe", 10L, "sha256:" + new string('B', 64))
        };
        Assert.Null(AppUpdateChecker.SelectInstallerAsset(relativeUrlAsset, version, Architecture.X64));
    }

    [Fact]
    public void SelectInstallerAsset_DigestWithoutPrefixAndLowercaseHex_IsAcceptedAndUppercased()
    {
        var version = new Version(1, 0, 0, 9);
        string lowerDigest = new string('a', 64);
        var assets = new[]
        {
            ($"YaguSetup-{version}-x64.exe", "https://github.com/o/r/y.exe", 10L, lowerDigest)
        };

        AppReleaseAsset? selected = AppUpdateChecker.SelectInstallerAsset(assets, version, Architecture.X64);

        Assert.NotNull(selected);
        Assert.Equal(new string('A', 64), selected!.Sha256);
    }

    [Fact]
    public void SelectInstallerAsset_NullDigest_ReturnsNull()
    {
        var version = new Version(1, 0, 0, 9);
        var assets = new (string Name, string Url, long Size, string? Digest)[]
        {
            ($"YaguSetup-{version}-x64.exe", "https://github.com/o/r/y.exe", 10L, null)
        };

        Assert.Null(AppUpdateChecker.SelectInstallerAsset(assets, version, Architecture.X64));
    }

    [Fact]
    public void SelectInstallerAsset_ArchitectureSpecificNamesForX86AndArm64()
    {
        var version = new Version(1, 0, 0, 9);
        var x86 = new[]
        {
            ($"YaguSetup-{version}-x86.exe", "https://github.com/o/r/y86.exe", 10L, "sha256:" + new string('1', 64))
        };
        var arm64 = new[]
        {
            ($"YaguSetup-{version}-arm64.exe", "https://github.com/o/r/yarm.exe", 10L, "sha256:" + new string('2', 64))
        };

        Assert.NotNull(AppUpdateChecker.SelectInstallerAsset(x86, version, Architecture.X86));
        Assert.NotNull(AppUpdateChecker.SelectInstallerAsset(arm64, version, Architecture.Arm64));
    }

    [Fact]
    public async Task CheckLatestAsync_NewerRelease_ReturnsNotesAndExactAsset()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        string json = $$"""
            {
              "tag_name":"v1.0.0.9",
              "name":"Yagu 1.0.0.9",
              "body":"fixed things",
              "html_url":"https://github.com/andrewtheart/yagu-search/releases/tag/v1.0.0.9",
              "draft":false,
              "prerelease":false,
              "published_at":"2026-07-26T00:00:00Z",
              "assets":[{
                "name":"YaguSetup-1.0.0.9-{{arch}}.exe",
                "browser_download_url":"https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.9/YaguSetup-1.0.0.9-{{arch}}.exe",
                "size":123,
                "digest":"sha256:{{new string('C', 64)}}"
              }]
            }
            """;
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.True(result!.UpdateAvailable);
        Assert.Equal(new Version(1, 0, 0, 9), result.LatestVersion);
        Assert.Equal("fixed things", result.Release!.ReleaseNotes);
        Assert.Equal(123, result.Release.Installer.Size);
    }

    [Fact]
    public async Task CheckLatestAsync_OlderRelease_IsAValidNoUpdateResponse()
    {
        string json = "{\"tag_name\":\"v1.0.0.7\",\"draft\":false,\"prerelease\":false,\"assets\":[]}";
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.False(result!.UpdateAvailable);
        Assert.Equal(new Version(1, 0, 0, 7), result.LatestVersion);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckLatestAsync_NewerReleaseWithoutVerifiableInstaller_PreservesLatestVersion()
    {
        string json = "{\"tag_name\":\"v1.0.0.9\",\"draft\":false,\"prerelease\":false,\"assets\":[]}";
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.True(result!.UpdateAvailable);
        Assert.Equal(new Version(1, 0, 0, 9), result.LatestVersion);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckLatestAsync_OmittedAssets_PreservesLatestVersionWithoutRelease()
    {
        const string json = "{\"tag_name\":\"v1.0.0.9\",\"draft\":false,\"prerelease\":false}";
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.Equal(new Version(1, 0, 0, 9), result!.LatestVersion);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckLatestAsync_OversizedMetadataFailsClosed()
    {
        using var client = new HttpClient(new StaticHandler(new string('x', AppUpdateChecker.MaxReleaseMetadataBytes + 1)));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckLatestAsync_InvalidCurrentVersion_ReturnsNull()
    {
        using var client = new HttpClient(new StaticHandler("{}"));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("invalid", client);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckLatestAsync_NonSuccessStatus_ReturnsNull()
    {
        using var client = new HttpClient(new StaticHandler("{}", HttpStatusCode.BadGateway));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CheckLatestAsync_DraftOrPrerelease_ReturnsNoUpdate(bool draft, bool prerelease)
    {
        string json = $$"""
            {
              "tag_name":"v1.0.0.9",
              "draft":{{draft.ToString().ToLowerInvariant()}},
              "prerelease":{{prerelease.ToString().ToLowerInvariant()}},
              "assets":[]
            }
            """;
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.Equal(new Version(1, 0, 0, 8), result!.LatestVersion);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckLatestAsync_InvalidTag_ReturnsNoUpdate()
    {
        string json = "{\"tag_name\":\"nightly\",\"draft\":false,\"prerelease\":false,\"assets\":[]}";
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.Equal(new Version(1, 0, 0, 8), result!.LatestVersion);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckLatestAsync_JsonNullDto_ReturnsNoUpdate()
    {
        using var client = new HttpClient(new StaticHandler("null"));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.Equal(new Version(1, 0, 0, 8), result!.LatestVersion);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckLatestAsync_LatestEqualsCurrentWithValidInstaller_DoesNotReturnRelease()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        string json = $$"""
            {
              "tag_name":"v1.0.0.8",
              "name":"Yagu 1.0.0.8",
              "body":"notes",
              "html_url":"https://github.com/andrewtheart/yagu-search/releases/tag/v1.0.0.8",
              "draft":false,
              "prerelease":false,
              "assets":[{
                "name":"YaguSetup-1.0.0.8-{{arch}}.exe",
                "browser_download_url":"https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.8/YaguSetup-1.0.0.8-{{arch}}.exe",
                "size":123,
                "digest":"sha256:{{new string('F', 64)}}"
              }]
            }
            """;
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.False(result!.UpdateAvailable);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckLatestAsync_InvalidJson_ReturnsNull()
    {
        using var client = new HttpClient(new StaticHandler("{"));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckLatestAsync_InvalidReleasePageAndLongNotes_FallsBackAndTruncates()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        string longNotes = new('N', AppUpdateChecker.MaxReleaseNotesChars + 20);
        string json = $$"""
            {
              "tag_name":"v1.0.0.9",
              "name":"",
              "body":"{{longNotes}}",
              "html_url":"http://example.com/not-github",
              "draft":false,
              "prerelease":false,
              "assets":[{
                "name":"YaguSetup-1.0.0.9-{{arch}}.exe",
                "browser_download_url":"https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.9/YaguSetup-1.0.0.9-{{arch}}.exe",
                "size":123,
                "digest":"sha256:{{new string('E', 64)}}"
              }]
            }
            """;
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result);
        Assert.NotNull(result!.Release);
        Assert.Equal(AppUpdateChecker.LatestReleasePage, result.Release!.ReleasePage);
        Assert.Equal("Yagu 1.0.0.9", result.Release.Name);
        Assert.EndsWith("[Release notes truncated by Yagu.]", result.Release.ReleaseNotes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckLatestAsync_OmittedNameAndBodyWithNonGitHubHttpsPage_UsesDefaults()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        string json = $$"""
            {
              "tag_name":"v1.0.0.9",
              "html_url":"https://example.com/releases/v1.0.0.9",
              "draft":false,
              "prerelease":false,
              "assets":[{
                "name":"YaguSetup-1.0.0.9-{{arch}}.exe",
                "browser_download_url":"https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.9/YaguSetup-1.0.0.9-{{arch}}.exe",
                "size":123,
                "digest":"sha256:{{new string('E', 64)}}"
              }]
            }
            """;
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.NotNull(result?.Release);
        Assert.Equal(AppUpdateChecker.LatestReleasePage, result!.Release!.ReleasePage);
        Assert.Equal("Yagu 1.0.0.9", result.Release.Name);
        Assert.Equal(string.Empty, result.Release.ReleaseNotes);
    }

    [Fact]
    public async Task CheckLatestAsync_TransportException_ReturnsNull()
    {
        using var client = new HttpClient(new ThrowingHandler(new HttpRequestException("network")));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", client);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckLatestAsync_NullClientWithCanceledToken_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.0.0.8", null, cts.Token);

        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyDownloadedAssetAsync_ReturnsFalse_ForMissingOrMismatchedOrUnreadablePaths()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yagu-update-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "asset.bin");
            await File.WriteAllBytesAsync(filePath, [1, 2, 3]);
            string goodHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(File.OpenRead(filePath)));

            bool missing = await AppUpdateChecker.VerifyDownloadedAssetAsync(
                Path.Combine(tempDir, "missing.bin"),
                new AppReleaseAsset("a.exe", new Uri("https://github.com/a/b"), 1, new string('0', 64)));
            bool mismatchedLength = await AppUpdateChecker.VerifyDownloadedAssetAsync(
                filePath,
                new AppReleaseAsset("a.exe", new Uri("https://github.com/a/b"), 999, goodHash));
            bool mismatchedHash = await AppUpdateChecker.VerifyDownloadedAssetAsync(
                filePath,
                new AppReleaseAsset("a.exe", new Uri("https://github.com/a/b"), 3, new string('0', 64)));
            bool directoryPath = await AppUpdateChecker.VerifyDownloadedAssetAsync(
                tempDir,
                new AppReleaseAsset("a.exe", new Uri("https://github.com/a/b"), 3, goodHash));

            Assert.False(missing);
            Assert.False(mismatchedLength);
            Assert.False(mismatchedHash);
            Assert.False(directoryPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task VerifyDownloadedAssetAsync_ReturnsTrue_ForMatchingAsset()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "yagu-update-verify-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(tempFile, [10, 20, 30, 40]);
            string hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(File.OpenRead(tempFile)));

            bool result = await AppUpdateChecker.VerifyDownloadedAssetAsync(
                tempFile,
                new AppReleaseAsset("a.exe", new Uri("https://github.com/a/b"), 4, hash));

            Assert.True(result);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task VerifyDownloadedAssetAsync_WhenFileLockedForReadWrite_ReturnsFalse()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "yagu-update-verify-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(tempFile, [7, 8, 9, 10]);
            string hash;
            await using (FileStream hashStream = File.OpenRead(tempFile))
            {
                hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(hashStream));
            }

            using var lockStream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            bool result = await AppUpdateChecker.VerifyDownloadedAssetAsync(
                tempFile,
                new AppReleaseAsset("a.exe", new Uri("https://github.com/a/b"), 4, hash));

            Assert.False(result);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task VerifyDownloadedAssetAsync_WhenCanceled_ReturnsFalse()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "yagu-update-verify-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(tempFile, new byte[4096]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppUpdateChecker.VerifyDownloadedAssetAsync(
                tempFile,
                new AppReleaseAsset("a.exe", new Uri("https://github.com/a/b"), 4096, new string('0', 64)),
                cts.Token));
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void MultiTermCommand_UsesVisibleCurlChecksSizeHashAndHidesMarkersFromPtyEcho()
    {
        var asset = new AppReleaseAsset(
            "YaguSetup-1.0.0.9-x64.exe",
            new Uri("https://github.com/o/r/releases/download/v1/a.exe"),
            123,
            new string('D', 64));

        string command = MultiTermUpdateDownloader.BuildDownloadCommand(
            asset, @"C:\Temp\Yagu's update.exe", "OK_MARKER", "FAIL_MARKER");

        Assert.Contains("curl.exe --location --fail --progress-bar", command);
        Assert.Contains("Downloaded size mismatch", command);
        Assert.Contains("Downloaded SHA-256 mismatch", command);
        Assert.DoesNotContain("OK_MARKER", command);
        Assert.DoesNotContain("FAIL_MARKER", command);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("OK_MARKER")), command);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("FAIL_MARKER")), command);
        Assert.Contains("Write-Host $ok", command);
        Assert.Contains("Write-Host $fail", command);
        Assert.Contains("Yagu''s update.exe", command);
    }

    private sealed class StaticHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}

public sealed class AppUpdateWiringRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Startup_AsksBeforeNetworkCheck_ShowsNotesAndTrustChecksBeforeRunAs()
    {
        string startup = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));
        string update = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AppUpdate.cs"));
        string verifier = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "AuthenticodeVerifier.cs"));

        Assert.DoesNotContain("BeginAppUpdateCheckAsync", startup);
        // One-time consent stays in the awaited startup chain; the automatic check is fire-and-forget so
        // it never becomes a launch modal (the every-launch prompt is gone).
        Assert.Contains("await MaybeShowAppUpdateConsentPromptAsync();", startup);
        Assert.Contains("_ = MaybeRunAutomaticAppUpdateCheckAsync();", startup);
        Assert.DoesNotContain("PromptForAppUpdateCheckOnLaunchAsync", startup);

        // Consent is asked only once (the undecided Prompt mode), never every launch.
        Assert.Contains("settings.AppUpdateCheckMode != AppUpdateCheckMode.Prompt", update);
        // Automatic checks are gated on the mode AND throttled before any network request is made.
        Assert.Contains("settings.AppUpdateCheckMode != AppUpdateCheckMode.Automatic", update);
        Assert.Contains("AppUpdateChecker.ShouldAutoCheck(", update);
        // Privacy: the GitHub request is defined only after the consent dialog.
        int promptIndex = update.IndexOf("YaguDialog.ShowAsync", StringComparison.Ordinal);
        int requestIndex = update.IndexOf("AppUpdateChecker.CheckLatestAsync", StringComparison.Ordinal);
        Assert.True(promptIndex >= 0 && requestIndex > promptIndex,
            "The GitHub request must be defined only after the consent dialog.");
        // Automatic results are non-modal: a newer, not-already-skipped version opens the InfoBar, and
        // "Skip this version" records it so it isn't renotified.
        Assert.Contains("AppUpdateInfoBar.IsOpen = true;", update);
        Assert.Contains("LastAppUpdateAlertedVersion = release.Version.ToString();", update);
        // The verified download/verify/install flow and its security gates are unchanged.
        Assert.Contains("Text = string.IsNullOrWhiteSpace(release.ReleaseNotes)", update);
        Assert.Contains("PrimaryButtonText = \"Download update\"", update);
        Assert.Contains("MultiTermUpdateDownloader.DownloadAsync(", update);
        Assert.Contains("AppUpdateChecker.VerifyDownloadedAssetAsync", update);
        Assert.Contains("AuthenticodeVerifier.IsInstallerTrustedForHostPublisher", update);
        Assert.Contains("failed Authenticode publisher verification", update);
        Assert.Contains("File.Delete(download.FilePath)", update);
        Assert.Contains("Verb = \"runas\"", update);
        int offerInstaller = update.IndexOf(
            "private async Task OfferVerifiedInstallerAsync(",
            StringComparison.Ordinal);
        Assert.True(offerInstaller >= 0);
        string installerFlow = update[offerInstaller..];
        int confirmation = installerFlow.IndexOf(
            "ConfirmExitWhileIndexingAsync(IndexingCloseTrigger.AppUpdate)",
            StringComparison.Ordinal);
        int verification = installerFlow.IndexOf(
            "AppUpdateChecker.VerifyDownloadedAssetAsync(installerPath, release.Installer)",
            StringComparison.Ordinal);
        int launch = installerFlow.IndexOf(
            "Process.Start(new ProcessStartInfo(installerPath)",
            StringComparison.Ordinal);
        Assert.True(confirmation >= 0 && verification > confirmation && launch > verification);
        Assert.Contains("_forceClose = true;", update);
        Assert.Contains("ShowTitleBar = false", update);
        Assert.Contains("the running Yagu build is not Authenticode-signed", verifier);
    }

    [Fact]
    public void NonModalUpdateBanner_AndSettingsControls_AreWired()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        string settings = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
        string settingsSvc = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));

        // The non-modal banner exists with its three actions (view / skip / remind-later-close).
        Assert.Contains("x:Name=\"AppUpdateInfoBar\"", xaml);
        Assert.Contains("CloseButtonClick=\"OnAppUpdateInfoBarRemindLater\"", xaml);
        Assert.Contains("Click=\"OnAppUpdateInfoBarViewRelease\"", xaml);
        Assert.Contains("Click=\"OnAppUpdateInfoBarSkipVersion\"", xaml);

        // Settings exposes the 3-way mode picker and an on-demand check wired to the owner hwnd.
        Assert.Contains("AppUpdateCheckMode.Automatic", settings);
        Assert.Contains("AppUpdateCheckMode.Off", settings);
        Assert.Contains("Content = \"Check for updates now\"", settings);
        Assert.Contains("_checkForUpdatesNow?.Invoke(_settingsHwnd)", settings);

        int updatesTab = settings.IndexOf("AddTab(\"Updates\")", StringComparison.Ordinal);
        int updateMode = settings.IndexOf("var appUpdateMode = new ComboBox", StringComparison.Ordinal);
        int developerTab = settings.IndexOf("AddTab(\"Developer Options\")", StringComparison.Ordinal);
        Assert.True(updatesTab >= 0 && updateMode > updatesTab && developerTab > updateMode,
            "Application update controls must live in the Updates tab before Developer Options.");
        Assert.Contains("\"Updates\" => \"\\uE895\"", settings);

        // Load migrates a legacy opt-out to Off but leaves everyone else at the Prompt default.
        Assert.Contains("MigrateLegacyAppUpdateChecks(settings)", settingsSvc);
        Assert.Contains("settings.AppUpdateCheckMode = AppUpdateCheckMode.Off;", settingsSvc);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Yagu.slnx"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
