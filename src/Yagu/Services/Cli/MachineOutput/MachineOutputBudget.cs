using System.Text;

namespace Yagu.Services.Cli.MachineOutput;

/// <summary>
/// Per-field and whole-response ceilings for machine-mode (<c>--output json</c>) output, measured in
/// <b>UTF-8 bytes at the producer boundary</b>.
/// </summary>
/// <remarks>
/// A match <em>count</em> bounds nothing: a single minified line, OCR block, or archive member name can
/// exceed a whole sensible response on its own. So every variable-length field is bounded independently
/// and carries its own truncation metadata, and the writer additionally stops once the whole response
/// reaches <see cref="MaxResponseBytes"/>. Every value here is a user-visible, persisted setting — see
/// the MCP Server settings tab — so the documented ranges below are the validation contract.
/// </remarks>
internal sealed record MachineOutputBudget
{
    /// <summary>Supported range for <see cref="MaxLineBytes"/> and <see cref="MaxContextLineBytes"/>.</summary>
    public const int MinLineBytes = 256;
    public const int MaxLineBytesCeiling = 1 << 20;         // 1 MiB

    /// <summary>Supported range for <see cref="MaxPathBytes"/>.</summary>
    public const int MinPathBytes = 260;
    public const int MaxPathBytesCeiling = 32 * 1024;

    /// <summary>Supported range for <see cref="MaxContextLines"/>.</summary>
    public const int MinContextLines = 0;
    public const int MaxContextLinesCeiling = 200;

    /// <summary>Supported range for <see cref="MaxResponseBytes"/>.</summary>
    public const long MinResponseBytes = 64 * 1024;
    public const long MaxResponseBytesCeiling = 256L * 1024 * 1024;

    /// <summary>Supported range for <see cref="MaxRecords"/>.</summary>
    public const int MinRecords = 1;
    public const int MaxRecordsCeiling = 1_000_000;

    public int MaxLineBytes { get; init; } = 8 * 1024;
    public int MaxContextLineBytes { get; init; } = 4 * 1024;
    public int MaxPathBytes { get; init; } = 4 * 1024;
    public int MaxContextLines { get; init; } = 20;
    public int MaxRecords { get; init; } = 500;
    public long MaxResponseBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>The shipped defaults, used when no persisted MCP settings are available.</summary>
    public static MachineOutputBudget Default { get; } = new();

    /// <summary>
    /// A budget with every field forced into its supported range. Callers building a budget from
    /// persisted settings go through this so an out-of-range stored value degrades to a usable ceiling
    /// instead of producing an unbounded or zero-length response.
    /// </summary>
    /// <remarks>
    /// The per-field ranges overlap: a 1 MiB line ceiling and a 64 KiB response ceiling are each legal on
    /// their own, but together no single record can ever fit, so every search would refuse its first
    /// match and return nothing. The per-record fields are therefore scaled down to fit the response
    /// ceiling rather than being clamped in isolation.
    /// </remarks>
    public static MachineOutputBudget Create(
        int maxLineBytes,
        int maxContextLineBytes,
        int maxPathBytes,
        int maxContextLines,
        int maxRecords,
        long maxResponseBytes)
    {
        long responseBytes = Math.Clamp(maxResponseBytes, MinResponseBytes, MaxResponseBytesCeiling);

        int lineBytes = Math.Clamp(maxLineBytes, MinLineBytes, MaxLineBytesCeiling);
        int contextLineBytes = Math.Clamp(maxContextLineBytes, MinLineBytes, MaxLineBytesCeiling);
        int pathBytes = Math.Clamp(maxPathBytes, MinPathBytes, MaxPathBytesCeiling);
        int contextLines = Math.Clamp(maxContextLines, MinContextLines, MaxContextLinesCeiling);

        // Half the response, so at least two records can land before the ceiling stops the stream.
        // JSON escaping and the envelope add to this, which the writer's own fit-check still catches.
        long perRecordAllowance = responseBytes / 2;

        while (LargestRecordBytes(lineBytes, contextLineBytes, pathBytes, contextLines) > perRecordAllowance)
        {
            if (contextLines > MinContextLines) { contextLines = 0; continue; }
            if (lineBytes > MinLineBytes) { lineBytes = Math.Max(MinLineBytes, lineBytes / 2); continue; }
            if (contextLineBytes > MinLineBytes) { contextLineBytes = Math.Max(MinLineBytes, contextLineBytes / 2); continue; }
            pathBytes = Math.Max(MinPathBytes, pathBytes / 2);
        }

        return new()
        {
            MaxLineBytes        = lineBytes,
            MaxContextLineBytes = contextLineBytes,
            MaxPathBytes        = pathBytes,
            MaxContextLines     = contextLines,
            MaxRecords          = Math.Clamp(maxRecords, MinRecords, MaxRecordsCeiling),
            MaxResponseBytes    = responseBytes,
        };
    }

