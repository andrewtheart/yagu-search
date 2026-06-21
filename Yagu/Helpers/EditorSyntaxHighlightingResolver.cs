using System;
using System.Collections.Generic;
using System.IO;

namespace Yagu.Helpers;

/// <summary>
/// Language identifiers understood by <see cref="EditorSyntaxHighlightingResolver"/>.
/// This mirrors the subset of the vendored TextControlBox editor's
/// <c>SyntaxHighlightID</c> values that Yagu maps file names onto. It is kept
/// independent of the TextControlBox (WindowsAppSDK) assembly so the mapping
/// logic stays unit-testable in the headless engine test project; the app-side
/// editor code translates these into the editor's own enum.
/// </summary>
public enum EditorSyntaxLanguage
{
    Batch,
    Cpp,
    CSharp,
    Inifile,
    Toml,
    CSS,
    CSVImproved,
    GCode,
    Gitignore,
    HexFile,
    Html,
    Java,
    Javascript,
    Json,
    Latex,
    Lua,
    Markdown,
    PHP,
    Python,
    QSharp,
    XML,
    SQL,
    X86Assembly,
}

/// <summary>
/// Maps a file name (by extension or well-known name) to the matching
/// <see cref="EditorSyntaxLanguage"/>. The extension table is derived from the
/// vendored TextControlBox language definitions plus a small set of curated
/// overrides for ambiguous or common-alias extensions.
/// </summary>
internal static class EditorSyntaxHighlightingResolver
{
    private static readonly Dictionary<string, EditorSyntaxLanguage> ExtensionMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Batch
            [".bat"] = EditorSyntaxLanguage.Batch,
            [".cmd"] = EditorSyntaxLanguage.Batch, // common alias

            // Config
            [".ini"] = EditorSyntaxLanguage.Inifile,
            [".cfg"] = EditorSyntaxLanguage.Inifile, // prefer INI over niche Klipper
            [".conf"] = EditorSyntaxLanguage.Inifile,
            [".toml"] = EditorSyntaxLanguage.Toml,

            // C / C++
            [".cpp"] = EditorSyntaxLanguage.Cpp,
            [".cxx"] = EditorSyntaxLanguage.Cpp,
            [".cc"] = EditorSyntaxLanguage.Cpp,
            [".hpp"] = EditorSyntaxLanguage.Cpp,
            [".h"] = EditorSyntaxLanguage.Cpp,
            [".c"] = EditorSyntaxLanguage.Cpp,

            // C#
            [".cs"] = EditorSyntaxLanguage.CSharp,

            // GCode
            [".ngc"] = EditorSyntaxLanguage.GCode,
            [".tap"] = EditorSyntaxLanguage.GCode,
            [".gcode"] = EditorSyntaxLanguage.GCode,
            [".nc"] = EditorSyntaxLanguage.GCode,
            [".cnc"] = EditorSyntaxLanguage.GCode,

            // Assembly
            [".asm"] = EditorSyntaxLanguage.X86Assembly,

            // Hex (note: .bin omitted — ordinary binaries should not be highlighted)
            [".hex"] = EditorSyntaxLanguage.HexFile,

            // Web markup / scripting
            [".html"] = EditorSyntaxLanguage.Html,
            [".htm"] = EditorSyntaxLanguage.Html,
            [".js"] = EditorSyntaxLanguage.Javascript,
            [".ts"] = EditorSyntaxLanguage.Javascript, // close enough lexically
            [".jsx"] = EditorSyntaxLanguage.Javascript,
            [".tsx"] = EditorSyntaxLanguage.Javascript,
            [".mjs"] = EditorSyntaxLanguage.Javascript,
            [".cjs"] = EditorSyntaxLanguage.Javascript,
            [".json"] = EditorSyntaxLanguage.Json,
            [".php"] = EditorSyntaxLanguage.PHP,
            [".css"] = EditorSyntaxLanguage.CSS,
            [".scss"] = EditorSyntaxLanguage.CSS,

            // Java (note: .class omitted — compiled bytecode is binary)
            [".java"] = EditorSyntaxLanguage.Java,

            // Q#
            [".qs"] = EditorSyntaxLanguage.QSharp,

            // XML family
            [".xml"] = EditorSyntaxLanguage.XML,
            [".xaml"] = EditorSyntaxLanguage.XML,

            // Python
            [".py"] = EditorSyntaxLanguage.Python,

            // CSV (prefer enhanced highlighter)
            [".csv"] = EditorSyntaxLanguage.CSVImproved,

            // LaTeX
            [".latex"] = EditorSyntaxLanguage.Latex,
            [".tex"] = EditorSyntaxLanguage.Latex,

            // Markdown
            [".md"] = EditorSyntaxLanguage.Markdown,
            [".markdown"] = EditorSyntaxLanguage.Markdown,

            // SQL / Lua
            [".sql"] = EditorSyntaxLanguage.SQL,
            [".lua"] = EditorSyntaxLanguage.Lua,
        };

    /// <summary>
    /// Resolves an <see cref="EditorSyntaxLanguage"/> from a file path or name.
    /// Returns <c>null</c> when no highlighter matches (caller should disable
    /// syntax highlighting in that case).
    /// </summary>
    public static EditorSyntaxLanguage? ResolveFromFileName(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        string name;
        try
        {
            name = Path.GetFileName(filePath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(name))
            return null;

        // Well-known dotfiles whose whole name is the "extension".
        if (string.Equals(name, ".gitignore", StringComparison.OrdinalIgnoreCase))
            return EditorSyntaxLanguage.Gitignore;

        string ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext))
            return null;

        return ExtensionMap.TryGetValue(ext, out var language) ? language : null;
    }
}
