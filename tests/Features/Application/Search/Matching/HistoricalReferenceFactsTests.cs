using System.Security.Cryptography;
using System.Text.Json;
using Listenarr.Application.Search.Matching;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "HistoricalReferenceFactsTests")]
[Trait("Category", "Application")]
public sealed class HistoricalReferenceFactsTests : BaseTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AutomaticSearch", name);

    [Fact]
    public void ReferenceSupplementIsFrozenAndDoesNotInventMissingCases()
    {
        var path = Fixture("historical-reference-facts.json");
        Assert.Equal("BD9F2414E8E9B527F5312FC109350393A81C1F80ABBADD065B8AF49F6CCFBCED",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        using var facts = JsonDocument.Parse(File.ReadAllBytes(path));
        var cases = facts.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(423, cases.Length);
        Assert.Equal(423, cases.Select(c => c.GetProperty("index").GetInt32()).Distinct().Count());
        Assert.All(cases, c => Assert.InRange(c.GetProperty("index").GetInt32(), 0, 497));
        // The author clarified that 27 is the correct pairing; the wrong-book comment meant 28.
        Assert.True(cases.Single(c => c.GetProperty("index").GetInt32() == 27)
            .GetProperty("fullMetadataVerdict").GetProperty("IsConfirmed").GetBoolean());
    }

    [Theory]
    [InlineData(27, true)]
    [InlineData(31, true)]
    [InlineData(39, false)] // Unknown coauthor is still unexplained even when size/narrator grammar is supported.
    [InlineData(40, true)]
    [InlineData(50, true)]
    [InlineData(51, true)]
    [InlineData(58, true)]
    [InlineData(59, true)]
    [InlineData(69, false)] // Release says Heroes of History; stored facts say Christian Heroes: Then & Now.
    [InlineData(81, true)]
    [InlineData(98, true)]
    [InlineData(121, false)] // Author words inside the protected title cannot become a separate credit.
    [InlineData(123, false)]
    [InlineData(129, false)]
    [InlineData(130, true)]
    [InlineData(131, false)]
    [InlineData(132, true)]
    [InlineData(136, true)]
    [InlineData(144, true)]
    public void ReportedPatternsHaveExplicitPolicyExpectations(int index, bool expected)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(Fixture("historical-releases.json")));
        using var facts = JsonDocument.Parse(File.ReadAllBytes(Fixture("historical-reference-facts.json")));
        var c = corpus.RootElement.GetProperty("cases")[index];
        var b = c.GetProperty("book");
        var f = facts.RootElement.GetProperty("cases").EnumerateArray().Single(f => f.GetProperty("index").GetInt32() == index);
        var book = new Audiobook
        {
            Title = b.GetProperty("title").GetString(),
            Subtitle = b.GetProperty("subtitle").GetString(),
            Authors = b.GetProperty("authors").EnumerateArray().Select(a => a.GetString()!).ToList(),
            Series = f.GetProperty("series").GetString(),
            SeriesNumber = f.GetProperty("seriesNumber").GetString(),
            Narrators = f.GetProperty("narrators").EnumerateArray().Select(a => a.GetString()!).ToList(),
            SeriesMemberships = f.GetProperty("memberships").EnumerateArray().Select(m => new AudiobookSeriesMembership
            {
                SeriesName = m.GetProperty("SeriesName").GetString(),
                SeriesNumber = m.GetProperty("SeriesNumber").GetString()
            }).ToList()
        };
        var result = new BookIdentityMatcher().Evaluate(BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current), new()
        {
            ReleaseName = c.GetProperty("releaseName").GetString()!,
            Authors = [new(c.GetProperty("inferredArtist").GetString()!, IdentityEvidenceOrigin.ReleaseNameDerived)]
        });
        Assert.True(result.IsConfirmed == expected, index + ": " + result);
    }

    [Theory]
    [InlineData("George Anastasia", "1666104000", "George Anastasia", true, true)]
    [InlineData("George Anastasia", "1666104000", "George Anastasia", false, true)]
    [InlineData("W. E. B. Griffin", "B074K62ZS2", "George Anastasia", true, false)]
    [InlineData("W. E. B. Griffin", "B074K62ZS2", "George Anastasia", false, false)]
    [InlineData("W. E. B. Griffin", "B074K62ZS2", "W. E. B. Griffin", true, true)]
    [InlineData("George Anastasia", "1666104000", "W. E. B. Griffin", true, false)]
    public void ClarifiedBloodAndHonorPairingsRequireTheCorrectCreditedAuthor(string author, string asin,
        string releaseAuthor, bool series, bool expected)
    {
        var book = new Audiobook
        {
            Title = "Blood and Honor",
            Authors = [author],
            Asin = asin,
            Subtitle = author == "George Anastasia" ? "Inside the Scarfo Mob - The Mafia's Most Violent Family" : null,
            Narrators = author == "George Anastasia" ? ["Joe Barrett"] : [],
            Series = series ? "Honor Bound" : null,
            SeriesNumber = series ? "2" : null,
            SeriesMemberships = series ? [new() { SeriesName = "Honor Bound", SeriesNumber = "2", IsPrimary = true }] : []
        };
        var release = releaseAuthor + " - Blood and Honor (2021)";
        var result = new BookIdentityMatcher().Evaluate(BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current), new()
        {
            ReleaseName = release,
            Authors = [new(releaseAuthor, IdentityEvidenceOrigin.ReleaseNameDerived)]
        });
        Assert.Equal(expected, result.IsConfirmed);
    }
}
