namespace Yagu.Services.Ai;

internal static class SemanticFileTypes
{
    private static readonly Dictionary<string, string[]> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = ["*.pdf"], ["png"] = ["*.png"],
        ["c#"] = ["*.cs"], ["c++"] = ["*.cpp"], ["f#"] = ["*.fs"],
        ["javascript"] = ["*.js"], ["typescript"] = ["*.ts"],
        ["python"] = ["*.py"], ["objective-c"] = ["*.m"],
        ["powershell"] = ["*.ps1"], ["shell"] = ["*.sh"], ["bash"] = ["*.sh"],
        ["text"] = ["*.txt"], ["markdown"] = ["*.md"], ["log"] = ["*.log"],
        ["json"] = ["*.json"], ["xml"] = ["*.xml"], ["yaml"] = ["*.yaml"],
        ["csv"] = ["*.csv"], ["zip"] = ["*.zip"], ["executable"] = ["*.exe"],
        ["word"] = ["*.docx", "*.doc"], ["excel"] = ["*.xlsx", "*.xls"],
        ["powerpoint"] = ["*.pptx", "*.ppt"],
        ["document"] = ["*.doc", "*.docx", "*.pdf", "*.txt", "*.rtf", "*.odt"],
        ["image"] = ["*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.webp", "*.tiff"],
        ["video"] = ["*.mp4", "*.mov", "*.avi", "*.mkv", "*.wmv"],
        ["audio"] = ["*.mp3", "*.wav", "*.flac", "*.aac", "*.ogg", "*.m4a", "*.wma"],
        ["config"] = ["*.json", "*.yaml", "*.yml", "*.xml", "*.ini", "*.toml"],
        ["source code"] = ["*.cs", "*.py", "*.js", "*.ts", "*.java", "*.cpp", "*.c", "*.go", "*.rs"],
    };

    internal static bool TryResolve(string? scope, out List<string> globs)
    {
        globs = [];
        if (string.IsNullOrWhiteSpace(scope))
            return false;
        string name = string.Join(" ", scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (name is "file" or "files")
            return true;
        foreach (string suffix in new[] { " files", " file", " scripts", " script", " archives", " archive", " documents", " document", " spreadsheets", " spreadsheet", " presentations", " presentation" })
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            name = name[..^suffix.Length];
            break;
        }
        name = name switch
        {
            "documents" => "document", "images" or "photos" or "pictures" => "image",
            "videos" or "movies" => "video", "logs" => "log", "music" => "audio", "code" => "source code",
            _ => name,
        };
        if (!Types.TryGetValue(name, out var extensions))
            return false;
        globs = [.. extensions];
        return true;
    }
}