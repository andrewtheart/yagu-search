using System.Collections.Generic;
using System.Linq;

namespace Yagu.Tests;

/// <summary>
/// Catalog of the diverse "semantic" search query scenarios. The scenarios are
/// authored in capability-grouped partial files (Literal, Regex, Filters, Modes,
/// SizeDate, Limits, Misc). Each scenario fully specifies its synthetic corpus,
/// search options, and expected outcome.
/// </summary>
public static partial class SemanticSearchQueryCatalog
{
    private static readonly Lazy<IReadOnlyList<SearchScenario>> AllLazy = new(BuildAll);
    private static readonly Lazy<IReadOnlyDictionary<string, SearchScenario>> ByNameLazy =
        new(() => AllLazy.Value.ToDictionary(s => s.Name, StringComparer.Ordinal));

    public static IReadOnlyList<SearchScenario> All => AllLazy.Value;

    public static SearchScenario Get(string name) => ByNameLazy.Value[name];

    private static IReadOnlyList<SearchScenario> BuildAll()
    {
        var list = new List<SearchScenario>();
        list.AddRange(LiteralScenarios());
        list.AddRange(RegexScenarios());
        list.AddRange(FilterScenarios());
        list.AddRange(ModeScenarios());
        list.AddRange(SizeDateScenarios());
        list.AddRange(LimitScenarios());
        list.AddRange(MiscScenarios());
        return list;
    }
}
