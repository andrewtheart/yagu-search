using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source pins for the Settings "current AI model" display + "Refresh Foundry cache" button. These
/// touch WinUI/Foundry-coupled files (SettingsWindow, MainViewModel, the translator) that are not
/// compiled into this assembly, so they are validated by asserting the source shape.
/// </summary>
public sealed class SemanticModelRefreshTests
{
    [Fact]
    public void Interface_ExposesRefreshCatalog()
    {
        string iface = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs"));
        Assert.Contains("void RefreshCatalog();", iface);
    }

    [Fact]
    public void Translator_RefreshCatalog_ClearsCatalogAndLoadedModel()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));
        Assert.Contains("public void RefreshCatalog()", src);
        // Must drop the cached catalog AND the loaded model so the next use re-queries Foundry Local.
        Assert.Contains("_catalog = null;", src);
        Assert.Contains("SelectedModelAlias = null;", src);
    }

    [Fact]
    public void ViewModel_CurrentModelDisplay_SurfacesLoadedAutomaticModel_AndResolveRefresh()
    {
        string vm = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "ViewModels", "MainViewModel.cs"));
        // Automatic mode surfaces the actually-loaded model name, not just a generic label.
        Assert.Contains("(_semanticTranslator as FoundryLocalSemanticQueryTranslator)?.SelectedModelAlias", vm);
        Assert.Contains("(automatic)", vm);
        // Resolve + refresh methods; refresh clears the catalog then re-resolves.
        Assert.Contains("public async Task<string> ResolveCurrentSemanticModelDisplayAsync(", vm);
        Assert.Contains("public async Task<string> RefreshFoundryCacheAsync(", vm);
        Assert.Contains("_semanticTranslator?.RefreshCatalog();", vm);
        Assert.Contains("options.FirstOrDefault(o => o.IsRecommended)", vm);
    }

    [Fact]
    public void SettingsWindow_HasRefreshFoundryCacheButton_AndResolvesCurrentModel()
    {
        string settings = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
        Assert.Contains("Refresh Foundry cache", settings);
        Assert.Contains("_viewModel.RefreshFoundryCacheAsync(", settings);
        // Background resolve so the actual current model shows without needing a click.
        Assert.Contains("_viewModel.ResolveCurrentSemanticModelDisplayAsync(", settings);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
