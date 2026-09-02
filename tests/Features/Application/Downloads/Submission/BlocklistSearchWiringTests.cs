using Listenarr.Application.Downloads.Contracts;
using Listenarr.Application.Search.Contracts;
using Listenarr.Domain.Downloads;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Submission;

/// <summary>
/// The unit tests for BlockedReleaseFilter pass whether or not DownloadService calls it.
/// This one fails if the call is removed, which is the point of having it.
/// </summary>
[Trait("Name", "BlocklistSearchWiringTests")]
[Trait("Category", "Application")]
public sealed class BlocklistSearchWiringTests : BaseTests
{
    private const string InfoHash = "ABCDEF1234567890ABCDEF1234567890ABCDEF12";

    [Fact]
    public async Task SearchAndDownloadAsync_WhenTheOnlyCandidateIsBlocked_ReportsNoAcceptableResults()
    {
        var search = new Mock<ISearchService>();
        search.Setup(service => service.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<SearchSortBy>(),
                It.IsAny<SearchSortDirection>(),
                It.IsAny<bool>()))
            .ReturnsAsync([
                new SearchResult
                {
                    Title = "The Only Listing",
                    MagnetLink = $"magnet:?xt=urn:btih:{InfoHash}&dn=book",
                    Size = 800_000_000,
                    Seeders = 20
                }
            ]);
        _services.AddSingleton(search.Object);
        Init();

        await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
            .WithHost("localhost")
            .WithPort(8080)
            .WithUsername("admin")
            .WithPassword("admin")
            .WithType("qbittorrent")
            .Build());

        var qualityProfile = await _qualityProfileRepository.AddAsync(
            new QualityProfileBuilder().Build());
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Blocked Book")
                .WithQualityProfile(qualityProfile)
                .Build());

        var downloadService = _provider.GetRequiredService<IDownloadService>();

        // Establish that this candidate is otherwise selectable. Without this the assertion
        // below passes for whatever reason the search already had to fail, which is how a
        // filter that is never called still looks green.
        var unblocked = await downloadService.SearchAndDownloadAsync(audiobook.Id);
        Assert.True(
            unblocked.Success,
            $"precondition: the candidate must be grabbable when nothing is blocked, got '{unblocked.Message}'");

        foreach (var created in (await _downloadRepository.GetAllAsync())
                 .Where(download => download.AudiobookId == audiobook.Id))
        {
            await _downloadRepository.RemoveAsync(created.Id);
        }

        var blocklist = _provider.GetRequiredService<IBlocklistService>();
        await blocklist.BlockAsync(
            audiobook.Id,
            ReleaseIdentity.For(InfoHash, null, null, null)!,
            "The Only Listing",
            800_000_000,
            "simulated earlier failure");

        var result = await downloadService.SearchAndDownloadAsync(audiobook.Id);

        Assert.False(result.Success);
        Assert.DoesNotContain(
            await _downloadRepository.GetAllAsync(),
            download => download.AudiobookId == audiobook.Id);
    }
}
