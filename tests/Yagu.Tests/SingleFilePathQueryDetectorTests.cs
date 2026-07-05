using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class SingleFilePathQueryDetectorTests : IDisposable
{
    private readonly string _root;

    public SingleFilePathQueryDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-singlefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Resolve_ExistingAbsoluteFilePath_ReturnsFullPath()
    {
        var path = Path.Combine(_root, "yagu.log");
        File.WriteAllText(path, "hello");

        var resolved = SingleFilePathQueryDetector.Resolve(path);

        Assert.Equal(Path.GetFullPath(path), resolved);
    }

    [Fact]
    public void Resolve_PathWrappedInQuotes_ReturnsFullPath()
    {
        var path = Path.Combine(_root, "file with spaces.txt");
        File.WriteAllText(path, "x");

        var resolved = SingleFilePathQueryDetector.Resolve($"\"{path}\"");

        Assert.Equal(Path.GetFullPath(path), resolved);
    }

    [Fact]
    public void Resolve_SurroundingWhitespace_IsTrimmed()
    {
        var path = Path.Combine(_root, "trim.txt");
        File.WriteAllText(path, "x");

        var resolved = SingleFilePathQueryDetector.Resolve($"   {path}   ");

        Assert.Equal(Path.GetFullPath(path), resolved);
    }

    [Fact]
    public void Resolve_NonExistentPath_ReturnsNull()
    {
        var path = Path.Combine(_root, "missing.txt");
        Assert.Null(SingleFilePathQueryDetector.Resolve(path));
    }

    [Fact]
    public void Resolve_DirectoryPath_ReturnsNull()
    {
        // A directory is not a file, so it must not hijack the search.
        Assert.Null(SingleFilePathQueryDetector.Resolve(_root));
    }

    [Fact]
    public void Resolve_RelativeOrBareFilename_ReturnsNull()
    {
        var path = Path.Combine(_root, "bare.txt");
        File.WriteAllText(path, "x");
        // Only the file name (not fully qualified) — must not be treated as a complete path.
        Assert.Null(SingleFilePathQueryDetector.Resolve("bare.txt"));
        Assert.Null(SingleFilePathQueryDetector.Resolve(@".\bare.txt"));
    }

    [Fact]
    public void Resolve_PathWithExtraText_ReturnsNull()
    {
        var path = Path.Combine(_root, "extra.txt");
        File.WriteAllText(path, "x");
        // The path plus another token is a content search, not a single complete path.
        Assert.Null(SingleFilePathQueryDetector.Resolve($"{path} error"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some search text")]
    public void Resolve_EmptyOrPlainQuery_ReturnsNull(string? query)
    {
        Assert.Null(SingleFilePathQueryDetector.Resolve(query));
    }

    [Fact]
    public void MainViewModel_ShortCircuitsTraditionalSearch_ToSingleFilePath()
    {
        // Source-pin the wiring: MainViewModel (not compiled into the test assembly) must detect the
        // single-file-path query in Traditional mode, before the Directory validation, and display the
        // file as a LineNumber:0 file-name match.
        string root = FindRepoRoot();
        string vm = File.ReadAllText(Path.Combine(root, "src", "Yagu", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("if (!IsSemanticQueryMode && Yagu.Helpers.SingleFilePathQueryDetector.Resolve(Query) is { } singleFilePath)", vm);
        Assert.Contains("await RunSingleFilePathDisplayAsync(singleFilePath)", vm);
        Assert.Contains("private async Task RunSingleFilePathDisplayAsync(string filePath)", vm);
        Assert.Contains("LineNumber: 0,", vm);

        // The short-circuit must come before the Directory existence check so the Directory box is
        // never able to block a complete-file-path lookup.
        int shortCircuit = vm.IndexOf("Yagu.Helpers.SingleFilePathQueryDetector.Resolve(Query)", StringComparison.Ordinal);
        int dirCheck = vm.IndexOf("Directory does not exist:", StringComparison.Ordinal);
        Assert.True(shortCircuit > 0 && dirCheck > 0 && shortCircuit < dirCheck,
            "Single-file-path detection must run before the Directory existence validation.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
