using System.Text;
using System.Text.RegularExpressions;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.Tests;

internal enum MatchNavCorpusShape
{
    MixedSmallFiles,
    LargeRandomFiles,
    ManySingleLongLines,
    DenseSingleLines,
    MultilinePairs,
    VariedLineLengths,
}

internal sealed record MatchNavScenario(
    string Id,
    string Query,
    MatchNavCorpusShape Shape,
    int FileCount,
    int ExpectedMatches,
    int MatchIterations,
    int MinimumScreenshots,
    int SearchWaitSeconds,
    bool UseRegex,
    bool ExactMatch,
    bool Multiline,
    string[] MatchSamples,
    int ExpectedHighlightLength);

internal static class MatchNavTestCorpus
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<MatchNavScenario> Scenarios { get; } = BuildScenarios();

    public static MatchNavScenario Get(string id)
        => Scenarios.Single(scenario => string.Equals(scenario.Id, id, StringComparison.Ordinal));

    public static void Create(string directory, MatchNavScenario scenario)
    {
        Directory.CreateDirectory(directory);
        switch (scenario.Shape)
        {
            case MatchNavCorpusShape.MixedSmallFiles:
                CreateMixedSmallFiles(directory, scenario);
                break;
            case MatchNavCorpusShape.LargeRandomFiles:
                CreateLargeRandomFiles(directory, scenario);
                break;
            case MatchNavCorpusShape.ManySingleLongLines:
                CreateManySingleLongLines(directory, scenario);
                break;
            case MatchNavCorpusShape.DenseSingleLines:
                CreateDenseSingleLines(directory, scenario);
                break;
            case MatchNavCorpusShape.MultilinePairs:
                CreateMultilinePairs(directory, scenario);
                break;
            case MatchNavCorpusShape.VariedLineLengths:
                CreateVariedLineLengths(directory, scenario);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        int actualMatches = CountMatches(directory, scenario);
        if (actualMatches != scenario.ExpectedMatches)
        {
            throw new InvalidOperationException(
                $"Corpus '{scenario.Id}' produces {actualMatches} matches for '{scenario.Query}', " +
                $"expected {scenario.ExpectedMatches}.");
        }
    }

    public static int CountMatches(string directory, MatchNavScenario scenario)
    {
        var options = new SearchOptions
        {
            Directory = directory,
            Query = scenario.Query,
            UseRegex = scenario.UseRegex,
            ExactMatch = scenario.ExactMatch,
            Multiline = scenario.Multiline,
        };
        EffectiveSearchPattern effective = EffectiveSearchPattern.Resolve(options);
        string pattern = effective.IsRegex ? effective.Pattern : Regex.Escape(effective.Pattern);
        Regex regex = SearchRegexFactory.Build(pattern, options);

        int count = 0;
        foreach (string path in Directory.GetFiles(directory, "*.txt").OrderBy(path => path, StringComparer.Ordinal))
        {
            if (scenario.Multiline)
            {
                string normalized = File.ReadAllText(path)
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n');
                count += regex.Count(normalized);
                continue;
            }

            foreach (string line in File.ReadLines(path))
                count += regex.Count(line);
        }
        return count;
    }

    private static IReadOnlyList<MatchNavScenario> BuildScenarios()
    {
        string exactLeft = RandomToken(0x1101, 9);
        string exactRight = RandomToken(0x1102, 9);
        string exactPhrase = $"{exactLeft} {exactRight}";

        string twoTermA = RandomToken(0x2201, 10);
        string twoTermB = RandomToken(0x2202, 10);
        string largeTermA = RandomToken(0x3301, 12);
        string largeTermB = RandomToken(0x3302, 12);
        string longTermA = RandomToken(0x4401, 11);
        string longTermB = RandomToken(0x4402, 11);
        string variedTermA = RandomToken(0x4451, 12);
        string variedTermB = RandomToken(0x4452, 12);
        string denseLiteral = RandomToken(0x5501, 10);

        string regexBase = RandomToken(0x6601, 8);
        string regexSampleA = regexBase + "A7";
        string regexSampleB = regexBase + "B4";
        string regexPattern = Regex.Escape(regexBase) + "(?:A7|B4)";

        string regexExactBase = RandomToken(0x7701, 8);
        string regexExactA = regexExactBase + "C2";
        string regexExactB = regexExactBase + "D8";
        string regexExactPattern = Regex.Escape(regexExactBase) + "(?:C2|D8)";

        string multilineStart = RandomToken(0x8801, 10);
        string multilineEnd = RandomToken(0x8802, 10);
        string multilinePattern = Regex.Escape(multilineStart) + @"\r?\n" + Regex.Escape(multilineEnd);

        return
        [
            new("literal-exact-random-phrase", exactPhrase, MatchNavCorpusShape.MixedSmallFiles,
                FileCount: 6, ExpectedMatches: 18, MatchIterations: 17, MinimumScreenshots: 17, SearchWaitSeconds: 4,
                UseRegex: false, ExactMatch: true, Multiline: false,
                MatchSamples: [exactPhrase], ExpectedHighlightLength: exactPhrase.Length),
            new("literal-two-random-terms", $"{twoTermA} {twoTermB}", MatchNavCorpusShape.MixedSmallFiles,
                FileCount: 8, ExpectedMatches: 24, MatchIterations: 23, MinimumScreenshots: 20, SearchWaitSeconds: 4,
                UseRegex: false, ExactMatch: false, Multiline: false,
                MatchSamples: [twoTermA, twoTermB], ExpectedHighlightLength: twoTermA.Length),
            new("literal-two-symbol-terms-large", $"{largeTermA} {largeTermB}", MatchNavCorpusShape.LargeRandomFiles,
                FileCount: 4, ExpectedMatches: 12, MatchIterations: 11, MinimumScreenshots: 11, SearchWaitSeconds: 6,
                UseRegex: false, ExactMatch: false, Multiline: false,
                MatchSamples: [largeTermA, largeTermB], ExpectedHighlightLength: largeTermA.Length),
            new("literal-two-terms-long-lines", $"{longTermA} {longTermB}", MatchNavCorpusShape.ManySingleLongLines,
                FileCount: 16, ExpectedMatches: 16, MatchIterations: 15, MinimumScreenshots: 15, SearchWaitSeconds: 6,
                UseRegex: false, ExactMatch: false, Multiline: false,
                MatchSamples: [longTermA, longTermB], ExpectedHighlightLength: longTermA.Length),
            new("literal-two-terms-varied-lines", $"{variedTermA} {variedTermB}", MatchNavCorpusShape.VariedLineLengths,
                FileCount: 5, ExpectedMatches: 15, MatchIterations: 14, MinimumScreenshots: 14, SearchWaitSeconds: 6,
                UseRegex: false, ExactMatch: false, Multiline: false,
                MatchSamples: [variedTermA, variedTermB], ExpectedHighlightLength: variedTermA.Length),
            new("literal-exact-random-dense", denseLiteral, MatchNavCorpusShape.DenseSingleLines,
                FileCount: 4, ExpectedMatches: 32, MatchIterations: 24, MinimumScreenshots: 20, SearchWaitSeconds: 5,
                UseRegex: false, ExactMatch: true, Multiline: false,
                MatchSamples: [denseLiteral], ExpectedHighlightLength: denseLiteral.Length),
            new("regex-random-alternation-dense", regexPattern, MatchNavCorpusShape.DenseSingleLines,
                FileCount: 4, ExpectedMatches: 32, MatchIterations: 24, MinimumScreenshots: 20, SearchWaitSeconds: 5,
                UseRegex: true, ExactMatch: false, Multiline: false,
                MatchSamples: [regexSampleA, regexSampleB], ExpectedHighlightLength: regexSampleA.Length),
            new("regex-with-exact-toggle-on", regexExactPattern, MatchNavCorpusShape.MixedSmallFiles,
                FileCount: 4, ExpectedMatches: 12, MatchIterations: 11, MinimumScreenshots: 11, SearchWaitSeconds: 4,
                UseRegex: true, ExactMatch: true, Multiline: false,
                MatchSamples: [regexExactA, regexExactB], ExpectedHighlightLength: regexExactA.Length),
            new("multiline-random-pairs", multilinePattern, MatchNavCorpusShape.MultilinePairs,
                FileCount: 5, ExpectedMatches: 10, MatchIterations: 9, MinimumScreenshots: 9, SearchWaitSeconds: 5,
                UseRegex: true, ExactMatch: false, Multiline: true,
                MatchSamples: [multilineStart, multilineEnd], ExpectedHighlightLength: multilineStart.Length),
        ];
    }

    private static void CreateMixedSmallFiles(string directory, MatchNavScenario scenario)
    {
        for (int fileIndex = 0; fileIndex < scenario.FileCount; fileIndex++)
        {
            var content = new StringBuilder();
            for (int line = 1; line <= 36; line++)
            {
                string token = (line, fileIndex) switch
                {
                    (6, _) => scenario.MatchSamples[fileIndex % scenario.MatchSamples.Length],
                    (19, _) => scenario.MatchSamples[(fileIndex + 1) % scenario.MatchSamples.Length],
                    (31, _) => scenario.MatchSamples[fileIndex % scenario.MatchSamples.Length],
                    _ => string.Empty,
                };
                content.AppendLine(token.Length == 0
                    ? $"file={fileIndex:D2} line={line:D2} ordinary punctuation: []{{}}; value={line * 17}."
                    : $"file={fileIndex:D2} line={line:D2} prefix [{token}] suffix; only the bracketed term matches.");
            }
            Write(directory, $"mixed-{fileIndex:D2}.txt", content.ToString());
        }
    }

    private static void CreateLargeRandomFiles(string directory, MatchNavScenario scenario)
    {
        for (int fileIndex = 0; fileIndex < scenario.FileCount; fileIndex++)
        {
            var content = new StringBuilder(capacity: 800_000);
            for (int line = 0; line < 5_600; line++)
            {
                string filler = RandomText(0x51A7 + fileIndex * 6_000 + line, 124);
                if (line is 40 or 2_800 or 5_540)
                {
                    int insertAt = 12 + ((line + fileIndex * 13) % 86);
                    int sampleIndex = (line + fileIndex) % scenario.MatchSamples.Length;
                    filler = filler.Insert(insertAt, $"[{scenario.MatchSamples[sampleIndex]}]");
                }
                content.AppendLine(filler);
            }
            Write(directory, $"large-random-{fileIndex:D2}.txt", content.ToString());
        }
    }

    private static void CreateManySingleLongLines(string directory, MatchNavScenario scenario)
    {
        for (int fileIndex = 0; fileIndex < scenario.FileCount; fileIndex++)
        {
            string line = RandomText(0x10A9 + fileIndex, 60_000);
            int insertAt = 128 + ((fileIndex * 2_357) % 55_000);
            string sample = scenario.MatchSamples[fileIndex % scenario.MatchSamples.Length];
            line = line.Insert(insertAt, $"[{sample}]");
            Write(directory, $"single-long-line-{fileIndex:D2}.txt", line);
        }
    }

    private static void CreateDenseSingleLines(string directory, MatchNavScenario scenario)
    {
        for (int fileIndex = 0; fileIndex < scenario.FileCount; fileIndex++)
        {
            var line = new StringBuilder(RandomText(0xD3A5 + fileIndex, 7_000));
            for (int occurrence = 0; occurrence < 8; occurrence++)
            {
                int insertAt = 180 + occurrence * 760;
                string sample = scenario.MatchSamples[(fileIndex + occurrence) % scenario.MatchSamples.Length];
                line.Insert(insertAt, $"[{sample}]");
            }
            Write(directory, $"dense-line-{fileIndex:D2}.txt", line.ToString());
        }
    }

    private static void CreateMultilinePairs(string directory, MatchNavScenario scenario)
    {
        string start = scenario.MatchSamples[0];
        string end = scenario.MatchSamples[1];
        for (int fileIndex = 0; fileIndex < scenario.FileCount; fileIndex++)
        {
            var content = new StringBuilder();
            content.AppendLine(RandomText(0x8810 + fileIndex, 96));
            for (int pair = 0; pair < 2; pair++)
            {
                content.AppendLine(start);
                content.AppendLine(end);
                content.AppendLine(RandomText(0x8890 + fileIndex * 4 + pair, 88));
            }
            Write(directory, $"multiline-pairs-{fileIndex:D2}.txt", content.ToString());
        }
    }

    private static void CreateVariedLineLengths(string directory, MatchNavScenario scenario)
    {
        int[] lengths = [96, 4_096, 180_000];
        for (int fileIndex = 0; fileIndex < scenario.FileCount; fileIndex++)
        {
            var content = new StringBuilder(capacity: lengths.Sum() + 256);
            for (int lineIndex = 0; lineIndex < lengths.Length; lineIndex++)
            {
                int length = lengths[lineIndex];
                string line = RandomText(0x4490 + fileIndex * lengths.Length + lineIndex, length);
                string sample = scenario.MatchSamples[(fileIndex + lineIndex) % scenario.MatchSamples.Length];
                int insertAt = lineIndex switch
                {
                    0 => 36,
                    1 => 1_900 + fileIndex * 17,
                    _ => 120_000 + fileIndex * 1_003,
                };
                content.AppendLine(line.Insert(insertAt, $"[{sample}]"));
            }
            Write(directory, $"varied-lines-{fileIndex:D2}.txt", content.ToString());
        }
    }

    private static string RandomToken(int seed, int length)
    {
        const string edgeAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        const string innerAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789_+?=-";
        var generator = new DeterministicGenerator((uint)seed);
        return string.Create(length, generator, static (span, state) =>
        {
            var random = state;
            span[0] = edgeAlphabet[random.Next(edgeAlphabet.Length)];
            for (int i = 1; i < span.Length - 1; i++)
                span[i] = innerAlphabet[random.Next(innerAlphabet.Length)];
            span[^1] = edgeAlphabet[random.Next(edgeAlphabet.Length)];
        });
    }

    private static string RandomText(int seed, int length)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyz 0123456789 .,;:_/\\()[]{}+-=";
        return string.Create(length, new DeterministicGenerator((uint)seed), static (span, generator) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = alphabet[generator.Next(alphabet.Length)];
        });
    }

    private struct DeterministicGenerator(uint seed)
    {
        private uint _state = seed == 0 ? 0x9E3779B9u : seed;

        public int Next(int exclusiveMaximum)
        {
            uint value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return (int)(value % (uint)exclusiveMaximum);
        }
    }

    private static void Write(string directory, string fileName, string content)
        => File.WriteAllText(Path.Combine(directory, fileName), content, Utf8NoBom);
}