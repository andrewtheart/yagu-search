using System;
using System.Collections.Generic;
using Yagu.Models;
using static Yagu.Tests.SearchScenarioRunner;

namespace Yagu.Tests;

public static partial class SemanticSearchQueryCatalog
{
    // File-size and date-range filter scenarios. Content is ASCII so each character
    // is one UTF-8 byte, letting sizes be specified exactly. Date filters use UTC
    // instants a year or more apart to stay clear of boundary/timezone ambiguity.
    private static IEnumerable<SearchScenario> SizeDateScenarios()
    {
        // ── Size filters ─────────────────────────────────────────────────────
        yield return Scn("size-range-min-and-max")
            .File("small.txt", Sized(6))
            .File("mid.txt", Sized(20))
            .File("large.txt", Sized(87))
            .Query("needle").Substring().MinSize(10).MaxSize(40)
            .ExpectFiles("mid.txt").Build();

        yield return Scn("size-min-only")
            .File("small.txt", Sized(6))
            .File("big.txt", Sized(56))
            .Query("needle").Substring().MinSize(20)
            .ExpectFiles("big.txt").Build();

        yield return Scn("size-max-only")
            .File("small.txt", Sized(6))
            .File("big.txt", Sized(56))
            .Query("needle").Substring().MaxSize(20)
            .ExpectFiles("small.txt").Build();

        yield return Scn("size-exact-band")
            .File("a.txt", Sized(10))
            .File("b.txt", Sized(30))
            .File("c.txt", Sized(50))
            .Query("needle").Substring().MinSize(25).MaxSize(35)
            .ExpectFiles("b.txt").Build();

        yield return Scn("size-min-excludes-all")
            .File("a.txt", Sized(10))
            .File("b.txt", Sized(20))
            .Query("needle").Substring().MinSize(100)
            .ExpectNoMatches().Build();

        yield return Scn("size-max-excludes-all")
            .File("a.txt", Sized(50))
            .File("b.txt", Sized(60))
            .Query("needle").Substring().MaxSize(10)
            .ExpectNoMatches().Build();

        yield return Scn("size-min-zero-includes-all")
            .File("a.txt", Sized(10))
            .File("b.txt", Sized(20))
            .Query("needle").Substring().MinSize(0)
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("size-large-threshold")
            .File("a.txt", Sized(100))
            .File("b.txt", Sized(1000))
            .Query("needle").Substring().MinSize(500)
            .ExpectFiles("b.txt").Build();

        yield return Scn("size-band-two-pass")
            .File("a.txt", Sized(10))
            .File("b.txt", Sized(30))
            .File("c.txt", Sized(50))
            .File("d.txt", Sized(70))
            .Query("needle").Substring().MinSize(20).MaxSize(60)
            .ExpectFiles("b.txt", "c.txt").Build();

        yield return Scn("size-tiny-vs-big")
            .File("tiny.txt", Sized(8))
            .File("huge.txt", Sized(200))
            .Query("needle").Substring().MaxSize(50)
            .ExpectFiles("tiny.txt").Build();

        yield return Scn("size-min-boundary-margin")
            .File("a.txt", Sized(100))
            .File("b.txt", Sized(300))
            .Query("needle").Substring().MinSize(200)
            .ExpectFiles("b.txt").Build();

        yield return Scn("size-max-boundary-margin")
            .File("a.txt", Sized(100))
            .File("b.txt", Sized(300))
            .Query("needle").Substring().MaxSize(200)
            .ExpectFiles("a.txt").Build();

        yield return Scn("size-range-single-pass")
            .File("a.txt", Sized(50))
            .File("b.txt", Sized(150))
            .File("c.txt", Sized(250))
            .Query("needle").Substring().MinSize(100).MaxSize(200)
            .ExpectFiles("b.txt").Build();

        yield return Scn("size-all-below-max")
            .File("a.txt", Sized(20))
            .File("b.txt", Sized(30))
            .File("c.txt", Sized(40))
            .Query("needle").Substring().MaxSize(1000)
            .ExpectFiles("a.txt", "b.txt", "c.txt").Build();

        yield return Scn("size-all-above-min")
            .File("a.txt", Sized(200))
            .File("b.txt", Sized(300))
            .File("c.txt", Sized(400))
            .Query("needle").Substring().MinSize(10)
            .ExpectFiles("a.txt", "b.txt", "c.txt").Build();

        yield return Scn("size-content-and-size")
            .File("a.txt", Sized(100))
            .File("b.txt", Sized(10))
            .File("c.txt", Other(100))
            .Query("needle").Substring().MinSize(50)
            .ExpectFiles("a.txt").Build();

        yield return Scn("size-with-include-ext")
            .File("a.cs", Sized(100))
            .File("b.txt", Sized(100))
            .File("c.cs", Sized(10))
            .Query("needle").Substring().Include("*.cs").MinSize(50)
            .ExpectFiles("a.cs").Build();

        yield return Scn("size-range-excludes-both-ends")
            .File("a.txt", Sized(10))
            .File("b.txt", Sized(50))
            .File("c.txt", Sized(90))
            .Query("needle").Substring().MinSize(30).MaxSize(70)
            .ExpectFiles("b.txt").Build();

        // ── Date filters ─────────────────────────────────────────────────────
        yield return Scn("modified-date-range")
            .FileAt("old.txt", "needle", modified: Utc(2020, 1, 1))
            .FileAt("mid.txt", "needle", modified: Utc(2024, 6, 15))
            .FileAt("new.txt", "needle", modified: Utc(2030, 1, 1))
            .Query("needle").Substring()
            .ModifiedAfter(Utc(2023, 1, 1)).ModifiedBefore(Utc(2025, 1, 1))
            .ExpectFiles("mid.txt").Build();

        yield return Scn("created-date-after")
            .FileAt("old.txt", "needle", created: Utc(2010, 1, 1))
            .FileAt("new.txt", "needle", created: Utc(2030, 1, 1))
            .Query("needle").Substring().CreatedAfter(Utc(2020, 1, 1))
            .ExpectFiles("new.txt").Build();

        yield return Scn("modified-after-only")
            .FileAt("old.txt", "needle", modified: Utc(2015, 1, 1))
            .FileAt("new.txt", "needle", modified: Utc(2025, 1, 1))
            .Query("needle").Substring().ModifiedAfter(Utc(2020, 1, 1))
            .ExpectFiles("new.txt").Build();

        yield return Scn("modified-before-only")
            .FileAt("old.txt", "needle", modified: Utc(2015, 1, 1))
            .FileAt("new.txt", "needle", modified: Utc(2025, 1, 1))
            .Query("needle").Substring().ModifiedBefore(Utc(2020, 1, 1))
            .ExpectFiles("old.txt").Build();

        yield return Scn("created-before-only")
            .FileAt("old.txt", "needle", created: Utc(2010, 1, 1))
            .FileAt("new.txt", "needle", created: Utc(2030, 1, 1))
            .Query("needle").Substring().CreatedBefore(Utc(2020, 1, 1))
            .ExpectFiles("old.txt").Build();

        yield return Scn("created-date-range")
            .FileAt("c1.txt", "needle", created: Utc(2018, 1, 1))
            .FileAt("c2.txt", "needle", created: Utc(2022, 1, 1))
            .FileAt("c3.txt", "needle", created: Utc(2028, 1, 1))
            .Query("needle").Substring()
            .CreatedAfter(Utc(2020, 1, 1)).CreatedBefore(Utc(2025, 1, 1))
            .ExpectFiles("c2.txt").Build();

        yield return Scn("modified-after-excludes-all")
            .FileAt("a.txt", "needle", modified: Utc(2010, 1, 1))
            .FileAt("b.txt", "needle", modified: Utc(2012, 1, 1))
            .Query("needle").Substring().ModifiedAfter(Utc(2020, 1, 1))
            .ExpectNoMatches().Build();

        yield return Scn("modified-before-excludes-all")
            .FileAt("a.txt", "needle", modified: Utc(2030, 1, 1))
            .FileAt("b.txt", "needle", modified: Utc(2031, 1, 1))
            .Query("needle").Substring().ModifiedBefore(Utc(2020, 1, 1))
            .ExpectNoMatches().Build();

        yield return Scn("modified-range-two-pass")
            .FileAt("a.txt", "needle", modified: Utc(2019, 1, 1))
            .FileAt("b.txt", "needle", modified: Utc(2021, 1, 1))
            .FileAt("c.txt", "needle", modified: Utc(2023, 1, 1))
            .FileAt("d.txt", "needle", modified: Utc(2031, 1, 1))
            .Query("needle").Substring()
            .ModifiedAfter(Utc(2020, 1, 1)).ModifiedBefore(Utc(2025, 1, 1))
            .ExpectFiles("b.txt", "c.txt").Build();

        yield return Scn("created-after-includes-all")
            .FileAt("a.txt", "needle", created: Utc(2025, 1, 1))
            .FileAt("b.txt", "needle", created: Utc(2026, 1, 1))
            .Query("needle").Substring().CreatedAfter(Utc(2000, 1, 1))
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("modified-recent-vs-old")
            .FileAt("old.txt", "needle", modified: Utc(2000, 1, 1))
            .FileAt("recent.txt", "needle", modified: Utc(2035, 1, 1))
            .Query("needle").Substring().ModifiedAfter(Utc(2030, 1, 1))
            .ExpectFiles("recent.txt").Build();

        yield return Scn("date-and-content")
            .FileAt("a.txt", "needle", modified: Utc(2024, 1, 1))
            .FileAt("b.txt", "needle", modified: Utc(2010, 1, 1))
            .FileAt("c.txt", "other", modified: Utc(2024, 1, 1))
            .Query("needle").Substring().ModifiedAfter(Utc(2023, 1, 1))
            .ExpectFiles("a.txt").Build();

        yield return Scn("modified-range-single")
            .FileAt("a.txt", "needle", modified: Utc(2018, 1, 1))
            .FileAt("b.txt", "needle", modified: Utc(2022, 1, 1))
            .FileAt("c.txt", "needle", modified: Utc(2026, 1, 1))
            .Query("needle").Substring()
            .ModifiedAfter(Utc(2020, 1, 1)).ModifiedBefore(Utc(2024, 1, 1))
            .ExpectFiles("b.txt").Build();

        yield return Scn("created-range-excludes-ends")
            .FileAt("a.txt", "needle", created: Utc(2018, 1, 1))
            .FileAt("b.txt", "needle", created: Utc(2022, 1, 1))
            .FileAt("c.txt", "needle", created: Utc(2026, 1, 1))
            .Query("needle").Substring()
            .CreatedAfter(Utc(2020, 1, 1)).CreatedBefore(Utc(2024, 1, 1))
            .ExpectFiles("b.txt").Build();

        yield return Scn("modified-and-size")
            .FileAt("a.txt", Sized(100), modified: Utc(2024, 1, 1))
            .FileAt("b.txt", Sized(10), modified: Utc(2024, 1, 1))
            .FileAt("c.txt", Sized(100), modified: Utc(2010, 1, 1))
            .Query("needle").Substring().MinSize(50).ModifiedAfter(Utc(2023, 1, 1))
            .ExpectFiles("a.txt").Build();

        yield return Scn("modified-after-future-excludes-all")
            .FileAt("a.txt", "needle", modified: Utc(2020, 1, 1))
            .FileAt("b.txt", "needle", modified: Utc(2021, 1, 1))
            .Query("needle").Substring().ModifiedAfter(Utc(2100, 1, 1))
            .ExpectNoMatches().Build();

        yield return Scn("created-after-and-modified-before")
            .FileAt("a.txt", "needle", created: Utc(2024, 1, 1), modified: Utc(2024, 6, 1))
            .FileAt("b.txt", "needle", created: Utc(2010, 1, 1), modified: Utc(2024, 6, 1))
            .Query("needle").Substring()
            .CreatedAfter(Utc(2020, 1, 1)).ModifiedBefore(Utc(2030, 1, 1))
            .ExpectFiles("a.txt").Build();
    }

    private static DateTimeOffset Utc(int year, int month, int day)
        => new(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Utc));

    /// <summary>ASCII content of exactly <paramref name="totalBytes"/> bytes containing the token "needle".</summary>
    private static string Sized(int totalBytes) => "needle" + new string('x', Math.Max(0, totalBytes - 6));

    /// <summary>ASCII content of exactly <paramref name="totalBytes"/> bytes that does NOT contain "needle".</summary>
    private static string Other(int totalBytes) => "other" + new string('y', Math.Max(0, totalBytes - 5));
}
