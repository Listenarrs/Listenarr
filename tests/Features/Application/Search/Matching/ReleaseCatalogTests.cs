using Listenarr.Tests.Common;
using System.Text;
using Listenarr.Application.Search.Matching;
using Listenarr.Infrastructure.Search.Matching;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "ReleaseCatalogTests")]
[Trait("Category", "Application")]
public sealed class ReleaseCatalogTests : BaseTests
{
    [Fact]
    public void ShippedCatalogIsValidAndReferenceSnapshotsUseItsHash()
    {
        var catalog = TestAnnotationCatalog.Current;
        Assert.True(catalog.IsAvailable);
        var book = new Audiobook { Title = "The Book", Authors = ["Mira Chen"] };
        Assert.NotEqual(BookMatchContext.From(book).Fingerprint, BookMatchContext.From(book, catalog: catalog).Fingerprint);
        var result = new BookIdentityMatcher().Evaluate(BookMatchContext.From(book), new() { ReleaseName = "Mira Chen - The Book" });
        Assert.False(result.IsConfirmed);
        Assert.Equal("annotation_catalog_unavailable", result.Reason);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"typo\":[]}")]
    public void InvalidResourcesFailClosed(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.False(ReleaseAnnotationCatalogLoader.Load(stream).IsAvailable);
    }

    [Fact]
    public void OversizeResourcesFailClosed()
    {
        using var stream = new MemoryStream(new byte[ReleaseAnnotationCatalogLoader.MaximumBytes + 1]);
        Assert.False(ReleaseAnnotationCatalogLoader.Load(stream).IsAvailable);
    }

    [Fact]
    public void PolicyDataIsCopiedAndOrderIndependent()
    {
        var labels = new[] { new AnnotationLabel("group.one", "One", AnnotationKind.Group, AnnotationPlacement.Suffix),
            new AnnotationLabel("group.two", "Two", AnnotationKind.Group, AnnotationPlacement.Suffix) };
        var first = new ReleaseAnnotationCatalog(1, labels, [], [], [], [], [], []);
        var second = new ReleaseAnnotationCatalog(1, labels.Reverse().ToArray(), [], [], [], [], [], []);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        labels[0] = new("group.changed", "Changed", AnnotationKind.Group, AnnotationPlacement.Suffix);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.Fingerprint, new ReleaseAnnotationCatalog(1, labels, [], [], [], [], [], []).Fingerprint);
        Assert.Throws<ArgumentException>(() => new ReleaseAnnotationCatalog(1, [labels[1], labels[1]], [], [], [], [], [], []));
    }
}
