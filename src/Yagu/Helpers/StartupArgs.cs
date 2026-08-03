namespace Yagu.Helpers;

/// <summary>
/// Pure parsing of the GUI process's startup command line. Kept free of any WinUI/Win32
/// dependency so it can be unit-tested directly (it is compiled into Yagu.Tests).
/// </summary>
internal static class StartupArgs
{
    // Flags that consume the following token as their value. When scanning for a bare positional
    // directory we must skip both the flag and its value so e.g. `--query C:\foo` never mistakes the
    // query value for a search directory.
    private static readonly string[] ValueFlags =
    {
        "--dir",
        "--query",
        "--window-mode",
        "--windowing-mode",
        "--window-focus-behavior",
        "--wait-for-pid",
    };

    /// <summary>Reads a <c>--name value</c> or <c>--name=value</c> argument (case-insensitive),
    /// trimming surrounding quotes. Returns null when the flag is absent.</summary>
    public static string? ParseStringArg(string[]? args, string name)
    {
        if (args is null || string.IsNullOrEmpty(name)) return null;
        var prefix = name + "=";
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is null) continue;
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim().Trim('"');
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return a[prefix.Length..].Trim().Trim('"');
        }
        return null;
    }

    /// <summary>Resolves the startup search directory: an explicit <c>--dir</c> when present,
    /// otherwise the first bare positional argument that is an existing directory. The Explorer
    /// "Search with Yagu" context menu launches <c>Yagu.exe "%1"</c> / <c>Yagu.exe "%V"</c> with the
    /// folder as a bare argument, so the positional fallback is what makes that populate the box.</summary>
    public static string? ParseDirectory(string[]? args)
    {
        var explicitDir = ParseStringArg(args, "--dir");
        if (!string.IsNullOrWhiteSpace(explicitDir)) return explicitDir;
        return ParsePositionalDirectory(args);
    }

    /// <summary>Returns the first bare (non-flag) argument that names an existing directory, or null.
    /// Only directories match — never files — so the executable path that Windows places at
    /// <c>Environment.GetCommandLineArgs()[0]</c> is never mistaken for a search folder.</summary>
    public static string? ParsePositionalDirectory(string[]? args)
    {
        if (args is null) return null;
        for (int i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // Skip flags. For value-taking flags, also skip their following value token.
            if (raw.StartsWith('-') || raw.StartsWith('/'))
            {
                if (Array.Exists(ValueFlags, f => string.Equals(raw, f, StringComparison.OrdinalIgnoreCase)))
                    i++;
                continue;
            }

            var candidate = raw.Trim().Trim('"');
            if (candidate.Length == 0) continue;

            try
            {
                if (System.IO.Directory.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Malformed path (invalid characters, too long, etc.) — keep scanning.
            }
        }
        return null;
    }
}
