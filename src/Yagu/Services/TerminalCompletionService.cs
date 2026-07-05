namespace Yagu.Services;

internal static class TerminalCompletionService
{
    private static readonly string[] BuiltInCommands =
    [
        "assoc", "break", "call", "cd", "chcp", "chdir", "cls", "color", "copy", "date",
        "del", "dir", "echo", "endlocal", "erase", "exit", "for", "ftype", "goto", "if",
        "md", "mkdir", "mklink", "move", "path", "pause", "popd", "prompt", "pushd", "rd",
        "rem", "ren", "rename", "rmdir", "set", "setlocal", "shift", "start", "time", "title",
        "type", "ver", "verify", "vol",
    ];

    // Common Windows PowerShell cmdlets and aliases so tab-completion in command position offers
    // PowerShell-native names (e.g. Get-ChildItem, cat, ls) in addition to PATH executables.
    private static readonly string[] PowerShellCommands =
    [
        "cat", "cd", "chdir", "clc", "clear", "cls", "copy", "cp", "cpi", "del", "dir", "echo",
        "erase", "exit", "foreach", "gc", "gci", "gcm", "gm", "gp", "gps", "group", "gsv",
        "Get-ChildItem", "Get-Command", "Get-Content", "Get-Help", "Get-Item", "Get-ItemProperty",
        "Get-Location", "Get-Member", "Get-Process", "Get-Service", "gl", "history", "iex", "ii",
        "Invoke-Expression", "Invoke-WebRequest", "iwr", "kill", "ls", "man", "md", "measure",
        "mkdir", "move", "mv", "New-Item", "ni", "Out-File", "Out-String", "popd", "pushd", "pwd",
        "rd", "rderase", "Remove-Item", "ren", "Rename-Item", "ri", "rm", "rmdir", "rni", "rvpa",
        "sajb", "sc", "Select-Object", "select", "Select-String", "Set-Content", "Set-Item",
        "Set-Location", "sl", "sls", "sort", "Sort-Object", "sp", "start", "sv", "tee", "type",
        "where", "Where-Object", "Write-Host", "Write-Output",
    ];

    private static readonly string[] ExecutableExtensions = [".exe", ".cmd", ".bat", ".com", ".ps1"];

    public sealed record Result(
        int RequestId,
        int ReplacementStart,
        int ReplacementLength,
        string ReplacementText,
        IReadOnlyList<string> Suggestions,
        bool HasMatches);

    private sealed record TokenInfo(int Start, int Length, string Text, char Quote, bool IsCommandPosition);

    private sealed record Candidate(string InsertText, string DisplayText, bool IsDirectory);

    public static Result Complete(int requestId, string input, int cursor, string promptText, string fallbackWorkingDirectory, TerminalShellKind shellKind = TerminalShellKind.Cmd)
    {
        input ??= string.Empty;
        cursor = Math.Clamp(cursor, 0, input.Length);
        var token = FindToken(input, cursor);
        string workingDirectory = ResolvePromptWorkingDirectory(promptText, fallbackWorkingDirectory);
        var candidates = token.IsCommandPosition && !LooksLikePathToken(token.Text)
            ? CompleteCommandToken(token, workingDirectory, shellKind)
            : CompletePathToken(token, workingDirectory);

        if (candidates.Count == 0)
            return new Result(requestId, token.Start, token.Length, string.Empty, [], HasMatches: false);

        string replacement = ChooseReplacement(token, candidates);
        return new Result(
            requestId,
            token.Start,
            token.Length,
            replacement,
            candidates.Select(c => c.DisplayText).Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToArray(),
            HasMatches: true);
    }

    private static TokenInfo FindToken(string input, int cursor)
    {
        int start = cursor;
        char quote = '\0';
        bool inQuote = false;

        for (int i = cursor - 1; i >= 0; i--)
        {
            char c = input[i];
            if (c is '"' or '\'')
            {
                quote = c;
                start = i + 1;
                inQuote = true;
                break;
            }

            if (char.IsWhiteSpace(c) || c is '&' or '|' or '<' or '>')
                break;

            start = i;
        }

        if (!inQuote)
            quote = '\0';

        string beforeToken = input[..start].TrimStart();
        bool commandPosition = beforeToken.Length == 0 || beforeToken.EndsWith('&') || beforeToken.EndsWith('|');
        return new TokenInfo(start, cursor - start, input[start..cursor], quote, commandPosition);
    }

