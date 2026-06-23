using System;
using System.Collections.Generic;
using Yagu.Models;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="ExcludedExtensionPredictor"/> — the pure prediction of "you searched for a
/// file whose extension is currently excluded by an advanced option". Locks in that warnings fire only
/// when the trailing extension token is actually excluded, and never for regex/Content-only searches.
/// </summary>
public sealed class ExcludedExtensionPredictorTests
{
    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    private static ExcludedExtensionWarning? Predict(
        string query,
        bool useRegex = false,
        bool exactMatch = true,
        SearchMode mode = SearchMode.FileNames,
        IReadOnlySet<string>? skip = null,
        IReadOnlySet<string>? binary = null,
        string includeGlobs = "",
        FilterPatternMode includeMode = FilterPatternMode.GlobPath,
        string excludeGlobs = "",
        FilterPatternMode excludeMode = FilterPatternMode.GlobPath)
        => ExcludedExtensionPredictor.Predict(
            query, useRegex, exactMatch, mode,
            skip ?? Set(), binary ?? Set(),
            includeGlobs, includeMode, excludeGlobs, excludeMode);

    [Fact]
    public void Predict_BinaryExtension_Warns()
    {
        var result = Predict("Memory Setup 0.3.42.exe", binary: Set("exe", "dll"));
        Assert.NotNull(result);
        Assert.Equal("exe", result!.Extension);
        Assert.Equal(ExtensionExclusionReason.BinaryExtensions, result.Reasons);
    }

    [Fact]
    public void Predict_SkipExtension_Warns()
    {
        var result = Predict("logfile.tmp", skip: Set("tmp", "bak"));
        Assert.NotNull(result);
        Assert.Equal("tmp", result!.Extension);
        Assert.Equal(ExtensionExclusionReason.SkipExtensions, result.Reasons);
    }

    [Fact]
    public void Predict_ExtensionNotExcluded_ReturnsNull()
    {
        Assert.Null(Predict("notes.txt", binary: Set("exe"), skip: Set("tmp")));
    }

    [Fact]
    public void Predict_NumericTail_NotExcluded_ReturnsNull()
    {
        // "report.2024" → ext "2024"; nothing excludes it, so no false positive.
        Assert.Null(Predict("report.2024", binary: Set("exe")));
    }

    [Fact]
    public void Predict_RegexMode_ReturnsNull()
    {
        Assert.Null(Predict(@"setup\.exe", useRegex: true, binary: Set("exe")));
    }

    [Fact]
    public void Predict_ContentOnlyMode_ReturnsNull()
    {
        Assert.Null(Predict("setup.exe", mode: SearchMode.Content, binary: Set("exe")));
    }

    [Theory]
    [InlineData(SearchMode.Both)]
    [InlineData(SearchMode.FileNames)]
    [InlineData(SearchMode.FileNameThenContent)]
    public void Predict_NameEvaluatingModes_Warn(SearchMode mode)
    {
        Assert.NotNull(Predict("setup.exe", mode: mode, binary: Set("exe")));
    }

    [Fact]
    public void Predict_ExcludeGlob_Warns()
    {
        var result = Predict("installer.exe", excludeGlobs: "*.exe");
        Assert.NotNull(result);
        Assert.Equal(ExtensionExclusionReason.ExcludeFilter, result!.Reasons);
    }

    [Fact]
    public void Predict_ExcludeBareExtensionToken_Warns()
    {
        // A bare short token like "exe" is an extension filter in GlobMatcher semantics.
        var result = Predict("installer.exe", excludeGlobs: "exe;log");
        Assert.NotNull(result);
        Assert.Equal(ExtensionExclusionReason.ExcludeFilter, result!.Reasons);
    }

    [Fact]
    public void Predict_RestrictiveIncludeFilter_OmitsExtension_Warns()
    {
        // Only *.txt is included, so a .exe query is excluded-by-omission.
        var result = Predict("installer.exe", includeGlobs: "*.txt");
        Assert.NotNull(result);
        Assert.Equal(ExtensionExclusionReason.IncludeFilter, result!.Reasons);
    }

