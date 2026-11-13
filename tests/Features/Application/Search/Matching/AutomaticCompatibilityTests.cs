using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "AutomaticCompatibilityTests")]
[Trait("Category", "Application")]
public sealed class AutomaticCompatibilityTests : BaseTests
{
    private static SearchResult Candidate(string release = "Mira Chen - The Harbor Lantern [M4B]") => new()
    {
        Id = "compatibility",
        Title = release,
        Format = "M4B",
        Quality = "M4B",
        Size = 100 * 1024 * 1024,
        IdentityEvidence = new() { ReleaseName = release }
    };
    private static Audiobook Book(string? subtitle = null) => new()
    { Title = "The Harbor Lantern", Subtitle = subtitle, Authors = ["Mira Chen"], Asin = "B000000001" };
    private static QualityProfile Profile(params string[] languages) => new() { PreferredLanguages = languages.ToList() };
    private static async Task<AutomaticCandidateScore> Score(Audiobook book, SearchResult result, QualityProfile? profile = null)
    {
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync((List<SearchResult> candidates, QualityProfile _) => candidates.Select(r =>
                new QualityScore { SearchResult = r, TotalScore = 100000 }).ToList());
        var scorer = new AutomaticSearchScoringService(quality.Object, Mock.Of<IAudiobookRepository>(),
            NullLogger<AutomaticSearchScoringService>.Instance, TestAnnotationCatalog.CreateProvider());
        var assessment = await scorer.ScoreAsync(book, [result], profile ?? Profile());
        var score = Assert.Single(assessment.Candidates);
        quality.Verify(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()), Times.Once);
        Assert.Equal(!score.IsRejected, scorer.SelectBest(assessment) != null);
        return score;
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Swedish", true)]
    [InlineData("English", true)]
    public async Task Case40PreservesSceneLanguageAndCannotBeRescuedByQuality(string? language, bool rejected)
    {
        var candidate = Candidate("James Patterson - Jack And Jill-AUDiOBOOK-WEB-SE-2021-CRAViNGS iNT");
        candidate.Language = language;
        var score = await Score(new() { Title = "Jack and Jill", Authors = ["James Patterson"] }, candidate, Profile("English"));
        Assert.True(score.Identity.IsConfirmed);
        Assert.False(score.Quality.IsRejected);
        Assert.Equal(rejected, score.IsRejected);
        Assert.Contains("language_mismatch", score.RejectionReasons);
        Assert.Contains(score.Compatibility.Languages, l => l.Language == "Swedish" && l.Origin == IdentityEvidenceOrigin.ReleaseNameDerived);
    }

    [Theory]
    [InlineData("English", "en-US", false)]
    [InlineData("ENG", "en", false)]
    [InlineData("sv-SE", "Swedish", false)]
    [InlineData("SWE", "SV", false)]
    [InlineData("Swedish", "English", true)]
    [InlineData("unknown", "English", false)]
    [InlineData(null, "English", false)]
    public async Task StructuredLanguageUsesCanonicalAliases(string? language, string preferred, bool rejected)
    {
        var candidate = Candidate(); candidate.Language = language;
        var score = await Score(Book(), candidate, Profile(preferred));
        Assert.Equal(rejected, score.IsRejected);
    }

    [Fact]
    public async Task MatchingSwedishTargetAndProfileAllowSwedishScene()
    {
        var candidate = Candidate("Mira Chen - The Harbor Lantern-AUDiOBOOK-WEB-SE-2021-CRAViNGS iNT");
        var book = Book(); book.Language = "sv-SE";
        Assert.False((await Score(book, candidate, Profile("Swedish"))).IsRejected);
        Assert.False((await Score(Book(), candidate)).IsRejected);
    }

    [Theory]
    [InlineData(IdentityEvidenceOrigin.Unattributed, true)]
    [InlineData(IdentityEvidenceOrigin.ProviderSupplied, true)]
    [InlineData(IdentityEvidenceOrigin.Verified, true)]
    [InlineData(IdentityEvidenceOrigin.ReleaseNameDerived, false)]
    [InlineData(IdentityEvidenceOrigin.ContextDerived, false)]
    public async Task LooseInferencesAreNotPromotedToExplicitMetadata(IdentityEvidenceOrigin origin, bool rejected)
    {
        var candidate = Candidate(); candidate.Language = "Swedish"; candidate.LanguageOrigin = origin;
        Assert.Equal(rejected, (await Score(Book(), candidate, Profile("English"))).IsRejected);
    }

