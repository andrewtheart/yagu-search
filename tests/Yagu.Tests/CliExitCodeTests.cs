using Yagu.Services.Cli.MachineOutput;

namespace Yagu.Tests;

/// <summary>
/// Pins the documented CLI exit-code map. Machine callers classify an invocation by exit code before
/// parsing any output, so these values are protocol and must not be renumbered.
/// </summary>
public class CliExitCodeTests
{
    [Fact]
    public void ExitCodes_KeepTheirDocumentedValues()
    {
        Assert.Equal(0, CliExitCode.Success);
        Assert.Equal(1, CliExitCode.NoResults);
        Assert.Equal(2, CliExitCode.InvalidInput);
        Assert.Equal(3, CliExitCode.PartialFailure);
        Assert.Equal(4, CliExitCode.Busy);
        Assert.Equal(70, CliExitCode.InternalFailure);
        Assert.Equal(130, CliExitCode.Cancelled);
    }

    [Fact]
    public void NoResults_IsASuccessOutcome_NotAnExecutionFailure()
    {
        Assert.True(CliExitCode.IsSuccess(CliExitCode.Success));
        Assert.True(CliExitCode.IsSuccess(CliExitCode.NoResults));
        Assert.False(CliExitCode.IsSuccess(CliExitCode.InvalidInput));
        Assert.False(CliExitCode.IsSuccess(CliExitCode.Cancelled));
    }

    [Theory]
    [InlineData(0, "success")]
    [InlineData(1, "no-results")]
    [InlineData(2, "invalid-input")]
    [InlineData(3, "partial-failure")]
    [InlineData(4, "busy")]
    [InlineData(70, "internal-failure")]
    [InlineData(130, "cancelled")]
    [InlineData(42, "unknown")]
    public void Describe_ReturnsAStableToken(int exitCode, string expected)
    {
        Assert.Equal(expected, CliExitCode.Describe(exitCode));
    }
}