    [Fact]
    public void Predict_IncludeFilterContainsExtension_ReturnsNull()
    {
        Assert.Null(Predict("installer.exe", includeGlobs: "*.exe;*.txt"));
    }

    [Fact]
    public void Predict_IncludeTargetExtensionIsBinaryExcluded_Warns()
    {
        // The Semantic scenario: the model resolves "exe files" into include glob *.exe, but .exe is
        // binary-excluded. The query itself carries no extension, so the include target is the signal.
        var result = Predict("yagu", includeGlobs: "*.exe", binary: Set("exe"));
        Assert.NotNull(result);
        Assert.Equal("exe", result!.Extension);
        Assert.True(result.Reasons.HasFlag(ExtensionExclusionReason.BinaryExtensions));
    }

    [Fact]
    public void Predict_IncludeTargetExtensionIsSkipExcluded_Warns()
    {
        var result = Predict("report", includeGlobs: "*.png", skip: Set("png"));
        Assert.NotNull(result);
        Assert.Equal("png", result!.Extension);
        Assert.True(result.Reasons.HasFlag(ExtensionExclusionReason.SkipExtensions));
    }

    [Fact]
    public void Predict_IncludeTargetExtensionNotExcluded_ReturnsNull()
    {
        // Restricting to *.cs while nothing excludes cs is a normal, valid filter — no warning.
        Assert.Null(Predict("foo", includeGlobs: "*.cs", binary: Set("exe"), skip: Set("tmp")));
    }

    [Fact]
    public void Predict_FolderIncludeFilter_DoesNotWarn()
    {
        // A genuine path-segment include like "node_modules" (not an extension pattern in GlobMatcher
        // semantics) contributes no included extensions, so it must not flag .exe.
        Assert.Null(Predict("installer.exe", includeGlobs: "node_modules"));
    }

    [Fact]
    public void Predict_ShortBareTokenIncludeFilter_IsTreatedAsExtension()
    {
        // GlobMatcher treats a short bare alphanumeric token (e.g. "cs") as the extension ".cs", so an
        // include filter of "cs" omits .exe — the predictor mirrors that and warns.
        var result = Predict("installer.exe", includeGlobs: "cs;ts");
        Assert.NotNull(result);
        Assert.Equal(ExtensionExclusionReason.IncludeFilter, result!.Reasons);
    }

    [Fact]
    public void Predict_MultipleReasons_AreCombined()
    {
        var result = Predict("installer.exe", binary: Set("exe"), excludeGlobs: "*.exe");
        Assert.NotNull(result);
        Assert.True(result!.Reasons.HasFlag(ExtensionExclusionReason.BinaryExtensions));
        Assert.True(result.Reasons.HasFlag(ExtensionExclusionReason.ExcludeFilter));
    }

    [Fact]
    public void Predict_ExactMatchOff_ChecksEachTerm()
    {
        // With ExactMatch off, "Memory Setup 0.3.42.exe" splits and the "0.3.42.exe" term yields "exe".
        var result = Predict("Memory Setup 0.3.42.exe", exactMatch: false, binary: Set("exe"));
        Assert.NotNull(result);
        Assert.Equal("exe", result!.Extension);
    }

    [Fact]
    public void Predict_RegexExcludeMode_IgnoresFilterExtensions()
    {
        // In regex filter mode the exclude text is opaque, so no extension-based warning from it.
        Assert.Null(Predict("installer.exe", excludeGlobs: @"\.exe$", excludeMode: FilterPatternMode.Regex));
    }

    [Fact]
    public void Predict_QuotedTerm_StripsQuotes()
    {
        var result = Predict("\"installer.exe\"", binary: Set("exe"));
        Assert.NotNull(result);
        Assert.Equal("exe", result!.Extension);
    }

    [Fact]
    public void ExtractCandidateExtensions_EmptyOrNoExtension_ReturnsEmpty()
    {
        Assert.Empty(ExcludedExtensionPredictor.ExtractCandidateExtensions("", true));
        Assert.Empty(ExcludedExtensionPredictor.ExtractCandidateExtensions("report", true));
        Assert.Empty(ExcludedExtensionPredictor.ExtractCandidateExtensions(".hiddenfile", true));
        Assert.Empty(ExcludedExtensionPredictor.ExtractCandidateExtensions("trailingdot.", true));
    }

