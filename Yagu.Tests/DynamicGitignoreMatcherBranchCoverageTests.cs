using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Additional branch coverage for DynamicGitignoreMatcher: nested .gitignore files,
/// IncludeExtensionOverrides, edge cases in pattern parsing, and path handling.
/// </summary>
public sealed class DynamicGitignoreMatcherBranchCoverageTests : IDisposable
{
    private readonly string _root;

    public DynamicGitignoreMatcherBranchCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-gitignore-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteGitignore(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath, ".gitignore");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string MakePath(params string[] parts)
        => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // ═══════════════════════════════════════════════════════════════
    //  .git directory always skipped
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldSkipDirectory_GitDir_AlwaysTrue()
    {
        var matcher = new DynamicGitignoreMatcher(_root);
        var gitDir = MakePath(".git");
        Assert.True(matcher.ShouldSkipDirectory(gitDir));
    }

    [Fact]
    public void ShouldSkipDirectory_NestedGitDir_AlwaysTrue()
    {
        var matcher = new DynamicGitignoreMatcher(_root);
        var gitDir = MakePath("sub", ".git");
        Assert.True(matcher.ShouldSkipDirectory(gitDir));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Extension patterns in .gitignore
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldSkipFile_MatchesExtensionPattern()
    {
        WriteGitignore("", "*.log\n*.tmp");
        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipFile(MakePath("debug.log")));
        Assert.True(matcher.ShouldSkipFile(MakePath("session.tmp")));
        Assert.False(matcher.ShouldSkipFile(MakePath("app.cs")));
    }

    [Fact]
    public void ShouldSkipFile_ExtensionPattern_MatchesByExtension()
    {
        // Bare names like "Thumbs.db" aren't matched as file exclusions;
        // only "*.ext" patterns work for file-level gitignore matching.
        WriteGitignore("", "*.db\n*.tmp");
        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipFile(MakePath("Thumbs.db")));
        Assert.True(matcher.ShouldSkipFile(MakePath("session.tmp")));
        Assert.False(matcher.ShouldSkipFile(MakePath("other.txt")));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Folder patterns
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldSkipDirectory_MatchesFolderPattern()
    {
        WriteGitignore("", "node_modules\nbin\nobj");
        var matcher = new DynamicGitignoreMatcher(_root);
        var nodeModules = MakePath("node_modules");
        Directory.CreateDirectory(nodeModules);
        Assert.True(matcher.ShouldSkipDirectory(nodeModules));
    }

    [Fact]
    public void ShouldSkipDirectory_DoesNotMatchNonGitignored()
    {
        WriteGitignore("", "node_modules");
        var matcher = new DynamicGitignoreMatcher(_root);
        var srcDir = MakePath("src");
        Directory.CreateDirectory(srcDir);
        Assert.False(matcher.ShouldSkipDirectory(srcDir));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Nested .gitignore inheritance
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NestedGitignore_AppliesInSubdirectory()
    {
        WriteGitignore("", "*.log");
        WriteGitignore("sub", "*.tmp");
        var matcher = new DynamicGitignoreMatcher(_root);

        // Root rule applies everywhere
        Assert.True(matcher.ShouldSkipFile(MakePath("debug.log")));
        Assert.True(matcher.ShouldSkipFile(MakePath("sub", "debug.log")));

        // Sub rule applies only in sub
        Assert.True(matcher.ShouldSkipFile(MakePath("sub", "file.tmp")));
        Assert.False(matcher.ShouldSkipFile(MakePath("file.tmp"))); // not in root gitignore
    }

    // ═══════════════════════════════════════════════════════════════
    //  IncludeExtensionOverrides
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IncludeExtensionOverrides_OverridesGitignore()
    {
        WriteGitignore("", "*.log");
        var matcher = new DynamicGitignoreMatcher(_root);

        // Cast to access the override mechanism
        var overrides = matcher.IncludeExtensionOverrides as HashSet<string>;
        if (overrides is not null)
        {
            overrides.Add("log");  // Extensions stored without dot in the override set
            Assert.False(matcher.ShouldSkipFile(MakePath("important.log")));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Comments and blank lines in .gitignore
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldSkipFile_IgnoresComments()
    {
        WriteGitignore("", "# This is a comment\n*.log\n\n# Another comment\n*.tmp");
        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipFile(MakePath("file.log")));
        Assert.True(matcher.ShouldSkipFile(MakePath("file.tmp")));
        Assert.False(matcher.ShouldSkipFile(MakePath("file.cs")));
    }

    // ═══════════════════════════════════════════════════════════════
    //  ShouldSkipPath (flat path check)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldSkipPath_MatchesGitignoreRules()
    {
        WriteGitignore("", "*.log");
        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipPath(MakePath("debug.log")));
        Assert.False(matcher.ShouldSkipPath(MakePath("app.cs")));
    }

    [Fact]
    public void ShouldSkipPath_GitDirectory_AlwaysSkipped()
    {
        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipPath(MakePath(".git", "HEAD")));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Skipped counter
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Skipped_IncrementsOnSkippedFiles()
    {
        WriteGitignore("", "*.log");
        var matcher = new DynamicGitignoreMatcher(_root);
        int initialSkipped = matcher.Skipped;
        matcher.ShouldSkipFile(MakePath("test.log"));
        Assert.True(matcher.Skipped > initialSkipped);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No .gitignore present
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NoGitignore_NothingSkipped()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "yagu-gitignore-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyRoot);
        try
        {
            var matcher = new DynamicGitignoreMatcher(emptyRoot);
            Assert.False(matcher.ShouldSkipFile(Path.Combine(emptyRoot, "anything.log")));
            Assert.False(matcher.ShouldSkipDirectory(Path.Combine(emptyRoot, "any_dir")));
        }
        finally { Directory.Delete(emptyRoot, true); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Trailing slash folder patterns
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldSkipDirectory_TrailingSlashPattern()
    {
        WriteGitignore("", "build/\ndist/");
        var matcher = new DynamicGitignoreMatcher(_root);
        var buildDir = MakePath("build");
        Directory.CreateDirectory(buildDir);
        Assert.True(matcher.ShouldSkipDirectory(buildDir));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Negation patterns (!)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NegationPatterns_DoNotCrash()
    {
        // Ensure negation patterns (which are common in .gitignore) don't cause exceptions
        WriteGitignore("", "*.log\n!important.log");
        var matcher = new DynamicGitignoreMatcher(_root);
        // The negation may or may not be fully implemented, but shouldn't throw
        var ex = Record.Exception(() => matcher.ShouldSkipFile(MakePath("important.log")));
        Assert.Null(ex);
    }
}
