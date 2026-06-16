using Yagu.Services;

namespace Yagu.Tests;

public sealed class DynamicGitignoreMatcherTests : IDisposable
{
    private readonly string _root;

    public DynamicGitignoreMatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dgm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteGitignore(string relativeDir, string content)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), content);
    }

    private void CreateDir(string relativeDir)
    {
        Directory.CreateDirectory(Path.Combine(_root, relativeDir));
    }

    private void CreateFile(string relativePath)
    {
        var dir = Path.GetDirectoryName(Path.Combine(_root, relativePath))!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(_root, relativePath), "");
    }

    // ─── ShouldSkipDirectory ────────────────────────────────────────

    [Fact]
    public void ShouldSkipDirectory_AlwaysExcludesGitFolder()
    {
        var matcher = new DynamicGitignoreMatcher(_root);
        var gitDir = Path.Combine(_root, ".git");
        Directory.CreateDirectory(gitDir);

        Assert.True(matcher.ShouldSkipDirectory(gitDir));
        Assert.Equal(1, matcher.Skipped);
    }

    [Fact]
    public void ShouldSkipDirectory_ExcludesFolderListedInGitignore()
    {
        WriteGitignore("", "node_modules/\n");
        CreateDir("node_modules");

        var matcher = new DynamicGitignoreMatcher(_root);
        var dir = Path.Combine(_root, "node_modules");

        Assert.True(matcher.ShouldSkipDirectory(dir));
    }

    [Fact]
    public void ShouldSkipDirectory_DoesNotExcludeUnlistedFolder()
    {
        WriteGitignore("", "node_modules/\n");
        CreateDir("src");

        var matcher = new DynamicGitignoreMatcher(_root);
        var dir = Path.Combine(_root, "src");

        Assert.False(matcher.ShouldSkipDirectory(dir));
    }

    [Fact]
    public void ShouldSkipDirectory_NestedGitignoreApplies()
    {
        // Root .gitignore doesn't exclude "build", but sub/.gitignore does
        WriteGitignore("", "");
        WriteGitignore("sub", "build/\n");
        CreateDir(Path.Combine("sub", "build"));

        var matcher = new DynamicGitignoreMatcher(_root);
        var dir = Path.Combine(_root, "sub", "build");

        Assert.True(matcher.ShouldSkipDirectory(dir));
    }

    [Fact]
    public void ShouldSkipDirectory_NestedGitignoreDoesNotAffectSiblings()
    {
        WriteGitignore("sub", "build/\n");
        CreateDir("other");
        CreateDir(Path.Combine("other", "build"));

        var matcher = new DynamicGitignoreMatcher(_root);
        var dir = Path.Combine(_root, "other", "build");

        // "other/build" is not below "sub", so sub's .gitignore shouldn't apply
        Assert.False(matcher.ShouldSkipDirectory(dir));
    }

    [Fact]
    public void ShouldSkipDirectory_BareNameWithoutSlash_TreatedAsFolder()
    {
        // Bare names that look like folders (no dot) are treated as folder exclusions
        WriteGitignore("", "dist\n");
        CreateDir("dist");

        var matcher = new DynamicGitignoreMatcher(_root);
        var dir = Path.Combine(_root, "dist");

        Assert.True(matcher.ShouldSkipDirectory(dir));
    }

    [Fact]
    public void ShouldSkipDirectory_DotPrefixedName_TreatedAsFolder()
    {
        WriteGitignore("", ".cache\n");
        CreateDir(".cache");

        var matcher = new DynamicGitignoreMatcher(_root);
        var dir = Path.Combine(_root, ".cache");

        Assert.True(matcher.ShouldSkipDirectory(dir));
    }

    // ─── ShouldSkipFile ─────────────────────────────────────────────

    [Fact]
    public void ShouldSkipFile_ExtensionExclusion()
    {
        WriteGitignore("", "*.log\n");
        CreateFile("test.log");

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "test.log");

        Assert.True(matcher.ShouldSkipFile(file));
    }

    [Fact]
    public void ShouldSkipFile_ExtensionNotExcluded()
    {
        WriteGitignore("", "*.log\n");
        CreateFile("test.txt");

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "test.txt");

        Assert.False(matcher.ShouldSkipFile(file));
    }

    [Fact]
    public void ShouldSkipFile_IncludeExtensionOverride_PreventExclusion()
    {
        WriteGitignore("", "*.cs\n");
        CreateFile("Program.cs");

        var matcher = new DynamicGitignoreMatcher(_root)
        {
            IncludeExtensionOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs" }
        };
        var file = Path.Combine(_root, "Program.cs");

        Assert.False(matcher.ShouldSkipFile(file));
    }

    [Fact]
    public void ShouldSkipFile_NestedExtensionExclusion()
    {
        WriteGitignore("sub", "*.tmp\n");
        CreateFile(Path.Combine("sub", "data.tmp"));

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "sub", "data.tmp");

        Assert.True(matcher.ShouldSkipFile(file));
    }

    [Fact]
    public void ShouldSkipFile_NoExtension_NotExcluded()
    {
        WriteGitignore("", "*.log\n");
        CreateFile("Makefile");

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "Makefile");

        Assert.False(matcher.ShouldSkipFile(file));
    }

    // ─── ShouldSkipPath (flat-path backend) ─────────────────────────

    [Fact]
    public void ShouldSkipPath_DirectoryExclusionApplies()
    {
        WriteGitignore("", "bin/\n");
        CreateDir("bin");
        CreateFile(Path.Combine("bin", "app.exe"));

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "bin", "app.exe");

        Assert.True(matcher.ShouldSkipPath(file));
    }

    [Fact]
    public void ShouldSkipPath_ExtensionExclusionApplies()
    {
        WriteGitignore("", "*.obj\n");
        CreateFile("code.obj");

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "code.obj");

        Assert.True(matcher.ShouldSkipPath(file));
    }

    [Fact]
    public void ShouldSkipPath_FileInAllowedDirectory_NotSkipped()
    {
        WriteGitignore("", "bin/\n");
        CreateFile(Path.Combine("src", "main.cs"));

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "src", "main.cs");

        Assert.False(matcher.ShouldSkipPath(file));
    }

    [Fact]
    public void ShouldSkipPath_DeeplyNestedExcludedDir()
    {
        WriteGitignore("", "obj/\n");
        CreateDir(Path.Combine("a", "b", "obj"));
        CreateFile(Path.Combine("a", "b", "obj", "file.dll"));

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "a", "b", "obj", "file.dll");

        Assert.True(matcher.ShouldSkipPath(file));
    }

    // ─── ParsedGitignore edge cases ─────────────────────────────────

    [Fact]
    public void Comments_AreIgnored()
    {
        WriteGitignore("", "# comment\nbin/\n");
        CreateDir("bin");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipDirectory(Path.Combine(_root, "bin")));
    }

    [Fact]
    public void NegationLines_AreSkipped()
    {
        // Negation is too complex — the matcher skips them
        WriteGitignore("", "*.log\n!important.log\n");
        CreateFile("important.log");

        var matcher = new DynamicGitignoreMatcher(_root);
        // Without negation support, important.log is still excluded
        Assert.True(matcher.ShouldSkipFile(Path.Combine(_root, "important.log")));
    }

    [Fact]
    public void BlankLines_AreIgnored()
    {
        WriteGitignore("", "\n\n  \nbin/\n\n");
        CreateDir("bin");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipDirectory(Path.Combine(_root, "bin")));
    }

    [Fact]
    public void PathRelativePattern_ExcludesFullPath()
    {
        CreateDir(Path.Combine("docs", "internal"));
        WriteGitignore("", "docs/internal\n");

        var matcher = new DynamicGitignoreMatcher(_root);
        var dir = Path.Combine(_root, "docs", "internal");

        Assert.True(matcher.ShouldSkipDirectory(dir));
    }

    [Fact]
    public void Skipped_Counter_Increments()
    {
        WriteGitignore("", "*.log\n");
        CreateFile("a.log");
        CreateFile("b.log");
        CreateFile("c.txt");

        var matcher = new DynamicGitignoreMatcher(_root);
        matcher.ShouldSkipFile(Path.Combine(_root, "a.log"));
        matcher.ShouldSkipFile(Path.Combine(_root, "b.log"));
        matcher.ShouldSkipFile(Path.Combine(_root, "c.txt"));

        Assert.Equal(2, matcher.Skipped);
    }

    [Fact]
    public void NoGitignoreFile_NothingExcluded()
    {
        CreateFile("test.log");
        CreateDir("node_modules");

        var matcher = new DynamicGitignoreMatcher(_root);

        Assert.False(matcher.ShouldSkipFile(Path.Combine(_root, "test.log")));
        Assert.False(matcher.ShouldSkipDirectory(Path.Combine(_root, "node_modules")));
    }

    [Fact]
    public void MultipleExtensionRules()
    {
        WriteGitignore("", "*.log\n*.tmp\n*.bak\n");
        CreateFile("data.log");
        CreateFile("data.tmp");
        CreateFile("data.bak");
        CreateFile("data.cs");

        var matcher = new DynamicGitignoreMatcher(_root);

        Assert.True(matcher.ShouldSkipFile(Path.Combine(_root, "data.log")));
        Assert.True(matcher.ShouldSkipFile(Path.Combine(_root, "data.tmp")));
        Assert.True(matcher.ShouldSkipFile(Path.Combine(_root, "data.bak")));
        Assert.False(matcher.ShouldSkipFile(Path.Combine(_root, "data.cs")));
    }

    [Fact]
    public void WildcardPatterns_NotTreatedAsExtensions()
    {
        // Patterns like "*.min.*" or "foo*" are not simple extension patterns
        WriteGitignore("", "foo*/\n");

        var matcher = new DynamicGitignoreMatcher(_root);
        // Should not crash or misclassify
        Assert.False(matcher.ShouldSkipDirectory(Path.Combine(_root, "foobar")));
    }

    [Fact]
    public void DirExclusionCache_IsReused()
    {
        WriteGitignore("", "obj/\n");
        CreateDir(Path.Combine("a", "obj"));
        CreateFile(Path.Combine("a", "obj", "file1.dll"));
        CreateFile(Path.Combine("a", "obj", "file2.dll"));

        var matcher = new DynamicGitignoreMatcher(_root);

        // Both calls should hit the cache on the second call
        Assert.True(matcher.ShouldSkipPath(Path.Combine(_root, "a", "obj", "file1.dll")));
        Assert.True(matcher.ShouldSkipPath(Path.Combine(_root, "a", "obj", "file2.dll")));
    }

    [Fact]
    public void ShouldSkipFile_NullDirectoryName_ReturnsFalse()
    {
        // A bare filename with no directory component
        var matcher = new DynamicGitignoreMatcher(_root);
        // Path.GetDirectoryName("file.txt") returns "" not null on Windows,
        // but a root path like "C:\" returns null for GetDirectoryName
        Assert.False(matcher.ShouldSkipFile("file.txt"));
    }

    [Fact]
    public void ShouldSkipPath_GitDirViaCache_IsExcluded()
    {
        // Exercise the .git check inside IsDirExcludedCached
        CreateDir(Path.Combine("deep", ".git", "objects"));
        CreateFile(Path.Combine("deep", ".git", "objects", "pack.idx"));

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "deep", ".git", "objects", "pack.idx");

        Assert.True(matcher.ShouldSkipPath(file));
    }

    [Fact]
    public void ShouldSkipPath_ParentExcluded_ChildAlsoExcluded()
    {
        WriteGitignore("", "vendor/\n");
        CreateDir(Path.Combine("vendor", "lib", "deep"));
        CreateFile(Path.Combine("vendor", "lib", "deep", "module.js"));

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "vendor", "lib", "deep", "module.js");

        Assert.True(matcher.ShouldSkipPath(file));
    }

    [Fact]
    public void ShouldSkipPath_FileAtRoot_ExtensionExcluded()
    {
        // File directly in root — tests the fileDir path in ShouldSkipPath
        WriteGitignore("", "*.bak\n");
        CreateFile("data.bak");

        var matcher = new DynamicGitignoreMatcher(_root);
        var file = Path.Combine(_root, "data.bak");

        Assert.True(matcher.ShouldSkipPath(file));
    }

    [Fact]
    public void FullPathExclusion_MatchesSubdirectory()
    {
        // A full-path pattern like "docs/internal" should exclude the dir
        // AND anything starting with that path prefix
        CreateDir(Path.Combine("docs", "internal", "secret"));
        WriteGitignore("", "docs/internal\n");

        var matcher = new DynamicGitignoreMatcher(_root);
        var subdir = Path.Combine(_root, "docs", "internal", "secret");

        Assert.True(matcher.ShouldSkipDirectory(subdir));
    }

    [Fact]
    public void TrailingSpaces_AreStripped()
    {
        WriteGitignore("", "bin   \n");
        CreateDir("bin");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipDirectory(Path.Combine(_root, "bin")));
    }

    [Fact]
    public void ExtensionPattern_WithInvalidChars_NotTreatedAsExtension()
    {
        // "*.foo bar" has a space — shouldn't be treated as extension exclusion
        WriteGitignore("", "*.foo bar\n");
        CreateFile("test.foo bar");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.False(matcher.ShouldSkipFile(Path.Combine(_root, "test.foo bar")));
    }

    [Fact]
    public void BareName_WithDot_NotDotPrefix_NotFolder()
    {
        // "readme.txt" has a dot but doesn't start with dot — not a "likely folder"
        // It won't be treated as a folder exclusion
        WriteGitignore("", "readme.txt\n");
        CreateDir("readme.txt"); // directory named readme.txt

        var matcher = new DynamicGitignoreMatcher(_root);
        // Since IsLikelyFolderName returns false for "readme.txt", it won't be an ExcludedFolder
        Assert.False(matcher.ShouldSkipDirectory(Path.Combine(_root, "readme.txt")));
    }

    [Fact]
    public void PathRelativePattern_NonExistentDir_NotAdded()
    {
        // A path pattern pointing to a non-existent directory is silently skipped
        WriteGitignore("", "nonexistent/subdir\n");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.False(matcher.ShouldSkipDirectory(Path.Combine(_root, "nonexistent", "subdir")));
    }

    [Fact]
    public void IsDirExcludedCached_EmptyDirName_ReturnsFalse()
    {
        // When GetFileName returns empty (e.g. root path), should return false
        var matcher = new DynamicGitignoreMatcher(_root);
        // _root itself has Length == _rootDirectory.Length, so the while loop
        // in ShouldSkipPath won't enter; but we can exercise via ShouldSkipDirectory
        // on a path that won't match anything
        Assert.False(matcher.ShouldSkipDirectory(_root));
    }

    [Fact]
    public void GitignoreLoadError_GracefullyIgnored()
    {
        // Create a directory named .gitignore (not a file) to trigger an IO exception
        var gitignoreDir = Path.Combine(_root, "sub", ".gitignore");
        Directory.CreateDirectory(gitignoreDir);
        CreateFile(Path.Combine("sub", "test.log"));

        var matcher = new DynamicGitignoreMatcher(_root);
        // Should not throw; just returns false
        Assert.False(matcher.ShouldSkipFile(Path.Combine(_root, "sub", "test.log")));
    }

    [Fact]
    public void ShouldSkipPath_FileDirectlyInRoot_NotExcluded()
    {
        // File at root level — GetDirectoryName returns root itself, which == _rootDirectory
        // This tests the "dir.Length > _rootDirectory.Length" exit condition
        CreateFile("rootfile.txt");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.False(matcher.ShouldSkipPath(Path.Combine(_root, "rootfile.txt")));
    }

    [Fact]
    public void ExtensionPattern_EmptyAfterDot_NotExtension()
    {
        // "*.  " after trim → "*." which has empty extension
        WriteGitignore("", "*.\n");

        var matcher = new DynamicGitignoreMatcher(_root);
        // Just verify it doesn't crash
        Assert.False(matcher.ShouldSkipFile(Path.Combine(_root, "file")));
    }

    [Fact]
    public void TrailingEscapedSpace_Preserved()
    {
        // Trailing space preceded by backslash is preserved per git spec
        // "bin\\ " → after stripping unescaped trailing spaces the escaped one stays
        WriteGitignore("", "logs\\ \n");

        var matcher = new DynamicGitignoreMatcher(_root);
        // "logs " (with trailing space) might be treated as folder
        // Just verify no crash and reasonable behavior
        Assert.False(matcher.ShouldSkipDirectory(Path.Combine(_root, "logs")));
    }

    [Fact]
    public void ShouldSkipDirectory_DeeplyNested_CascadesFromAncestor()
    {
        // Exercise the parent-excluded cascading in IsDirExcludedCached
        WriteGitignore("", "build/\n");
        CreateDir(Path.Combine("build", "sub1", "sub2", "sub3"));
        CreateFile(Path.Combine("build", "sub1", "sub2", "sub3", "deep.dll"));

        var matcher = new DynamicGitignoreMatcher(_root);
        // Each nested dir should be excluded via parent cascading
        Assert.True(matcher.ShouldSkipPath(
            Path.Combine(_root, "build", "sub1", "sub2", "sub3", "deep.dll")));
    }

    [Fact]
    public void ShouldSkipFile_InSubDir_AncestorRuleApplies()
    {
        // Root .gitignore excludes *.log — file in sub/deep/file.log should match
        WriteGitignore("", "*.log\n");
        CreateDir(Path.Combine("sub", "deep"));
        CreateFile(Path.Combine("sub", "deep", "file.log"));

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipFile(Path.Combine(_root, "sub", "deep", "file.log")));
    }

    [Fact]
    public void DirOnlySlash_FolderExclusion()
    {
        // "output/" with trailing slash → dirOnly=true, treated as folder
        WriteGitignore("", "output/\n");
        CreateDir("output");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipDirectory(Path.Combine(_root, "output")));
    }

    [Fact]
    public void PathPattern_WithForwardSlash_ResolvesCorrectly()
    {
        // "src/generated" with forward slash → path-relative pattern
        CreateDir(Path.Combine("src", "generated"));
        WriteGitignore("", "src/generated\n");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipDirectory(Path.Combine(_root, "src", "generated")));
    }

    // ─── ShouldSkipFile: null directory case ────────────────────────

    [Fact]
    public void ShouldSkipFile_RootPathOnly_ReturnsFalse()
    {
        // Path.GetDirectoryName on a root-level file like "C:\" returns null
        var matcher = new DynamicGitignoreMatcher(_root);
        // A file at the drive root — GetDirectoryName returns null → false
        Assert.False(matcher.ShouldSkipFile(@"C:\"));
    }

    // ─── ShouldSkipPath: intermediate directory excluded ────────────

    [Fact]
    public void ShouldSkipPath_IntermediateGitDir_ReturnsTrue()
    {
        // An intermediate .git directory should cause the path to be skipped
        CreateDir(Path.Combine(".git", "objects"));
        CreateFile(Path.Combine(".git", "objects", "pack", "file.pack"));

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipPath(
            Path.Combine(_root, ".git", "objects", "pack", "file.pack")));
    }

    [Fact]
    public void ShouldSkipPath_FileInExcludedSubdir_ReturnsTrue()
    {
        WriteGitignore("", "node_modules\n");
        CreateDir(Path.Combine("node_modules", "lodash"));
        CreateFile(Path.Combine("node_modules", "lodash", "index.js"));

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipPath(
            Path.Combine(_root, "node_modules", "lodash", "index.js")));
    }

    [Fact]
    public void ShouldSkipPath_FileMatchesExtensionRule_ReturnsTrue()
    {
        WriteGitignore("", "*.log\n");
        CreateFile("debug.log");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipPath(Path.Combine(_root, "debug.log")));
    }

    [Fact]
    public void ShouldSkipPath_FileNotExcluded_ReturnsFalse()
    {
        WriteGitignore("", "*.log\n");
        CreateFile("app.cs");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.False(matcher.ShouldSkipPath(Path.Combine(_root, "app.cs")));
    }

    // ─── GetOrLoadRules: unreadable .gitignore ──────────────────────

    [Fact]
    public void GetOrLoadRules_LockedGitignore_DoesNotThrow()
    {
        var dir = Path.Combine(_root, "locked");
        Directory.CreateDirectory(dir);
        var gitignorePath = Path.Combine(dir, ".gitignore");
        File.WriteAllText(gitignorePath, "*.tmp\n");

        // Hold the file exclusively so it can't be read
        using var fs = new FileStream(gitignorePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var matcher = new DynamicGitignoreMatcher(_root);
        // Should not throw — falls through the catch
        Assert.False(matcher.ShouldSkipFile(Path.Combine(dir, "test.tmp")));
    }

    // ─── MatchesAnyAncestorFolderRule: full path matching ───────────

    [Fact]
    public void MatchesAnyAncestorFolderRule_FullPathPatternExclusion()
    {
        // A path-relative pattern resolves to an ExcludedFullPaths entry
        CreateDir(Path.Combine("build", "output"));
        WriteGitignore("", "build/output\n");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipDirectory(Path.Combine(_root, "build", "output")));
    }

    [Fact]
    public void MatchesAnyAncestorFolderRule_ChildOfExcludedFullPath()
    {
        // A subdirectory of an excluded full path should also be excluded
        CreateDir(Path.Combine("build", "output", "logs"));
        WriteGitignore("", "build/output\n");

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipDirectory(Path.Combine(_root, "build", "output", "logs")));
    }

    // ─── MatchesAnyAncestorFileRule: nested gitignore ───────────────

    [Fact]
    public void MatchesAnyAncestorFileRule_NestedGitignoreExcludesFile()
    {
        CreateDir("subdir");
        WriteGitignore("subdir", "*.gen\n");
        CreateFile(Path.Combine("subdir", "output.gen"));

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipFile(Path.Combine(_root, "subdir", "output.gen")));
    }

    [Fact]
    public void MatchesAnyAncestorFileRule_ParentGitignore_ExcludesInChild()
    {
        // Root .gitignore excludes *.tmp; file is in a subdirectory
        WriteGitignore("", "*.tmp\n");
        CreateDir("deep");
        CreateFile(Path.Combine("deep", "cache.tmp"));

        var matcher = new DynamicGitignoreMatcher(_root);
        Assert.True(matcher.ShouldSkipFile(Path.Combine(_root, "deep", "cache.tmp")));
    }
}

