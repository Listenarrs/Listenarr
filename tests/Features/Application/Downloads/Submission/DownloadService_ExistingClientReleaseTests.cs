using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Submission
{
    /// <summary>
    /// A client that already holds the release being grabbed refuses the add (#1221). When the only
    /// Listenarr record for that torrent belonged to a book that has since been deleted, the grab
    /// adopts the client's copy for the book being grabbed instead of failing.
    /// </summary>
    [Trait("Name", "DownloadService_ExistingClientReleaseTests")]
    [Trait("Category", "Downloads")]
    public class DownloadService_ExistingClientReleaseTests : BaseTests
    {
        private const string ClientId = "qb-1";
        private const string Hash = "ABCDEF1234567890ABCDEF1234567890ABCDEF12";
        private const string OtherHash = "1234567890ABCDEF1234567890ABCDEF12345678";
        private const int DeletedAudiobookId = 987654;

        public override async Task InitializeAsync()
        {
            var gateway = new Mock<IDownloadClientGateway>();
            gateway
                .Setup(g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<PreparedDownloadSubmission>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DownloadClientRejectedReleaseException("qBittorrent already holds this release."));
            _services.AddSingleton(gateway.Object);
            Init();

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(Path.GetTempPath())
                .Build());
            await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Id = ClientId,
                Name = "local qbit",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080,
                IsEnabled = true
            });
        }

        [Fact]
        public async Task RejectedRelease_HeldForADeletedBook_IsAdoptedForTheBookBeingGrabbed()
        {
            var book = await CreateAudiobook();
            var held = await AddHeldDownloadAsync(DeletedAudiobookId, Hash);
            var recordsBefore = (await _downloadRepository.GetAllAsync()).Count;

            var downloadId = await Send(book.Id, Hash);

            Assert.Equal(held.Id, downloadId);
            var adopted = await _downloadRepository.GetByIdAsync(held.Id);
            Assert.NotNull(adopted);
            Assert.Equal(book.Id, adopted!.AudiobookId);
            Assert.Equal(DownloadStatus.Completed, adopted.Status);
            Assert.Equal(string.Empty, adopted.FinalPath);
            Assert.Equal("/downloads/Held Release", adopted.Metadata["ClientContentPath"]?.ToString());
            Assert.Equal(recordsBefore, (await _downloadRepository.GetAllAsync()).Count);
        }

        [Fact]
        public async Task RejectedRelease_HeldForADeletedBook_QueuesAnImportJobForTheAdoptedRecord()
        {
            var book = await CreateAudiobook();
            var held = await AddHeldDownloadAsync(DeletedAudiobookId, Hash);

            await Send(book.Id, Hash);

            // The monitor only enqueues on a status change, so adoption has to queue the import itself.
            var job = await _provider.GetRequiredService<IDownloadProcessingJobService>().GetNextJobAsync();
            Assert.NotNull(job);
            Assert.Equal(held.Id, job!.DownloadId);
        }

        [Fact]
        public async Task RejectedRelease_HeldForADeletedBook_RecordsGrabbedAndNoRejection()
        {
            var book = await CreateAudiobook();
            var held = await AddHeldDownloadAsync(DeletedAudiobookId, Hash);

            await Send(book.Id, Hash);

            var events = (await _historyRepository.GetPagedAsync(100, 0))
                .Where(h => string.Equals(h.DownloadId, held.Id, StringComparison.OrdinalIgnoreCase))
                .Select(h => h.EventType)
                .ToList();
            Assert.Contains(HistoryEvents.Grabbed, events);
            Assert.DoesNotContain(HistoryEvents.DownloadFailed, events);
        }

        [Fact]
        public async Task RejectedRelease_HeldForALiveBook_StillThrowsAndLeavesTheRecordAlone()
        {
            var book = await CreateAudiobook();
            var otherBook = await CreateAudiobook();
            var held = await AddHeldDownloadAsync(otherBook.Id, Hash);

            await Assert.ThrowsAsync<DownloadClientRejectedReleaseException>(() => Send(book.Id, Hash));

            var untouched = await _downloadRepository.GetByIdAsync(held.Id);
            Assert.Equal(otherBook.Id, untouched!.AudiobookId);
            Assert.Equal(DownloadStatus.Moved, untouched.Status);
        }

        [Fact]
        public async Task RejectedRelease_WithNoRecordForThatHash_StillThrows()
        {
            var book = await CreateAudiobook();
            var held = await AddHeldDownloadAsync(DeletedAudiobookId, OtherHash);

            await Assert.ThrowsAsync<DownloadClientRejectedReleaseException>(() => Send(book.Id, Hash));

            var untouched = await _downloadRepository.GetByIdAsync(held.Id);
            Assert.Equal(DeletedAudiobookId, untouched!.AudiobookId);
        }

        private async Task<Download> AddHeldDownloadAsync(int audiobookId, string hash)
        {
            var held = new DownloadBuilder()
                .WithStatus(DownloadStatus.Moved)
                .Build();
            held.DownloadClientId = ClientId;
            held.AudiobookId = audiobookId;
            held.FinalPath = "/library/Held Release";
            held.SetMetadata("TorrentHash", hash);
            held.SetMetadata("ClientContentPath", "/downloads/Held Release");
            return await _downloadRepository.AddAsync(held);
        }

        private Task<string> Send(int audiobookId, string hash)
        {
            var searchResult = new SearchResult
            {
                Title = "The Time Machine",
                Artist = "H. G. Wells",
                DownloadType = "Torrent",
                MagnetLink = $"magnet:?xt=urn:btih:{hash}",
                Size = 123456789
            };
            return _provider.GetRequiredService<DownloadService>()
                .SendToDownloadClientAsync(searchResult, ClientId, audiobookId);
        }
    }
}
