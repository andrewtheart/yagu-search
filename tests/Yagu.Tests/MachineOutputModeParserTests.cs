using Yagu.Services.Cli.MachineOutput;

namespace Yagu.Tests;

/// <summary>
/// Covers the <c>--output</c> flag parser. It deliberately rejects an unrecognised value instead of
/// defaulting to text: a consumer that asked for JSON and silently received grep output would parse
/// garbage, so the failure has to surface as invalid input at the CLI boundary.
/// </summary>
public class MachineOutputModeParserTests
{
    [Theory]
    [InlineData("grep")]
    [InlineData("text")]
    public void TryParse_TextAliases_SelectGrep(string raw)
    {
        Assert.True(MachineOutputModeParser.TryParse(raw, out MachineOutputMode mode));
        Assert.Equal(MachineOutputMode.Grep, mode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("ndjson")]
    public void TryParse_JsonAliases_SelectJson(string raw)
    {
        Assert.True(MachineOutputModeParser.TryParse(raw, out MachineOutputMode mode));
        Assert.Equal(MachineOutputMode.Json, mode);
    }

    [Theory]
    [InlineData("JSON", true)]
    [InlineData("NdJson", true)]
    [InlineData("GREP", false)]
    [InlineData("Text", false)]
    public void TryParse_IsCaseInsensitive(string raw, bool expectJson)
    {
        // The mode enum is internal, so the discriminator has to be a public type here.
        Assert.True(MachineOutputModeParser.TryParse(raw, out MachineOutputMode mode));
        Assert.Equal(expectJson ? MachineOutputMode.Json : MachineOutputMode.Grep, mode);
    }

    [Theory]
    [InlineData("  json  ")]
    [InlineData("\tjson\n")]
    public void TryParse_SurroundingWhitespace_IsIgnored(string raw)
    {
        Assert.True(MachineOutputModeParser.TryParse(raw, out MachineOutputMode mode));
        Assert.Equal(MachineOutputMode.Json, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_MissingValue_IsRejected(string? raw)
    {
        Assert.False(MachineOutputModeParser.TryParse(raw, out MachineOutputMode mode));
        Assert.Equal(MachineOutputMode.Grep, mode);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("yaml")]
    [InlineData("jsonl")]
    [InlineData("js on")]
    public void TryParse_UnknownValue_IsRejectedRatherThanDefaultingToText(string raw)
    {
        Assert.False(MachineOutputModeParser.TryParse(raw, out MachineOutputMode mode));

        // The out value must still be usable: a caller that ignores the bool gets text, never a
        // half-initialised mode.
        Assert.Equal(MachineOutputMode.Grep, mode);
    }
}
