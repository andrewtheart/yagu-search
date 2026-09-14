using System.Text.RegularExpressions;

namespace Yagu.Tests;

/// <summary>
/// Guards test isolation around process-wide static state. These are not tests of product behaviour;
/// they stop a whole category of intermittent, hard-to-diagnose suite failures from creeping back in.
/// </summary>
public sealed class TestIsolationRegressionTests
{
    [Fact]
    public void EveryClassThatWritesFileListerBackend_JoinsTheSerializingCollection()
    {
        // FileLister.Backend is process-wide. xUnit serializes tests only WITHIN a collection and runs
        // separate collections in parallel, so a class that writes this static outside
        // [Collection("FileListerBackend")] can flip the backend underneath a concurrent search test.
        // That produced a genuinely puzzling failure: a scenario using the deterministic Managed walker
        // intermittently found none of the files it had just written, because the backend had been
        // switched to Auto and the query went to Everything, whose index had never seen a brand-new
        // temp directory.
        var offenders = new List<string>();

        foreach (string path in EnumerateTestSources())
        {
            string text = File.ReadAllText(path);
            if (!text.Contains("FileLister.Backend =", StringComparison.Ordinal))
                continue;

            foreach (ClassDeclaration decl in EnumerateClasses(text))
            {
                if (!WritesBackend(text, decl))
                    continue;
                if (!decl.Attributes.Contains("Collection(\"FileListerBackend\")", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(path)}: {decl.Name}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These classes write the process-wide FileLister.Backend but are not in " +
            "[Collection(\"FileListerBackend\")], so they run in parallel with the search suites and can " +
            "corrupt their backend selection: " + string.Join(", ", offenders));
    }

    [Fact]
    public void GuardItself_DetectsAnUnprotectedMutator()
    {
        // A guard that cannot fail is worse than none, so prove the detection actually fires.
        const string Offending = """
            namespace Yagu.Tests;

            public class RogueTests
            {
                public void Dispose() { FileLister.Backend = FileListerBackend.Auto; }
            }
            """;
        var found = EnumerateClasses(Offending)
            .Where(d => WritesBackend(Offending, d))
            .ToList();

        Assert.Single(found);
        Assert.Equal("RogueTests", found[0].Name);
        Assert.DoesNotContain("Collection(\"FileListerBackend\")", found[0].Attributes, StringComparison.Ordinal);
    }

    private readonly record struct ClassDeclaration(string Name, string Attributes, int Start, int End);

    private static bool WritesBackend(string text, ClassDeclaration decl) =>
        text.AsSpan(decl.Start, decl.End - decl.Start).Contains("FileLister.Backend =", StringComparison.Ordinal);

    /// <summary>
    /// Splits a file into top-level class bodies, pairing each with the attribute block directly above
    /// it. Bodies run to the next top-level declaration, which is enough to attribute a mutation to the
    /// class that contains it.
    /// </summary>
    private static List<ClassDeclaration> EnumerateClasses(string text)
    {
        var matches = Regex.Matches(
            text,
            @"(?<attrs>(?:^[ \t]*\[[^\]]*\][ \t]*\r?\n)*)^(?:public|internal)[^\r\n]*?\bclass\s+(?<name>\w+)",
            RegexOptions.Multiline);

        var result = new List<ClassDeclaration>();
        for (int i = 0; i < matches.Count; i++)
        {
            int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            result.Add(new ClassDeclaration(
                matches[i].Groups["name"].Value,
                matches[i].Groups["attrs"].Value,
                matches[i].Index,
                end));
        }
        return result;
    }

    private static IEnumerable<string> EnumerateTestSources()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "Yagu.Tests")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Directory
            .EnumerateFiles(Path.Combine(dir!, "tests", "Yagu.Tests"), "*.cs", SearchOption.AllDirectories)
            // This file holds a deliberately offending sample for the self-test below, so scanning it
            // would report itself.
            .Where(p => !Path.GetFileName(p).Equals(OwnFileName, StringComparison.OrdinalIgnoreCase));
    }

    private const string OwnFileName = "TestIsolationRegressionTests.cs";
}
