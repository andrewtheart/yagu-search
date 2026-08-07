using System.Security.Cryptography;

namespace Yagu.Tests;

public sealed class MatchNavTestCorpusTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "yagu-match-nav-corpus-tests-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> ScenarioIds()
        => MatchNavTestCorpus.Scenarios.Select(scenario => new object[] { scenario.Id });

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void Create_ProducesExactFilesAndOccurrences(string scenarioId)
    {
        MatchNavScenario scenario = MatchNavTestCorpus.Get(scenarioId);
        string directory = Path.Combine(_root, scenario.Id);

        MatchNavTestCorpus.Create(directory, scenario);

        Assert.Equal(scenario.FileCount, Directory.GetFiles(directory, "*.txt").Length);
        Assert.Equal(scenario.ExpectedMatches, MatchNavTestCorpus.CountMatches(directory, scenario));
        Assert.All(scenario.MatchSamples, sample => Assert.Equal(scenario.ExpectedHighlightLength, sample.Length));
    }

    [Fact]
    public void Scenarios_CoverRegexMultilineExactAndFocusedTwoTermCombinations()
    {
        Assert.Contains(MatchNavTestCorpus.Scenarios, scenario => !scenario.UseRegex);
        Assert.Contains(MatchNavTestCorpus.Scenarios, scenario => scenario.UseRegex);
        Assert.Contains(MatchNavTestCorpus.Scenarios, scenario => !scenario.Multiline);
        Assert.Contains(MatchNavTestCorpus.Scenarios, scenario => scenario.Multiline);
        Assert.Contains(MatchNavTestCorpus.Scenarios, scenario => scenario.ExactMatch);
        Assert.Contains(MatchNavTestCorpus.Scenarios, scenario => !scenario.ExactMatch);

        MatchNavScenario[] twoTermScenarios = MatchNavTestCorpus.Scenarios
            .Where(scenario => !scenario.UseRegex && !scenario.ExactMatch && !scenario.Multiline)
            .ToArray();
        Assert.True(twoTermScenarios.Length >= 3);
        Assert.All(twoTermScenarios, scenario =>
        {
            Assert.Contains(' ', scenario.Query);
            Assert.Equal(2, Yagu.Services.SearchQueryParser.ParseLiteralTerms(scenario.Query, exactMatch: false).Count);
            Assert.Equal(2, scenario.MatchSamples.Length);
        });
    }

    [Fact]
    public void LargeRandomFiles_AreLargeSparseAndDeterministic()
    {
        MatchNavScenario scenario = MatchNavTestCorpus.Get("literal-two-symbol-terms-large");
        string first = Path.Combine(_root, "large-first");
        string second = Path.Combine(_root, "large-second");
        MatchNavTestCorpus.Create(first, scenario);
        MatchNavTestCorpus.Create(second, scenario);

        string[] firstFiles = Directory.GetFiles(first, "*.txt").Order().ToArray();
        string[] secondFiles = Directory.GetFiles(second, "*.txt").Order().ToArray();
        Assert.True(firstFiles.Sum(path => new FileInfo(path).Length) >= 2_500_000);
        Assert.Equal(scenario.ExpectedMatches, MatchNavTestCorpus.CountMatches(first, scenario));
        Assert.Equal(HashFiles(firstFiles), HashFiles(secondFiles));
    }

    [Fact]
    public void ManySingleLongLines_HaveOneMatchOnOneVeryLongLinePerFile()
    {
        MatchNavScenario scenario = MatchNavTestCorpus.Get("literal-two-terms-long-lines");
        string directory = Path.Combine(_root, scenario.Id);
        MatchNavTestCorpus.Create(directory, scenario);

        foreach (string path in Directory.GetFiles(directory, "*.txt"))
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('\r', text);
            Assert.True(text.Length >= 60_000);
            Assert.Equal(1, scenario.MatchSamples.Count(text.Contains));
        }
    }

    [Fact]
    public void DenseSingleLines_HaveSeveralSeparatedOccurrencesPerFile()
    {
        MatchNavScenario scenario = MatchNavTestCorpus.Get("regex-random-alternation-dense");
        string directory = Path.Combine(_root, scenario.Id);
        MatchNavTestCorpus.Create(directory, scenario);

        Assert.All(Directory.GetFiles(directory, "*.txt"), path =>
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotContain('\n', text);
            Assert.Equal(8, scenario.MatchSamples.Sum(sample => CountInFile(path, sample)));
        });
    }

    [Fact]
    public void MultilinePairs_HaveTwoCrossLineMatchesPerFile()
    {
        MatchNavScenario scenario = MatchNavTestCorpus.Get("multiline-random-pairs");
        string directory = Path.Combine(_root, scenario.Id);
        MatchNavTestCorpus.Create(directory, scenario);

        Assert.All(Directory.GetFiles(directory, "*.txt"), path =>
        {
            string normalized = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(2, CountInText(normalized, $"{scenario.MatchSamples[0]}\n{scenario.MatchSamples[1]}"));
        });
    }

    [Fact]
    public void VariedLineLengths_MixShortMediumAndPathologicalMatchedLinesPerFile()
    {
        MatchNavScenario scenario = MatchNavTestCorpus.Get("literal-two-terms-varied-lines");
        string directory = Path.Combine(_root, scenario.Id);
        MatchNavTestCorpus.Create(directory, scenario);

        Assert.All(Directory.GetFiles(directory, "*.txt"), path =>
        {
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.InRange(lines[0].Length, 90, 140);
            Assert.InRange(lines[1].Length, 4_000, 5_000);
            Assert.True(lines[2].Length >= 180_000);
            Assert.All(lines, line => Assert.Equal(1, scenario.MatchSamples.Count(line.Contains)));
        });
    }

    private static int CountInFile(string path, string query)
    {
        string text = File.ReadAllText(path);
        return CountInText(text, query);
    }

    private static int CountInText(string text, string query)
    {
        int count = 0;
        for (int start = 0; (start = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase)) >= 0; start += query.Length)
            count++;
        return count;
    }

    private static string HashFiles(IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths)
            hash.AppendData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}