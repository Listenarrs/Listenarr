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
        var fromOneIndexer = ReleaseIdentity.For("ABCDEF", "https://a.example.com/x.torrent", "A Book", 100);
        var fromAnother = ReleaseIdentity.For("abcdef", "https://b.example.com/y.torrent", "A Book", 100);

        Assert.Equal(fromOneIndexer, fromAnother);
        Assert.StartsWith("btih:", fromOneIndexer);
    }

    [Fact]
    public void For_UsenetReleaseGrabbedTwice_HasTheSameIdentityBothTimes()
    {
        // The failure this replaced. A Usenet release URL is a per-fetch download link: the
        // indexer mints a new token every grab, so the two URLs below are the same release. On a
        // live install one dead post was grabbed several hundred times over half a day, identical
        // title and identical size every time, and the old URL hash produced a new blocklist row
        // on every failure and therefore never matched.
        var firstGrab = ReleaseIdentity.For(
            null, "https://indexer.example.com/getnzb?id=abc&apikey=TOKEN1", "Some Book Unabridged", 734003200);
        var secondGrab = ReleaseIdentity.For(
            null, "https://indexer.example.com/getnzb?id=abc&apikey=TOKEN2", "Some Book Unabridged", 734003200);

        Assert.Equal(firstGrab, secondGrab);
        Assert.StartsWith("name:", firstGrab);
    }

    [Fact]
    public void For_SameTitleDifferentSize_IsADifferentRelease()
    {
        // Size is in the key so that a shared title does not collapse two genuinely different
        // releases onto one blocklist entry.
        var small = ReleaseIdentity.For(null, "https://indexer.example.com/a", "Some Book Unabridged", 734003200);
        var large = ReleaseIdentity.For(null, "https://indexer.example.com/b", "Some Book Unabridged", 999999999);

        Assert.NotEqual(small, large);
    }

    [Fact]
    public void For_TitleDifferingOnlyByCaseAndSpacing_IsTheSameRelease()
    {
        var one = ReleaseIdentity.For(null, "https://indexer.example.com/a", "Some  Book   Unabridged", 100);
        var two = ReleaseIdentity.For(null, "https://indexer.example.com/b", "  some book unabridged  ", 100);

        Assert.Equal(one, two);
    }

    [Fact]
    public void For_WithNoTitle_StillFallsBackToTheUrl()
    {
        var first = ReleaseIdentity.For(null, "https://indexer.example.com/nzb/123", null, null);
        var same = ReleaseIdentity.For(null, "  HTTPS://Indexer.Example.com/nzb/123  ", "   ", null);
        var other = ReleaseIdentity.For(null, "https://indexer.example.com/nzb/124", null, null);

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
        Assert.StartsWith("url:", first);
    }

    [Fact]
    public void For_WithNothingIdentifying_ReturnsNull()
    {
        // Better to record nothing than to invent an identity that would match the wrong
        // release later.
        Assert.Null(ReleaseIdentity.For(null, null, null, null));
        Assert.Null(ReleaseIdentity.For("   ", "  ", "  ", null));
    }
}
