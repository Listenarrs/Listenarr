using Listenarr.Application.Search.Matching;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseAnnotationExtensionTests")]
[Trait("Category", "Application")]
public sealed class ReleaseAnnotationExtensionTests : BaseTests
{
    private static Audiobook Book(string? position = "3") => new()
    {
        Title = "The Quiet Orchard",
        Authors = ["Marisol Vega"],
        Series = "Northern Shore",
        SeriesNumber = position,
        Narrators = ["Simon Burns"]
    };

    private static bool Confirms(Audiobook book, string release) => new BookIdentityMatcher().Evaluate(
        BookMatchContext.From(book), new() { ReleaseName = release }).IsConfirmed;

    [Theory]
    [InlineData(null, "3", true)]
    [InlineData("", "3", true)]
    [InlineData("3", "03", true)]
    [InlineData("3", "III", true)]
    [InlineData("3.5", "03.5", true)]
    [InlineData("3", "4", false)]
    [InlineData("3.5", "35", false)]
    [InlineData("unknown", "3", false)]
    [InlineData(null, "3-4", false)]
    [InlineData(null, "3 & 4", false)]
    [InlineData(null, "IIII", false)]
    public void AttachedPositionDistinguishesMissingFromConflictingFacts(string? stored, string releasePosition, bool expected)
    {
        Assert.Equal(expected, Confirms(Book(stored), $"Marisol Vega - Northern Shore {releasePosition} - The Quiet Orchard [M4B]"));
    }

    [Fact]
    public void DuplicateMissingAndKnownMembershipsAreResolvedWithoutHidingConflicts()
    {
        var book = Book(null);
        book.SeriesMemberships = [new() { SeriesName = "Northern Shore", SeriesNumber = "3" }];
        Assert.True(Confirms(book, "Marisol Vega - Northern Shore 3 - The Quiet Orchard"));
        Assert.False(Confirms(book, "Marisol Vega - Northern Shore 4 - The Quiet Orchard"));
        book.SeriesMemberships.Add(new() { SeriesName = "Northern Shore", SeriesNumber = "4" });
        Assert.False(Confirms(book, "Marisol Vega - Northern Shore 3 - The Quiet Orchard"));
        book.SeriesMemberships.Reverse();
        Assert.False(Confirms(book, "Marisol Vega - Northern Shore 3 - The Quiet Orchard"));
    }

    [Fact]
    public void MissingPositionDoesNotExplainAStandaloneNumberOrMissingIdentity()
    {
        var book = Book(null);
        Assert.False(Confirms(book, "Marisol Vega - Book 3 - The Quiet Orchard"));
        Assert.False(Confirms(book, "Marisol Vega - Northern Shore 3 - Another Work"));
        Assert.False(Confirms(book, "Other Writer - Northern Shore 3 - The Quiet Orchard"));
        Assert.False(Confirms(book, "Northern Shore 3 - The Quiet Orchard"));
        Assert.False(Confirms(book, "Marisol Vega - Northern Shore 3 - The Quiet Orchard - Northern Shore 4"));
    }

    [Theory]
    [InlineData("The Wheel of Time", "Wheel of Time")]
    [InlineData("The First Law Trilogy", "The First Law")]
    [InlineData("Presidential Agent", "Presidential Agents")]
    [InlineData("The Boxcar Children Mysteries", "Boxcar Children")]
    [InlineData("Christian Heroes: Then & Now", "Christian Heroes Then & Now")]
    public void SeriesAliasesAreScopedToTheStoredSeries(string stored, string variant)
    {
        var book = Book(); book.Series = stored;
        Assert.True(Confirms(book, $"Marisol Vega - {variant} 3 - The Quiet Orchard"));
        Assert.False(Confirms(book, $"Marisol Vega - {variant} 4 - The Quiet Orchard"));
        book.Series = "Other Series";
        Assert.False(Confirms(book, $"Marisol Vega - {variant} 3 - The Quiet Orchard"));
    }

    [Fact]
    public void AliasCollisionsCannotHideConflictingSecondaryMemberships()
    {
        var book = Book(); book.Series = "The Wheel of Time";
        book.SeriesMemberships = [new() { SeriesName = "Wheel of Time", SeriesNumber = "4" }];
        Assert.False(Confirms(book, "Marisol Vega - Wheel of Time 3 - The Quiet Orchard"));
    }