    [Fact]
    public void ExtractFilterExtensions_ParsesStarDotAndBareTokens()
    {
        var set = ExcludedExtensionPredictor.ExtractFilterExtensions("*.exe; log ; node_modules; *.BLOCKMAP");
        Assert.Contains("exe", set);
        Assert.Contains("log", set);
        Assert.Contains("blockmap", set);          // *.ext form has no length cap
        Assert.DoesNotContain("node_modules", set); // too long for a bare extension token
    }

    [Fact]
    public void Predict_QueryWithoutExtension_ReturnsNull()
    {
        // No trailing extension token → no candidates → null (covers the empty-candidates path).
        Assert.Null(Predict("report", binary: Set("exe")));
    }

    [Fact]
    public void Predict_RegexIncludeMode_IgnoresFilterExtensions()
    {
        // In regex include-filter mode the include text is opaque, so no include-omission warning.
        Assert.Null(Predict("installer.exe", includeGlobs: "*.txt", includeMode: FilterPatternMode.Regex));
    }

    [Fact]
    public void Predict_DuplicateExtensionTerms_ReturnsSingleWarning()
    {
        // ExactMatch off with two terms sharing the same excluded ext exercises the de-dup path.
        var result = Predict("a.exe b.exe", exactMatch: false, binary: Set("exe"));
        Assert.NotNull(result);
        Assert.Equal("exe", result!.Extension);
    }

    [Fact]
    public void ExtractCandidateExtensions_OverlongExtension_IsIgnored()
    {
        // A 17-char "extension" is implausible and must be skipped.
        Assert.Empty(ExcludedExtensionPredictor.ExtractCandidateExtensions("file.abcdefghijklmnopq", true));
    }

    [Fact]
    public void ExtractCandidateExtensions_NonAlphanumericExtension_IsIgnored()
    {
        Assert.Empty(ExcludedExtensionPredictor.ExtractCandidateExtensions("archive.tar.g-z", true));
    }

    [Fact]
    public void ExtractCandidateExtensions_DuplicateTerms_AreDeduplicated()
    {
        var result = ExcludedExtensionPredictor.ExtractCandidateExtensions("a.exe b.exe", exactMatch: false);
        Assert.Equal(new[] { "exe" }, result);
    }

    [Fact]
    public void ExtractFilterExtensions_ShortNonAlphanumericToken_IsIgnored()
    {
        // A short bare token with a non-alphanumeric char is not an extension pattern.
        var set = ExcludedExtensionPredictor.ExtractFilterExtensions("a-b; *.; *.c+d");
        Assert.Empty(set);
    }

    [Theory]
    [InlineData(null, "exe", "")]
    [InlineData("   ", "exe", "   ")]
    [InlineData("exe;dll;pdb", "exe", "dll;pdb")]
    [InlineData("*.exe;*.txt", "exe", "*.txt")]
    [InlineData(".exe;.log", "exe", ".log")]
    [InlineData("dll;pdb", "exe", "dll;pdb")] // ext not present → unchanged
    public void RemoveExtensionToken_RemovesMatchingTokens(string? list, string ext, string expected)
    {
        Assert.Equal(expected, ExcludedExtensionPredictor.RemoveExtensionToken(list, ext));
    }

    [Fact]
    public void AppendExtensionToken_AddsStarDotForm_WhenAbsent()
    {
        Assert.Equal("*.txt;*.exe", ExcludedExtensionPredictor.AppendExtensionToken("*.txt", "exe"));
    }

    [Fact]
    public void AppendExtensionToken_NullList_ProducesSingleToken()
    {
        Assert.Equal("*.exe", ExcludedExtensionPredictor.AppendExtensionToken(null, "exe"));
    }

    [Theory]
    [InlineData("*.exe", "exe")]
    [InlineData("exe", "exe")]
    [InlineData(".exe", "exe")]
    public void AppendExtensionToken_AlreadyPresent_ReturnsUnchanged(string list, string ext)
    {
        Assert.Equal(list, ExcludedExtensionPredictor.AppendExtensionToken(list, ext));
    }
}
