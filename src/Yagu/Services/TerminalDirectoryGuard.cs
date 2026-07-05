namespace Yagu.Services;

internal static class TerminalDirectoryGuard
{
    public const string MarkerPrefix = "__YAGU_TERMINAL_CWD_GUARD_";

    public static string CreateMarker()
        => MarkerPrefix + Guid.NewGuid().ToString("N") + "__";

    public static string BuildChangeDirectoryProbeCommand(string directory, string marker, TerminalShellKind shellKind = TerminalShellKind.Cmd)
    {
        if (shellKind == TerminalShellKind.PowerShell)
        {
            string psDirectory = directory.Replace("'", "''", StringComparison.Ordinal);
            return $"Set-Location -LiteralPath '{psDirectory}'; Write-Output '{marker}'";
        }

        string escapedDirectory = directory.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"cd /d \"{escapedDirectory}\" && echo {marker}";
    }

    public static bool TryExtractPromptDirectory(string output, string marker, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(marker))
            return false;

        int markerIndex = output.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        int cursor = markerIndex + marker.Length;
        while (cursor < output.Length && char.IsWhiteSpace(output[cursor]))
            cursor++;

        int promptEnd = output.IndexOf('>', cursor);
        if (promptEnd <= cursor)
            return false;

        directory = StripPowerShellPromptPrefix(output[cursor..promptEnd].Trim());
        return !string.IsNullOrWhiteSpace(directory);
    }

    public static bool DirectoriesEqual(string expectedDirectory, string actualDirectory)
    {
        string expected = NormalizeDirectoryForComparison(expectedDirectory);
        string actual = NormalizeDirectoryForComparison(actualDirectory);
        return !string.IsNullOrEmpty(expected)
            && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    public static string RemoveMarkerLine(string output, string marker)
    {
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(marker))
            return output;

        int markerIndex = output.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return output;

        int lineStart = output.LastIndexOf('\n', markerIndex);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        int lineEnd = output.IndexOf('\n', markerIndex + marker.Length);
        lineEnd = lineEnd < 0 ? markerIndex + marker.Length : lineEnd + 1;

        string before = output[..lineStart];
        string after = output[lineEnd..];
        if (before.Length == 0 && after.Length > 0 && after[0] != '\r' && after[0] != '\n')
            before = "\r\n";

        return before + after;
    }

    /// <summary>Strips the leading "PS " that Windows PowerShell prepends to its prompt. A valid
    /// Windows working directory never begins with "PS ", so this is safe for the cmd path too.</summary>
    private static string StripPowerShellPromptPrefix(string directory)
        => directory.StartsWith("PS ", StringComparison.Ordinal) ? directory[3..].Trim() : directory;

    private static string NormalizeDirectoryForComparison(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return string.Empty;
        try
        {
            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')));
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }
}