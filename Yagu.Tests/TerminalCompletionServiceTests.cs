using Yagu.Services;

namespace Yagu.Tests;

public sealed class TerminalCompletionServiceTests : IDisposable
{
    private readonly string _tempDir;

    public TerminalCompletionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yagu-terminal-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Complete_CompletesQuotedDirectoryFromPromptWorkingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Alpha Beta"));

        var result = TerminalCompletionService.Complete(
            requestId: 42,
            input: "cd \"Al",
            cursor: "cd \"Al".Length,
            promptText: _tempDir + ">",
            fallbackWorkingDirectory: Directory.GetCurrentDirectory());

        Assert.True(result.HasMatches);
        Assert.Equal(42, result.RequestId);
        Assert.Equal(4, result.ReplacementStart);
        Assert.Equal(2, result.ReplacementLength);
        Assert.Equal("Alpha Beta" + Path.DirectorySeparatorChar, result.ReplacementText);
        Assert.Contains("Alpha Beta" + Path.DirectorySeparatorChar, result.Suggestions);
    }

    [Fact]
    public void Complete_CompletesBuiltInCommandAtCommandPosition()
    {
        var result = TerminalCompletionService.Complete(
            requestId: 7,
            input: "ech",
            cursor: 3,
            promptText: _tempDir + ">",
            fallbackWorkingDirectory: _tempDir);

        Assert.True(result.HasMatches);
        Assert.Equal(0, result.ReplacementStart);
        Assert.Equal(3, result.ReplacementLength);
        Assert.Equal("echo ", result.ReplacementText);
        Assert.Contains("echo", result.Suggestions);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}

public sealed class TerminalCompletionServiceBranchTests : IDisposable
{
    private readonly string _tempDir;

    public TerminalCompletionServiceBranchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yagu-tcs-branch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Complete_EmptyInput_NoMatches()
    {
        var result = TerminalCompletionService.Complete(1, "", 0, _tempDir + ">", _tempDir);
        Assert.Equal(1, result.RequestId);
    }

    [Fact]
    public void Complete_NullInput_HandledGracefully()
    {
        var result = TerminalCompletionService.Complete(1, null!, 0, _tempDir + ">", _tempDir);
        Assert.Equal(1, result.RequestId);
    }

    [Fact]
    public void Complete_CursorBeyondInputLength_Clamped()
    {
        var result = TerminalCompletionService.Complete(1, "cd", 999, _tempDir + ">", _tempDir);
        Assert.Equal(1, result.RequestId);
    }

    [Fact]
    public void Complete_CursorNegative_Clamped()
    {
        var result = TerminalCompletionService.Complete(1, "cd", -5, _tempDir + ">", _tempDir);
        Assert.Equal(1, result.RequestId);
    }

    [Fact]
    public void Complete_AfterPipe_TreatsAsCommandPosition()
    {
        var result = TerminalCompletionService.Complete(1, "dir |ec", 7, _tempDir + ">", _tempDir);
        Assert.True(result.HasMatches);
        Assert.Contains("echo", result.Suggestions);
    }

    [Fact]
    public void Complete_AfterAmpersand_TreatsAsCommandPosition()
    {
        var result = TerminalCompletionService.Complete(1, "cls &ec", 7, _tempDir + ">", _tempDir);
        Assert.True(result.HasMatches);
        Assert.Contains("echo", result.Suggestions);
    }

    [Fact]
    public void Complete_PathWithDotSlash_TreatsAsPathToken()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "subfolder"));
        var result = TerminalCompletionService.Complete(1, @".\sub", 5, _tempDir + ">", _tempDir);
        Assert.True(result.HasMatches);
        Assert.Contains(@".\subfolder\", result.Suggestions);
    }

    [Fact]
    public void Complete_PathWithTilde_TreatsAsPathToken()
    {
        var result = TerminalCompletionService.Complete(1, "~", 1, _tempDir + ">", _tempDir);
        Assert.Equal(1, result.RequestId);
    }

    [Fact]
    public void Complete_PathWithSlash_TreatsAsPathToken()
    {
        var result = TerminalCompletionService.Complete(1, "some/pa", 7, _tempDir + ">", _tempDir);
        Assert.Equal(1, result.RequestId);
    }

    [Fact]
    public void Complete_NonexistentDirectory_ReturnsNoMatches()
    {
        var result = TerminalCompletionService.Complete(
            1, @"Z:\nonexistent\path\xyz", 22, _tempDir + ">", _tempDir);
        Assert.False(result.HasMatches);
    }

    [Fact]
    public void Complete_FileCompletion_ListsFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(_tempDir, "readme.md"), "world");

        var result = TerminalCompletionService.Complete(1, "type read", 9, _tempDir + ">", _tempDir);
        Assert.True(result.HasMatches);
        Assert.Contains(result.Suggestions, s => s.Contains("readme"));
    }

    [Fact]
    public void Complete_MultipleMatches_ReturnsCommonPrefix()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "alpha1"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "alpha2"));

        var result = TerminalCompletionService.Complete(1, "cd al", 5, _tempDir + ">", _tempDir);
        Assert.True(result.HasMatches);
        Assert.True(result.Suggestions.Count >= 2);
        Assert.StartsWith("alpha", result.ReplacementText);
    }

    [Fact]
    public void Complete_SingleDirectoryMatch_AppendsBackslash()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "uniquedir"));

        var result = TerminalCompletionService.Complete(1, "cd uniqu", 8, _tempDir + ">", _tempDir);
        Assert.True(result.HasMatches);
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), result.ReplacementText);
    }

    [Fact]
    public void Complete_InvalidPromptText_FallsBackToFallbackDirectory()
    {
        File.WriteAllText(Path.Combine(_tempDir, "fallback.txt"), "x");
        var result = TerminalCompletionService.Complete(1, "type fall", 9, "not a prompt", _tempDir);
        Assert.True(result.HasMatches);
    }

    [Fact]
    public void Complete_EmptyPromptText_FallsBackToFallbackDirectory()
    {
        File.WriteAllText(Path.Combine(_tempDir, "fb2.txt"), "x");
        var result = TerminalCompletionService.Complete(1, "type fb", 7, "", _tempDir);
        Assert.True(result.HasMatches);
    }

    [Fact]
    public void Complete_QuotedTokenAtCommandPosition_CompletesPath()
    {
        File.WriteAllText(Path.Combine(_tempDir, "my file.txt"), "x");
        var result = TerminalCompletionService.Complete(1, "\"my ", 4, _tempDir + ">", _tempDir);
        Assert.Equal(1, result.RequestId);
    }

    [Fact]
    public void Complete_DirectoryWithSpace_QuotesResult()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "has space"));
        var result = TerminalCompletionService.Complete(1, "cd has", 6, _tempDir + ">", _tempDir);
        Assert.True(result.HasMatches);
        Assert.Contains(result.Suggestions, s => s.Contains("has space"));
    }
}
