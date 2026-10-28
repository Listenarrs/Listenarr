using Listenarr.Application.Search.Matching;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseLayoutTests")]
[Trait("Category", "Application")]
public sealed class ReleaseLayoutTests : BaseTests
{
    private static BookIdentityAssessment Assess(Audiobook book, string release) =>
        new BookIdentityMatcher().Evaluate(BookMatchContext.From(book), new() { ReleaseName = release });

    [Theory]
    [InlineData("Memorys.Legion-The.Complete.Expanse.Story.Collection.by.James.S.A.Corey")]
    [InlineData("Memory's Legion The Complete Expanse Story Collection by James S. A. Corey")]
    [InlineData("Memory's Legion - The Complete Expanse Story Collection by James S. A. Corey")]
    public void StoredSubtitleAndExplicitCreditSurvivePresentationJoins(string release)
    {
        var book = new Audiobook { Title = "Memory's Legion", Subtitle = "The Complete Expanse Story Collection", Authors = ["James S. A. Corey"] };
        Assert.True(Assess(book, release).IsConfirmed);
    }

    [Theory]
    [InlineData("Fahrenheit.451.by.Ray.Bradbury")]
    [InlineData("Fahrenheit.451.written.by.Ray.Bradbury")]
    [InlineData("Fahrenheit.451.author: Ray.Bradbury")]
    public void SceneCreditCueUsesTheComparisonView(string release)
    {
        Assert.True(Assess(new() { Title = "Fahrenheit 451", Authors = ["Ray Bradbury"] }, release).IsConfirmed);
    }

    [Theory]
    [InlineData("The Quiet Orchard: Notes from the Northern Shore by Marisol Vega")]
    [InlineData("The Quiet Orchard - Notes from the Northern Shore by Marisol Vega")]
    [InlineData("The Quiet Orchard Notes from the Northern Shore by Marisol Vega")]
    [InlineData("The.Quiet.Orchard-Notes.from.the.Northern.Shore.by.Marisol.Vega")]
    [InlineData("Marisol Vega - The Quiet Orchard Notes from the Northern Shore")]
    public void SubtitleBindingUsesOnlyCompleteStoredComponents(string release)
    {
        var book = new Audiobook { Title = "The Quiet Orchard", Subtitle = "Notes from the Northern Shore", Authors = ["Marisol Vega"] };
        Assert.True(Assess(book, release).IsConfirmed);
        Assert.False(Assess(book, release + " - Another Work").IsConfirmed);
        Assert.False(Assess(book, release.Replace("Marisol", "Different", StringComparison.Ordinal)).IsConfirmed);
    }

    [Theory]
    [InlineData("The Quiet Orchard Unknown Notes from the Northern Shore by Marisol Vega")]
    [InlineData("Notes from the Northern Shore The Quiet Orchard by Marisol Vega")]
    [InlineData("Notes from the Northern Shore by Marisol Vega")]
    [InlineData("The Quiet Orchard Notes from Another Shore by Marisol Vega")]
    [InlineData("The Quiet Orchard Notes from the Northern Shore")]
    [InlineData("The.Quiet.Orchard.narrated.by.Marisol.Vega")]
    [InlineData("Novel.narrated.by.The.Quiet.Orchard-Marisol.Vega")]
    [InlineData("The.Quiet.Orchard.nearby.Marisol.Vega")]
    public void SubtitleAndCreditCuesDoNotExplainOtherWordsOrRoles(string release)
    {
        var book = new Audiobook { Title = "The Quiet Orchard", Subtitle = "Notes from the Northern Shore", Authors = ["Marisol Vega"] };
        Assert.False(Assess(book, release).IsConfirmed);
    }

    [Fact]
    public void CreditCueCannotRemoveWordsFromAKnownName()
    {
        var book = new Audiobook { Title = "The Quiet Orchard", Authors = ["By Smith"] };
        Assert.True(Assess(book, "By Smith - The Quiet Orchard").IsConfirmed);
        Assert.True(Assess(book, "The Quiet Orchard by By Smith").IsConfirmed);
        Assert.False(Assess(book, "Smith - The Quiet Orchard").IsConfirmed);
    }

