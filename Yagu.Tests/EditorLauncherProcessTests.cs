using System.Diagnostics;
using Yagu.Services;

namespace Yagu.Tests;

[Collection("EditorLauncher")]
public sealed class EditorLauncherProcessTests : IDisposable
{
    public void Dispose()
    {
        EditorLauncher.TestProcessLauncher = null;
    }

    [Fact]
    public void LaunchProcess_UsesTestSeam()
    {
        ProcessStartInfo? captured = null;
        EditorLauncher.TestProcessLauncher = psi => captured = psi;

        var launcher = new EditorLauncher { Command = "testexe {file}" };
        launcher.Open("myfile.txt", 1);

        Assert.NotNull(captured);
        Assert.Equal("testexe", captured!.FileName);
    }

    [Fact]
    public void OpenTerminalAt_UsesTestSeam()
    {
        ProcessStartInfo? captured = null;
        EditorLauncher.TestProcessLauncher = psi => captured = psi;

        var tempFile = Path.GetTempFileName();
        try
        {
            bool result = EditorLauncher.OpenTerminalAt(tempFile);
            Assert.True(result);
            Assert.NotNull(captured);
            Assert.Equal("wt.exe", captured!.FileName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void OpenTerminalAt_EmptyDir_ReturnsFalse()
    {
        EditorLauncher.TestProcessLauncher = _ => { };
        // A filename without a directory component
        bool result = EditorLauncher.OpenTerminalAt("justfilename.txt");
        Assert.False(result);
    }
}
