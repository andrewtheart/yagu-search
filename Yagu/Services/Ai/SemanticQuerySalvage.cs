using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Yagu.Models;
using Yagu.Services.Ocr;

namespace Yagu.Services.Ai;

/// <summary>
/// Deterministic, model-FREE "best guess" translator. Small on-device models occasionally return prose
/// or no JSON even for trivial queries — e.g. phi-mini reliably fails "jpg files containing the word
/// secret" (a narrow model quirk: "jpg"/"images" + "secret" ~= steganography). When that happens the
/// caller would otherwise drop to a bare literal-text search and lose the obvious intent. This rebuilds
/// the unambiguous parts of the query — file-type globs, a content term, image-OCR intent, hidden-file
/// preference, and a known folder — with the SAME rules the system prompt teaches the model, so the
/// query still resolves to a sensible search. Pure and unit-testable; the caller surfaces
/// "AI couldn't interpret that — using a best guess" when this fires.
/// </summary>
internal static class SemanticQuerySalvage
{
    private static Regex Cue(string alternation) =>
        new($@"\b(?:{alternation})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Multi-extension GROUP words and OFFICE synonyms, mirroring the prompt's FILE GROUPS / OFFICE
    // SYNONYMS rules. Order matters: the specific office kinds precede the generic "documents" group so
    // "word documents" maps to Word formats rather than the broad document set.
    private static readonly (Regex Cue, string[] Globs)[] GroupRules =
    {
        (Cue(@"images?|photos?|pictures?|screenshots?"),            new[] { "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.webp", "*.tiff" }),
        (Cue(@"videos?|movies?"),                                   new[] { "*.mp4", "*.mov", "*.avi", "*.mkv", "*.wmv" }),
        (Cue(@"word\s+docs?|word\s+documents?|ms\s*word|winword"),  new[] { "*.docx", "*.doc" }),
        (Cue(@"excel|spreadsheets?|workbooks?"),                    new[] { "*.xlsx", "*.xls" }),
        (Cue(@"powerpoints?|presentations?|slide\s*decks?"),        new[] { "*.pptx", "*.ppt" }),
        (Cue(@"documents?"),                                        new[] { "*.doc", "*.docx", "*.pdf", "*.txt", "*.rtf", "*.odt" }),
    };

    // Single-extension TYPE words that are NOT literally the extension (the plain-word languages /
    // script kinds the symbol-based LanguageExtensionGlobs deliberately omits).
    private static readonly Dictionary<string, string> TypeWordExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powershell"] = "ps1", ["posh"] = "ps1",
        ["shell"] = "sh", ["bash"] = "sh",
        ["batch"] = "bat",
        ["python"] = "py",
        ["ruby"] = "rb",
        ["rust"] = "rs",
        ["golang"] = "go",
        ["markdown"] = "md",
        ["yaml"] = "yml",
        ["text"] = "txt",
    };

    // "<word> files/scripts/archives/..." — the general single-type filter. Captures the type word.
    private static readonly Regex TypeWordFiles = new(
        @"\b([A-Za-z0-9+#]{1,12})\s+(?:files?|scripts?|documents?|archives?|programs?|sources?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Content term: "... containing/mentioning/with the word X (in them)".
    private static readonly Regex ContentTerm = new(
        @"\b(?:contain(?:s|ing)?|mention(?:s|ing)?|with\s+the\s+words?|has\s+the\s+words?|about|that\s+(?:says?|mentions?))\s+(?:the\s+words?\s+)?[""'\u201C\u2018]?([A-Za-z0-9 _./+#@-]{1,40}?)[""'\u201D\u2019]?(?:\s+in\s+(?:it|them|the\s+files?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Builds a best-guess <see cref="SemanticSearchPlan"/> from <paramref name="query"/> using
    /// only deterministic rules. Returns <c>true</c> when it recovered at least one concrete constraint
    /// (a file-type glob, content term, known folder, or hidden-file preference); <c>false</c> means the
    /// query had nothing unambiguous to salvage and the caller should fall back to a literal search.</summary>
    public static bool TryBuildPlan(string? query, out SemanticSearchPlan plan)
    {
        plan = new SemanticSearchPlan();
        if (string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();

        var globs = new List<string>();
        void Add(string g) { if (!globs.Contains(g, StringComparer.OrdinalIgnoreCase)) globs.Add(g); }

        // 1) Multi-extension groups + office synonyms.
        foreach (var (cue, gs) in GroupRules)
            if (cue.IsMatch(q))
                foreach (var g in gs) Add(g);

        // 2) Symbol-bearing languages (c#, c++, f#, objective-c, js, ts, ruby) via the shared detector.
        foreach (var hit in LanguageExtensionGlobs.FromQuery(q))
            Add("*." + hit.Canonical);

        // 3) "<word> files/scripts/..." -> extension (a type-word synonym OR a known literal extension).
        foreach (Match m in TypeWordFiles.Matches(q))
        {
            string w = m.Groups[1].Value;
            if (TypeWordExtension.TryGetValue(w, out var mapped)) Add("*." + mapped);
            else if (KnownFileExtensions.Default.Contains(w.ToLowerInvariant())) Add("*." + w.ToLowerInvariant());
        }

        // 4) Explicit ".ext files" (reuses the applier's dotted-extension rule).
        SemanticPlanApplier.ApplyExplicitExtensionGlobs(globs, q);

        // 5) Content term.
        string? term = ExtractContentTerm(q);

        // 6) Hidden-file preference + known folder ("my desktop", "downloads", ...).
        bool? hidden = SemanticPlanApplier.DetectHiddenFilePreference(q);
        string? dir = SemanticPlanApplier.TryResolveKnownFolder(q, out var folder) ? folder : null;

        // A salvage is only worth surfacing if it recovered at least one concrete constraint.
        if (globs.Count == 0 && string.IsNullOrEmpty(term) && dir is null && hidden is null)
            return false;

        plan = new SemanticSearchPlan
        {
            IncludeGlobs = globs.Count > 0 ? globs : null,
            Pattern = term,
            SearchMode = string.IsNullOrEmpty(term) ? "filenames" : "content",
            SearchImageText = (!string.IsNullOrEmpty(term) && globs.Any(IsImageGlob)) ? true : null,
            SearchHidden = hidden,
            Directory = dir,
        };
        return true;
    }

    private static string? ExtractContentTerm(string q)
    {
        var m = ContentTerm.Match(q);
        if (!m.Success) return null;
        string t = m.Groups[1].Value.Trim().Trim('"', '\'', '\u201C', '\u201D', '\u2018', '\u2019').Trim();
        return t.Length == 0 ? null : t;
    }

    private static bool IsImageGlob(string glob)
    {
        int dot = glob.LastIndexOf('.');
        return dot >= 0 && ImageOcrSupport.DefaultImageExtensions.Contains(glob[(dot + 1)..].Trim());
    }
}
