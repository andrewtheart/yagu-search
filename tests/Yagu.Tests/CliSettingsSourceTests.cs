using Yagu.Services.Cli;

namespace Yagu.Tests;

/// <summary>
/// Covers <c>--settings-source</c>. The CLI resolves <c>.yagu.json</c> from the working directory first,
/// so a server-launched run must be able to pin the source explicitly rather than inherit whichever
/// workspace it happened to start in.
/// </summary>
public class CliSettingsSourceTests
{
    [Theory]
    [InlineData("global")]
    [InlineData("GLOBAL")]
    [InlineData(" global ")]
    public void TryParse_Global_SelectsTheProfileSettings(string raw)
    {
        Assert.True(CliSettingsSource.TryParse(raw, out var source, out string? error));

        Assert.Null(error);
        Assert.Equal(CliSettingsSourceKind.Global, source.Kind);
        Assert.Null(source.Path);
    }

    [Theory]
    [InlineData(@"file:C:\config\.yagu.json", @"C:\config\.yagu.json")]
    [InlineData(@"FILE:""C:\config\.yagu.json""", @"C:\config\.yagu.json")]
    public void TryParse_FilePrefix_CapturesThePath(string raw, string expected)
    {
        Assert.True(CliSettingsSource.TryParse(raw, out var source, out string? error));

        Assert.Null(error);
        Assert.Equal(CliSettingsSourceKind.File, source.Kind);
        Assert.Equal(expected, source.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("file:")]
    [InlineData("workspace")]
    [InlineData("auto")]
    public void TryParse_UnrecognisedValue_FailsLoudlyInsteadOfDefaulting(string? raw)
    {
        Assert.False(CliSettingsSource.TryParse(raw, out var source, out string? error));

        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(CliSettingsSourceKind.Auto, source.Kind);
    }

    [Fact]
    public void Auto_IsTheHistoricalWorkingDirectoryResolution()
    {
        Assert.Equal(CliSettingsSourceKind.Auto, CliSettingsSource.Auto.Kind);
        Assert.Null(CliSettingsSource.Auto.Path);
    }
}
