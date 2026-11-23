using Listenarr.Application.Search.Matching;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseBracketFieldTests")]
[Trait("Category", "Application")]
public sealed class ReleaseBracketFieldTests : BaseTests
{
    private static Audiobook Book(string title = "Dracula", string author = "Bram Stoker") =>
        new() { Title = title, Authors = [author], Language = "English" };

    private static BookIdentityAssessment Evaluate(Audiobook book, string release) => new BookIdentityMatcher().Evaluate(
        BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current), new() { ReleaseName = release });

    [Theory]
    [InlineData("Dracula by Bram Stoker [M4B]")]
    [InlineData("Dracula by Bram Stoker [ENG / M4B]")]
    [InlineData("Dracula by Bram Stoker [ENG / MP3]")]
    [InlineData("Dracula by Bram Stoker [M4B / ENG]")]
    [InlineData("Dracula by Bram Stoker [M4B / Unabridged]")]
    [InlineData("Dracula by Bram Stoker [ENG]")]
    [InlineData("Dracula by Bram Stoker [English]")]
    [InlineData("Dracula by Bram Stoker (ENG / M4B)")]
    [InlineData("Dracula by Bram Stoker [English / M4B / 64 kb/s]")]
    public void SlashJoinedTechnicalAndLanguageFieldsAreExplained(string release)
    {
        var assessment = Evaluate(Book(), release);
        Assert.Equal(BookIdentityStatus.CorroboratedTitle, assessment.Status);
        Assert.Equal("complete_title_and_credit", assessment.Reason);
    }

    [Theory]
    [InlineData("Dracula by Bram Stoker [ENG / Frankenstein]")]
    [InlineData("Dracula by Bram Stoker [ENG / M4B / Mary Shelley]")]
    [InlineData("Dracula by Bram Stoker ENG / M4B")]
    [InlineData("Dracula by Bram Stoker ENG")]
    [InlineData("Dracula by Bram Stoker [M4B] ENG [MP3]")]
    [InlineData("Dracula by Bram Stoker (ENG]")]
    [InlineData("Dracula by English [M4B]")]
    [InlineData("Dracula by Mary Shelley [ENG / M4B]")]
    [InlineData("Frankenstein by Mary Shelley [ENG / M4B]")]
    [InlineData("Dracula / Frankenstein by Bram Stoker [ENG / M4B]")]
    [InlineData("Frankenstein / Dracula by Bram Stoker [ENG / M4B]")]
    public void UnrelatedTextAroundSlashesAndLanguageWordsStaysUnexplained(string release)
    {
        Assert.False(Evaluate(Book(), release).IsConfirmed);
    }

    [Theory]
    [InlineData("Dracula / Bram Stoker [M4B]", true)]
    [InlineData("Dracula / Mary Shelley [M4B]", false)]
    [InlineData("Dracula / Bram Stoker / Mary Shelley [M4B]", false)]
    public void ASlashSeparatedCreditIsAcceptedOnlyForTheKnownAuthor(string release, bool expected)
    {
        Assert.Equal(expected, Evaluate(Book(), release).IsConfirmed);
    }

    [Theory]
    [InlineData("Fire / Ice by Bram Stoker [ENG / M4B]", true)]
    [InlineData("Fire / Ice by Bram Stoker [M4B]", true)]
    [InlineData("Fire by Bram Stoker [ENG / M4B]", false)]
    [InlineData("Ice by Bram Stoker [ENG / M4B]", false)]
    [InlineData("Fire / Ice / Dracula by Bram Stoker [ENG / M4B]", false)]
    public void ASlashInsideTheReferenceTitleIsNotASeparatorOfTheTitle(string release, bool expected)
    {
        Assert.Equal(expected, Evaluate(Book("Fire / Ice"), release).IsConfirmed);
    }

    [Theory]
    [InlineData("[ENG / M4B]", "English")]
    [InlineData("[ENG]", "English")]
    [InlineData("[FRE / M4B]", "French")]
    [InlineData("[M4B / Deutsch]", "German")]
    public void DeclaredLanguageIsReportedAsReleaseDerivedEvidence(string tag, string language)
    {
        var assessment = Evaluate(Book(), $"Dracula by Bram Stoker {tag}");
        var evidence = Assert.Single(assessment.Languages);
        Assert.Equal(language, evidence.Language);
        Assert.Equal(IdentityEvidenceOrigin.ReleaseNameDerived, evidence.Origin);
    }

    [Fact]
    public void ReleasesWithoutALanguageTokenReportNoLanguage()
    {
        Assert.Empty(Evaluate(Book(), "Dracula by Bram Stoker [M4B / Unabridged]").Languages);
    }

    private static async Task<AutomaticCandidateScore> Score(string release)
    {
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync((List<SearchResult> candidates, QualityProfile _) => candidates.Select(r =>
                new QualityScore { SearchResult = r, TotalScore = 100000 }).ToList());
        var scorer = new AutomaticSearchScoringService(quality.Object, Mock.Of<IAudiobookRepository>(),
            NullLogger<AutomaticSearchScoringService>.Instance, TestAnnotationCatalog.CreateProvider());
        var candidate = new SearchResult
        {
            Id = "bracket-field",
            Title = release,
            Format = "M4B",
            Quality = "M4B",
            Size = 100 * 1024 * 1024,
            IdentityEvidence = new() { ReleaseName = release }
        };
        var assessment = await scorer.ScoreAsync(Book(), [candidate], new QualityProfile { PreferredLanguages = ["English"] });
        return Assert.Single(assessment.Candidates);
    }

    [Theory]
    [InlineData("Dracula by Bram Stoker [ENG / M4B]", false, null)]
    [InlineData("Dracula by Bram Stoker [FRE / M4B]", true, "language_mismatch")]
    [InlineData("Dracula by Bram Stoker [M4B / Deutsch]", true, "language_mismatch")]
    [InlineData("Dracula by Bram Stoker [ENG / FRE]", true, "language_evidence_conflict")]
    public async Task ADeclaredLanguageThatConflictsWithTheBookIsRejectedNotConfirmedSilently(string release, bool rejected, string? reason)
    {
        var score = await Score(release);
        Assert.True(score.Identity.IsConfirmed);
        Assert.Equal(rejected, score.IsRejected);
        if (reason != null) Assert.Contains(reason, score.RejectionReasons);
    }
}
