using Listenarr.Application.Downloads.Contracts;
using Listenarr.Application.Downloads.Submission;
using Listenarr.Domain.Downloads;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Downloads.Submission;

/// <summary>
/// The blocklist is only useful if the identity written when a release fails is the identity
/// computed when the next search looks at the same release. Three defects in a row were the same
/// mistake: two sides derived that key independently and drifted apart. These pin the round trip
/// rather than either side of it.
/// </summary>
[Trait("Name", "ReleaseIdentityRoundTripTests")]
[Trait("Category", "Application")]
public sealed class ReleaseIdentityRoundTripTests : BaseTests
{
    private const string Title = "Some Book Unabridged";
    private const long AdvertisedSize = 734_003_200;

    // What the download client reports back once the item is in its queue. QueueItemConverter
    // writes this straight over Download.TotalSize, and DirectDownloadProcessor does the same
    // thing three more times as bytes arrive.
    private const long SizeTheClientReportsLater = 812_345_678;

    [Fact]
    [Trait("Scenario", "The identity survives the download client rewriting the size")]
    public async Task AReleaseBlockedAfterFailure_IsExcludedFromTheNextSearch_EvenAfterTheClientRewroteTheSize()
    {
        // The live failure this exists for: one correctly formatted blocklist row written after the
        // first failure, then more than a hundred further grabs of the identical release over the
        // next eleven hours. The row was keyed on Download.TotalSize, which the queue poller had
        // already overwritten with the client's own number, while the search side kept keying on
        // the size the indexer advertised.
        var grabbed = UsenetResult("https://indexer.example.com/getnzb?id=abc&apikey=TOKEN1");

        var download = GrabbedDownloadFor(grabbed);
        download.TotalSize = SizeTheClientReportsLater;

        // What DownloadMonitorService.OnDownloadFailed blocks the release under.
        var blockedUnder = ReleaseIdentity.ForGrabbed(download);
        Assert.NotNull(blockedUnder);

        var blocklist = new Mock<IBlocklistService>();
        blocklist.Setup(service => service.GetBlockedIdentifiersAsync(7))
            .ReturnsAsync([blockedUnder]);

        // The same dead post comes back on the next pass, with a freshly minted download link.
        var comesBackAgain = new QualityScore
        {
            TotalScore = 90,
            SearchResult = UsenetResult("https://indexer.example.com/getnzb?id=abc&apikey=TOKEN2")
        };

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [comesBackAgain], NullLogger.Instance);

