using Listenarr.Application.Downloads.Contracts;
using Listenarr.Application.Downloads.Submission;
using Listenarr.Domain.Audiobooks;
using Listenarr.Domain.Downloads;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Downloads.Submission;

[Trait("Name", "BlockedReleaseFilterTests")]
[Trait("Category", "Application")]
public sealed class BlockedReleaseFilterTests : BaseTests
{
    private const string Magnet = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12&dn=book";

    [Fact]
    public async Task ExcludeAsync_DropsAReleaseAlreadyBlockedForThisBook()
    {
        // The loop in #838: the only result is the one that already failed, so a search
        // that cannot tell must grab it again.
        var identifier = ReleaseIdentity.For("ABCDEF1234567890ABCDEF1234567890ABCDEF12", null)!;
        var blocklist = new Mock<IBlocklistService>();
        blocklist.Setup(service => service.GetBlockedIdentifiersAsync(7))
            .ReturnsAsync([identifier]);

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [Scored(Magnet)], NullLogger.Instance);

        Assert.Empty(kept);
    }

    [Fact]
    public async Task ExcludeAsync_KeepsADifferentReleaseOfTheSameBook()
    {
        // Blocking one release must not ban the title. If this ever fails, a single bad
        // release takes the book out of circulation permanently.
        var blocklist = new Mock<IBlocklistService>();
        blocklist.Setup(service => service.GetBlockedIdentifiersAsync(7))
            .ReturnsAsync([ReleaseIdentity.For("0000000000000000000000000000000000000000", null)!]);

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [Scored(Magnet)], NullLogger.Instance);

        Assert.Single(kept);
    }

    [Fact]
    public async Task ExcludeAsync_MatchesATorrentByInfoHashEvenWhenTheUrlChanged()
    {
        // Indexers hand back different URLs for the same torrent. Keying on the URL alone
        // would let the same release back in on the next search.
        var identifier = ReleaseIdentity.For("ABCDEF1234567890ABCDEF1234567890ABCDEF12", null)!;
        var blocklist = new Mock<IBlocklistService>();
        blocklist.Setup(service => service.GetBlockedIdentifiersAsync(7))
            .ReturnsAsync([identifier]);

        var moved = Scored(Magnet);
        moved.SearchResult.TorrentUrl = "https://indexer.example.com/a-completely-different-path.torrent";

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [moved], NullLogger.Instance);

        Assert.Empty(kept);
    }

    [Fact]
    public async Task ExcludeAsync_WithNothingBlocked_DoesNotQueryPerResult()
    {
        var blocklist = new Mock<IBlocklistService>();
        blocklist.Setup(service => service.GetBlockedIdentifiersAsync(7))
            .ReturnsAsync([]);

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [Scored(Magnet), Scored(Magnet)], NullLogger.Instance);

        Assert.Equal(2, kept.Count);
        blocklist.Verify(service => service.GetBlockedIdentifiersAsync(7), Times.Once);
    }

    private static QualityScore Scored(string magnet) => new()
    {
        TotalScore = 90,
        SearchResult = new SearchResult { Title = "Book", MagnetLink = magnet }
    };
}
