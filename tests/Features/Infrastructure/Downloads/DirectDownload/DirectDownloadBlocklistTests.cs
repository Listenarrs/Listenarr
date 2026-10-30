/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Net;
using Listenarr.Infrastructure.Downloads.DirectDownload.Sources;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.DirectDownload;

/// <summary>
/// A direct download is never seen by the external-client monitor, so the worker has to write
/// the blocklist entry itself. These go from the failed transfer to the next search filter,
/// because an entry that is written under a key the filter does not derive blocks nothing.
/// </summary>
[Trait("Name", "DirectDownloadBlocklistTests")]
[Trait("Category", "DirectDownloadProcessor")]
public sealed class DirectDownloadBlocklistTests : BaseTests
{
    private const string PolicyKey = "TestPolicy";
    private const string DownloadUrl = "http://93.184.216.34/alice.m4b";
    private const string ListingTitle = "The Failing Listing";
    private const long ListingSize = 734_003_200;

    [Fact]
    public async Task ProcessDownloadAsync_WhenTheTransferFails_BlocksTheReleaseForTheNextSearch()
    {
        var (audiobook, download) = await ArrangeFailingDownloadAsync(failedDownloadHandlingEnabled: true);

        await _provider.GetRequiredService<IDirectDownloadProcessor>()
            .ProcessDownloadAsync(download, CancellationToken.None);

        Assert.Equal(DownloadStatus.Failed, download.Status);
        var entry = Assert.Single(await _provider.GetRequiredService<IBlocklistService>()
            .GetForAudiobookAsync(audiobook.Id));
        Assert.Equal(download.GetMetadataString(ReleaseIdentity.MetadataKey), entry.ReleaseIdentifier);

        // The next search sees the same release behind a fresh per-fetch link.
        var kept = await BlockedReleaseFilter.ExcludeAsync(
            _provider.GetRequiredService<IBlocklistService>(),
            audiobook.Id,
            [Scored(ListingUrl("TOKEN2"))],
            NullLogger.Instance);
        Assert.Empty(kept);
    }

    [Fact]
    public async Task ProcessDownloadAsync_WithFailedDownloadHandlingOff_WritesNoBlocklistEntry()
    {
        var (audiobook, download) = await ArrangeFailingDownloadAsync(failedDownloadHandlingEnabled: false);

        await _provider.GetRequiredService<IDirectDownloadProcessor>()
            .ProcessDownloadAsync(download, CancellationToken.None);

        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Empty(await _provider.GetRequiredService<IBlocklistService>()
            .GetForAudiobookAsync(audiobook.Id));

        var kept = await BlockedReleaseFilter.ExcludeAsync(
            _provider.GetRequiredService<IBlocklistService>(),
            audiobook.Id,
            [Scored(ListingUrl("TOKEN2"))],
            NullLogger.Instance);
        Assert.Single(kept);
    }

    private async Task<(Audiobook Audiobook, Download Download)> ArrangeFailingDownloadAsync(
        bool failedDownloadHandlingEnabled)
    {
        using var httpClient = new HttpClient(new DelegatingHandlerMock((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);
        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(new Mock<IDownloadProcessingJobService>().Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));

        var settings = new ApplicationSettingsBuilder();
        await _applicationSettingsRepository.SaveAsync(
            (failedDownloadHandlingEnabled ? settings.WithFailedDownloadHandling() : settings.WithoutFailedDownloadHandling())
            .Build());

        var audiobook = await CreateAudiobook();
        var download = await _downloadRepository.AddAsync(new Download
        {
            Id = $"ddl-{Guid.NewGuid():N}",
            AudiobookId = audiobook.Id,
            Title = ListingTitle,
            OriginalUrl = DownloadUrl,
            ExpectedFileSize = ListingSize,
            DownloadClientId = DirectDownloadMetadataKeys.ClientId,
            Status = DownloadStatus.Queued,
            StartedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                [DirectDownloadMetadataKeys.DownloadType] = DirectDownloadMetadataKeys.ClientId,
                [DirectDownloadMetadataKeys.SourcePolicyKey] = PolicyKey,
                // What DirectDownloadWorkflow stamps from the search result at the grab.
                [ReleaseIdentity.MetadataKey] = ReleaseIdentity.For(Scored(ListingUrl("TOKEN1")).SearchResult)!
            }
        });
        return (audiobook, download);
    }

    private static string ListingUrl(string token) => $"https://indexer.example.com/getnzb?id=abc&apikey={token}";

    private static QualityScore Scored(string url) => new()
    {
        SearchResult = new SearchResult { Title = ListingTitle, Size = ListingSize, NzbUrl = url }
    };

    private sealed class TestDirectDownloadSourcePolicy : IDirectDownloadSourcePolicy
    {
        public int Priority => 0;
        public string Key => PolicyKey;

        public bool CanPrepare(Indexer indexer, TrustedDownloadCandidate candidate, IReadOnlyList<Uri> uris) => true;

        public bool TryValidateArtifactPlan(IReadOnlyList<Uri> uris, out string error)
        {
            error = string.Empty;
            return true;
        }

        public bool TryValidateInitialUri(Uri uri, out string error)
        {
            error = string.Empty;
            return true;
        }

        public bool TryValidateRedirectUri(Uri uri, Uri previousUri, out string error)
        {
            error = string.Empty;
            return true;
        }

        public string GetFileName(Uri uri, Download download) => "alice.m4b";
    }
}
