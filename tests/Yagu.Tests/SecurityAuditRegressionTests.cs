using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Locks in the fixes made during the security audit so they cannot silently regress:
///   1. The downloaded Everything Search installer is never executed elevated unless it carries a
///      valid Authenticode signature from voidtools (OWASP A08 software-integrity).
///   2. The telemetry Function sanitizes the caller-supplied correlation id before it is used to
///      build blob paths (blob-path injection / cross-report overwrite).
///   3. The es.exe directory-autocomplete query is passed via ArgumentList so a user-typed prefix
///      cannot inject additional es.exe switches (argument injection).
/// </summary>
public sealed class SecurityAuditRegressionTests
{
    // ── AuthenticodeVerifier: behavioural (deterministic, machine-independent) ─────────────────────

    [Fact]
    public void IsTrustedPublisher_MissingFile_FailsSafe()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"yagu-nope-{Guid.NewGuid():N}.exe");

        bool trusted = AuthenticodeVerifier.IsTrustedPublisher(missing, "voidtools", out string reason);

        Assert.False(trusted);
        Assert.Equal("file not found", reason);
    }

    [Fact]
    public void IsTrustedPublisher_NullOrEmptyPath_FailsSafe()
    {
        Assert.False(AuthenticodeVerifier.IsTrustedPublisher(string.Empty, "voidtools", out _));
        Assert.False(AuthenticodeVerifier.IsTrustedPublisher("   ", "voidtools", out _));
    }

    [Fact]
    public void IsTrustedPublisher_UnsignedFile_IsRejected()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"yagu-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(temp, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
        try
        {
            bool trusted = AuthenticodeVerifier.IsTrustedPublisher(temp, "voidtools", out string reason);

            Assert.False(trusted);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void IsTrustedPublisher_UnsignedFile_RejectedEvenWithoutPublisherConstraint()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"yagu-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllText(temp, "not a real signed binary");
        try
        {
            Assert.False(AuthenticodeVerifier.IsTrustedPublisher(temp, expectedPublisher: null, out _));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void IsWorkerTrustedForHost_NeverThrows_AndReasonMatchesOutcome()
    {
        // Contract, independent of whether the test host is signed: allow => empty reason;
        // reject => a non-empty explanation. The method must never throw.
        string bogus = Path.Combine(Path.GetTempPath(), $"yagu-noworker-{Guid.NewGuid():N}.exe");

        bool allowed = AuthenticodeVerifier.IsWorkerTrustedForHost(bogus, out string reason);

        if (allowed)
            Assert.True(string.IsNullOrEmpty(reason));
        else
            Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void IsWorkerTrustedForHost_IsGatedOnHostSignature()
    {
        // When the host (this test process) is itself Authenticode-signed, enforcement is active and a
        // missing/unsigned worker must be rejected (fail-safe). On an unsigned dev host there is no
        // publisher identity to bind to, so the worker is allowed — the no-op dev path.
        string bogus = Path.Combine(Path.GetTempPath(), $"yagu-noworker-{Guid.NewGuid():N}.exe");
        bool hostSigned = !string.IsNullOrEmpty(Environment.ProcessPath)
            && AuthenticodeVerifier.IsTrustedPublisher(Environment.ProcessPath!, null, out _);

        bool allowed = AuthenticodeVerifier.IsWorkerTrustedForHost(bogus, out _);

        if (hostSigned)
            Assert.False(allowed);
        else
            Assert.True(allowed);
    }

    // ── AuthenticodeVerifier: source-pin the security-critical policy flags ────────────────────────

    [Fact]
    public void AuthenticodeVerifier_UsesWholeChainRevocationAndFailsSafe()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "AuthenticodeVerifier.cs"));

        // Whole-chain revocation checking must stay on so a revoked signer is rejected.
        Assert.Contains("WTD_REVOKE_WHOLECHAIN", source);
        Assert.Contains("WTD_REVOCATION_CHECK_CHAIN", source);
        // The verify state must always be closed to avoid leaking WinVerifyTrust state data.
        Assert.Contains("WTD_STATEACTION_CLOSE", source);
        // WINTRUST_ACTION_GENERIC_VERIFY_V2 policy GUID.
        Assert.Contains("00AAC56B-CD44-11d0-8CC2-00C04FC295EE", source);
    }

    [Fact]
    public void AuthenticodeVerifier_WorkerCheck_IsHostGatedAndSamePublisher()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "AuthenticodeVerifier.cs"));

        // The check is gated on the HOST's own signature, so unsigned dev builds skip enforcement.
        Assert.Contains("public static bool IsWorkerTrustedForHost", source);
        Assert.Contains("Environment.ProcessPath", source);
        Assert.Contains("HostSignerSubject", source);
        // A signed host requires the worker to be signed by the SAME publisher.
        Assert.Contains("does not match host publisher", source);
    }

    // ── Out-of-process workers must be signature-verified BEFORE they are launched ───────────────

    [Fact]
    public void OcrWorker_VerifiesSignatureBeforeLaunch()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Yagu", "Services", "Ocr", "WorkerOcrEngine.cs"));

        AssertWorkerVerifiedBeforeStart(source, "process.Start()");
    }

    [Fact]
    public void SemanticWorker_VerifiesSignatureBeforeLaunch()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Yagu", "Services", "Ai", "Worker", "WorkerSemanticQueryTranslator.cs"));

        AssertWorkerVerifiedBeforeStart(source, "proc.Start()");
    }

    private static void AssertWorkerVerifiedBeforeStart(string source, string startCall)
    {
        int verifyIndex = source.IndexOf(
            "AuthenticodeVerifier.IsWorkerTrustedForHost", StringComparison.Ordinal);
        int startIndex = source.IndexOf(startCall, StringComparison.Ordinal);

        Assert.True(verifyIndex >= 0, "Worker launch site must call AuthenticodeVerifier.IsWorkerTrustedForHost.");
        Assert.True(startIndex >= 0, $"Worker launch site must call {startCall}.");
        Assert.True(verifyIndex < startIndex,
            "The signature check must run BEFORE the worker process is started.");
    }

    // ── Installer execution sites must verify the signature BEFORE elevating ───────────────────────

    [Fact]
    public void GuiInstaller_VerifiesSignatureBeforeRunAs()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        AssertVerifyBeforeRunAs(source);
    }

    [Fact]
    public void CliInstaller_VerifiesSignatureBeforeRunAs()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        AssertVerifyBeforeRunAs(source);
    }

    [Fact]
    public void EverythingInstallerDownloads_UseHttps()
    {
        // The download URL is built centrally in EverythingAssetPaths.DownloadUrl; assert it is HTTPS
        // and targets the voidtools "Everything-" setup, and that no plaintext-HTTP URL exists.
        // ("http" + "://" dodges the insecure-URL analyzer on this negative assertion.)
        const string insecureVoidtools = "http" + "://www.voidtools.com";
        string assetPaths = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Yagu", "Services", "EverythingAssetPaths.cs"));
        Assert.Contains("https://www.voidtools.com/", assetPaths);
        Assert.Contains("Everything-", assetPaths);
        Assert.DoesNotContain(insecureVoidtools, assetPaths);

        // Both installer sites must obtain the URL from that central helper (never hand-roll an http:// one).
        foreach (string relative in new[]
                 {
                     Path.Combine("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"),
                     Path.Combine("src", "Yagu", "CliRunner.cs"),
                 })
        {
            string source = File.ReadAllText(Path.Combine(FindRepoRoot(), relative));
            Assert.Contains("EverythingAssetPaths.DownloadUrl(", source);
            Assert.DoesNotContain(insecureVoidtools, source);
        }
    }

    private static void AssertVerifyBeforeRunAs(string source)
    {
        int verifyIndex = source.IndexOf(
            "AuthenticodeVerifier.IsTrustedPublisher(installerPath, EverythingAssetPaths.TrustedPublisher",
            StringComparison.Ordinal);
        int runAsIndex = source.IndexOf("\"runas\"", StringComparison.Ordinal);

        Assert.True(verifyIndex >= 0, "Installer site must call AuthenticodeVerifier.IsTrustedPublisher.");
        Assert.True(runAsIndex >= 0, "Installer site must elevate via Verb = \"runas\".");
        Assert.True(verifyIndex < runAsIndex,
            "The signature check must run BEFORE the installer is launched elevated.");
    }

    // ── Azure telemetry Function: correlation-id sanitization + private blob container ─────────────

    [Fact]
    public void TelemetryFunction_SanitizesCorrelationIdBeforeUse()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "cloud", "Yagu.TelemetryFunction", "TelemetryFunctions.cs"));

        // The raw payload value must be routed through the sanitizer, never used verbatim.
        Assert.Contains("SanitizeCorrelationId(payload.CorrelationId)", source);
        Assert.DoesNotContain("correlationId = payload.CorrelationId;", source);

        // The sanitizer must bound length and restrict to a path-safe charset, else mint a fresh id.
        Assert.Contains("MaxCorrelationIdChars", source);
        Assert.Contains("Guid.NewGuid().ToString(\"N\")", source);
    }

    [Fact]
    public void TelemetryFunction_BugReportContainerIsPrivate()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "cloud", "Yagu.TelemetryFunction", "TelemetryFunctions.cs"));

        Assert.Contains("PublicAccessType.None", source);
        Assert.DoesNotContain("PublicAccessType.Blob", source);
        Assert.DoesNotContain("PublicAccessType.Container", source);
    }

    // ── es.exe directory autocomplete: no argument injection via the user-typed prefix ────────────

    [Fact]
    public void DirectoryAutoComplete_BuildsEsExeArgsWithArgumentList_NotRawArgumentString()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Yagu", "Services", "DirectoryAutoCompleteService.cs"));

        // The production process builder must add each argument individually (ArgumentList), so the
        // caller-influenced query is a single escaped argument that cannot inject extra es.exe
        // switches. It must NOT pour an interpolated query into the raw Arguments string.
        Assert.Contains("ArgumentList.Add(query)", source);
        Assert.DoesNotContain("Arguments = arguments", source);
        Assert.DoesNotContain("Arguments = $\"-max-results", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