    [Theory]
    [InlineData("Ray Bradbury")]
    [InlineData("Ray-Bradbury")]
    [InlineData("by Ray Bradbury")]
    public void ReferenceSubtitleCannotAlsoSupplyTheAuthorCredit(string subtitle)
    {
        var book = new Audiobook { Title = "The Book", Subtitle = subtitle, Authors = ["Ray Bradbury"] };
        Assert.False(Assess(book, "The Book " + subtitle.Replace('-', ' ')).IsConfirmed);
        Assert.True(Assess(book, "The Book " + subtitle + " by Ray Bradbury").IsConfirmed);
    }

    [Fact]
    public void StructuredFieldsAndNormalizedCreditKeepOriginalRanges()
    {
        const string release = "The.Quiet.Orchard-Notes.from.the.Northern.Shore.author：\tRene\u0301.Descartes";
        var book = new Audiobook { Title = "The Quiet Orchard", Subtitle = "Notes from the Northern Shore", Authors = ["René Descartes"] };
        var parsed = ReleaseNameInterpreter.Interpret(release, BookMatchContext.From(book), []);
        Assert.True(parsed.Complete);
        Assert.True(parsed.Credited);
        var title = Assert.Single(parsed.Spans, s => s.Role == ReleaseSpanRole.PrimaryTitle);
        var subtitle = Assert.Single(parsed.Spans, s => s.Role == ReleaseSpanRole.ReferenceSubtitle);
        var credit = Assert.Single(parsed.Spans, s => s.Role == ReleaseSpanRole.AuthorCredit);
        Assert.Equal("The.Quiet.Orchard", release.Substring(title.Start, title.Length));
        Assert.Equal("Notes.from.the.Northern.Shore", release.Substring(subtitle.Start, subtitle.Length));
        Assert.Equal("author：\tRene\u0301.Descartes", release.Substring(credit.Start, credit.Length));
    }

    [Fact]
    public void SupplementaryLettersCannotCreateATitleBoundaryInsideAWord()
    {
        var book = new Audiobook { Title = "Orchard", Authors = ["𐐀na Vale"] };
        Assert.False(Assess(book, "𐐀na Vale - Orchard𐐀na Vale [M4B]").IsConfirmed);
        Assert.True(Assess(book, "𐐀na Vale - Orchard [M4B]").IsConfirmed);
    }

    [Theory]
    [InlineData("David Weber A Rising Thunder")]
    [InlineData("A Rising Thunder David Weber")]
    [InlineData("A Rising Thunder David Weber [M4A] [Variable]")]
    public void CompleteAuthorCanOccupyAnAnchoredWhitespaceField(string release)
    {
        var book = new Audiobook { Title = "A Rising Thunder", Authors = ["David Weber"] };
        Assert.True(Assess(book, release).IsConfirmed);
        Assert.False(Assess(book, release + " Another Work").IsConfirmed);
        Assert.False(Assess(book, release.Replace("David Weber", "David Different", StringComparison.Ordinal)).IsConfirmed);
        Assert.False(Assess(book, "A Rising Thunder narrated by David Weber").IsConfirmed);
    }

    [Fact]
    public void CompleteKnownCoauthorsCanUseAmpersands()
    {
        var book = new Audiobook { Title = "Unlucky 13", Authors = ["James Patterson", "Maxine Paetro"] };
        Assert.True(Assess(book, "James Patterson & Maxine Paetro - Unlucky 13").IsConfirmed);
        Assert.False(Assess(book, "James Patterson & Unknown Writer - Unlucky 13").IsConfirmed);
        Assert.False(Assess(book, "James Paetro & Maxine Patterson - Unlucky 13").IsConfirmed);
    }

