using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Application.Downloads.Submission
{
    public class DownloadServiceTests : BaseTests
    {
        private DownloadClientConfiguration _client = new DownloadClientConfigurationBuilder().Build();
        private Audiobook _audiobook = new AudiobookBuilder().Build();
        private Download _download = new DownloadBuilder().Build();
        private MetadataServiceMock metadataServiceMock = new MetadataServiceMock();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IMetadataService>(metadataServiceMock);
            Init();

            await InitData();
        }

        private async Task InitData()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(Path.GetTempPath())
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080,
                IsEnabled = true
            });

            _audiobook = await CreateAudiobook();

            _download.DownloadClientId = _client.Id;
            _download.AudiobookId = _audiobook.Id;
            await _downloadRepository.AddAsync(_download);
        }

        [Fact]
        public async Task SendToDownloadClientAsync_StoresMagnetHashFallback_WhenClientReturnsNoId()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<PreparedDownloadSubmission>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new DownloadClientSubmissionResult("ABCDEF1234567890ABCDEF1234567890ABCDEF12"));
            _services.AddSingleton(gatewayMock.Object);

            var queueServiceMock = new Mock<IDownloadQueueService>();
            queueServiceMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<QueueItem>
            {
                new QueueItem
                {
                    Id = "tracked-1",
                    Title = "Dune - Frank Herbert [M4B]",
                    Status = "completed",
                    DownloadClient = "local qbit",
                    DownloadClientId = "qb-1",
                    DownloadClientType = "qbittorrent",
                    AddedAt = DateTime.UtcNow.AddHours(-2)
                }
            });
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await InitData();

            var downloadService = _provider.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "Torrent",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12&dn=Artemis",
                Size = 123456789
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult, "qb-1");
            var persisted = await _downloadRepository.GetByIdAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("ABCDEF1234567890ABCDEF1234567890ABCDEF12", persisted!.Metadata["ClientDownloadId"]?.ToString());
            Assert.Equal("ABCDEF1234567890ABCDEF1234567890ABCDEF12", persisted.Metadata["TorrentHash"]?.ToString());
        }

        [Fact]
        public async Task SendToDownloadClientAsync_WhenClientSubmissionFails_RemovesProvisionalDownloadAndDoesNotRecordGrab()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>(MockBehavior.Strict);
            gatewayMock
                .Setup(g => g.AddAsync(
                    It.Is<DownloadClientConfiguration>(client => client.Id == "qb-1"),
                    It.IsAny<PreparedDownloadSubmission>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DownloadClientSubmissionException("Unable to obtain a verified hash from the torrent metadata."));

            var historyMock = new Mock<IDownloadHistoryService>(MockBehavior.Strict);
            var notificationMock = new Mock<INotificationService>(MockBehavior.Strict);
            _services.AddSingleton(gatewayMock.Object);
            _services.AddSingleton(historyMock.Object);
            _services.AddSingleton(notificationMock.Object);

            Init();
            await InitData();
            var initialDownloadCount = (await _downloadRepository.GetAllAsync()).Count;
            var downloadService = _provider.GetRequiredService<DownloadService>();
            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "Torrent",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12",
                Size = 123456789
            };

            await Assert.ThrowsAsync<DownloadClientSubmissionException>(
                () => downloadService.SendToDownloadClientAsync(searchResult, _client.Id));

            Assert.Equal(initialDownloadCount, (await _downloadRepository.GetAllAsync()).Count);
            gatewayMock.VerifyAll();
            historyMock.VerifyNoOtherCalls();
            notificationMock.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SendToDownloadClientAsync_DerivesTorrent_WhenRequestSpoofsDdl()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<PreparedDownloadSubmission>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new DownloadClientSubmissionResult("client-download-123"));
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await InitData();

            var downloadService = _provider.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "DDL",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12&dn=Artemis",
                Size = 123456789
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult, "qb-1");
            var persisted = await _downloadRepository.GetByIdAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("qb-1", persisted!.DownloadClientId);
            Assert.Equal("Torrent", persisted.Metadata["DownloadType"]?.ToString());
            gatewayMock.Verify(
                g => g.AddAsync(
                    _client,
                    It.Is<PreparedDownloadSubmission>(submission => submission is PreparedTorrentSubmission),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SendToDownloadClientAsync_DerivesTrustedInternetArchiveAsDdl()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await InitData();

            await _indexerRepository.AddAsync(new Indexer
            {
                Id = 42,
                Name = "Internet Archive",
                Url = "https://archive.org/advancedsearch.php",
                Type = "Torrent",
                Implementation = "InternetArchive",
                IsEnabled = true
            });

            var downloadService = _provider.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "DDL",
                IndexerId = 42,
                IndexerImplementation = "InternetArchive",
                TorrentUrl = "https://archive.org/download/artemis_book/artemis.m4b",
                Size = 123456789,
                Source = "Internet Archive"
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult, "qb-1");
            var persisted = await _downloadRepository.GetByIdAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("DDL", persisted!.DownloadClientId);
            Assert.Equal("DDL", persisted.Metadata["DownloadType"]?.ToString());
            gatewayMock.Verify(
                g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<PreparedDownloadSubmission>(), It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateAsync_WhenDownloadDoesNotExist_DoesNotPersistOrNotify()
        {
            var missingDownload = new DownloadBuilder()
                .WithId("missing-download")
                .WithStatus(DownloadStatus.Queued)
                .Build();

            var downloadRepository = new Mock<IDownloadRepository>(MockBehavior.Strict);
            downloadRepository
                .Setup(r => r.GetByIdAsync(missingDownload.Id))
                .ReturnsAsync((Download?)null);

            var notificationService = new Mock<INotificationService>(MockBehavior.Strict);

            _services.AddSingleton(downloadRepository.Object);
            _services.AddSingleton(notificationService.Object);
            Init();

            var downloadService = _provider.GetRequiredService<DownloadService>();

            await downloadService.UpdateAsync(missingDownload);

            downloadRepository.Verify(r => r.GetByIdAsync(missingDownload.Id), Times.Once);
            downloadRepository.Verify(r => r.UpdateAsync(It.IsAny<Download>()), Times.Never);
            notificationService.VerifyNoOtherCalls();
        }
    }
}