    [Fact]
    public async Task VerifiedIdentifierCannotOverrideLanguageMismatch()
    {
        var candidate = Candidate(); candidate.Language = "Swedish";
        candidate.IdentityEvidence = candidate.IdentityEvidence! with
        { Identifiers = [new("asin", "B000000001", IdentityEvidenceOrigin.Verified)] };
        var score = await Score(Book(), candidate, Profile("English"));
        Assert.Equal(BookIdentityStatus.ConfirmedIdentifier, score.Identity.Status);
        Assert.True(score.IsRejected);
    }

    [Fact]
    public async Task StoredLanguageStillConstrainsAProfileWithMultipleLanguages()
    {
        var book = Book(); book.Language = "English";
        var candidate = Candidate(); candidate.Language = "Swedish";
        Assert.True((await Score(book, candidate, Profile("English", "Swedish"))).IsRejected);
    }

    [Fact]
    public async Task LiteralSceneLikeTitleIsNotLanguageEvidence()
    {
        var title = "WEB-SE-2021-CRAViNGS iNT";
        var candidate = Candidate(title);
        candidate.IdentityEvidence = new() { ReleaseName = title, Authors = [new("Mira Chen", IdentityEvidenceOrigin.ProviderSupplied)] };
        Assert.False((await Score(new() { Title = title, Authors = ["Mira Chen"] }, candidate, Profile("English"))).IsRejected);
    }

    [Theory]
    [InlineData("Adapted for Modern Readers")]
    [InlineData("Dramatized Adaptation")]
    [InlineData("Abridged")]
    [InlineData("Unabridged")]
    [InlineData("Retold for Young Readers")]
    [InlineData("(Abridged)")]
    [InlineData("[Unabridged]")]
    [InlineData("(Adapted for Modern Readers)")]
    public async Task ExplicitEditionTargetsNeedSupportingEvidence(string subtitle)
    {
        var score = await Score(Book(subtitle), Candidate());
        Assert.True(score.Identity.IsConfirmed);
        Assert.True(score.IsRejected);
        Assert.Contains("edition_evidence_required", score.RejectionReasons);
    }

    [Fact]
    public async Task Case86IsConfirmedAsAWorkButDeferredAsAnEdition()
    {
        var score = await Score(new()
        {
            Title = "The Adventures of Tom Sawyer",
            Subtitle = "Adapted for Modern Readers",
            Authors = ["Mark Twain", "Jeremy Dinghinton"]
        }, Candidate("Mark Twain 2019 The Adventures of Tom Sawyer (Classic Fiction)"));
        Assert.True(score.Identity.IsConfirmed);
        Assert.True(score.IsRejected);
        Assert.Contains("edition_evidence_required", score.RejectionReasons);
    }

    [Theory]
    [InlineData("Adapted for Modern Readers", "Mira Chen - The Harbor Lantern: Adapted for Modern Readers [M4B]", false)]
    [InlineData("Adapted for Modern Readers", "Mira Chen - The Harbor Lantern: Adapted for Modern Readers [Unabridged]", false)]
    [InlineData("Abridged", "Mira Chen - The Harbor Lantern [Abridged] [M4B]", false)]
    [InlineData("Unabridged", "Mira Chen - The Harbor Lantern [Unabridged] [M4B]", false)]
    [InlineData("Unabridged", "Mira Chen - The Harbor Lantern [2019 abridged MP3]", true)]
    public async Task EditionSupportAndContradictionsAreChecked(string subtitle, string release, bool rejected)
    {
        Assert.Equal(rejected, (await Score(Book(subtitle), Candidate(release))).IsRejected);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("matching", false)]
    [InlineData("unverified", true)]
    public async Task OnlyVerifiedEditionIdentifiersCanSupplyMissingEditionEvidence(string id, bool rejected)
    {
        var candidate = Candidate();
        candidate.IdentityEvidence = candidate.IdentityEvidence! with
        {
            Identifiers = id == "none" ? [] :
            [new("asin", "B000000001", id == "matching" ? IdentityEvidenceOrigin.Verified : IdentityEvidenceOrigin.ReleaseNameDerived)]
        };
        Assert.Equal(rejected, (await Score(Book("Adapted for Modern Readers"), candidate)).IsRejected);
    }