    private static List<Candidate> CompleteCommandToken(TokenInfo token, string workingDirectory, TerminalShellKind shellKind)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        AddCommandCandidates(candidates, token.Text, shellKind);
        AddExecutableCandidatesFromDirectory(candidates, workingDirectory, token.Text);

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddExecutableCandidatesFromDirectory(candidates, directory, token.Text);
        }

        return candidates.Values
            .OrderBy(c => c.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
    }

    private static void AddCommandCandidates(Dictionary<string, Candidate> candidates, string prefix, TerminalShellKind shellKind)
    {
        string[] commands = shellKind == TerminalShellKind.PowerShell ? PowerShellCommands : BuiltInCommands;
        foreach (string command in commands)
        {
            if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            candidates.TryAdd(command, new Candidate(command + " ", command, IsDirectory: false));
        }
    }

    private static void AddExecutableCandidatesFromDirectory(Dictionary<string, Candidate> candidates, string directory, string prefix)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string extension = Path.GetExtension(file);
                if (!ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                candidates.TryAdd(name, new Candidate(QuoteIfNeeded(name) + " ", name, IsDirectory: false));
            }
        }
        catch
        {
        }
    }

    private static List<Candidate> CompletePathToken(TokenInfo token, string workingDirectory)
    {
        string unescapedToken = token.Text.Trim();
        string directoryPart = ExtractDirectoryPart(unescapedToken);
        string namePrefix = unescapedToken[directoryPart.Length..];
        string searchDirectory = ResolveSearchDirectory(workingDirectory, directoryPart);
        if (string.IsNullOrWhiteSpace(searchDirectory) || !Directory.Exists(searchDirectory))
            return [];

        var candidates = new List<Candidate>();
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(searchDirectory))
            {
                string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(CreatePathCandidate(token, directoryPart, name, isDirectory: true));
            }

            foreach (string file in Directory.EnumerateFiles(searchDirectory))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(CreatePathCandidate(token, directoryPart, name, isDirectory: false));
            }
        }
        catch
        {
        }

        return candidates
            .OrderByDescending(c => c.IsDirectory)
            .ThenBy(c => c.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
    }

    private static Candidate CreatePathCandidate(TokenInfo token, string directoryPart, string name, bool isDirectory)
    {
        string pathText = directoryPart + name + (isDirectory ? Path.DirectorySeparatorChar : string.Empty);
        string insertText = token.Quote == '\0'
            ? QuoteIfNeeded(pathText)
            : pathText;
        return new Candidate(insertText, pathText, isDirectory);
    }

    private static string ChooseReplacement(TokenInfo token, IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 1)
            return candidates[0].InsertText;

        string commonPrefix = GetCommonPrefix(candidates.Select(c => c.InsertText));
        return commonPrefix.Length > token.Text.Length
            ? commonPrefix
            : token.Text;
    }

    private static string GetCommonPrefix(IEnumerable<string> values)
    {
        string? prefix = null;
        foreach (string value in values)
        {
            if (prefix is null)
            {
                prefix = value;
                continue;
            }

            int length = Math.Min(prefix.Length, value.Length);
            int i = 0;
            for (; i < length; i++)
            {
                if (char.ToUpperInvariant(prefix[i]) != char.ToUpperInvariant(value[i]))
                    break;
            }

            prefix = prefix[..i];
        }

        return prefix ?? string.Empty;
    }

    private static string ExtractDirectoryPart(string token)
    {
        int slash = Math.Max(token.LastIndexOf('\\'), token.LastIndexOf('/'));
        return slash >= 0 ? token[..(slash + 1)] : string.Empty;
    }

    private static bool LooksLikePathToken(string token)
          => token.Length > 0 && token[0] == '.'
              || token.Length > 0 && token[0] == '~'
           || token.Contains('\\')
           || token.Contains('/')
           || Path.IsPathRooted(token);

    private static string ResolveSearchDirectory(string workingDirectory, string directoryPart)
    {
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(directoryPart);
            if (string.IsNullOrEmpty(expanded))
                return workingDirectory;

            if (Path.IsPathRooted(expanded))
                return Path.GetFullPath(expanded);

            return Path.GetFullPath(Path.Combine(workingDirectory, expanded));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolvePromptWorkingDirectory(string promptText, string fallbackWorkingDirectory)
    {
        string? candidate = TryExtractPromptWorkingDirectory(promptText);
        if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            return candidate;

        return Directory.Exists(fallbackWorkingDirectory) ? fallbackWorkingDirectory : AppContext.BaseDirectory;
    }

    private static string? TryExtractPromptWorkingDirectory(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            return null;

        string line = promptText.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
        if (!line.EndsWith('>'))
            return null;

        string directory = line[..^1].Trim();
        // Windows PowerShell prefixes its prompt with "PS "; a valid working directory never
        // starts with "PS ", so stripping it is safe for the cmd path too.
        if (directory.StartsWith("PS ", StringComparison.Ordinal))
            directory = directory[3..].Trim();
        return directory.Length == 0 ? null : directory;
    }

    private static string QuoteIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOfAny([' ', '&', '(', ')', '[', ']', '{', '}', '^', '=', ';', '!', '\'', '+', ',', '`', '~']) < 0)
            return value;

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}