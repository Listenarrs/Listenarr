using System.Security.Cryptography;
using System.Text.Json;
using Listenarr.Application.Search.Matching;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "HistoricalReleaseCorpusTests")]
[Trait("Category", "Application")]
public sealed class HistoricalReleaseCorpusTests : BaseTests
{
    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AutomaticSearch", file);

    public static IEnumerable<object[]> Adjudications()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Fixture("historical-releases.json")));
        using var judgments = JsonDocument.Parse(File.ReadAllText(Fixture("historical-adjudications.json")));
        foreach (var judgment in judgments.RootElement.GetProperty("cases").EnumerateArray())
        {
            var index = judgment.GetProperty("index").GetInt32();
            yield return [index, corpus.RootElement.GetProperty("cases")[index].GetRawText(),
                judgment.GetProperty("expectedConfirmed").GetBoolean()];
        }
    }

    [Theory]
    [MemberData(nameof(Adjudications))]
    public void IndependentlyAdjudicatedHistoricalIdentityIsPreserved(int index, string json, bool expected)
    {
        using var doc = JsonDocument.Parse(json);
        var c = doc.RootElement;
        var reference = c.GetProperty("book");
        var book = new Audiobook
        {
            Title = reference.GetProperty("title").GetString(),
            Subtitle = reference.GetProperty("subtitle").GetString(),
            Authors = reference.GetProperty("authors").EnumerateArray().Select(a => a.GetString()!).ToList()
        };
        var evidence = new CandidateIdentityEvidence
        {
            ReleaseName = c.GetProperty("releaseName").GetString()!,
            Authors = [new(c.GetProperty("inferredArtist").GetString()!, IdentityEvidenceOrigin.ReleaseNameDerived)]
        };
        var result = new BookIdentityMatcher().Evaluate(BookMatchContext.From(book), evidence);
        Assert.True(result.IsConfirmed == expected, index + ": " + result);
    }

    [Fact]
    public void HistoricalObservationsAreFrozenAndNotUsedAsCorrectnessLabels()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Fixture("historical-adjudications.json")));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Fixture("historical-releases.json"))));
        Assert.Equal(expected.RootElement.GetProperty("corpusSha256").GetString(), hash.ToLowerInvariant());
        using var corpus = JsonDocument.Parse(File.ReadAllText(Fixture("historical-releases.json")));
        Assert.Equal(498, corpus.RootElement.GetProperty("cases").GetArrayLength());
        using var partitions = JsonDocument.Parse(File.ReadAllText(Fixture("historical-partitions.json")));
        var development = partitions.RootElement.GetProperty("development").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var holdout = partitions.RootElement.GetProperty("holdout").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Empty(development.Intersect(holdout));
        Assert.Equal(Enumerable.Range(0, 498), development.Concat(holdout).Order());
    }
}