    [Fact]
    public async Task CollaborationsDoNotRequireEveryStoredAuthor()
    {
        var book = Book(); book.Authors!.Add("Another Coauthor");
        Assert.False((await Score(book, Candidate())).IsRejected);
    }

    [Theory]
    [InlineData("A History of Adaptation")]
    [InlineData("The Unabridged History of Travel")]
    public async Task LiteralSubtitleWordsDoNotInventEditionRequirements(string subtitle)
    {
        Assert.False((await Score(Book(subtitle), Candidate())).IsRejected);
    }

    [Fact]
    public async Task LanguageAndSubtitleMutationsInvalidateCandidateAssessment()
    {
        var candidate = Candidate();
        var score = await Score(Book(), candidate);
        Assert.True(score.SourceIsCurrent);
        candidate.Language = "Swedish";
        Assert.False(score.SourceIsCurrent);
        candidate.Language = null;
        candidate.Subtitle = "Abridged";
        Assert.False(score.SourceIsCurrent);
        candidate.Subtitle = null;
        candidate.LanguageOrigin = IdentityEvidenceOrigin.ContextDerived;
        Assert.False(score.SourceIsCurrent);
    }

    [Theory]
    [InlineData("Mira Chen - The Harbor Lantern [2019 abridged MP3]")]
    [InlineData("Mira Chen - The Harbor Lantern [Unabridged Abridged]")]
    public async Task EditionMarkersCannotHideInCombinedTechnicalFields(string release)
    {
        var score = await Score(Book(), Candidate(release));
        Assert.True(score.IsRejected);
        Assert.NotEmpty(score.Compatibility.Reasons);
    }

    [Fact]
    public async Task SceneLanguageCannotBeHiddenByAStoredSeriesName()
    {
        var book = Book(); book.Series = "WEB-SE-2021-CRAViNGS iNT";
        var candidate = Candidate("Mira Chen - The Harbor Lantern-AUDiOBOOK-WEB-SE-2021-CRAViNGS iNT");
        Assert.True((await Score(book, candidate, Profile("English"))).IsRejected);
    }

    [Fact]
    public async Task LanguageMutationDuringQualityScoringCannotUseAnOldCompatibilityDecision()
    {
        var candidate = Candidate(); candidate.Language = "English";
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .Callback(() => candidate.Language = "Swedish")
            .ReturnsAsync([new() { SearchResult = candidate, TotalScore = 10000 }]);
        var scorer = new AutomaticSearchScoringService(quality.Object, Mock.Of<IAudiobookRepository>(),
            NullLogger<AutomaticSearchScoringService>.Instance, TestAnnotationCatalog.CreateProvider());
        var batch = await scorer.ScoreAsync(Book(), [candidate], Profile("English"));
        Assert.Null(scorer.SelectBest(batch));
        Assert.Contains("candidate_changed", batch.Candidates[0].RejectionReasons);
    }

