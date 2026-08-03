using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class StartupArgsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _exePath;

    public StartupArgsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yagu-startupargs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // A real file so we can prove ParsePositionalDirectory never matches a file (e.g. the exe path).
        _exePath = Path.Combine(_dir, "Yagu.exe");
        File.WriteAllText(_exePath, "stub");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- ParseStringArg ----

    [Fact]
    public void ParseStringArg_SpaceSeparated_ReturnsValue()
    {
        var args = new[] { "--query", "hello world" };
        Assert.Equal("hello world", StartupArgs.ParseStringArg(args, "--query"));
    }

    [Fact]
    public void ParseStringArg_EqualsForm_ReturnsValue()
    {
        var args = new[] { "--query=needle" };
        Assert.Equal("needle", StartupArgs.ParseStringArg(args, "--query"));
    }

    [Fact]
    public void ParseStringArg_TrimsSurroundingQuotes()
    {
        var args = new[] { "--dir", "\"C:\\Program Files\"" };
        Assert.Equal("C:\\Program Files", StartupArgs.ParseStringArg(args, "--dir"));
    }

    [Fact]
    public void ParseStringArg_IsCaseInsensitive()
    {
        var args = new[] { "--QUERY", "x" };
        Assert.Equal("x", StartupArgs.ParseStringArg(args, "--query"));
    }

    [Fact]
    public void ParseStringArg_Absent_ReturnsNull()
    {
        Assert.Null(StartupArgs.ParseStringArg(new[] { "--other", "v" }, "--query"));
    }

    [Fact]
    public void ParseStringArg_NullArgs_ReturnsNull()
    {
        Assert.Null(StartupArgs.ParseStringArg(null, "--query"));
    }

    // ---- ParsePositionalDirectory ----

    [Fact]
    public void ParsePositionalDirectory_BareExistingDirectory_ReturnsIt()
    {
        // Simulates the Explorer context menu: Yagu.exe "C:\folder".
        var args = new[] { _exePath, _dir };
        Assert.Equal(_dir, StartupArgs.ParsePositionalDirectory(args));
    }

    [Fact]
    public void ParsePositionalDirectory_OnlyExePath_ReturnsNull()
    {
        // args[0] is the executable path (a file, not a directory) — must never be treated as a folder.
        var args = new[] { _exePath };
        Assert.Null(StartupArgs.ParsePositionalDirectory(args));
    }

    [Fact]
    public void ParsePositionalDirectory_NonExistentPath_ReturnsNull()
    {
        var args = new[] { Path.Combine(_dir, "does-not-exist") };
        Assert.Null(StartupArgs.ParsePositionalDirectory(args));
    }

    [Fact]
    public void ParsePositionalDirectory_SkipsFlags()
    {
        var args = new[] { "--cli", "-x", _dir };
        Assert.Equal(_dir, StartupArgs.ParsePositionalDirectory(args));
    }

    [Fact]
    public void ParsePositionalDirectory_SkipsValueFlagAndItsValue()
    {
        // The value of --query happens to be the directory path; it must not be picked up as a positional dir.
        var args = new[] { "--query", _dir };
        Assert.Null(StartupArgs.ParsePositionalDirectory(args));
    }

    [Fact]
    public void ParsePositionalDirectory_QuotedDirectory_IsUnquoted()
    {
        var args = new[] { "\"" + _dir + "\"" };
        Assert.Equal(_dir, StartupArgs.ParsePositionalDirectory(args));
    }

    // ---- ParseDirectory ----

    [Fact]
    public void ParseDirectory_ExplicitDirFlag_TakesPrecedence()
    {
        var other = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(other);
        var args = new[] { other, "--dir", _dir };
        Assert.Equal(_dir, StartupArgs.ParseDirectory(args));
    }

    [Fact]
    public void ParseDirectory_NoDirFlag_FallsBackToPositional()
    {
        var args = new[] { _exePath, _dir };
        Assert.Equal(_dir, StartupArgs.ParseDirectory(args));
    }

    [Fact]
    public void ParseDirectory_Nothing_ReturnsNull()
    {
        Assert.Null(StartupArgs.ParseDirectory(new[] { _exePath, "--cli" }));
    }
}
