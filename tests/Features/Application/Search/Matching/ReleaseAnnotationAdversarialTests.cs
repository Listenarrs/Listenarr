using Listenarr.Application.Search.Matching;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseAnnotationAdversarialTests")]
[Trait("Category", "Application")]
public sealed class ReleaseAnnotationAdversarialTests : BaseTests
{
    private static Audiobook Book(string? number = "3") => new()
    {
        Title = "The Quiet Orchard",
        Authors = ["Marisol Vega"],
        Series = "Northern Shore",
        SeriesNumber = number,
        Narrators = ["Simon Burns"]
    };
    private static bool Assess(Audiobook book, string release) => new BookIdentityMatcher().Evaluate(
        BookMatchContext.From(book), new() { ReleaseName = release }).IsConfirmed;

    public static IEnumerable<object[]> Mutations()
    {
        string[] suffixes = ["[M4B] 227.33 MB", "[M4B] 1.25 GiB", "[MP3]-JD07", "[MP3] {FerraBit}",
            "(miok) [Audiobook] (Horror)", "-AUDiOBOOK-WEB-SE-2021-CRAViNGS iNT", "[MP3] [64 Kbps]"];
        foreach (var suffix in suffixes)
            foreach (var join in new[] { " - ", " — ", " − " })
            {
                var release = "Marisol Vega" + join + "Northern Shore 3" + join + "The Quiet Orchard " + suffix;
                yield return [release, true];
                yield return [release.Replace("Marisol Vega", "Other Writer"), false];
                yield return [release.Replace("The Quiet Orchard", "Another Work"), false];
                yield return [release.Replace("Northern Shore 3", "Northern Shore 4"), false];
                yield return [release + " - Another Work", false];
                yield return ["Another Work - " + release, false];
                yield return [release + " {UnknownGroup}", false];
                yield return [release + " - Northern Shore 4", false];
                yield return [release.Replace("Northern Shore 3", "Other Series 3"), false];
                yield return [release.Replace("Northern Shore 3", "Northern Shore 3 & 4"), false];
            }
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public void SupportedAnnotationsCannotExcuseMissingOrContradictoryIdentity(string release, bool expected) =>
        Assert.Equal(expected, Assess(Book(), release));

    [Theory]
    [InlineData("Audio")]
    [InlineData("Novel")]
    [InlineData("1984")]
    public void TechnicalInterpretationCannotHideAKnownSeriesConflict(string series)
    {
        var book = Book(); book.Series = series;
        Assert.False(Assess(book, $"Marisol Vega - {series} 2021 - The Quiet Orchard [M4B]"));
        Assert.False(Assess(book, $"Marisol.Vega-{series}.2021-The.Quiet.Orchard.MP3"));
        book.SeriesNumber = null;
        Assert.False(Assess(book, $"Marisol Vega - {series} 2021 - The Quiet Orchard ({series} 2022) [M4B]"));
    }

    [Theory]
    [InlineData("Marisol Vega - Northern Shore 3 - The Quiet Orchard (Northern Shore 4)")]
    [InlineData("Marisol Vega - Northern Shore 3 - The Quiet Orchard - Northern Shore 4")]
    [InlineData("Marisol Vega - Northern Shore 3 - Northern Shore 4 - The Quiet Orchard")]
    public void UnknownReferencePositionCannotExcuseContradictoryReleasePositions(string release) =>
        Assert.False(Assess(Book(null), release));

    [Fact]
    public void ContextClonesRebuildTheIndexAndSnapshotCallerOwnedReferences()
    {
        var source = new[] { new ReleaseSeriesReference("Northern Shore", "3") };
        var context = BookMatchContext.From(Book()) with { SeriesReferences = source };
        source[0] = new("Northern Shore", "4");
        var evidence = new CandidateIdentityEvidence { ReleaseName = "Marisol Vega - Northern Shore 3 - The Quiet Orchard" };
        Assert.True(new BookIdentityMatcher().Evaluate(context, evidence).IsConfirmed);
        var changed = context with { SeriesReferences = [new("Northern Shore", "4")] };
        Assert.False(new BookIdentityMatcher().Evaluate(changed, evidence).IsConfirmed);
    }

    [Fact]
    public void ReferenceOrderingAndDuplicatesDoNotChangeEligibility()
    {
        var book = Book(null);
        book.SeriesMemberships = [new() { SeriesName = "Northern Shore", SeriesNumber = "III" },
            new() { SeriesName = "Other Series", SeriesNumber = "2" }, new() { SeriesName = "Northern Shore" }];
        const string release = "Marisol Vega - Northern Shore 03 - The Quiet Orchard [M4B]";
        var before = BookMatchContext.From(book).Fingerprint;
        Assert.True(Assess(book, release));
        book.SeriesMemberships.Reverse();
        Assert.Equal(before, BookMatchContext.From(book).Fingerprint);
        Assert.True(Assess(book, release));
    }

    [Fact]
    public void UnicodeSpansAndCancellationRemainConservative()
    {
        var book = Book(); book.Authors = ["René Vega"];
        const string release = "Rene\u0301.Vega-Northern.Shore.3-The.Quiet.Orchard.MP3-JD07";
        var context = BookMatchContext.From(book);
        var parsed = ReleaseNameInterpreter.Interpret(release, context, []);
        Assert.True(parsed.Complete);
        Assert.True(parsed.Credited);
        Assert.All(parsed.Spans, span => Assert.InRange(span.Start + span.Length, 1, release.Length));
        var author = Assert.Single(parsed.Spans, span => span.Role == ReleaseSpanRole.AuthorCredit);
        Assert.Equal("Rene\u0301.Vega", release.Substring(author.Start, author.Length));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReleaseNameInterpreter.Interpret(release, context, [], cancellation.Token));
        var result = new BookIdentityMatcher().Evaluate(context, new() { ReleaseName = new string('x', 16385) });
        Assert.False(result.IsConfirmed);
    }

    [Fact]
    public void CommaSceneSeparatorsPreserveSubtitleRolesAndLiteralPeriods()
    {
        var book = Book(); book.Title = "William Booth"; book.Subtitle = "Soup, Soap, and Salvation";
        Assert.True(Assess(book, "Marisol.Vega-William.Booth-Soup,.Soap,.and.Salvation.MP3-JD07"));
        Assert.False(Assess(book, "Other.Writer-William.Booth-Soup,.Soap,.and.Salvation.MP3-JD07"));
        book.Authors = ["Soup, Soap, and Salvation"];
        Assert.False(Assess(book, "William.Booth-Soup,.Soap,.and.Salvation.MP3-JD07"));
        book = Book(); book.Title = "S.";
        Assert.False(Assess(book, "Marisol.Vega-S.MP3-JD07"));
        Assert.False(Assess(book, "Marisol.Vega-S.MP3-JD07-Another.Work"));
    }
}
