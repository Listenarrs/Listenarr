using Listenarr.Tests.Common;
using Listenarr.Application.Search.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseRecallAdversarialTests")]
[Trait("Category", "Application")]
public sealed class ReleaseRecallAdversarialTests : BaseTests
{
    private static Audiobook Book() => new()
    {
        Id = 9,
        Title = "The Quiet Orchard",
        Authors = ["Marisol Vega"],
        Series = "Northern Shore",
        SeriesNumber = "3",
        Narrators = ["Ramon de Ocampo"],
        QualityProfile = new()
    };
    private static BookMatchContext Context(Audiobook book) => BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current);
    private static bool Confirms(Audiobook book, string release) => new BookIdentityMatcher().Evaluate(Context(book), new() { ReleaseName = release }).IsConfirmed;

    [Theory]
    [InlineData("Marisol Vega - The Quiet Orchard-AUDiOBOOK-WEB-EN-2021-EarReaders iNT")]
    [InlineData("Marisol.Vega-The.Quiet.Orchard.M4B-Ocampo.2010.EarReaders")]
    [InlineData("Marisol Vega - The Quiet Orchard [retail iNT]")]
    [InlineData("NMR Unrelated text Marisol Vega - The Quiet Orchard")]
    [InlineData("Marisol Vega - The Quiet Orchard [EarReaders Another Work]")]
    [InlineData("Marisol Vega - The Quiet Orchard - Northern Shore Book 4 [EarReaders]")]
    [InlineData("NMR--Other Writer - The Quiet Orchard (Audible) {dwg}")]
    public void CatalogPlacementAndConflictRulesCannotBeBypassed(string release) => Assert.False(Confirms(Book(), release));

    [Fact]
    public void CatalogLabelCannotMasqueradeAsAnAuthorOrSeries()
    {
        var book = Book(); book.Authors!.Add("NMR");
        Assert.False(Confirms(book, "Marisol Vega - The Quiet Orchard [NMR]"));
        book.Authors.RemoveAt(1); book.Series = "NMR";
        Assert.False(Confirms(book, "Marisol Vega - The Quiet Orchard [NMR]"));
    }

    [Fact]
    public void AYearInsideTheLiteralSeriesOrTitleIsNotAnOrdinal()
    {
        var book = Book(); book.Series = "Northern Shore 2010";
        Assert.True(Confirms(book, "Marisol Vega - Northern Shore 2010 - The Quiet Orchard"));
        book.Title = "1984"; book.Series = null; book.SeriesNumber = null;
        Assert.True(Confirms(book, "Marisol Vega - 1984 [M4B]"));
        Assert.False(Confirms(book, "Other Writer - 1984 [M4B]"));
    }

    [Theory]
    [InlineData("(Northern Shore) 2010 [M4B]", false)]
    [InlineData("(Northern Shore) 2010 MP3", false)]
    [InlineData("(Northern Shore) [2010 MP3]", true)]
    [InlineData("(Northern Shore) Book 3 [2010]", true)]
    [InlineData("(Northern Shore) Book 4 [2010]", false)]
    public void AdjacentUnnumberedSeriesCannotHideAnAmbiguousYear(string tail, bool expected) =>
        Assert.Equal(expected, Confirms(Book(), "Marisol Vega - The Quiet Orchard " + tail));

    [Fact]
    public void SubtitleDescriptorCannotHideAConflictingPositionOrUnknownText()
    {
        var book = Book(); book.Subtitle = "Northern Shore";
        Assert.True(Confirms(book, "The Quiet Orchard: Northern Shore Series - Marisol Vega [M4B]"));
        Assert.False(Confirms(book, "The Quiet Orchard: Northern Shore Series Book 4 - Marisol Vega [M4B]"));
        Assert.False(Confirms(book, "The Quiet Orchard: Northern Shore Series Another Work - Marisol Vega [M4B]"));
        book.Series = null; book.SeriesNumber = null;
        Assert.False(Confirms(book, "The Quiet Orchard: Northern Shore Series - Marisol Vega [M4B]"));
    }

    [Fact]
    public void ParentheticalCreditRequiresAWholeKnownSeriesAndCannotBorrowTheSubtitle()
    {
        var book = Book();
        Assert.True(Confirms(book, "The Quiet Orchard Northern Shore(marisol vega) [MP3]"));
        Assert.False(Confirms(book, "The Quiet Orchard Unknown Shore(marisol vega) [MP3]"));
        Assert.False(Confirms(book, "The Quiet Orchard Northern Shore(other writer) [MP3]"));
        Assert.False(Confirms(book, "The Quiet Orchard Northern Shore 4(marisol vega) [MP3]"));
        book.Subtitle = "Marisol Vega";
        Assert.False(Confirms(book, "The Quiet Orchard Northern Shore(marisol vega) [MP3]"));
    }

    [Fact]
    public async Task UnrelatedCatalogSnapshotInvalidatesPreviouslyScoredCandidates()
    {
        var book = Book();
        var candidate = new SearchResult { Id = "one", Title = "Marisol Vega - The Quiet Orchard" };
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync([new() { SearchResult = candidate, TotalScore = 100000 }]);
        var repository = new Mock<IAudiobookRepository>();
        repository.Setup(r => r.GetByIdSnapshotAsync(book.Id, It.IsAny<CancellationToken>())).ReturnsAsync(book);
        var original = new AutomaticSearchScoringService(quality.Object, repository.Object,
            NullLogger<AutomaticSearchScoringService>.Instance, TestAnnotationCatalog.Current);
        var different = new ReleaseAnnotationCatalog(1, [], [], [], [], [], [], []);
        var changed = new AutomaticSearchScoringService(quality.Object, repository.Object,
            NullLogger<AutomaticSearchScoringService>.Instance, different);
        var batch = await original.ScoreAsync(book, [candidate], book.QualityProfile!);
        Assert.NotNull(original.SelectBest(batch));
        Assert.True(await original.IsCurrentAsync(batch, book.Id));
        Assert.False(await changed.IsCurrentAsync(batch, book.Id));
    }

    [Fact]
    public async Task UnavailableCatalogStillScoresButNeverSelectsEvenVerifiedIds()
    {
        var book = Book(); book.Asin = "B000000001";
        var candidate = new SearchResult
        {
            Id = "one",
            Title = "Marisol Vega - The Quiet Orchard",
            IdentityEvidence = new()
            { ReleaseName = "Marisol Vega - The Quiet Orchard", Identifiers = [new("asin", "B000000001", IdentityEvidenceOrigin.Verified)] }
        };
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync([new() { SearchResult = candidate, TotalScore = 100000 }]);
        var scorer = new AutomaticSearchScoringService(quality.Object, Mock.Of<IAudiobookRepository>(),
            NullLogger<AutomaticSearchScoringService>.Instance, ReleaseAnnotationCatalog.Unavailable);
        var batch = await scorer.ScoreAsync(book, [candidate], book.QualityProfile!);
        quality.Verify(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()), Times.Once);
        Assert.Null(scorer.SelectBest(batch));
        Assert.Contains("annotation_catalog_unavailable", batch.Candidates[0].RejectionReasons);
    }
}
