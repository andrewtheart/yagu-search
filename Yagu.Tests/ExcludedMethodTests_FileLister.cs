using System.Diagnostics;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Tests for FileLister admin path logic, elevation check, SetKnownTotalFiles,
/// FindEsExe, WaitForEverythingSdkReady, RealProcess.
/// </summary>
public sealed class ExcludedMethodTests_FileLister : IDisposable
{
    private readonly Func<bool>? _originalElevationOverride;

    public ExcludedMethodTests_FileLister()
    {
        _originalElevationOverride = FileLister.ElevationOverride;
    }

    public void Dispose()
    {
        FileLister.ElevationOverride = _originalElevationOverride;
    }

    [Fact]
    public void CheckIsElevated_WithOverride_ReturnsOverrideValue()
    {
        FileLister.ElevationOverride = () => true;
        Assert.True(FileLister.CheckIsElevated());

        FileLister.ElevationOverride = () => false;
        Assert.False(FileLister.CheckIsElevated());
    }

    [Fact]
    public void CheckIsElevated_WithoutOverride_ReturnsBoolean()
    {
        FileLister.ElevationOverride = null;
        // Just verify it doesn't throw; value depends on test runner elevation
        _ = FileLister.CheckIsElevated();
    }

    [Fact]
    public void NormalizeAdminSegment_Null_ReturnsNull()
    {
        Assert.Null(FileLister.NormalizeAdminSegment(""));
        Assert.Null(FileLister.NormalizeAdminSegment("   "));
    }

    [Fact]
    public void NormalizeAdminSegment_AddsLeadingBackslash()
    {
        Assert.Equal(@"\Windows", FileLister.NormalizeAdminSegment("Windows"));
    }

    [Fact]
    public void NormalizeAdminSegment_TrimsTrailingBackslash()
    {
        Assert.Equal(@"\Windows\System32", FileLister.NormalizeAdminSegment(@"\Windows\System32\"));
    }

    [Fact]
    public void NormalizeAdminSegment_NormalizesForwardSlash()
    {
        Assert.Equal(@"\Temp\Sub", FileLister.NormalizeAdminSegment("/Temp/Sub"));
    }

    [Fact]
    public void NormalizeAdminSegment_JustSlash_ReturnsNull()
    {
        Assert.Null(FileLister.NormalizeAdminSegment(@"\"));
        Assert.Null(FileLister.NormalizeAdminSegment("/"));
    }

    [Fact]
    public void IsAdminProtectedPath_MatchesDefaultSegments()
    {
        var lister = new FileLister
        {
            ExcludeAdminProtectedPaths = true
        };
        Assert.True(lister.IsAdminProtectedPath(@"C:\Windows\System32\config"));
        Assert.True(lister.IsAdminProtectedPath(@"C:\$Recycle.Bin"));
        Assert.True(lister.IsAdminProtectedPath(@"C:\System Volume Information"));
    }

    [Fact]
    public void IsAdminProtectedPath_NoMatchForNormalPaths()
    {
        var lister = new FileLister
        {
            ExcludeAdminProtectedPaths = true
        };
        Assert.False(lister.IsAdminProtectedPath(@"C:\Users\test\Documents"));
        Assert.False(lister.IsAdminProtectedPath(@"D:\Projects\MyApp"));
    }

    [Fact]
    public void IsAdminProtectedPath_UsesOverrideWhenSet()
    {
        var lister = new FileLister
        {
            ExcludeAdminProtectedPaths = true,
            AdminProtectedPathSegmentsOverride = new[] { @"\MyCustom\Path" }
        };
        Assert.True(lister.IsAdminProtectedPath(@"C:\MyCustom\Path"));
        Assert.False(lister.IsAdminProtectedPath(@"C:\Windows\System32\config")); // default not used
    }

    [Fact]
    public void IsAdminProtectedPath_SegmentInMiddleOfPath()
    {
        var lister = new FileLister();
        Assert.True(lister.IsAdminProtectedPath(@"C:\Windows\System32\config\systemprofile"));
    }

    [Fact]
    public void EffectiveAdminProtectedPathSegments_ReturnsDefault_WhenNoOverride()
    {
        var lister = new FileLister();
        Assert.Same(FileLister.DefaultAdminProtectedPathSegments, lister.EffectiveAdminProtectedPathSegments);
    }

    [Fact]
    public void EffectiveAdminProtectedPathSegments_ReturnsOverride_WhenSet()
    {
        var custom = new[] { @"\Custom" };
        var lister = new FileLister { AdminProtectedPathSegmentsOverride = custom };
        Assert.Same(custom, lister.EffectiveAdminProtectedPathSegments);
    }

    [Fact]
    public void EffectiveAdminProtectedPathSegments_ReturnsDefault_WhenOverrideEmpty()
    {
        var lister = new FileLister { AdminProtectedPathSegmentsOverride = Array.Empty<string>() };
        Assert.Same(FileLister.DefaultAdminProtectedPathSegments, lister.EffectiveAdminProtectedPathSegments);
    }

    [Fact]
    public void ShouldExcludeAdminPaths_False_WhenExcludeAdminPathsDisabled()
    {
        var lister = new FileLister { ExcludeAdminProtectedPaths = false };
        Assert.False(lister.ShouldExcludeAdminPaths);
    }

    [Fact]
    public void SetKnownTotalFiles_UInt_ClampsToIntMax()
    {
        var lister = new FileLister();
        lister.SetKnownTotalFiles((uint)42);
        Assert.Equal(42, lister.KnownTotalFiles);
    }

    [Fact]
    public void SetKnownTotalFiles_UInt_MaxValue_ClampsToIntMax()
    {
        var lister = new FileLister();
        lister.SetKnownTotalFiles(uint.MaxValue);
        Assert.Equal(int.MaxValue, lister.KnownTotalFiles);
    }

    [Fact]
    public void FindEsExe_Public_DoesNotThrow()
    {
        // The public overload uses real File.Exists; just verify it doesn't throw
        _ = FileLister.FindEsExe();
    }

    [Fact]
    public async Task WaitForEverythingSdkReady_ImmediateReady_ReturnsReady()
    {
        var result = await FileLister.WaitForEverythingSdkReadyAsync(
            () => EverythingReadinessResult.Ready(100, 100, Array.Empty<string>()),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        Assert.True(result.IsReady);
    }

    [Fact]
    public async Task WaitForEverythingSdkReady_NeverReady_TimesOut()
    {
        var result = await FileLister.WaitForEverythingSdkReadyAsync(
            () => EverythingReadinessResult.NotReady("still loading"),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);
        Assert.False(result.IsReady);
        Assert.Contains("Timed out", result.Error);
    }

    [Fact]
    public async Task WaitForEverythingSdkReady_CancellationThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FileLister.WaitForEverythingSdkReadyAsync(
                () => EverythingReadinessResult.NotReady("x"),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(100),
                cts.Token));
    }

    [Fact]
    public async Task RealProcess_CanBeInstantiated()
    {
        // RealProcess is a thin wrapper - verify it can be constructed
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo hello",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        var proc = new FileLister.RealProcess(psi);
        proc.Start();
        var line = await proc.ReadLineAsync(CancellationToken.None);
        await proc.WaitForExitAsync(CancellationToken.None);
        Assert.Equal("hello", line);
        Assert.Equal(0, proc.ExitCode);
    }
}
