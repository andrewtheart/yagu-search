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