    [Theory]
    [InlineData("[M4B] 227.33 MB", true)]
    [InlineData("[M4B] 1.25 GiB", true)]
    [InlineData("[M4B] -227 MB", false)]
    [InlineData("[M4B] 0 MB", false)]
    [InlineData("[M4B] 1e3 MB", false)]
    [InlineData("[M4B] 227.33 Unknown", false)]
    [InlineData("[M4B] 227.33", false)]
    [InlineData("[M4B]-JD07", true)]
    [InlineData("[M4B] {FerraBit}", true)]
    [InlineData("[M4B] {Another Book}", false)]
    [InlineData("(miok) [Audiobook] (Horror)", true)]
    [InlineData("(miok) [Audiobook] (Another Work)", false)]
    [InlineData("(miok) [Audiobook] (Sci-Fi)", true)]
    [InlineData("[M4B] - UnknownGroup", false)]
    [InlineData("[M4B] - Another Work - PoF", false)]
    [InlineData("-AUDiOBOOK-WEB-SE-2021-CRAViNGS iNT", true)]
    [InlineData("-AUDiOBOOK-WEB-SE-2021-UnknownGroup iNT", false)]
    [InlineData("-AUDiOBOOK-WEB-SE-2021-CRAViNGS Another Work", false)]
    [InlineData("-WEB-Another-2021-CRAViNGS iNT", false)]
    public void TechnicalAndGroupFieldsRemainWholeAndBounded(string suffix, bool expected)
    {
        Assert.Equal(expected, Confirms(Book(), "Marisol Vega - The Quiet Orchard " + suffix));
    }

    [Fact]
    public void NarratorSurnameNeedsAUniqueReferenceAndTechnicalSceneLayout()
    {
        const string release = "Marisol.Vega-The.Quiet.Orchard.Unabr-M4B.dcrypt64k-Burns.2010.NMR";
        var book = Book();
        Assert.True(Confirms(book, release));
        Assert.False(Confirms(book, release.Replace("Burns", "Jones")));
        Assert.False(Confirms(book, "Marisol Vega - The Quiet Orchard - Burns"));
        book.Narrators = ["Simon Burns", "Jane Burns"];
        Assert.False(Confirms(book, release));
        book.Narrators = [];
        Assert.False(Confirms(book, release));
        book.Authors = ["Simon Burns"];
        Assert.False(Confirms(book, release));
    }

    [Theory]
    [InlineData("Horror")]
    [InlineData("JD07")]
    [InlineData("227.33 MB")]
    [InlineData("WEB SE 2021")]
    public void AnnotationWordsCannotSupplyMissingIdentity(string word)
    {
        var book = Book(); book.Title = word;
        Assert.False(Confirms(book, $"Marisol Vega - Another Work [M4B] ({word})"));
        book = Book(); book.Authors = [word];
        Assert.False(Confirms(book, $"The Quiet Orchard [M4B] ({word})"));
        book = Book(); book.Subtitle = word; book.Authors = [word];
        Assert.False(Confirms(book, $"The Quiet Orchard {word} [M4B]"));
    }

    [Fact]
    public void MissingFactsAndConflictingFactsInvalidateTheAssessment()
    {
        var book = Book(null);
        var first = BookMatchContext.From(book).Fingerprint;
        book.SeriesNumber = "4";
        Assert.NotEqual(first, BookMatchContext.From(book).Fingerprint);
        Assert.False(Confirms(book, "Marisol Vega - Northern Shore 3 - The Quiet Orchard"));
    }

    [Fact]
    public void CompleteDottedSuffixCreditCannotBeTakenFromInsideTheTitle()
    {
        Assert.True(Confirms(Book(), "The.Quiet.Orchard.Marisol.Vega.Audiobook.MP3-JD07"));
        Assert.False(Confirms(Book(), "The.Quiet.Orchard.Marisol.Other.Audiobook.MP3-JD07"));
        var book = Book(); book.Title = "Stories of Marisol Vega";
        Assert.False(Confirms(book, "Stories.of.Marisol.Vega.Audiobook.MP3-JD07"));
    }

    [Fact]
    public void RuleDiagnosticsPreserveRawSpansAndDoNotClaimVerifiedPositions()
    {
        const string release = "Marisol.Vega-Northern.Shore.03-The.Quiet.Orchard.MP3-JD07";
        var parsed = ReleaseNameInterpreter.Interpret(release, BookMatchContext.From(Book(null)), []);
        Assert.True(parsed.Complete);
        var series = Assert.Single(parsed.Spans, s => s.Role == ReleaseSpanRole.SeriesName);
        Assert.Equal("series.reference", series.RuleId);
        Assert.Equal("unverified_position", series.MatchStrength);
        Assert.Equal("Northern.Shore.03", release.Substring(series.Start, series.Length));
        var group = Assert.Single(parsed.Spans, s => s.Role == ReleaseSpanRole.GroupMarker);
        Assert.Equal("group.catalog", group.RuleId);
        Assert.Equal("JD07", release.Substring(group.Start, group.Length));
        Assert.All(parsed.Spans, s => Assert.Equal(IdentityEvidenceOrigin.ReleaseNameDerived, s.Origin));
    }
}
