using Listenarr.Domain.Downloads;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Downloads;

[Trait("Name", "ReleaseIdentityTests")]
[Trait("Category", "Domain")]
public sealed class ReleaseIdentityTests : BaseTests
{
    [Fact]
    public void For_PrefersTheInfoHash_SoOneReleaseIsOneIdentityAcrossIndexers()
    {
        var fromOneIndexer = ReleaseIdentity.For("ABCDEF", "https://a.example.com/x.torrent");
        var fromAnother = ReleaseIdentity.For("abcdef", "https://b.example.com/y.torrent");

        Assert.Equal(fromOneIndexer, fromAnother);
        Assert.StartsWith("btih:", fromOneIndexer);
    }

    [Fact]
    public void For_FallsBackToTheReleaseUrl_WhichIsWhatUsenetHas()
    {
        var first = ReleaseIdentity.For(null, "https://indexer.example.com/nzb/123");
        var same = ReleaseIdentity.For(null, "  HTTPS://Indexer.Example.com/nzb/123  ");
        var other = ReleaseIdentity.For(null, "https://indexer.example.com/nzb/124");

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
        Assert.StartsWith("url:", first);
    }

    [Fact]
    public void For_WithNothingIdentifying_ReturnsNull()
    {
        // Better to record nothing than to invent an identity that would match the wrong
        // release later.
        Assert.Null(ReleaseIdentity.For(null, null));
        Assert.Null(ReleaseIdentity.For("   ", "  "));
    }
}