    private static long LargestRecordBytes(int lineBytes, int contextLineBytes, int pathBytes, int contextLines)
        => (long)lineBytes + pathBytes + ((long)contextLineBytes * contextLines);

    /// <summary>
    /// The next smaller set of per-field ceilings, or null once every field is already at its floor.
    /// </summary>
    /// <remarks>
    /// <see cref="Create"/> sizes records against the RAW source bytes, but the wire carries JSON: a
    /// line of <c>&lt;</c> or <c>"</c> characters escapes to roughly six bytes each, so a record sized to
    /// fit can still be refused by the response ceiling. The writer walks this ladder rather than
    /// dropping such a record, because dropping the FIRST one reports "no results" for a search that
    /// genuinely matched. Fields are given up worst-value-first: context lines, then the match line,
    /// then context width, and the path last because a shortened path is the least useful of the four.
    /// </remarks>
    public MachineOutputBudget? Shrink()
    {
        if (MaxContextLines > MinContextLines)
            return this with { MaxContextLines = MinContextLines };

        if (MaxLineBytes > MinLineBytes)
            return this with { MaxLineBytes = Math.Max(MinLineBytes, MaxLineBytes / 2) };

        if (MaxContextLineBytes > MinLineBytes)
            return this with { MaxContextLineBytes = Math.Max(MinLineBytes, MaxContextLineBytes / 2) };

        if (MaxPathBytes > MinPathBytes)
            return this with { MaxPathBytes = Math.Max(MinPathBytes, MaxPathBytes / 2) };

        return null;
    }

    /// <summary>
    /// Trims <paramref name="value"/> to at most <paramref name="maxBytes"/> UTF-8 bytes without
    /// splitting a code point (or a surrogate pair), reporting whether anything was dropped and how
    /// large the original was so a client can distinguish truncated text from complete text.
    /// </summary>
    public static string TruncateUtf8(string? value, int maxBytes, out bool truncated, out int originalByteCount)
    {
        value ??= string.Empty;
        originalByteCount = Encoding.UTF8.GetByteCount(value);
        truncated = false;

        if (maxBytes <= 0 || originalByteCount <= maxBytes)
            return value;

        truncated = true;

        int bytes = 0;
        int i = 0;
        while (i < value.Length)
        {
            bool surrogatePair = char.IsHighSurrogate(value[i])
                && i + 1 < value.Length
                && char.IsLowSurrogate(value[i + 1]);

            int charCount = surrogatePair ? 2 : 1;
            int codePointBytes = surrogatePair ? 4 : Utf8ByteCount(value[i]);

            if (bytes + codePointBytes > maxBytes)
                break;

            bytes += codePointBytes;
            i += charCount;
        }

        return value[..i];
    }

    // A lone surrogate is transcoded to U+FFFD (3 bytes) by System.Text.Encoding.UTF8, so it costs the
    // same as any other BMP code point here.
    private static int Utf8ByteCount(char c) => c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
}
