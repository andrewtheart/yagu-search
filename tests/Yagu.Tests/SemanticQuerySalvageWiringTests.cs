using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source pins for the deterministic salvage wiring in <c>MainViewModel</c> (a WinUI-coupled file not
/// compiled into the test assembly). The pure salvage logic is unit-tested in
/// <see cref="SemanticQuerySalvageTests"/>; this pins that the view-model actually invokes it on model
/// failure, surfaces the "best guess" status, and runs the salvaged plan like a real one.
/// </summary>
public sealed class SemanticQuerySalvageWiringTests
{
    private static string RepoRoot()
    {
        string dir = System.AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Yagu.slnx")))
            dir = Directory.GetParent(dir)?.FullName!;
        return dir ?? throw new DirectoryNotFoundException("Yagu.slnx not found above the test output directory.");
    }

    private static string ViewModel() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Yagu", "ViewModels", "MainViewModel.cs"));

    private static string CliRunner() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Yagu", "CliRunner.cs"));

    [Fact]
    public void SingleTokenQuery_SkipsModelInGuiAndCli()
    {
        string viewModel = ViewModel();
        string cli = CliRunner();

        Assert.Contains("if (SemanticQuerySalvage.IsSingleTokenQuery(text))", viewModel);
        Assert.Contains("return SemanticTranslationOutcome.Failed;", viewModel);

        int guard = cli.IndexOf("if (SemanticQuerySalvage.IsSingleTokenQuery(semanticText))", StringComparison.Ordinal);
        int translator = cli.IndexOf("new FoundryLocalSemanticQueryTranslator", guard, StringComparison.Ordinal);
        Assert.True(guard >= 0 && translator > guard, "CLI must bypass before constructing the AI translator.");
        Assert.Contains("return FallBackToTraditional(args);", cli[guard..translator]);
    }

    [Fact]
    public void TranslateSemanticQuery_OnModelFailure_TriesSalvageBeforeLiteralFallback()
    {
        string src = ViewModel();

        // A new outcome distinguishes a salvaged best-guess from a total failure.
        Assert.Contains("Salvaged,", src);

        // On no usable model plan, attempt the deterministic salvage, apply it, and surface a
        // "best guess" status that begins with the same "AI couldn't interpret that" phrasing.
        Assert.Contains("if (SemanticQuerySalvage.TryBuildPlan(text, out var salvagePlan))", src);
        Assert.Contains("SemanticPlanApplier.ApplyToTarget(salvagePlan, context, this)", src);
        Assert.Contains("AI couldn't interpret that \u2014 using our best guess: ", src);
        Assert.Contains("return SemanticTranslationOutcome.Salvaged;", src);
    }

    [Fact]
    public void SubmitSearch_TreatsSalvagedLikeApplied()
    {
        string src = ViewModel();
        // A salvaged plan arms the defaults snapshot (leaves the resolution visible) exactly like Applied.
        Assert.Contains("outcome is SemanticTranslationOutcome.Applied or SemanticTranslationOutcome.Salvaged", src);
        // The literal-fallback message stays for the genuine no-salvage case.
        Assert.Contains("AI couldn't interpret that \u2014 searching for the text directly.", src);
    }
}
