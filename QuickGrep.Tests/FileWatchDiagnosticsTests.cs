using QuickGrep.Services;

namespace QuickGrep.Tests;

public class FileWatchDiagnosticsCoverageTests
{
    /// <summary>
    /// Save and restore static state to avoid polluting other tests.
    /// </summary>
    private static void RunWithCleanState(Action action)
    {
        // Clear, run test, then restore default pattern
        FileWatchDiagnostics.Clear();
        try
        {
            action();
        }
        finally
        {
            FileWatchDiagnostics.Clear();
            FileWatchDiagnostics.Add("lvl_jotun_gpm_rockskipcontest.sbp");
        }
    }

    [Fact]
    public void DefaultPattern_IsWatched()
    {
        Assert.True(FileWatchDiagnostics.IsWatched(@"C:\data\lvl_jotun_gpm_rockskipcontest.sbp"));
    }

    [Fact]
    public void DefaultPattern_CaseInsensitive()
    {
        Assert.True(FileWatchDiagnostics.IsWatched(@"C:\DATA\LVL_JOTUN_GPM_ROCKSKIPCONTEST.SBP"));
    }

    [Fact]
    public void IsWatched_ReturnsFalse_ForUnknownFile()
    {
        Assert.False(FileWatchDiagnostics.IsWatched(@"C:\normal\file.txt"));
    }

    [Fact]
    public void Add_And_IsWatched()
    {
        RunWithCleanState(() =>
        {
            FileWatchDiagnostics.Add("mypattern.dat");
            Assert.True(FileWatchDiagnostics.IsWatched(@"C:\some\path\mypattern.dat"));
        });
    }

    [Fact]
    public void Clear_RemovesAllPatterns()
    {
        RunWithCleanState(() =>
        {
            FileWatchDiagnostics.Add("pattern1");
            FileWatchDiagnostics.Clear();
            Assert.False(FileWatchDiagnostics.IsWatched("pattern1"));
        });
    }

    [Fact]
    public void IsWatched_EmptyPatterns_ReturnsFalse()
    {
        RunWithCleanState(() =>
        {
            // After Clear, patterns is empty
            Assert.False(FileWatchDiagnostics.IsWatched("anything"));
        });
    }

    [Fact]
    public void Checkpoint_DoesNotThrow()
    {
        // Just exercises the code path; Checkpoint logs via LogService
        FileWatchDiagnostics.Checkpoint(@"C:\test\file.txt", "test-phase", 100, "extra info");
        FileWatchDiagnostics.Checkpoint(@"C:\test\file.txt", "test-phase"); // no optional params
    }
}