    [Fact]
    public void OnlyExplicitCompleteSeriesSubtitlesSupplySeriesFacts()
    {
        const string release = "James.S.A.Corey-The.Expanse.03.5-The.Churn.Unabr-64k.[2014]";
        var book = new Audiobook { Title = "The Churn", Authors = ["James S. A. Corey"], Subtitle = "The Expanse, Book 3.5" };
        Assert.True(Assess(book, release).IsConfirmed);
        book.Subtitle = "The Expanse, Book 35";
        Assert.False(Assess(book, release).IsConfirmed);
        book.Subtitle = "An Expanse Novella";
        Assert.False(Assess(book, release).IsConfirmed);
        book.Subtitle = "The Expanse, Book 3.5 - Another Work";
        Assert.False(Assess(book, release).IsConfirmed);
        book.Subtitle = "The Expanse, Book 3.5";
        book.Series = "The Expanse";
        book.SeriesNumber = "35";
        Assert.False(Assess(book, release).IsConfirmed);
    }

    [Theory]
    [InlineData("[M4A] [Variable]")]
    [InlineData("[audio]")]
    [InlineData("(.MP3)")]
    [InlineData("-Unb")]
    public void AdditionalWholeFieldTechnicalTagsDoNotHideWords(string metadata)
    {
        var book = new Audiobook { Title = "The Book", Authors = ["Some Writer"] };
        Assert.True(Assess(book, "Some Writer - The Book " + metadata).IsConfirmed);
        Assert.False(Assess(book, "Some Writer - The Book " + metadata + " Other Work").IsConfirmed);
    }

    [Fact]
    public void TerminalScenePunctuationDoesNotEraseLiteralTitlePunctuation()
    {
        Assert.True(Assess(new() { Title = "You Like It Darker", Authors = ["Stephen King"] },
            "Stephen.King-You.Like.It.Darker.").IsConfirmed);
        Assert.False(Assess(new() { Title = "S.", Authors = ["J. J. Abrams"] }, "J.J.Abrams-S").IsConfirmed);
    }

    [Fact]
    public void BracketedKnownSeriesAndFollowingPositionMustBothAgree()
    {
        var book = new Audiobook { Title = "Savage Road", Authors = ["Christine Feehan"], Subtitle = "Torpedo Ink, Book 7" };
        Assert.True(Assess(book, "Savage Road (Torpedo Ink) Bk 7 - Christine Feehan [MP3]").IsConfirmed);
        Assert.False(Assess(book, "Savage Road (Torpedo Ink) Bk 8 - Christine Feehan [MP3]").IsConfirmed);
        Assert.False(Assess(book, "Savage Road (Other Series) Bk 7 - Christine Feehan [MP3]").IsConfirmed);
        Assert.False(Assess(book, "Savage Road Torpedo Ink - Christine Feehan [MP3]").IsConfirmed);
    }

    [Theory]
    [InlineData("[64 Kbps]")]
    [InlineData("[128kbps]")]
    [InlineData("[80 kb/s]")]
    [InlineData("[320k] (Unabridged Edition).")]
    public void TechnicalFieldsDoNotSupplyMissingIdentity(string suffix)
    {
        var book = new Audiobook { Title = "Around the World in 80 Days", Authors = ["Jules Verne"] };
        Assert.True(Assess(book, "Around the World in 80 Days - Jules Verne " + suffix).IsConfirmed);
        foreach (var release in new[] {
            "Around the World in 80 Days - Other Writer " + suffix,
            "Another Work - Jules Verne " + suffix,
            "Around the World in 80 Days - Jules Verne " + suffix + " - Another Work",
            "Around the World in 80 Days - Jules Verne [64 Kbps Another Work]",
            "Around the World in 80 Days " + suffix })
            Assert.False(Assess(book, release).IsConfirmed);
    }

    [Fact]
    public void SceneSeriesPositionAndRawSpansArePreserved()
    {
        const string release = "Pierce.Brown-Red.Rising.03-Morning.Star.Unabr-64k.[2016]";
        var book = new Audiobook { Title = "Morning Star", Authors = ["Pierce Brown"], Series = "Red Rising", SeriesNumber = "3" };
        var parsed = ReleaseNameInterpreter.Interpret(release, BookMatchContext.From(book), []);
        Assert.True(parsed.Complete, parsed.UnavailableReason);
        Assert.True(parsed.Credited);
        var title = Assert.Single(parsed.Spans, s => s.Role == ReleaseSpanRole.PrimaryTitle);
        Assert.Equal("Morning.Star", release.Substring(title.Start, title.Length));
        Assert.Contains(parsed.Spans, s => s.Role == ReleaseSpanRole.SeriesPosition && release.Substring(s.Start, s.Length) == "03");
        Assert.False(Assess(book, release.Replace(".03-", ".04-", StringComparison.Ordinal)).IsConfirmed);
        book.Series = null;
        book.SeriesNumber = null;
        Assert.False(Assess(book, release).IsConfirmed);
    }

