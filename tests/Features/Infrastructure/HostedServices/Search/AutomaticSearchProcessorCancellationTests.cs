/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.HostedServices.Search
{
    // Follow-up to #757 (#949): an OperationCanceledException that is not the host stopping can be a
    // local cancellation or timeout. These drive the whole
    // automatic-search cycle to prove a debug broadcast can no longer abort a book, that a real
    // shutdown still stops the cycle, and that a download-client timeout keeps the fast retry.
    [Trait("Name", "AutomaticSearchProcessorCancellationTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class AutomaticSearchProcessorCancellationTests : BaseTests
    {
        private const string Timeout = "The request was canceled due to the configured HttpClient.Timeout";

        [Fact]
        public async Task RunCycleAsync_BroadcastTimesOut_StillQueuesTheDownload()
        {
            // Given: every debug broadcast throws a timeout-style TaskCanceledException
            // while the host is not stopping
            var download = NewDownloadService();
            download.Setup(d => d.StartDownloadAsync(
                    It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()))
                .ReturnsAsync("download-1");
            var broadcasts = 0;
            var hub = NewHubContext(() =>
            {
                broadcasts++;
                throw new TaskCanceledException(Timeout);
            });
            InitWith(download, hub);
            var audiobook = await SeedMonitoredAudiobookAsync();

            // When: an automatic search cycle runs
            await RunCycleAsync(CancellationToken.None);

            // Then: both broadcasts failed, the book was still grabbed, and it was stamped as searched
            Assert.Equal(2, broadcasts);
            download.Verify(d => d.StartDownloadAsync(
                    It.IsAny<SearchResult>(), DirectDownloadMetadataKeys.ClientId, audiobook.Id),
                Times.Once);
            Assert.DoesNotContain(await EligibleAudiobooksAsync(), a => a.Id == audiobook.Id);
        }

        [Fact]
        public async Task RunCycleAsync_BroadcastCanceledByHostShutdown_StopsWithoutQueuing()
        {
            // Given: the host stops while a debug broadcast is in flight
            var download = NewDownloadService();
            using var stopping = new CancellationTokenSource();
            var hub = NewHubContext(() =>
            {
                stopping.Cancel();
                throw new OperationCanceledException(stopping.Token);
            });
            InitWith(download, hub);
            var audiobook = await SeedMonitoredAudiobookAsync();

            // When: an automatic search cycle runs with the host's stopping token
            await RunCycleAsync(stopping.Token);

            // Then: the cancellation is not swallowed by the broadcast catch. Nothing is grabbed
            // and the book is not stamped as searched.
            download.Verify(d => d.StartDownloadAsync(
                It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
            Assert.Contains(await EligibleAudiobooksAsync(), a => a.Id == audiobook.Id);
        }

        [Fact]
        public async Task RunCycleAsync_DownloadClientTimesOut_RetriesTheBookNextCycle()
        {
            // Given: the download client times out on the first submission and accepts the second
            var download = NewDownloadService();
            download.SetupSequence(d => d.StartDownloadAsync(
                    It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()))
                .ThrowsAsync(new TaskCanceledException(Timeout))
                .ReturnsAsync("download-1");
            InitWith(download, hub: null);
            var audiobook = await SeedMonitoredAudiobookAsync();

            // When: the first cycle hits the timeout
            await RunCycleAsync(CancellationToken.None);

            // Then: the timeout does not escape the cycle and the book is still eligible,
            // not parked behind the per-book cooldown
            Assert.Contains(await EligibleAudiobooksAsync(), a => a.Id == audiobook.Id);

            // When: the next cycle runs
            await RunCycleAsync(CancellationToken.None);

            // Then: the submission is retried, and only now is the book stamped as searched
            download.Verify(d => d.StartDownloadAsync(
                    It.IsAny<SearchResult>(), DirectDownloadMetadataKeys.ClientId, audiobook.Id),
                Times.Exactly(2));
            Assert.DoesNotContain(await EligibleAudiobooksAsync(), a => a.Id == audiobook.Id);
        }

        private static Mock<IDownloadService> NewDownloadService() => new();

        private static Mock<IHubContext<DownloadHub>> NewHubContext(Action onBroadcast)
        {
            var clientProxy = new Mock<IClientProxy>();
            clientProxy
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    onBroadcast();
                    return Task.CompletedTask;
                });
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.All).Returns(clientProxy.Object);
            var hubContext = new Mock<IHubContext<DownloadHub>>();
            hubContext.Setup(h => h.Clients).Returns(hubClients.Object);
            return hubContext;
        }

        private void InitWith(Mock<IDownloadService> download, Mock<IHubContext<DownloadHub>>? hub)
        {
            var search = new Mock<ISearchService>();
            search.Setup(s => s.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(() => new List<SearchResult>
                {
                    new()
                    {
                        Title = "Andy Weir - Artemis - 2017 - M4B",
                        DownloadType = DirectDownloadMetadataKeys.ClientId
                    }
                });
            // Scoring has its own tests: accept the result with a positive, non-rejected score.
            var quality = new Mock<IQualityProfileService>();
            quality.Setup(q => q.ScoreSearchResults(
                    It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
                .ReturnsAsync((List<SearchResult> results, QualityProfile _) =>
                    results.Select(r => new QualityScore { SearchResult = r, TotalScore = 100 }).ToList());

            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
                builder.WithSingleton(quality.Object);
                if (hub != null)
                {
                    builder.WithSingleton(hub.Object);
                }
            });
        }

        private async Task<Audiobook> SeedMonitoredAudiobookAsync() =>
            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Artemis")
                .WithAuthor("Andy Weir")
                .WithMonitored()
                .WithQualityProfile(new QualityProfile { Name = "Any" })
                .Build());

        // The same query the cycle uses to pick books: never searched, or searched before the cutoff.
        private Task<List<Audiobook>> EligibleAudiobooksAsync() =>
            _audiobookRepository.GetMonitoredAudiobooksForSearchAsync(DateTime.UtcNow.AddHours(-1));

        private async Task RunCycleAsync(CancellationToken stoppingToken)
        {
            var processor = new AutomaticSearchProcessor(
                NullLogger<AutomaticSearchProcessor>.Instance,
                _provider.GetRequiredService<IServiceScopeFactory>());
            await processor.RunCycleAsync(stoppingToken);
        }
    }
}
