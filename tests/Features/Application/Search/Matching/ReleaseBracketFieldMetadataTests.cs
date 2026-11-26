using Listenarr.Application.Search.Matching;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseBracketFieldMetadataTests")]
[Trait("Category", "Application")]
public sealed class ReleaseBracketFieldMetadataTests : BaseTests
{
    private static Audiobook Book(string title = "Dracula", string[]? authors = null, string[]? narrators = null, string? series = null)
    {
        var book = new Audiobook { Title = title, Authors = [.. authors ?? ["Bram Stoker"]], Language = "English" };
        if (narrators != null) book.Narrators = [.. narrators];
        if (series != null) book.SeriesMemberships = [new() { SeriesName = series, SeriesNumber = "1", IsPrimary = true }];
        return book;
    }

    private static BookIdentityAssessment Evaluate(Audiobook book, string release) => new BookIdentityMatcher().Evaluate(
        BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current), new() { ReleaseName = release });

    [Theory]
    [InlineData("Dracula by Bram Stoker - Narrated by AC/DC [M4B]", "Dracula", "Bram Stoker", "AC/DC", true)]
    [InlineData("DC by Bram Stoker - Narrated by AC/DC [M4B]", "DC", "Bram Stoker", "AC/DC", true)]
    [InlineData("DC by Bram Stoker - Read by AC/DC [M4B]", "DC", "Bram Stoker", "AC/DC", true)]
    [InlineData("Bram Stoker - Narrated by AC/DC [M4B]", "DC", "Bram Stoker", "AC/DC", false)]
    [InlineData("Bram Stoker - Narrated by AC/DC [M4B]", "DC", "Bram Stoker", null, false)]
    [InlineData("DC by Bram Stoker - Narrated by AC/DC [M4B]", "DC", "Bram Stoker", null, false)]
    [InlineData("DC by Bram Stoker - Narrator: AC/DC [M4B]", "DC", "Bram Stoker", "AC/DC", true)]
    [InlineData("Bram Stoker - Narrator: AC/DC [M4B]", "DC", "Bram Stoker", "AC/DC", false)]
    [InlineData("Bram Stoker - Narrator: AC/DC [M4B]", "DC", "Bram Stoker", null, false)]
    [InlineData("DC by Bram Stoker - Narrator : AC/DC [M4B]", "DC", "Bram Stoker", "AC/DC", true)]
    [InlineData("Bram Stoker - Narrator : AC/DC [M4B]", "DC", "Bram Stoker", "AC/DC", false)]
    [InlineData("Dracula by Bram Stoker - Narrated by Bob Thomley / Frankenstein by Mary Shelley [M4B]", "Dracula", "Bram Stoker", "Bob Thomley", false)]
    [InlineData("Dracula by Bram Stoker - Narrated by Bob Thomley / Mary Shelley [M4B]", "Dracula", "Bram Stoker", "Bob Thomley", false)]
    public void ATitleInsideASlashContainingNarratorCreditIsNotAnAnchor(string release, string title, string author, string? narrator, bool expected)
    {
        var book = Book(title, [author], narrator is null ? null : [narrator]);
        Assert.Equal(expected, Evaluate(book, release).IsConfirmed);
    }

    private static async Task<AutomaticCandidateScore> Score(Audiobook book, string release)
    {
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync((List<SearchResult> candidates, QualityProfile _) => candidates.Select(r =>
                new QualityScore { SearchResult = r, TotalScore = 100000 }).ToList());
        var scorer = new AutomaticSearchScoringService(quality.Object, Mock.Of<IAudiobookRepository>(),
            NullLogger<AutomaticSearchScoringService>.Instance, TestAnnotationCatalog.CreateProvider());
        var candidate = new SearchResult
        {
            Id = "bracket-field-metadata",
            Title = release,
            Format = "M4B",
            Quality = "M4B",
            Size = 100 * 1024 * 1024,
            IdentityEvidence = new() { ReleaseName = release }
        };
        var assessment = await scorer.ScoreAsync(book, [candidate], new QualityProfile { PreferredLanguages = ["English"] });
        return Assert.Single(assessment.Candidates);
    }

    [Theory]
    [InlineData("Dracula by Bram Stoker [Dan]", "Dan", null)]
    [InlineData("Dracula by Bram Stoker [Dan / M4B]", "Dan", null)]
    [InlineData("Dracula by Bram Stoker [M4B / Dan]", "Dan", null)]
    [InlineData("Dracula by Bram Stoker [It]", null, "It")]
    public async Task ATokenThatMatchesTheBookMetadataIsNotLanguageEvidence(string release, string? coauthor, string? series)
    {
        var book = Book(authors: coauthor is null ? null : ["Bram Stoker", coauthor], series: series);
        var score = await Score(book, release);
        Assert.True(score.Identity.IsConfirmed);
        Assert.Empty(score.Identity.Languages);
        Assert.False(score.IsRejected);
    }

    [Theory]
    [InlineData("Dracula by Bram Stoker [DAN / M4B]", "Danish")]
    [InlineData("Dracula by Bram Stoker [IT / M4B]", "Italian")]
    [InlineData("Dracula by Bram Stoker [Dan]", "Danish")]
    [InlineData("Dracula by Bram Stoker [It]", "Italian")]
    public async Task ATokenWithoutAMetadataMatchStillDeclaresItsLanguage(string release, string language)
    {
        var score = await Score(Book(), release);
        Assert.Equal(language, Assert.Single(score.Identity.Languages).Language);
        Assert.True(score.IsRejected);
        Assert.Contains("language_mismatch", score.RejectionReasons);
    }

    [Theory]
    [InlineData("Dracula by Bram Stoker [German]", "German", "German")]
    [InlineData("Dracula by Bram Stoker [French]", "The French", "French")]
    public async Task AFullLanguageNameThatMatchesASeriesStillDeclaresItsLanguage(string release, string series, string language)
    {
        var score = await Score(Book(series: series), release);
        Assert.Equal(language, Assert.Single(score.Identity.Languages).Language);
        Assert.True(score.IsRejected);
        Assert.Contains("language_mismatch", score.RejectionReasons);
    }

    [Fact]
    public async Task AMetadataMatchDoesNotHideADifferentLanguageInTheSameBracket()
    {
        var score = await Score(Book(authors: ["Bram Stoker", "Dan"]), "Dracula by Bram Stoker [Dan / FRE]");
        Assert.Equal("French", Assert.Single(score.Identity.Languages).Language);
        Assert.Contains("language_mismatch", score.RejectionReasons);
    }

    [Theory]
    [InlineData("Dracula by Bram Stoker - Narrated by Bob Thomley [FRE / M4B]")]
    [InlineData("Dracula by Bram Stoker - Narrated by Bob Thomley / FRE [M4B]")]
    [InlineData("Dracula by Bram Stoker - Read by Bob Thomley / GER [M4B]")]
    [InlineData("Dracula by Bram Stoker - Narrator: Bob Thomley / GER [M4B]")]
    [InlineData("Dracula by Bram Stoker - Narrated by Bob Thomley/FRE [M4B]")]
    [InlineData("Dracula by Bram Stoker - Narrated by Bob Thomley / French [M4B]")]
    public async Task ASlashAfterAnExplicitNarratorDoesNotSwallowADeclaredLanguage(string release)
    {
        var score = await Score(Book(narrators: ["Bob Thomley"]), release);
        Assert.False(score.Identity.IsConfirmed && !score.IsRejected);
    }

    [Theory]
    [InlineData("Dracula by Bram Stoker [Narrated by Bob Thomley / FRE]", "French")]
    [InlineData("Dracula by Bram Stoker [Read by Bob Thomley / GER]", "German")]
    public async Task ABracketedNarratorCreditWithASlashLanguageStillDeclaresTheLanguage(string release, string language)
    {
        var score = await Score(Book(narrators: ["Bob Thomley"]), release);
        Assert.Equal(language, Assert.Single(score.Identity.Languages).Language);
        Assert.True(score.IsRejected);
        Assert.Contains("language_mismatch", score.RejectionReasons);
    }
}
