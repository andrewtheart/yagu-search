using System.Diagnostics;
using Yagu.Services;

namespace Yagu.Tests;

[Collection("EditorLauncher")]
public class EditorLauncherTests : IDisposable
{
    public void Dispose()
    {
        EditorLauncher.TestProcessLauncher = null;
    }
    [Fact]
    public void DefaultCommand_IsVsCode()
    {
        Assert.Equal("code --goto \"{file}:{line}\"", EditorLauncher.DefaultCommand);
    }

    [Fact]
    public void Split_QuotedExe()
    {
        var launcher = new EditorLauncher { Command = "\"C:\\Program Files\\editor.exe\" --goto test.txt" };
        // Exercise Open which calls Split internally. We can't verify the exe/args directly
        // since Split is private, but Open will return false since the exe doesn't exist.
        bool result = launcher.Open("test.txt", 1);
        Assert.False(result); // exe doesn't exist
    }

    [Fact]
    public void Split_SimpleExe()
    {
        // Use a non-existent exe to exercise the non-quoted Split path without spawning a real process.
        var launcher = new EditorLauncher { Command = "fakeeditor_qg test.txt" };
        bool result = launcher.Open("test.txt", 1);
        Assert.False(result); // exe doesn't exist
    }

    [Fact]
    public void Split_NoArguments()
    {
        var launcher = new EditorLauncher { Command = "fakeeditor_qg.exe" };
        bool result = launcher.Open("test.txt", 1);
        Assert.False(result); // exe doesn't exist
    }

    [Fact]
    public void Split_EmptyCommand()
    {
        var launcher = new EditorLauncher { Command = "" };
        bool result = launcher.Open("test.txt", 1);
        Assert.False(result);
    }

    [Fact]
    public void Split_WhitespaceCommand()
    {
        var launcher = new EditorLauncher { Command = "   " };
        bool result = launcher.Open("test.txt", 1);
        Assert.False(result);
    }

    [Fact]
    public void Open_ReplacesPlaceholders()
    {
        // A command with non-existent exe to exercise the replacement logic
        var launcher = new EditorLauncher { Command = "nonexistent_editor_xyz --file \"{file}\" --line {line}" };
        bool result = launcher.Open(@"C:\test\file.txt", 42);
        Assert.False(result); // exe doesn't exist
    }

