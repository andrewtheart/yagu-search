namespace Yagu.Services.Cli;

/// <summary>Which settings file a <c>--cli</c> invocation reads (the <c>--settings-source</c> flag).</summary>
internal enum CliSettingsSourceKind
{
    /// <summary>Historical behavior: working-directory <c>.yagu.json</c> → launch-directory → global AppData.</summary>
    Auto,

    /// <summary>The per-user AppData settings only. Workspace <c>.yagu.json</c> files are ignored.</summary>
    Global,

    /// <summary>An explicit settings file.</summary>
    File,
}

/// <summary>
/// A parsed <c>--settings-source</c> value.
/// </summary>
/// <remarks>
/// The CLI resolves <c>.yagu.json</c> from the <em>current working directory</em> first, so a
/// server/editor-launched invocation would otherwise silently inherit whichever workspace it happened
/// to be started in. Machine callers pin the source explicitly and the CLI reports the file it
/// actually used in a diagnostic record.
/// </remarks>
internal sealed record CliSettingsSource(CliSettingsSourceKind Kind, string? Path)
{
    public static CliSettingsSource Auto { get; } = new(CliSettingsSourceKind.Auto, null);

    /// <summary>
    /// Parses <c>global</c> or <c>file:&lt;path&gt;</c>. Returns false with a human-readable
    /// <paramref name="error"/> for anything else, so an unrecognised value fails loudly instead of
    /// quietly falling back to the working-directory search.
    /// </summary>
    public static bool TryParse(string? raw, out CliSettingsSource source, out string? error)
    {
        source = Auto;
        error = null;

        string value = raw?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "--settings-source requires a value: 'global' or 'file:<path>'.";
            return false;
        }

        if (string.Equals(value, "global", StringComparison.OrdinalIgnoreCase))
        {
            source = new CliSettingsSource(CliSettingsSourceKind.Global, null);
            return true;
        }

        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            string path = value[5..].Trim().Trim('"');
            if (path.Length == 0)
            {
                error = "--settings-source file: requires a path, e.g. file:C:\\config\\.yagu.json";
                return false;
            }

            source = new CliSettingsSource(CliSettingsSourceKind.File, path);
            return true;
        }

        error = $"unrecognised --settings-source '{value}'. Expected 'global' or 'file:<path>'.";
        return false;
    }
}