        Assert.Empty(kept);
    }

    [Fact]
    [Trait("Scenario", "The identity is worked out once, at grab time")]
    public void TheIdentityStampedAtGrabTime_IsTheOneTheSearchSideComputes()
    {
        // The narrower statement of the same thing, so a failure here says which half moved.
        var grabbed = UsenetResult("https://indexer.example.com/getnzb?id=abc&apikey=TOKEN1");
        var download = GrabbedDownloadFor(grabbed);
        download.TotalSize = SizeTheClientReportsLater;

        Assert.Equal(ReleaseIdentity.For(grabbed), ReleaseIdentity.ForGrabbed(download));
    }

    [Fact]
    [Trait("Scenario", "The grab writes the identity down")]
    public void AGrabbedDownload_CarriesTheGrabTimeIdentityInItsMetadata()
    {
        // ExpectedFileSize would let the fallback reach the same answer for this path, which is
        // deliberate belt and braces. The stamp is what makes the answer a value that was written
        // once rather than one derived a second time from a settable property, so it gets its own
        // assertion instead of riding on the round trip above.
        var grabbed = UsenetResult("https://indexer.example.com/getnzb?id=abc&apikey=TOKEN1");
        var download = GrabbedDownloadFor(grabbed);

        Assert.Equal(
            ReleaseIdentity.For(grabbed),
            download.GetMetadataString(ReleaseIdentity.MetadataKey));
    }

    [Fact]
    [Trait("Scenario", "A download grabbed before the stamp existed")]
    public async Task ADownloadWithNoStampedIdentity_IsStillBlockedUnderTheSearchSideIdentity()
    {
        // Rows already in the database when this ships have no stamp. The fallback has to be no
        // worse than what it replaces, and the size it falls back to is the advertised one, since
        // TotalSize is the field that moves.
        var grabbed = UsenetResult("https://indexer.example.com/getnzb?id=abc&apikey=TOKEN1");

        var legacy = new Download
        {
            Id = "legacy-download",
            AudiobookId = 7,
            Title = Title,
            OriginalUrl = "https://indexer.example.com/getnzb?id=abc&apikey=TOKEN1",
            ExpectedFileSize = AdvertisedSize,
            TotalSize = SizeTheClientReportsLater,
            Metadata = []
        };
        Assert.Null(legacy.GetMetadataString(ReleaseIdentity.MetadataKey));

        var blockedUnder = ReleaseIdentity.ForGrabbed(legacy);
        Assert.NotNull(blockedUnder);

        var blocklist = new Mock<IBlocklistService>();
        blocklist.Setup(service => service.GetBlockedIdentifiersAsync(7))
            .ReturnsAsync([blockedUnder]);

        var comesBackAgain = new QualityScore
        {
            TotalScore = 90,
            SearchResult = UsenetResult("https://indexer.example.com/getnzb?id=abc&apikey=TOKEN2")
        };

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            blocklist.Object, 7, [comesBackAgain], NullLogger.Instance);

        Assert.Empty(kept);
    }

    [Fact]
    [Trait("Scenario", "Direct downloads carry the stamp too")]
    public async Task ADirectDownloadReservation_CarriesTheGrabTimeIdentity()
    {
        // Direct downloads do not go through DownloadRecordFactory, so they need the stamp put on
        // separately or they are unblockable in exactly the same way.
        Download? persisted = null;
        var repository = new Mock<IDownloadRepository>();
        repository.Setup(repo => repo.AddAsync(It.IsAny<Download>()))
            .Callback<Download>(download => persisted = download)
            .ReturnsAsync((Download download) => download);

        var submission = new PreparedDirectDownloadSubmission(
            Title,
            "Author",
            "Album",
            "Source",
            "M4B",
            "en",
            AdvertisedSize,
            "https://archive.example.com/book.m4b",
            [new PreparedDirectDownloadArtifact(
                new Uri("https://archive.example.com/book.m4b"),
                "book.m4b",
                AdvertisedSize,
                DirectDownloadArtifactPackaging.File)],
            "InternetArchive");

        var workflow = new DirectDownloadWorkflow(
            repository.Object,
            NullLogger<DirectDownloadWorkflow>.Instance);

        await workflow.CreateTrackedDownloadAsync(submission, audiobookId: 7, releaseIdentifier: "name:deadbeef");

        Assert.NotNull(persisted);
        Assert.Equal("name:deadbeef", persisted.GetMetadataString(ReleaseIdentity.MetadataKey));
    }

    [Fact]
    [Trait("Scenario", "Nothing downstream of the grab derives its own identity")]
    public void OnlyTheGrabAndTheSearchFilter_DeriveAReleaseIdentity()
    {
        // The three tests above pin this instance of the defect. This one pins its shape. An
        // identity derived independently on two sides from mutable state diverges again the next
        // time somebody edits a field, so the failure path and everything else downstream of the
        // grab must read the stamped value rather than work one out.
        //
        // Reverting DownloadMonitorService to deriving its own fails this and names the file.
        var root = TestUtils.FindRepositoryRoot();
        var projects = new[]
        {
            "listenarr.domain",
            "listenarr.application",
            "listenarr.infrastructure",
            "listenarr.api"
        };

        var allowed = new[]
        {
            // Owns the derivation.
            Path.Join("listenarr.domain", "Downloads", "ReleaseIdentity.cs"),
            // The grab, where the search result is in hand and nothing has moved yet.
            Path.Join("listenarr.application", "Downloads", "Submission", "TrustedDownloadCandidateFactory.cs"),
            // The search side, which only ever has search results to go on.
            Path.Join("listenarr.application", "Downloads", "Submission", "BlockedReleaseFilter.cs")
        };

        var offenders = projects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Join(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("ReleaseIdentity.For(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .Where(relative => !allowed.Contains(relative, StringComparer.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These derive a release identity for themselves instead of reading the one stamped at "
                + "grab time:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static SearchResult UsenetResult(string nzbUrl) => new()
    {
        Id = "release-1",
        Title = Title,
        Artist = "Author",
        Album = Title,
        Source = "Indexer",
        Size = AdvertisedSize,
        NzbUrl = nzbUrl,
        DownloadType = "Usenet"
    };

    /// <summary>
    /// The download record a grab actually produces, built by the production factories rather
    /// than by hand, so a change to either of them shows up here.
    /// </summary>
    private static Download GrabbedDownloadFor(SearchResult result)
    {
        var candidate = TrustedDownloadCandidateFactory.Create(result);
        var prepared = new PreparedUsenetSubmission(
            candidate.Title,
            candidate.Artist,
            candidate.Album,
            candidate.Source,
            candidate.Quality,
            candidate.Language,
            candidate.Size,
            result.NzbUrl,
            [1, 2, 3],
            "book.nzb");

        return DownloadRecordFactory.CreateQueuedDownload(
            "download-1",
            candidate,
            prepared,
            new DownloadClientConfigurationBuilder().Build(),
            "client-1",
            audiobookId: 7);
    }
}
