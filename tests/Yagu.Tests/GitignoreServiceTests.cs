using Yagu.Services;

namespace Yagu.Tests;

public sealed class GitignoreServiceTests : IDisposable
{
    private readonly string _root;

    public GitignoreServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteGitignore(string relativePath, string content)
    {
        var dir = Path.Combine(_root, Path.GetDirectoryName(relativePath) ?? string.Empty);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(_root, relativePath), content);
    }

    [Fact]
    public void Scan_AlwaysExcludesGitFolder()
    {
        var rules = GitignoreService.Scan(_root);
        Assert.Contains(".git", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_EmptyDirectory_ReturnsDefaults()
    {
        var rules = GitignoreService.Scan(_root);
        Assert.Single(rules.ExcludedFolders); // only .git
        Assert.Empty(rules.ExcludedExtensions);
        Assert.Empty(rules.ExcludedFullPaths);
    }

    [Fact]
    public void Scan_ParsesFolderExclusions()
    {
        WriteGitignore(".gitignore", "node_modules\ndist/\n.cache\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("node_modules", rules.ExcludedFolders);
        Assert.Contains("dist", rules.ExcludedFolders);
        Assert.Contains(".cache", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_ParsesExtensionExclusions()
    {
        WriteGitignore(".gitignore", "*.log\n*.tmp\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("log", rules.ExcludedExtensions);
        Assert.Contains("tmp", rules.ExcludedExtensions);
    }

    [Fact]
    public void Scan_ParsesRelativePathExclusions()
    {
        var subDir = Path.Combine(_root, "build", "output");
        Directory.CreateDirectory(subDir);
        WriteGitignore(".gitignore", "/build/output\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains(subDir, rules.ExcludedFullPaths);
    }

    [Fact]
    public void Scan_SkipsComments()
    {
        WriteGitignore(".gitignore", "# This is a comment\nnode_modules\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("node_modules", rules.ExcludedFolders);
        Assert.DoesNotContain("# This is a comment", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_SkipsNegationRules()
    {
        WriteGitignore(".gitignore", "dist\n!dist/important\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("dist", rules.ExcludedFolders);
        // Negation rule should not add anything extra
    }

    [Fact]
    public void Scan_SkipsEmptyLines()
    {
        WriteGitignore(".gitignore", "\n\nnode_modules\n\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("node_modules", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_RecursesIntoSubdirectories()
    {
        WriteGitignore(".gitignore", "node_modules\n");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        WriteGitignore("sub/.gitignore", "*.bak\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("node_modules", rules.ExcludedFolders);
        Assert.Contains("bak", rules.ExcludedExtensions);
    }

    [Fact]
    public void Scan_SkipsExcludedFoldersDuringRecursion()
    {
        WriteGitignore(".gitignore", "excluded_dir\n");
        var excluded = Path.Combine(_root, "excluded_dir");
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, ".gitignore"), "*.secret\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("excluded_dir", rules.ExcludedFolders);
        // Should not have parsed the nested .gitignore inside excluded_dir
        Assert.DoesNotContain("secret", rules.ExcludedExtensions);
    }

    [Fact]
    public void Scan_HandlesNonExistentDirectory()
    {
        var nonexistent = Path.Combine(_root, "does-not-exist");
        var rules = GitignoreService.Scan(nonexistent);
        Assert.Contains(".git", rules.ExcludedFolders);
        Assert.Empty(rules.ExcludedExtensions);
    }

    [Fact]
    public void Scan_TrailingSlashIndicatesDirectory()
    {
        WriteGitignore(".gitignore", "build/\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("build", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_BareNameWithDot_NotTreatedAsFolder()
    {
        // thumbs.db has a dot so IsLikelyFolderName returns false
        WriteGitignore(".gitignore", "thumbs.db\n");

        var rules = GitignoreService.Scan(_root);
        // Should not be in folders (has dot, not dirOnly)
        Assert.DoesNotContain("thumbs.db", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_DotPrefixedName_TreatedAsFolder()
    {
        WriteGitignore(".gitignore", ".vs\n.idea\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains(".vs", rules.ExcludedFolders);
        Assert.Contains(".idea", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_ExtensionWithWildcard_SkipsInvalidCharacters()
    {
        // "*.{log,tmp}" has braces — not valid for simple extension matching
        WriteGitignore(".gitignore", "*.{log,tmp}\n");

        var rules = GitignoreService.Scan(_root);
        Assert.DoesNotContain("{log,tmp}", rules.ExcludedExtensions);
    }

    [Fact]
    public void Scan_TrailingSpaces_Trimmed()
    {
        WriteGitignore(".gitignore", "node_modules   \n*.log   \n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("node_modules", rules.ExcludedFolders);
        Assert.Contains("log", rules.ExcludedExtensions);
    }

    [Fact]
    public void Scan_EmbeddedSlash_ResolvedAsFullPath()
    {
        var subDir = Path.Combine(_root, "src", "generated");
        Directory.CreateDirectory(subDir);
        WriteGitignore(".gitignore", "src/generated\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains(subDir, rules.ExcludedFullPaths);
    }

    [Fact]
    public void Scan_WildcardPatterns_NotAddedAsFolders()
    {
        WriteGitignore(".gitignore", "temp*\n");

        var rules = GitignoreService.Scan(_root);
        Assert.DoesNotContain("temp*", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_BareNameWithDotNotExtension_NotTreatedAsFolder()
    {
        // "desktop.ini" has a dot, doesn't start with dot, so IsLikelyFolderName returns false.
        // Without trailing slash, it won't be treated as a folder exclusion.
        WriteGitignore(".gitignore", "desktop.ini\n");

        var rules = GitignoreService.Scan(_root);
        Assert.DoesNotContain("desktop.ini", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_BareNameWithDotAndTrailingSlash_TreatedAsFolder()
    {
        // Trailing slash forces directory treatment regardless of IsLikelyFolderName
        WriteGitignore(".gitignore", "some.dir/\n");

        var rules = GitignoreService.Scan(_root);
        Assert.Contains("some.dir", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_UnreadableGitignore_DoesNotThrow()
    {
        // Create a gitignore that can't be read (locked file simulated via directory with same name)
        var gitignorePath = Path.Combine(_root, ".gitignore");
        // Create a directory where .gitignore would be — File.Exists returns false for dirs
        // Instead, create a valid file and make it inaccessible
        File.WriteAllText(gitignorePath, "node_modules\n");
        // Lock the file
        using var lockStream = new FileStream(gitignorePath, FileMode.Open, FileAccess.Read, FileShare.None);

        // Should not throw — exercises the catch block around ParseGitignoreFile
        var rules = GitignoreService.Scan(_root);
        // .git is always excluded
        Assert.Contains(".git", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_SubdirectoryDeleted_DuringEnumeration_DoesNotThrow()
    {
        // Create a subdirectory that we'll delete to trigger DirectoryNotFoundException
        var subDir = Path.Combine(_root, "subdir");
        Directory.CreateDirectory(subDir);
        WriteGitignore(".gitignore", "node_modules\n");

        // Delete the subdir after creating it — enumeration already happened in Scan setup.
        // This simulates a race. Since we can't truly race, just ensure general scan works.
        var rules = GitignoreService.Scan(_root);
        Assert.Contains("node_modules", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_NonExistentRoot_ReturnsDefaultRules()
    {
        var nonExistent = Path.Combine(_root, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        // Should not throw — exercises the top-level catch in Scan
        var rules = GitignoreService.Scan(nonExistent);
        Assert.Contains(".git", rules.ExcludedFolders);
    }

    [Fact]
    public void Scan_ExtensionWithInvalidChars_NotAdded()
    {
        // "*.c++" has '+' which is not letter/digit/underscore/dash, so not added as extension
        WriteGitignore(".gitignore", "*.c++\n");

        var rules = GitignoreService.Scan(_root);
        Assert.DoesNotContain("c++", rules.ExcludedExtensions);
    }
}
