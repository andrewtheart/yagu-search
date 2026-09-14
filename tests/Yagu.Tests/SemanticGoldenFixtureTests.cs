namespace Yagu.Tests;

internal static class SemanticGoldenFixture
{
    internal const string DefaultDirectory = "${DEFAULT_DIRECTORY}";
    internal static readonly string[] ComparedFields =
    [
        "directory", "pattern", "search-mode", "regex", "case-sensitive", "exact-match", "multiline",
        "include", "exclude", "min-size", "max-size", "created-after", "created-before", "modified-after",
        "modified-before", "sort", "group", "hidden", "archives", "binary", "image-text", "pdf-text", "obey-gitignore", "error",
    ];

    internal static Dictionary<string, Dictionary<string, string>> ParseTranscript(string transcript)
    {
        var plans = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string? query = null;
        Dictionary<string, string>? fields = null;
        using var reader = new StringReader(transcript);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("===QUERY=== ", StringComparison.Ordinal))
            {
                if (query is not null) throw new InvalidDataException($"Unfinished query block: {query}");
                query = line[12..].Trim();
                if (query.Length == 0 || plans.ContainsKey(query)) throw new InvalidDataException("Empty or duplicate query block.");
                fields = new(StringComparer.Ordinal);
            }
            else if (line == "===END===")
            {
                if (query is null) throw new InvalidDataException("Unexpected query end marker.");
                plans.Add(query, fields!);
                query = null;
                fields = null;
            }
            else if (fields is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                int separator = line.IndexOf(':');
                if (separator >= 0 && !fields.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim()))
                    throw new InvalidDataException($"Duplicate output field in query: {query}");
            }
        }
        if (query is not null) throw new InvalidDataException($"Unfinished query block: {query}");
        return plans;
    }

    internal static List<string> Compare(
        Dictionary<string, Dictionary<string, string>> expected,
        Dictionary<string, Dictionary<string, string>> actual,
        string defaultDirectory)
    {
        var mismatches = new List<string>();
        foreach (string query in actual.Keys.Except(expected.Keys, StringComparer.Ordinal))
            mismatches.Add($"[{query}] unexpected query output");
        foreach (var (query, fields) in expected)
        {
            if (!actual.TryGetValue(query, out var observed))
            {
                mismatches.Add($"[{query}] missing query output");
                continue;
            }
            foreach (string field in ComparedFields)
            {
                fields.TryGetValue(field, out string? wanted);
                observed.TryGetValue(field, out string? got);
                if (field == "directory" && wanted == DefaultDirectory) wanted = defaultDirectory;
                if (!string.Equals(wanted ?? "", got ?? "", StringComparison.Ordinal))
                    mismatches.Add($"[{query}] {field}: expected '{wanted ?? "(unset)"}', actual '{got ?? "(unset)"}'");
            }
        }
        return mismatches;
    }

    internal static void ValidateSuccessfulPlans(Dictionary<string, Dictionary<string, string>> plans)
    {
        if (plans.Count == 0)
            throw new InvalidDataException("The semantic golden fixture contains no plans.");
        foreach (var (query, fields) in plans)
        {
            if (fields.TryGetValue("error", out string? error) && !string.IsNullOrWhiteSpace(error))
                throw new InvalidDataException($"The semantic golden fixture records a model failure for '{query}': {error}");
            if (!fields.TryGetValue("pattern", out string? pattern) || string.IsNullOrWhiteSpace(pattern) || pattern == "(none)")
                throw new InvalidDataException($"The semantic golden fixture has no usable search pattern for '{query}'.");
        }
    }
}

public sealed class SemanticGoldenFixtureTests
{
    [Theory]
    [InlineData("===QUERY=== q\n  pattern : text")]
    [InlineData("===QUERY=== q\n===QUERY=== other\n===END===")]
    [InlineData("===QUERY=== q\n===END===\n===QUERY=== q\n===END===")]
    [InlineData("===END===")]
    [InlineData("===QUERY=== q\n  pattern : a\n  pattern : b\n===END===")]
    public void ParseTranscript_RejectsIncompleteOrDuplicateOutput(string transcript)
        => Assert.Throws<InvalidDataException>(() => SemanticGoldenFixture.ParseTranscript(transcript));

    [Fact]
    public void Compare_RequiresTheCompleteQuerySetAndActualDirectory()
    {
        var expected = new Dictionary<string, Dictionary<string, string>>
        {
            ["query"] = new() { ["directory"] = SemanticGoldenFixture.DefaultDirectory, ["pattern"] = "text" },
        };
        Assert.Single(SemanticGoldenFixture.Compare(expected, [], @"C:\scope"));
        var actual = SemanticGoldenFixture.ParseTranscript("===QUERY=== query\r\n  directory : C:\\scope\r\n  pattern : text\r\n===END===\r\n");
        Assert.Empty(SemanticGoldenFixture.Compare(expected, actual, @"C:\scope"));
        actual["query"]["directory"] = @"C:\elsewhere";
        Assert.Single(SemanticGoldenFixture.Compare(expected, actual, @"C:\scope"));
        actual["extra"] = [];
        Assert.Equal(2, SemanticGoldenFixture.Compare(expected, actual, @"C:\scope").Count);
    }

    [Fact]
    public void ReviewedFixture_HasNoEmptyOrFailedPlans()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string path = Path.Combine(directory!.FullName, "tests", "Yagu.Tests", "TestData", "SemanticEval", "expected-plans.json");
        var plans = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
        Assert.NotNull(plans);
        SemanticGoldenFixture.ValidateSuccessfulPlans(plans!);
    }

    [Fact]
    public void ValidateSuccessfulPlans_RejectsAnEmptyFixture()
        => Assert.Throws<InvalidDataException>(() => SemanticGoldenFixture.ValidateSuccessfulPlans([]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("(none)")]
    [InlineData("   ")]
    public void ValidateSuccessfulPlans_RejectsMissingPatterns(string? pattern)
    {
        var fields = new Dictionary<string, string>();
        if (pattern is not null) fields["pattern"] = pattern;
        Assert.Throws<InvalidDataException>(() => SemanticGoldenFixture.ValidateSuccessfulPlans(new() { ["query"] = fields }));
    }

    [Fact]
    public void ValidateSuccessfulPlans_RejectsTransientFailuresEvenWithAPattern()
    {
        var plans = new Dictionary<string, Dictionary<string, string>>
        {
            ["query"] = new() { ["pattern"] = "invoice", ["error"] = "Operation was cancelled" },
        };
        Assert.Throws<InvalidDataException>(() => SemanticGoldenFixture.ValidateSuccessfulPlans(plans));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateSuccessfulPlans_AcceptsSuccessfulExpectations(string? error)
    {
        var fields = new Dictionary<string, string> { ["pattern"] = "invoice" };
        if (error is not null) fields["error"] = error;
        SemanticGoldenFixture.ValidateSuccessfulPlans(new() { ["query"] = fields });
    }
}