    [Theory]
    [InlineData("3.5", "03.5", true)]
    [InlineData("3.5", "35", false)]
    [InlineData("3", "III", true)]
    [InlineData("3", "IIII", false)]
    public void SeriesNumbersAreComparedWithoutLosingFractionalPositions(string expected, string supplied, bool matches)
    {
        var book = new Audiobook { Title = "The Churn", Authors = ["James S. A. Corey"], Series = "The Expanse", SeriesNumber = expected };
        Assert.Equal(matches, Assess(book, "The Churn - James S. A. Corey (The Expanse, Book " + supplied + ") [64 Kbps]").IsConfirmed);
    }

    [Fact]
    public void ConflictingReferenceSeriesPositionsRemainUnresolved()
    {
        var book = new Audiobook
        {
            Title = "The Churn",
            Authors = ["James S. A. Corey"],
            Series = "The Expanse",
            SeriesNumber = "3.5",
            SeriesMemberships = [new() { SeriesName = "The Expanse", SeriesNumber = "35" }]
        };
        Assert.False(Assess(book, "The Churn - James S. A. Corey (The Expanse, Book 3.5)").IsConfirmed);
    }

    [Fact]
    public void NarratorRoleDoesNotCorroborateAuthorship()
    {
        const string release = "The Long Walk - (Narrated by Kirby Heyborne) - Stephen King [M4B]";
        var book = new Audiobook { Title = "The Long Walk", Authors = ["Stephen King"] };
        var parsed = ReleaseNameInterpreter.Interpret(release, BookMatchContext.From(book), []);
        Assert.True(parsed.Complete);
        Assert.True(parsed.Credited);
        Assert.Contains(parsed.Spans, s => s.Role == ReleaseSpanRole.NarratorCredit && s.Value.Contains("Kirby Heyborne"));
        Assert.False(Assess(book, "The Long Walk - (Narrated by Stephen King) [M4B]").IsConfirmed);
        book.Title = "Kirby Heyborne";
        Assert.False(Assess(book, release).IsConfirmed);
    }

    [Fact]
    public void UnlabeledNarratorRequiresReferenceFacts()
    {
        const string release = "C.S.Lewis-A.Grief.Observed.Ralph.Cosham-PoF";
        var book = new Audiobook { Title = "A Grief Observed", Authors = ["C. S. Lewis"], Narrators = ["Ralph Cosham"] };
        Assert.True(Assess(book, release).IsConfirmed);
        book.Narrators = [];
        Assert.False(Assess(book, release).IsConfirmed);
        book.Narrators = ["Another Reader"];
        Assert.False(Assess(book, release).IsConfirmed);
        book.Narrators = ["Ralph Cosham"];
        Assert.False(Assess(book, "A Grief Observed - C. S. Lewis - Ralph Cosham").IsConfirmed);
    }

    [Fact]
    public void SeriesAndBundleTitlesCannotBecomeThePrimaryWork()
    {
        var book = new Audiobook { Title = "Leviathan", Authors = ["Scott Westerfeld"], Series = "Leviathan", SeriesNumber = "2" };
        Assert.False(Assess(book, "Behemoth [Leviathan 02] - Scott Westerfeld [MP3] [80 Kbps]").IsConfirmed);
        book = new() { Title = "Golden Son", Authors = ["Pierce Brown"], Series = "Red Rising", SeriesNumber = "2" };
        Assert.False(Assess(book, "Pierce Brown Red Rising and Golden Son Book 1 & 2 MP3").IsConfirmed);
    }

