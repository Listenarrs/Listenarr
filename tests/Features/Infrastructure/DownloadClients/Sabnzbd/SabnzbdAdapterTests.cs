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
using System.Net;
using Listenarr.Domain.Downloads.Exceptions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;


namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Sabnzbd
{
    public class SabnzbdAdapterTests : BaseTests
    {
        private DownloadClientConfiguration _client = null!;
        private SabnzbdApiMock sabnzbdApiMock = null!;

        public override async Task InitializeAsync()
        {
            sabnzbdApiMock = _provider.GetRequiredService<SabnzbdApiMock>();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("http://192.168.50.111/sab")
                .WithPort(8080)
                .WithoutSsl()
                .WithApiKey("secret")
                .WithType("sabnzbd")
                .Build());
        }

        [Fact]
        public async Task TestConnectionAsync_NormalizesHostWithSchemeAndPath()
        {
            var apiMock = _provider.GetRequiredService<SabnzbdApiMock>();

            var downloadClientGateway = _provider.GetRequiredService<IDownloadClientGateway>();
            var (success, message) = await downloadClientGateway.TestConnectionAsync(_client);

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);

            var capturedUri = apiMock.GetLastRequest().RequestUri;
            Assert.NotNull(capturedUri);
            Assert.Equal("http", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(8080, capturedUri.Port);
            Assert.Equal("/api", capturedUri.AbsolutePath);
            Assert.Contains("mode=version", capturedUri.Query, StringComparison.Ordinal);
            Assert.Contains("output=json", capturedUri.Query, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PollSABnzbd_Queue_StringFields_UpdateProgress()
        {
            // Seed download (simulating a SABnzbd download record with DownloadClientId set to the NZO ID)
            var audiobook = await CreateAudiobook();
            var download = new Download
            {
                Id = "dq1",
                Title = "William Faulkner - The Sound and the Fury",
                Status = DownloadStatus.Queued,
                DownloadPath = string.Empty,
                DownloadClientId = _client.Id,
                StartedAt = DateTime.UtcNow,
                AudiobookId = audiobook.Id,
                TotalSize = (long)(100 * 1024 * 1024) // 100 MB
            };
            download.SetExternalId("SABnzbd_nzo_20f9svw_");
            await _downloadRepository.AddAsync(download);

            var monitor = _provider.GetRequiredService<DownloadMonitorService>();
            await monitor.MonitorDownloadsAsync(CancellationToken.None);

            // Verify the DB download was updated with progress ~50.5 (progress is stored as decimal)
            var updated = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(updated);
            Assert.True(updated.Progress > 50 && updated.Progress < 51);
            // downloaded size should reflect ~50.5% of 100 MB -> ~50.5 MB
            Assert.True(updated.DownloadedSize > 50 * 1024 * 1024 && updated.DownloadedSize < 51 * 1024 * 1024);
        }

        [Fact]
        public async Task PollSABnzbd_Mapping_StripsNumericSuffix_AndFinalizesDownload()
        {
            // Create a file under a directory WITHOUT the numeric suffix (this is the real local layout)
            var source = FileService.GetTempDirectory("download");
            var destination = FileService.GetTempDirectory("destination");
            var sourceFile = await FileService.GetFileAsync(source, "The Sound and the Fury.m4b");
            var basePath = Path.Join(destination, "The Sound and the Fury");

            sabnzbdApiMock.contentPath = source;

            _client.DownloadPath = source;
            await _downloadClientConfigurationRepository.SaveAsync(_client);

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithPath(source)
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(_client)
                .WithDownloading(0)
                .WithPath(source)
                .WithExternalId(SabnzbdApiMock.COMPLETED_FILE_SABNZBD)
                .Build());

            var monitor = _provider.GetRequiredService<DownloadMonitorService>();
            await monitor.MonitorDownloadsAsync(CancellationToken.None);

            var jobs = await _downloadProcessingJobRepository.GetRecentAsync(2);
            Assert.Single(jobs);
            var job = jobs.First();
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);

            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.Equal(ProcessingJobStatus.Completed, job.Status);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.Equal(DownloadStatus.Moved, download.Status);
        }

        /// <summary>
        /// Regression test for https://github.com/Listenarrs/Listenarr/issues/631
        /// SABnzbd downloads were getting "Import Blocked" with "has no path set" because
        /// FetchDownloadsAsync marked downloads as Completed but never set DownloadPath
        /// from the SABnzbd history storage field.
        /// </summary>
        [Fact]
        public async Task PollSABnzbd_SetsDownloadPath_FromHistoryStorage_WhenPathWasEmpty()
        {
            var source = FileService.GetTempDirectory("sabnzbd-path-test");
            var destination = FileService.GetTempDirectory("sabnzbd-path-destination");
            await FileService.GetFileAsync(source, "hello.m4b");

            sabnzbdApiMock.contentPath = source;

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destination)
                .Build());

            // Simulate a download created with no DownloadPath (empty), as would happen
            // if SABnzbd hadn't yet reported the storage location when the download was added.
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithPath(string.Empty)
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(_client)
                .WithDownloading(0)
                .WithTitle("hello")
                .WithClientDownloadId(SabnzbdApiMock.COMPLETED_FILE_SABNZBD)
                .Build());

            var monitor = _provider.GetRequiredService<DownloadMonitorService>();
            await monitor.MonitorDownloadsAsync(CancellationToken.None);

            var updated = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(updated);
            Assert.Equal(DownloadStatus.Completed, updated.Status);
            Assert.False(string.IsNullOrEmpty(updated.DownloadPath),
                "DownloadPath must be populated from SABnzbd history storage so the import processor can find the files");
            Assert.Equal(source, updated.DownloadPath);
        }

        [Fact]
        public async Task GetQueueAsync_WithTrackedActiveItem_DoesNotCallHistory()
        {
            sabnzbdApiMock.HistoryStatusCode = HttpStatusCode.InternalServerError;
            sabnzbdApiMock.ResetRequestHistory();
            var adapter = MockUtils.CreateSabnzbdAdapter(_provider);

            var items = await adapter.GetQueueAsync(_client, ["SABnzbd_nzo_20f9svw_"]);

            Assert.Single(items);
            Assert.DoesNotContain(sabnzbdApiMock.RequestHistory,
                request => request.RequestUri.Query.Contains("mode=history", StringComparison.Ordinal));
        }

        [Fact]
        public async Task GetQueueAsync_WithMissingTrackedItem_Throws_WhenHistoryFails()
        {
            sabnzbdApiMock.HistoryStatusCode = HttpStatusCode.InternalServerError;
            sabnzbdApiMock.ResetRequestHistory();
            var adapter = MockUtils.CreateSabnzbdAdapter(_provider);

            await Assert.ThrowsAsync<DownloadClientAdapterPollingException>(
                () => adapter.GetQueueAsync(_client, ["missing-nzo-id"]));

            Assert.Contains(sabnzbdApiMock.RequestHistory,
                request => request.RequestUri.Query.Contains("mode=history", StringComparison.Ordinal));
        }

        [Fact]
        public async Task GetQueueAsync_FullSnapshot_DoesNotThrow_WhenHistoryFails()
        {
            sabnzbdApiMock.HistoryStatusCode = HttpStatusCode.InternalServerError;
            sabnzbdApiMock.ResetRequestHistory();
            var adapter = MockUtils.CreateSabnzbdAdapter(_provider);

            var items = await adapter.GetQueueAsync(_client);

            Assert.NotEmpty(items);
            Assert.Contains(sabnzbdApiMock.RequestHistory,
                request => request.RequestUri.Query.Contains("mode=history", StringComparison.Ordinal));
        }

        [Fact]
        public async Task GetQueueAsync_WithTrackedIds_UsesMonitorHistoryLimit()
        {
            sabnzbdApiMock.ResetRequestHistory();
            var adapter = MockUtils.CreateSabnzbdAdapter(_provider);

            var items = await adapter.GetQueueAsync(_client, [SabnzbdApiMock.COMPLETED_FILE_SABNZBD]);

            Assert.Single(items);
            var historyRequest = Assert.Single(sabnzbdApiMock.RequestHistory,
                request => request.RequestUri.Query.Contains("mode=history", StringComparison.Ordinal));
            Assert.Contains("limit=100", historyRequest.RequestUri.Query, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetQueueAsync_FullSnapshot_UsesDisplayHistoryLimit()
        {
            sabnzbdApiMock.ResetRequestHistory();
            var adapter = MockUtils.CreateSabnzbdAdapter(_provider);

            var items = await adapter.GetQueueAsync(_client);

            Assert.NotEmpty(items);
            var historyRequest = Assert.Single(sabnzbdApiMock.RequestHistory,
                request => request.RequestUri.Query.Contains("mode=history", StringComparison.Ordinal));
            Assert.Contains("limit=30", historyRequest.RequestUri.Query, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetImportItemAsync_QueueItemWithStaleContentPath_ResolvesFromHistoryStorage()
        {
            var source = FileService.GetTempDirectory("sabnzbd-history-storage");
            var stalePath = Path.Join(FileService.GetTempDirectory("sabnzbd-stale-path"), "missing-folder");
            sabnzbdApiMock.contentPath = source;

            var adapter = MockUtils.CreateSabnzbdAdapter(_provider);
            var original = new QueueItem
            {
                Id = SabnzbdApiMock.COMPLETED_FILE_SABNZBD,
                Title = "hello",
                Status = "completed",
                ContentPath = stalePath
            };

            var resolved = await adapter.GetImportItemAsync(_client, new Download { Id = "download-1" }, original);

            Assert.Equal(source, resolved.ContentPath);
            Assert.Equal(stalePath, original.ContentPath);
        }

        [Fact]
        public async Task PollSABnzbd_SchedulesRetry_AndFinalizes_WhenFileArrives()
        {
            var sourceDirectory = FileService.GetTempDirectory("listenarr-test");
            var destinationDirectory = FileService.GetTempDirectory("listenarr-destination");
            var filePath = Path.Join(sourceDirectory, "The Sound and the Fury.m4b");

            sabnzbdApiMock.contentPath = filePath;

            // Seed download
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destinationDirectory)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(at: DateTime.UtcNow)
                .WithDownloadClientConfiguration(_client)
                .WithAudiobook(audiobook)
                .WithPath(sourceDirectory)
                .WithExternalId(SabnzbdApiMock.COMPLETED_FILE_SABNZBD)
                .Build());

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotNull(jobId);
            var job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);

            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Check job is still pending
            job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.Equal(1, job.RetryCount);

            await TestUtils.CancelJobRetryWait(_downloadProcessingJobRepository, job);

            // Wait a short time then create the file so the scheduled retry will find it
            await Task.Delay(200);

            var sourceFile = await FileService.GetFileAsync(sourceDirectory, "The Sound and the Fury.m4b");

            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Check job is completed
            job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Completed, job.Status);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public async Task RemoveAsync_DeletesQueueAndHistoryWithConfiguredFilePolicy(
            bool deleteFiles,
            bool expectDeleteFilesParameter)
        {
            var gateway = _provider.GetRequiredService<IDownloadClientGateway>();

            var result = await gateway.RemoveAsync(_client, "SABnzbd_nzo_remove", deleteFiles);

            Assert.True(result);
            Assert.Equal(2, sabnzbdApiMock.RemovalRequests.Count);
            Assert.All(sabnzbdApiMock.RemovalRequests, uri =>
            {
                Assert.Contains("name=delete", uri.Query, StringComparison.Ordinal);
                Assert.Contains("value=SABnzbd_nzo_remove", uri.Query, StringComparison.Ordinal);
                Assert.Equal(expectDeleteFilesParameter, uri.Query.Contains("del_files=1", StringComparison.Ordinal));
            });
        }

        [Fact]
        public void ActiveQueueItem_DoesNotInventImportContentPath_FromConfiguredDownloadPath()
        {
            _client.DownloadPath = FileUtils.GetAbsolutePath("sabnzbd-active-root");
            using var document = System.Text.Json.JsonDocument.Parse(
                """
                {
                  "nzo_id": "sab-active-1",
                  "filename": "Book Folder",
                  "status": "Downloading",
                  "percentage": "50",
                  "mb": "100",
                  "mbleft": "50"
                }
                """);

            var item = SabnzbdResponseMapper.MapQueueSlotToQueueItem(
                _client,
                document.RootElement,
                string.Empty,
                speed: 0);

            Assert.NotNull(item);
            Assert.Equal(_client.DownloadPath, item!.RemotePath);
            Assert.Null(item.ContentPath);
            Assert.NotNull(item.SourceFiles);
            Assert.Empty(item.SourceFiles);
        }

        [Fact]
        public void ActiveQueueItem_UsesExplicitStoragePath_WhenSabReportsOne()
        {
            _client.DownloadPath = FileUtils.GetAbsolutePath("sabnzbd-active-root");
            var storagePath = FileUtils.GetAbsolutePath("sabnzbd-storage", "Book Folder");
            using var document = System.Text.Json.JsonDocument.Parse(
                $$"""
                {
                  "nzo_id": "sab-active-2",
                  "filename": "Book Folder",
                  "status": "Completed",
                  "percentage": "100",
                  "mb": "100",
                  "mbleft": "0",
                  "storage": "{{storagePath.Replace("\\", "\\\\")}}"
                }
                """);

            var item = SabnzbdResponseMapper.MapQueueSlotToQueueItem(
                _client,
                document.RootElement,
                string.Empty,
                speed: 0);

            Assert.NotNull(item);
            Assert.Equal(storagePath, item!.RemotePath);
            Assert.Equal(storagePath, item.ContentPath);
        }

        [Theory]
        [InlineData("Completed", "completed")]
        [InlineData("Failed", "failed")]
        public void HistoryStatus_IsMappedWithoutBehaviorDrift(string status, string expected)
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                $$"""{"nzo_id":"sab-1","name":"Book","status":"{{status}}","storage":"/downloads/book"}""");

            var item = SabnzbdResponseMapper.MapHistorySlotToQueueItem(
                _client,
                document.RootElement,
                string.Empty,
                new HashSet<string>());

            Assert.NotNull(item);
            Assert.Equal(expected, item!.Status);
            Assert.Equal("/downloads/book", item.ContentPath);
        }
    }
}
