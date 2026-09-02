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
        var identifier = ReleaseIdentity.For("ABCDEF1234567890ABCDEF1234567890ABCDEF12", null, null, null)!;
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
            .ReturnsAsync([ReleaseIdentity.For("0000000000000000000000000000000000000000", null, null, null)!]);

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [Scored(Magnet)], NullLogger.Instance);

        Assert.Single(kept);
    }

    [Fact]
    public async Task ExcludeAsync_MatchesATorrentByInfoHashEvenWhenTheUrlChanged()
    {
        // Indexers hand back different URLs for the same torrent. Keying on the URL alone
        // would let the same release back in on the next search.
        var identifier = ReleaseIdentity.For("ABCDEF1234567890ABCDEF1234567890ABCDEF12", null, null, null)!;
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

    [Fact]
    public async Task ExcludeAsync_DropsAUsenetReleaseWhoseDownloadLinkChangedSinceItWasBlocked()
    {
        // The defect this replaced, end to end. A Usenet indexer mints a new download link with a
        // new token on every grab, so the release blocked after one failure arrived at the next
        // search with a different URL. Keying on that URL meant the write side and the read side
        // computed different identities and the filter never matched, which on a live install
        // produced several hundred grabs of one dead post for a single book over half a day.
        const string title = "Some Book Unabridged";
        const long size = 734003200;

        var blockedWhenItFailed = ReleaseIdentity.For(
            null, "https://indexer.example.com/getnzb?id=abc&apikey=TOKEN1", title, size)!;

        var blocklist = new Mock<IBlocklistService>();
        blocklist.Setup(service => service.GetBlockedIdentifiersAsync(7))
            .ReturnsAsync([blockedWhenItFailed]);

        var comesBackWithANewLink = new QualityScore
        {
            TotalScore = 90,
            SearchResult = new SearchResult
            {
                Title = title,
                Size = size,
                NzbUrl = "https://indexer.example.com/getnzb?id=abc&apikey=TOKEN2"
            }
        };

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [comesBackWithANewLink], NullLogger.Instance);

        Assert.Empty(kept);
    }

    [Fact]
    public async Task ExcludeAsync_KeepsAUsenetReleaseOfADifferentSize()
    {
        // The other half: blocking one release must not ban every release sharing its title.
        const string title = "Some Book Unabridged";

        var blocklist = new Mock<IBlocklistService>();
        blocklist.Setup(service => service.GetBlockedIdentifiersAsync(7))
            .ReturnsAsync([ReleaseIdentity.For(null, "https://indexer.example.com/a", title, 734003200)!]);

        var different = new QualityScore
        {
            TotalScore = 90,
            SearchResult = new SearchResult
            {
                Title = title,
                Size = 999999999,
                NzbUrl = "https://indexer.example.com/b"
            }
        };

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [different], NullLogger.Instance);

        Assert.Single(kept);
    }

    private static QualityScore Scored(string magnet) => new()
    {
        TotalScore = 90,
        SearchResult = new SearchResult { Title = "Book", MagnetLink = magnet }
    };
}
