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

    [Fact]
    [Trait("Scenario", "Every path that grabs a release consults the blocklist")]
    public void EveryGrabPath_ConsultsTheBlocklist()
    {
        // The test above only covers SearchAndDownloadAsync. That is one of two paths that actually
        // grab, and the blocklist was wired into it alone: AutomaticSearchService scores results and
        // calls StartDownloadAsync directly, so a release could fail, be blocked, and be grabbed
        // again by the next automatic pass a minute later. A live install looped for hours that way
        // while every unit test here passed.
        //
        // This asserts the invariant rather than one instance of it: a production file that starts a
        // download has to consult the blocklist somewhere in the same file.
        var root = TestUtils.FindRepositoryRoot();
        var projects = new[] { "listenarr.application", "listenarr.infrastructure", "listenarr.api" };

        var offenders = projects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Join(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(file => new { File = file, Text = File.ReadAllText(file) })
            // The call, not the declaration: a file that only defines StartDownloadAsync is not a
            // grab path.
            .Where(entry => entry.Text.Contains("await downloadService.StartDownloadAsync(", StringComparison.Ordinal)
                         || entry.Text.Contains("await _downloadService.StartDownloadAsync(", StringComparison.Ordinal))
            .Where(entry => !entry.Text.Contains("BlockedReleaseFilter", StringComparison.Ordinal)
                         && !entry.Text.Contains("IBlocklistService", StringComparison.Ordinal))
            .Select(entry => Path.GetRelativePath(root, entry.File))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These start a download without consulting the blocklist:" + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }
}
