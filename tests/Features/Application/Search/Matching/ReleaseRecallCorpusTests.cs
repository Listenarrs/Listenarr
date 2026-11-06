using Listenarr.Tests.Common;
using System.Text.Json;
using Listenarr.Application.Search.Matching;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseRecallCorpusTests")]
[Trait("Category", "Application")]
public sealed class ReleaseRecallCorpusTests : BaseTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AutomaticSearch", name);
    public static IEnumerable<object[]> ReviewedChanges()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fixture("recall-adjudications-v6.json")));
        foreach (var row in doc.RootElement.GetProperty("cases").EnumerateArray())
            yield return [row.GetProperty("index").GetInt32(), row.GetProperty("context").GetString()!, row.GetProperty("expectedConfirmed").GetBoolean()];
    }

    private static Audiobook Reference(JsonElement book, JsonElement? facts = null)
    {
        var result = new Audiobook
        {
            Title = book.GetProperty("title").GetString(),
            Subtitle = book.GetProperty("subtitle").GetString(),
            Authors = book.GetProperty("authors").EnumerateArray().Select(a => a.GetString()!).ToList()
        };
        if (facts is { } f)
        {
            result.Series = f.GetProperty("series").GetString(); result.SeriesNumber = f.GetProperty("seriesNumber").GetString();
            result.Narrators = f.GetProperty("narrators").EnumerateArray().Select(n => n.GetString()!).ToList();
            result.SeriesMemberships = f.GetProperty("memberships").EnumerateArray().Select(m => new AudiobookSeriesMembership
            { SeriesName = m.GetProperty("SeriesName").GetString(), SeriesNumber = m.GetProperty("SeriesNumber").GetString() }).ToList();
        }
        return result;
    }

    [Theory]
    [MemberData(nameof(ReviewedChanges))]
    public void EveryChangedExpectationUsesOnlyItsExportedReferenceFacts(int index, string mode, bool expected)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Fixture("historical-releases.json")));
        using var supplement = JsonDocument.Parse(File.ReadAllText(Fixture("historical-reference-facts.json")));
        var row = corpus.RootElement.GetProperty("cases")[index];
        JsonElement? facts = mode == "full" ? supplement.RootElement.GetProperty("cases").EnumerateArray()
            .Single(f => f.GetProperty("index").GetInt32() == index) : null;
        var book = Reference(row.GetProperty("book"), facts);
        var context = BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current);
        var release = row.GetProperty("releaseName").GetString()!;
        var result = new BookIdentityMatcher().Evaluate(context, new() { ReleaseName = release });
        Assert.Equal(expected, result.IsConfirmed);
        var spans = ReleaseNameInterpreter.Interpret(release, context, []).Spans;
        Assert.Contains(spans, s => s.Role == ReleaseSpanRole.PrimaryTitle);
        Assert.Contains(spans, s => s.Role == ReleaseSpanRole.AuthorCredit);
        Assert.All(spans, s => Assert.InRange(s.Start + s.Length, 1, release.Length));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(28)]
    [InlineData(35)]
    [InlineData(41)]
    [InlineData(46)]
    [InlineData(101)]
    [InlineData(115)]
    [InlineData(133)]
    [InlineData(163)]
    [InlineData(158)]
    [InlineData(430)]
    [InlineData(174)]
    [InlineData(292)]
    [InlineData(367)]
    [InlineData(378)]
    [InlineData(159)]
    [InlineData(306)]
    [InlineData(233)]
    [InlineData(3)]
    public void OriginalWrongBookControlsRemainDeferred(int index)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Fixture("historical-releases.json")));
        using var supplement = JsonDocument.Parse(File.ReadAllText(Fixture("historical-reference-facts.json")));
        var row = corpus.RootElement.GetProperty("cases")[index];
        var facts = supplement.RootElement.GetProperty("cases").EnumerateArray().Where(f => f.GetProperty("index").GetInt32() == index).ToArray();
        var book = Reference(row.GetProperty("book"), facts.Length == 0 ? null : facts[0]);
        Assert.False(new BookIdentityMatcher().Evaluate(BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current),
            new() { ReleaseName = row.GetProperty("releaseName").GetString()! }).IsConfirmed);
    }

    [Fact]
    public void RealProwlarrHeldOutPairsPreserveTargetsAndConflictingCredits()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fixture("prowlarr-recall-pairs.json")));
        foreach (var row in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var facts = row.GetProperty("facts");
            var book = Reference(row.GetProperty("book"), facts.ValueKind == JsonValueKind.Null ? null : facts);
            var result = new BookIdentityMatcher().Evaluate(BookMatchContext.From(book, catalog: TestAnnotationCatalog.Current),
                new() { ReleaseName = row.GetProperty("releaseName").GetString()! });
            Assert.True(result.IsConfirmed == row.GetProperty("expectedConfirmed").GetBoolean(), row.GetProperty("releaseName").GetString() + ": " + result.Reason);
        }
    }
}
