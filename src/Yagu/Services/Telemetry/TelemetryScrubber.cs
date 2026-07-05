using System.Text.RegularExpressions;

namespace Yagu.Services.Telemetry;

/// <summary>
/// Privacy scrubber for the SILENT telemetry path. Yagu is a file-search tool, so exception messages
/// and free text can contain the user's file paths, drive labels, and (indirectly) search terms.
/// Before any anonymized telemetry leaves the machine, all filesystem paths are redacted to
/// <c>&lt;path&gt;</c> (preserving only a trailing file extension, which is useful and non-identifying).
/// <para>
/// This applies to the automatic telemetry channel only. The explicit, user-reviewed bug-report flow
/// deliberately includes the full settings file and log (the user sees exactly what is sent and opts
/// in per report), so it does not pass through this scrubber.
/// </para>
/// </summary>
internal static partial class TelemetryScrubber
{
    // Drive-letter paths (C:\..., C:/...) and UNC paths (\\server\share\...). Stops at whitespace,
    // quotes, angle brackets, pipes and closing brackets so surrounding text/delimiters survive.
    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|\\\\)[^\s""'<>|)\]]*", RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    /// <summary>Redacts filesystem paths from <paramref name="text"/>. Null/empty returns empty.
    /// A redacted path keeps only its file extension, e.g. <c>C:\Users\jane\q.txt</c> → <c>&lt;path&gt;.txt</c>.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return PathRegex().Replace(text, static m =>
        {
            string match = m.Value;
            int dot = match.LastIndexOf('.');
            int sep = Math.Max(match.LastIndexOf('\\'), match.LastIndexOf('/'));
            // Keep a short, clearly-an-extension suffix (letters/digits only, after the last separator).
            if (dot > sep && dot < match.Length - 1)
            {
                string ext = match[(dot + 1)..];
                if (ext.Length is > 0 and <= 8 && ext.All(char.IsLetterOrDigit))
                    return "<path>." + ext;
            }
            return "<path>";
        });
    }

    /// <summary>A path-scrubbed, non-identifying summary of an exception suitable for the silent
    /// telemetry channel.</summary>
    public readonly record struct ScrubbedException(string Type, string Message, string StackTrace);

    /// <summary>Builds a scrubbed summary of <paramref name="ex"/> (or an empty summary when null).
    /// Includes the inner exception chain types but redacts every message and stack path.</summary>
    public static ScrubbedException Describe(Exception? ex)
    {
        if (ex is null)
            return new ScrubbedException(string.Empty, string.Empty, string.Empty);

        // Type chain (outer→inner) is safe and useful; messages are scrubbed.
        var typeChain = new List<string>();
        for (Exception? cur = ex; cur is not null && typeChain.Count < 8; cur = cur.InnerException)
            typeChain.Add(cur.GetType().FullName ?? cur.GetType().Name);

        return new ScrubbedException(
            string.Join(" -> ", typeChain),
            Scrub(ex.Message),
            Scrub(ex.StackTrace));
    }
}