    [Fact]
    public void OpenContainingFolder_WithValidFile_ReturnsTrue()
    {
        EditorLauncher.TestProcessLauncher = _ => { };
        var tempFile = Path.GetTempFileName();
        try
        {
            bool result = EditorLauncher.OpenContainingFolder(tempFile);
            Assert.True(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void OpenContainingFolder_WithEmptyPath_ReturnsFalse()
    {
        bool result = EditorLauncher.OpenContainingFolder("");
        Assert.False(result);
    }

    [Fact]
    public void OpenContainingFolder_WithFileNameOnly_ReturnsFalse()
    {
        // Path.GetDirectoryName("file.txt") returns "" on some platforms
        bool result = EditorLauncher.OpenContainingFolder("file.txt");
        Assert.False(result);
    }

    [Fact]
    public void OpenTerminalAt_WithValidFile_ReturnsTrue()
    {
        EditorLauncher.TestProcessLauncher = _ => { };
        var tempFile = Path.GetTempFileName();
        try
        {
            bool result = EditorLauncher.OpenTerminalAt(tempFile);
            Assert.True(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void OpenTerminalAt_WithEmptyPath_ReturnsFalse()
    {
        bool result = EditorLauncher.OpenTerminalAt("");
        Assert.False(result);
    }

    [Fact]
    public void OpenTerminalAt_WithFileNameOnly_ReturnsFalse()
    {
        bool result = EditorLauncher.OpenTerminalAt("file.txt");
        Assert.False(result);
    }
}

// ─── EditorLauncher.SplitCommandLine ────────────────────────────────────

[Collection("EditorLauncher")]
public class EditorLauncherSplitTests
{
    [Fact]
    public void Split_EmptyString_ReturnsEmptyExeAndArgs()
    {
        var (exe, args) = EditorLauncher.Split("");
        Assert.Equal("", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Split_WhitespaceOnly_ReturnsEmptyExeAndArgs()
    {
        var (exe, args) = EditorLauncher.Split("   ");
        Assert.Equal("", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Split_QuotedExe_WithArgs()
    {
        var (exe, args) = EditorLauncher.Split("\"C:\\Program Files\\app.exe\" --flag value");
        Assert.Equal("C:\\Program Files\\app.exe", exe);
        Assert.Equal("--flag value", args);
    }

    [Fact]
    public void Split_SimpleExe_NoArgs()
    {
        var (exe, args) = EditorLauncher.Split("notepad.exe");
        Assert.Equal("notepad.exe", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Split_SimpleExe_WithArgs()
    {
        var (exe, args) = EditorLauncher.Split("code --goto file:10");
        Assert.Equal("code", exe);
        Assert.Equal("--goto file:10", args);
    }

    [Fact]
    public void Split_QuotedExe_NoClosingQuote()
    {
        var (exe, args) = EditorLauncher.Split("\"C:\\unclosed\\path");
        // No closing quote → falls through to space-split; no space → entire string is exe
        Assert.Equal("\"C:\\unclosed\\path", exe);
        Assert.Equal("", args);
    }
}

// ─── EditorLauncher: Open / OpenContainingFolder / OpenTerminalAt ───

[Collection("EditorLauncher")]
public class EditorLauncherSeamTests : IDisposable
{
    private readonly string _root;

    public EditorLauncherSeamTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        EditorLauncher.TestProcessLauncher = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Open_Success_ReturnsTrue_CapturesPsi()
    {
        ProcessStartInfo? captured = null;
        EditorLauncher.TestProcessLauncher = psi => captured = psi;
        var launcher = new EditorLauncher { Command = "code.exe -g \"{file}\":{line}" };
        bool result = launcher.Open(Path.Combine(_root, "test.txt"), 42);
        Assert.True(result);
        Assert.NotNull(captured);
        Assert.Equal("code.exe", captured!.FileName);
        Assert.Contains("42", captured.Arguments);
    }

    [Fact]
    public void Open_ProcessLauncherThrows_ReturnsFalse()
    {
        EditorLauncher.TestProcessLauncher = _ => throw new InvalidOperationException("test");
        var launcher = new EditorLauncher { Command = "code.exe \"{file}\"" };
        bool result = launcher.Open("test.txt", 1);
        Assert.False(result);
    }

    [Fact]
    public void OpenContainingFolder_Success_ReturnsTrue()
    {
        ProcessStartInfo? captured = null;
        EditorLauncher.TestProcessLauncher = psi => captured = psi;
        var filePath = Path.Combine(_root, "test.txt");
        File.WriteAllText(filePath, "hi");
        bool result = EditorLauncher.OpenContainingFolder(filePath);
        Assert.True(result);
        Assert.NotNull(captured);
        Assert.Equal("explorer.exe", captured!.FileName);
    }

    [Fact]
    public void OpenContainingFolder_EmptyDir_ReturnsFalse()
    {
        bool result = EditorLauncher.OpenContainingFolder("");
        Assert.False(result);
    }

    [Fact]
    public void OpenContainingFolder_Throws_ReturnsFalse()
    {
        EditorLauncher.TestProcessLauncher = _ => throw new Exception("test");
        bool result = EditorLauncher.OpenContainingFolder(Path.Combine(_root, "test.txt"));
        Assert.False(result);
    }

    [Fact]
    public void OpenTerminalAt_Success_ReturnsTrue()
    {
        ProcessStartInfo? captured = null;
        EditorLauncher.TestProcessLauncher = psi => captured = psi;
        var filePath = Path.Combine(_root, "test.txt");
        File.WriteAllText(filePath, "hi");
        bool result = EditorLauncher.OpenTerminalAt(filePath);
        Assert.True(result);
        Assert.NotNull(captured);
        Assert.Equal("wt.exe", captured!.FileName);
    }

    [Fact]
    public void OpenTerminalAt_EmptyPath_ReturnsFalse()
    {
        bool result = EditorLauncher.OpenTerminalAt("");
        Assert.False(result);
    }

    [Fact]
    public void OpenTerminalAt_WtFails_FallsToPowershell()
    {
        int callCount = 0;
        ProcessStartInfo? captured = null;
        EditorLauncher.TestProcessLauncher = psi =>
        {
            callCount++;
            if (psi.FileName == "wt.exe") throw new Exception("wt not found");
            captured = psi;
        };
        var filePath = Path.Combine(_root, "test.txt");
        File.WriteAllText(filePath, "hi");
        bool result = EditorLauncher.OpenTerminalAt(filePath);
        Assert.True(result);
        Assert.Equal(2, callCount);
        Assert.Equal("powershell.exe", captured!.FileName);
    }

    [Fact]
    public void OpenTerminalAt_BothFail_ReturnsFalse()
    {
        EditorLauncher.TestProcessLauncher = _ => throw new Exception("fail");
        var filePath = Path.Combine(_root, "test.txt");
        File.WriteAllText(filePath, "hi");
        bool result = EditorLauncher.OpenTerminalAt(filePath);
        Assert.False(result);
    }
}
