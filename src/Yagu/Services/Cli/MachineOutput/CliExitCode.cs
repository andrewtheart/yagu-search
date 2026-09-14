namespace Yagu.Services.Cli.MachineOutput;

/// <summary>
/// The CLI's documented exit-code map. Machine consumers (the MCP bridge) classify an invocation by
/// exit code before parsing a single byte of output, so these values are part of the machine protocol
/// and must not be renumbered.
/// </summary>
/// <remarks>
/// <see cref="NoResults"/> is a <em>success</em> outcome — a search that matched nothing ran correctly.
/// Treating a non-zero exit as "the command failed" is the classic mis-read of this map.
/// </remarks>
internal static class CliExitCode
{
    /// <summary>The command completed and produced at least one result.</summary>
    public const int Success = 0;

    /// <summary>The command completed correctly but matched nothing. Not a failure.</summary>
    public const int NoResults = 1;

    /// <summary>Arguments were missing, malformed, or referenced something that does not exist.</summary>
    public const int InvalidInput = 2;

    /// <summary>Some of the requested work succeeded and some failed; output is incomplete.</summary>
    public const int PartialFailure = 3;

    /// <summary>A conflicting exclusive operation (e.g. an index write) already holds the resource.</summary>
    public const int Busy = 4;

    /// <summary>An unexpected internal failure. The bridge should surface this as a tool error.</summary>
    public const int InternalFailure = 70;

    /// <summary>The command was cancelled (Ctrl+C or a cancelled bridge request).</summary>
    public const int Cancelled = 130;

    /// <summary>True when <paramref name="exitCode"/> means the command ran correctly, with or without results.</summary>
    public static bool IsSuccess(int exitCode) => exitCode is Success or NoResults;

    /// <summary>A stable lowercase token for <paramref name="exitCode"/>, used in machine-mode error records.</summary>
    public static string Describe(int exitCode) => exitCode switch
    {
        Success         => "success",
        NoResults       => "no-results",
        InvalidInput    => "invalid-input",
        PartialFailure  => "partial-failure",
        Busy            => "busy",
        InternalFailure => "internal-failure",
        Cancelled       => "cancelled",
        _               => "unknown",
    };
}
