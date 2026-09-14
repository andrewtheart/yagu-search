namespace Yagu.Services.Cli.MachineOutput;

/// <summary>Selects how a <c>--cli</c> invocation renders its stdout (the <c>--output</c> flag).</summary>
internal enum MachineOutputMode
{
    /// <summary>Human/grep-style text. The default, byte-identical to Yagu's historical output.</summary>
    Grep,

    /// <summary>One JSON envelope per line (NDJSON), described by <see cref="MachineOutputWriter"/>.</summary>
    Json,
}

/// <summary>Parses the <c>--output</c> flag value.</summary>
internal static class MachineOutputModeParser
{
    /// <summary>
    /// Maps <paramref name="raw"/> to a mode. Returns false for an unrecognised value so the caller can
    /// fail with <see cref="CliExitCode.InvalidInput"/> rather than silently defaulting to text.
    /// </summary>
    public static bool TryParse(string? raw, out MachineOutputMode mode)
    {
        mode = MachineOutputMode.Grep;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "grep":
            case "text":
                mode = MachineOutputMode.Grep;
                return true;
            case "json":
            case "ndjson":
                mode = MachineOutputMode.Json;
                return true;
            default:
                return false;
        }
    }
}