    [Fact]
    public async Task PersistedLanguageChangeInvalidatesAssessment()
    {
        var book = Book(); book.Id = 42; book.QualityProfile = Profile("English");
        var candidate = Candidate(); candidate.Language = "English";
        var books = new Mock<IAudiobookRepository>();
        books.Setup(b => b.GetByIdSnapshotAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(book);
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync([new() { SearchResult = candidate, TotalScore = 100 }]);
        var scorer = new AutomaticSearchScoringService(quality.Object, books.Object,
            NullLogger<AutomaticSearchScoringService>.Instance, TestAnnotationCatalog.CreateProvider());
        var batch = await scorer.ScoreAsync(book, [candidate], book.QualityProfile);
        Assert.True(await scorer.IsCurrentAsync(batch, 42));
        book.Language = "Swedish";
        Assert.False(await scorer.IsCurrentAsync(batch, 42));
    }

    [Theory]
    [InlineData("English; Swedish", true)]
    [InlineData("en/Swedish", true)]
    [InlineData("English, ENG", false)]
    public async Task MultipleLanguageClaimsAreNotReducedToTheFirstValue(string language, bool rejected)
    {
        var candidate = Candidate(); candidate.Language = language;
        Assert.Equal(rejected, (await Score(Book(), candidate, Profile("English", "Swedish"))).IsRejected);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Unabridged", true)]
    public async Task VerifiedEditionIdFillsUnknownFactsButNeverOverridesContradictions(string? targetEdition, bool rejected)
    {
        var candidate = Candidate(); candidate.Subtitle = "Abridged";
        candidate.IdentityEvidence = candidate.IdentityEvidence! with
        { Identifiers = [new("asin", "B000000001", IdentityEvidenceOrigin.Verified)] };
        Assert.Equal(rejected, (await Score(Book(targetEdition), candidate)).IsRejected);
    }

    [Fact]
    public async Task EditionArticlesArePresentationOnlyWithinARecognizedDesignation()
    {
        var candidate = Candidate(); candidate.Subtitle = "Dramatized Adaptation";
        Assert.False((await Score(Book("A Dramatized Adaptation"), candidate)).IsRejected);
    }

    [Theory]
    [InlineData(IdentityEvidenceOrigin.ReleaseNameDerived, false)]
    [InlineData(IdentityEvidenceOrigin.ProviderSupplied, true)]
    public async Task LanguageProvenanceSurvivesIndexerAndMetadataRoundTrips(IdentityEvidenceOrigin origin, bool rejected)
    {
        var candidate = Candidate(); candidate.Language = "Swedish"; candidate.LanguageOrigin = origin;
        var indexer = SearchResultConverters.ToSearchResult(SearchResultConverters.ToIndexerSearchResult(candidate));
        var metadata = SearchResultConverters.ToSearchResult(SearchResultConverters.ToMetadata(indexer));
        Assert.Equal(origin, metadata.LanguageOrigin);
        Assert.Equal(rejected, (await Score(Book(), metadata, Profile("English"))).IsRejected);
    }

    [Fact]
    public async Task LegacyConverterTitleGuessCannotBecomeExplicitLanguageEvidence()
    {
        var candidate = SearchResultConverters.ToSearchResult(new IndexerSearchResult
        {
            Title = "Mira Chen - FR [M4B]",
            DownloadType = "Torrent",
            IdentityEvidence = new() { ReleaseName = "Mira Chen - FR [M4B]" }
        });
        Assert.Equal("French", candidate.Language);
        Assert.Equal(IdentityEvidenceOrigin.ReleaseNameDerived, candidate.LanguageOrigin);
        Assert.False((await Score(new() { Title = "FR", Authors = ["Mira Chen"] }, candidate, Profile("English"))).IsRejected);
    }

    [Fact]
    public async Task AmbiguousReferenceLanguageDoesNotSilentlyChooseOneLanguage()
    {
        var book = Book(); book.Language = "English; Swedish";
        var candidate = Candidate(); candidate.Language = "English";
        var score = await Score(book, candidate, Profile("English", "Swedish"));
        Assert.True(score.IsRejected);
        Assert.Contains("reference_language_ambiguous", score.RejectionReasons);
    }

    [Theory]
    [InlineData(null, IdentityEvidenceOrigin.ContextDerived)]
    [InlineData("Swedish", IdentityEvidenceOrigin.ProviderSupplied)]
    public async Task BothMetadataConversionPathsPreserveLanguageSource(string? language, IdentityEvidenceOrigin expected)
    {
        var converter = new Listenarr.Application.Search.Metadata.MetadataConverters(null,
            NullLogger<Listenarr.Application.Search.Metadata.MetadataConverters>.Instance);
        var metadata = new AudibleBookMetadata { Title = "The Harbor Lantern", Authors = ["Mira Chen"], Language = language };
        var unified = await converter.ConvertMetadataToSearchResultAsync(metadata, "B000000001", fallbackLanguage: "Swedish");
        var separate = await converter.ConvertMetadataToMetadataSearchResultAsync(metadata, "B000000001", fallbackLanguage: "Swedish");
        Assert.Equal(expected, unified.LanguageOrigin);
        Assert.Equal(expected, separate.LanguageOrigin);
        Assert.Equal(expected, SearchResultConverters.ToSearchResult(separate).LanguageOrigin);
        Assert.Equal("Swedish", separate.Language);
    }
}
