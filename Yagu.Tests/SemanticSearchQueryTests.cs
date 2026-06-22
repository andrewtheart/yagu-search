using System.Linq;
using System.Threading.Tasks;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Data-driven "semantic" search query suite: a large catalog of diverse scenarios
/// exercising combinations and permutations of Yagu search capabilities and options
/// (literal/substring/whole-word/regex, case sensitivity, search modes, include/exclude
/// glob and regex filters, size and date ranges, result limits, depth, skip-extensions,
/// binary, hidden files, multi-term queries). Each scenario builds its own synthetic
/// directory tree and asserts an explicit expected result.
///
/// Runs against the real <see cref="SearchService"/> pipeline with the deterministic
/// Managed file-lister backend (matching <c>SearchServiceTests</c>). The suite is
/// serialized into the FileListerBackend collection so the static backend toggle is
/// safe.
/// </summary>
[Collection("FileListerBackend")]
public sealed class SemanticSearchQueryTests : IDisposable
{
    private readonly FileListerBackend _originalBackend;

    public SemanticSearchQueryTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
    }

    public void Dispose() => FileLister.Backend = _originalBackend;

    public static IEnumerable<object[]> ScenarioNames =>
        SemanticSearchQueryCatalog.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Scenario(string name)
    {
        var scenario = SemanticSearchQueryCatalog.Get(name);
        await SearchScenarioRunner.RunAsync(scenario);
    }

    [Fact]
    public void Catalog_HasUniqueNames()
    {
        var names = SemanticSearchQueryCatalog.All.Select(s => s.Name).ToList();
        var duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate scenario names: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void Catalog_HasExactly300Scenarios()
    {
        Assert.Equal(300, SemanticSearchQueryCatalog.All.Count);
    }
}
