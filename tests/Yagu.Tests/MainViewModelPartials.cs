namespace Yagu.Tests;

/// <summary>
/// Reads the <c>MainViewModel</c> production source for source-pin tests.
///
/// <para>MainViewModel is WinUI-coupled, so it is not compiled into Yagu.Tests and is pinned by
/// scraping its source. It is split across <c>MainViewModel*.cs</c> partial-class files (the same
/// convention as <c>MainWindow*.cs</c>), so a pin must read <b>every</b> partial — otherwise moving a
/// member from one partial to another would silently turn an assertion into a no-op.</para>
/// </summary>
internal static class MainViewModelPartials
{
    private static readonly Lazy<string> LazyText = new(ReadAllPartials);

    /// <summary>Every <c>src/Yagu/ViewModels/MainViewModel*.cs</c> partial, concatenated.</summary>
    public static string Text => LazyText.Value;

    private static string ReadAllPartials()
    {
        string viewModelsDir = Path.Combine(FindRepoRoot(), "src", "Yagu", "ViewModels");
        var sources = Directory.GetFiles(viewModelsDir, "MainViewModel*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText);
        return string.Join(Environment.NewLine, sources);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