    [Theory]
    [InlineData("NMR")]
    [InlineData("PoF")]
    public void KnownTerminalGroupMarkersDoNotPermitOtherText(string group)
    {
        var book = new Audiobook { Title = "Hard Times", Authors = ["Charles Dickens"] };
        Assert.True(Assess(book, "Charles Dickens - Hard Times (" + group).IsConfirmed);
        Assert.False(Assess(book, "Charles Dickens - Hard Times (UnknownGroup)").IsConfirmed);
        Assert.False(Assess(book, "Charles Dickens - Hard Times - Another Work (" + group).IsConfirmed);
    }

    [Fact]
    public void LiteralPunctuationAndMetadataShapedTitlesSurvive()
    {
        var book = new Audiobook { Title = "S.", Authors = ["J. J. Abrams"] };
        Assert.False(Assess(book, "J.J. Abrams - S [64 Kbps]").IsConfirmed);
        Assert.True(Assess(book, "J.J. Abrams - S. [64 Kbps]").IsConfirmed);
        book = new() { Title = "64 Kbps", Authors = ["Alice Example"] };
        Assert.True(Assess(book, "Alice Example - 64 Kbps [M4B]").IsConfirmed);
        Assert.False(Assess(book, "Alice Example - Another Work [64 Kbps]").IsConfirmed);
    }

    [Fact]
    public void CanonicalUnicodeSpansReferToTheOriginalText()
    {
        const string release = "Rene\u0301 Descartes - Discourse on Method [64 Kbps]";
        var book = new Audiobook { Title = "Discourse on Method", Authors = ["René Descartes"] };
        var result = ReleaseNameInterpreter.Interpret(release, BookMatchContext.From(book), []);
        Assert.True(result.Complete);
        var author = Assert.Single(result.Spans, s => s.Role == ReleaseSpanRole.AuthorCredit);
        Assert.Equal("Rene\u0301 Descartes", release.Substring(author.Start, author.Length));
    }

    [Fact]
    public void CoauthorNamesStayCompleteAndIndependent()
    {
        var book = new Audiobook { Title = "Sleeping Beauties", Authors = ["Stephen King", "Owen King"] };
        Assert.True(Assess(book, "Sleeping Beauties - Stephen King, Owen King [64 Kbps]").IsConfirmed);
        Assert.False(Assess(book, "Sleeping Beauties - Stephen Owen King [64 Kbps]").IsConfirmed);
    }

    [Fact]
    public void ReferenceNarratorsAndSecondarySeriesAffectTheFingerprint()
    {
        var book = new Audiobook
        {
            Title = "The Book",
            Authors = ["Some Writer"],
            Narrators = ["First Reader"],
            SeriesMemberships = [new() { SeriesName = "Secondary", SeriesNumber = "3.5" }]
        };
        var first = BookMatchContext.From(book).Fingerprint;
        book.Narrators = ["Second Reader"];
        Assert.NotEqual(first, BookMatchContext.From(book).Fingerprint);
        first = BookMatchContext.From(book).Fingerprint;
        book.SeriesMemberships[0].SeriesNumber = "35";
        Assert.NotEqual(first, BookMatchContext.From(book).Fingerprint);
    }

    [Fact]
    public void CancellationCannotFallBackToAConfirmation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new BookIdentityMatcher().Evaluate(
            BookMatchContext.From(new() { Title = "The Book", Authors = ["Some Writer"] }),
            new() { ReleaseName = "Some Writer - The Book [64 Kbps]" }, cancellation.Token));
    }

    [Fact]
    public void UnexplainedSourceTextIsRetainedInDiagnostics()
    {
        const string release = "The Long Walk - Stephen King [64 Kbps] - Another Work";
        var parsed = ReleaseNameInterpreter.Interpret(release,
            BookMatchContext.From(new() { Title = "The Long Walk", Authors = ["Stephen King"] }), []);
        Assert.False(parsed.Complete);
        var unknown = Assert.Single(parsed.Spans, s => s.Role == ReleaseSpanRole.Unknown);
        Assert.Equal("Another Work", release.Substring(unknown.Start, unknown.Length));
    }
}
