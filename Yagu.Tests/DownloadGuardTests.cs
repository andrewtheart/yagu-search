using Yagu.OcrWorker;

namespace Yagu.Tests;

/// <summary>
/// Covers <see cref="DownloadGuard"/>, the worker-side hard enforcement point for "no external OCR
/// download without consent". The guard reads the process-global <c>YAGU_OCR_ALLOW_DOWNLOAD</c>
/// environment variable, so these tests run in the shared environment collection and restore the
/// variable around each case.
/// </summary>
[Collection("WorkerOcrEngineEnvironment")]
public sealed class DownloadGuardTests : IDisposable
{
    private const string AllowEnvVar = "YAGU_OCR_ALLOW_DOWNLOAD";
    private readonly string? _original;

    public DownloadGuardTests() => _original = Environment.GetEnvironmentVariable(AllowEnvVar);

    public void Dispose() => Environment.SetEnvironmentVariable(AllowEnvVar, _original);

    [Fact]
    public void DownloadsAllowed_TrueWhenVariableAbsent()
    {
        // Absent variable keeps standalone/dev worker runs downloading as before.
        Environment.SetEnvironmentVariable(AllowEnvVar, null);
        Assert.True(DownloadGuard.DownloadsAllowed);
    }

    [Fact]
    public void DownloadsAllowed_TrueWhenExplicitlyOne()
    {
        Environment.SetEnvironmentVariable(AllowEnvVar, "1");
        Assert.True(DownloadGuard.DownloadsAllowed);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("")]
    [InlineData("yes")]
    public void DownloadsAllowed_TrueForAnyValueExceptZero(string value)
    {
        Environment.SetEnvironmentVariable(AllowEnvVar, value);
        Assert.True(DownloadGuard.DownloadsAllowed);
    }

    [Fact]
    public void DownloadsAllowed_FalseWhenExplicitlyZero()
    {
        Environment.SetEnvironmentVariable(AllowEnvVar, "0");
        Assert.False(DownloadGuard.DownloadsAllowed);
    }

    [Fact]
    public void EnsureAllowed_DoesNotThrowWhenAllowed()
    {
        Environment.SetEnvironmentVariable(AllowEnvVar, "1");
        DownloadGuard.EnsureAllowed("engine runtime"); // should not throw
    }

    [Fact]
    public void EnsureAllowed_ThrowsWithComponentNameWhenBlocked()
    {
        Environment.SetEnvironmentVariable(AllowEnvVar, "0");

        var ex = Assert.Throws<OcrDownloadNotAllowedException>(
            () => DownloadGuard.EnsureAllowed("language models"));

        Assert.Contains("language models", ex.Message);
        Assert.Contains("not authorized", ex.Message);
    }
}
