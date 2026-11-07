using Listenarr.Tests.Common;
using Listenarr.Application.Search.Matching;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseRecallRuleTests")]
[Trait("Category", "Application")]
public sealed class ReleaseRecallRuleTests : BaseTests
{
    private static Audiobook Book(string? series = "Northern Shore", string? position = "3", string narrator = "Ramon de Ocampo") => new()
    { Title = "The Quiet Orchard", Authors = ["Marisol Vega"], Series = series, SeriesNumber = position, Narrators = [narrator] };
    private static BookIdentityAssessment Match(Audiobook book, string release) => new BookIdentityMatcher().Evaluate(
        BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current), new() { ReleaseName = release });

    [Theory]
    [InlineData("NMR--Marisol Vega - The Quiet Orchard", true)]
    [InlineData("NMR - Other Writer - The Quiet Orchard", false)]
    [InlineData("EarReaders--Marisol Vega - The Quiet Orchard", false)]
    [InlineData("Marisol Vega - The Quiet Orchard [EarReaders]", true)]
    [InlineData("Marisol Vega - The Quiet Orchard [MP3] zeke23", true)]
    [InlineData("Marisol Vega - The Quiet Orchard zeke23", false)]
    [InlineData("Marisol Vega - The Quiet Orchard (retail) {dwg}", true)]
    [InlineData("Marisol Vega - The Quiet Orchard retail", false)]
    [InlineData("Marisol Vega - The Quiet Orchard (Audible)", true)]
    [InlineData("Marisol Vega - The Quiet Orchard [UnknownGroup]", false)]
    [InlineData("Marisol Vega - Another Work [EarReaders]", false)]
    [InlineData("The Quiet Orchard [EarReaders]", false)]
    public void CatalogLabelsExplainOnlyTheirPermittedLayouts(string release, bool confirmed) =>
        Assert.Equal(confirmed, Match(Book(), release).IsConfirmed);

    [Theory]
    [InlineData("Northern Shore", "The Northern Shore", true)]
    [InlineData("The Northern Shore", "Northern Shore", true)]
    [InlineData("Northern Shore Trilogy", "The Northern Shore", true)]
    [InlineData("Northern Shore", "The Northern Shore Series", true)]
    [InlineData("Presidential Agent", "Presidential Agents", true)]
    [InlineData("Northern Shore", "Northern Shore Adventures", false)]
    [InlineData("Northern Shore", "Northern Shores", false)]
    public void SeriesVariantsAreBoundedAndReferenceBacked(string stored, string release, bool expected) =>
        Assert.Equal(expected, Match(Book(stored), $"Marisol Vega - {release}, Bk 3 - The Quiet Orchard [M4B]").IsConfirmed);

    [Fact]
    public void AliasCollisionDoesNotMergeDistinctFactsEvenWhenTheirPositionsAgree()
    {
        var book = Book("Northern Shore Series");
        book.SeriesMemberships = [new() { SeriesName = "Northern Shore Trilogy", SeriesNumber = "3" }];
        var result = Match(book, "Marisol Vega - The Northern Shore 3 - The Quiet Orchard");
        Assert.False(result.IsConfirmed);
        Assert.Equal("series_alias_ambiguous", result.Reason);
        Assert.True(Match(book, "Marisol Vega - Northern Shore Series 3 - The Quiet Orchard").IsConfirmed);
    }

    [Theory]
    [InlineData("Northern Shore 2010", "3", false, "numeric_annotation_ambiguous")]
    [InlineData("Northern Shore Book 2010", "3", false, "series_position_conflict")]
    [InlineData("Northern Shore #2010", "3", false, "series_position_conflict")]
    [InlineData("Northern Shore Book 2010", "2010", true, "complete_title_and_credit")]
    [InlineData("Northern Shore 3", "3", true, "complete_title_and_credit")]
    [InlineData("Northern Shore 4", "3", false, "series_position_conflict")]
    public void YearShapesDoNotSilentlyBecomeMatchingOrConflictingPositions(string field, string stored, bool expected, string reason)
    {
        var result = Match(Book(position: stored), $"Marisol Vega - {field} - The Quiet Orchard [M4B]");
        Assert.Equal(expected, result.IsConfirmed);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData("Ramon de Ocampo", "de.Ocampo", true)]
    [InlineData("Ramon de Ocampo", "Ocampo", false)]
    [InlineData("Grover Gardner Jr.", "Gardner", true)]
    [InlineData("Grover Gardner Jr.", "Gardner.Jr", true)]
    [InlineData("Grover Gardner Jr.", "Jr", false)]
    [InlineData("Ruben Santiago-Hudson", "Santiago.Hudson", true)]
    [InlineData("Ruben Santiago-Hudson", "Hudson", false)]
    [InlineData("Mary O'Connell", "OConnell", true)]
    [InlineData("Full Cast", "Cast", false)]
    [InlineData("Virtual Voice", "Voice", false)]
    [InlineData("Ramon de Ocampo", "Unknown", false)]
    public void NarratorVariantsKeepCompoundSurnamesAndDoNotUseSuffixesAsNames(string stored, string candidate, bool expected) =>
        Assert.Equal(expected, Match(Book(narrator: stored), $"Marisol.Vega-The.Quiet.Orchard.Unabr-M4B-{candidate}.2010.NMR").IsConfirmed);

    [Fact]
    public void SharedNarratorSurnameOrAuthorSurnameIsNotCorroboration()
    {
        var book = Book(narrator: "Grover Gardner Jr.");
        book.Narrators!.Add("John Gardner");
        Assert.False(Match(book, "Marisol.Vega-The.Quiet.Orchard.M4B-Gardner.2010.NMR").IsConfirmed);
        book.Narrators.RemoveAt(1);
        book.Authors!.Add("John Gardner");
        Assert.False(Match(book, "Marisol.Vega-The.Quiet.Orchard.M4B-Gardner.2010.NMR").IsConfirmed);
    }

    [Fact]
    public void FullNarratorPrefixRequiresAnAlreadyMatchedAuthorAndStoredReader()
    {
        var book = Book(narrator: "Grover Gardner");
        Assert.True(Match(book, "Marisol.Vega,.Grover.Gardner-The.Quiet.Orchard").IsConfirmed);
        Assert.False(Match(book, "Other.Writer,.Grover.Gardner-The.Quiet.Orchard").IsConfirmed);
        Assert.False(Match(book, "Marisol.Vega,.Other.Reader-The.Quiet.Orchard").IsConfirmed);
        Assert.False(Match(book, "Marisol.Vega,.Gardner-The.Quiet.Orchard").IsConfirmed);
    }

    [Theory]
    [InlineData("64kbps", "technical.bitrate")]
    [InlineData("64 kb/s", "technical.bitrate")]
    [InlineData("64k", "technical.bitrate")]
    [InlineData("64kb", "technical.legacy_bits")]
    [InlineData("64 KB", "technical.size")]
    [InlineData("1.02 GB", "technical.size")]
    [InlineData("2010", "technical.year")]
    public void TechnicalUnitDiagnosticsPreserveTheirMeaningAndOffsets(string field, string rule)
    {
        var release = $"Marisol Vega - The Quiet Orchard [{field}]";
        var parsed = ReleaseNameInterpreter.Interpret(release, BookMatchContext.From(Book(), catalog: TestAnnotationCatalog.Current), []);
        Assert.True(parsed.Complete);
        var span = Assert.Single(parsed.Spans, s => s.RuleId == rule);
        Assert.Equal(field, release.Substring(span.Start, span.Length));
    }

    [Fact]
    public void LanguageAndEditionCompatibilitySurviveNewLabels()
    {
        var book = Book();
        var release = "Marisol Vega - The Quiet Orchard-AUDiOBOOK-WEB-SE-2021-CRAViNGS iNT [EarReaders]";
        var assessment = Match(book, release);
        Assert.True(assessment.IsConfirmed);
        Assert.Contains(assessment.Languages, l => l.Language == "Swedish");
        var adapted = Book(); adapted.Subtitle = "Adapted for Modern Readers";
        var candidate = new SearchResult { Title = "Marisol Vega - The Quiet Orchard [GraphicAudio]", IdentityEvidence = new() { ReleaseName = "Marisol Vega - The Quiet Orchard [GraphicAudio]" } };
        var context = BookMatchContext.From(adapted, catalog: TestAnnotationCatalog.Current);
        Assert.Contains("edition_evidence_required", AutomaticCompatibilityPolicy.Evaluate(context, candidate,
            new BookIdentityMatcher().Evaluate(context, BookIdentityMatcher.Snapshot(candidate))).Reasons);
    }
}
