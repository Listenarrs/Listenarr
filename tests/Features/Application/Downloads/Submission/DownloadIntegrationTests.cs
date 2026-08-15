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
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Application.Downloads.Submission
{
    public class DownloadIntegrationTests : BaseTests
    {
        [Theory]
        [InlineData("qbittorrent", "Torrent", false)]
        [InlineData("qbittorrent", "Torrent", true)]
        [InlineData("transmission", "Torrent", false)]
        [InlineData("transmission", "Torrent", true)]
        [InlineData("sabnzbd", "Usenet", false)]
        [InlineData("sabnzbd", "Usenet", true)]
        [InlineData("nzbget", "Usenet", false)]
        [InlineData("nzbget", "Usenet", true)]
        public async Task IndexerToClientToImport_Works_ForSingleAndMultiFile(
            string clientType,
            string downloadType,
            bool isMultiFile)
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-e2e-out");
            var sourceRoot = FileService.GetTempDirectory("listenarr-e2e-src");

            var files = isMultiFile
                ? await CreateMultiFileSourceAsync(sourceRoot)
                : await CreateSingleFileSourceAsync(sourceRoot);

            var audiobook = new AudiobookBuilder()
                .WithAuthor("Test Author")
                .WithBasePath(Path.Join(outputRoot, "library"))
                .WithTitle($"E2E {downloadType} {(isMultiFile ? "Multi" : "Single")}")
                .Build();

            var downloadClient = new DownloadClientConfigurationBuilder()
                .WithType(clientType)
                .WithHost("localhost")
                .WithPort(8080)
                .WithApiKey("apikey")
                .WithEnabled()
                .WithPath(sourceRoot)
                .Build();

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                EnableMetadataProcessing = true,
                CompletedFileAction = FileAction.Move,
                AllowedFileExtensions = [".m4b", ".mp3"],
                EnabledNotificationTriggers = [],
                WebhookUrl = string.Empty
            };

            var metadataMock = new Mock<IMetadataService>();
            metadataMock
                .Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync((string path) => new AudioMetadata
                {
                    Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    BitRate = 128000,
                    Duration = TimeSpan.FromMinutes(5)
                });
            _services.AddSingleton(metadataMock.Object);

            var preparerMock = new Mock<IDownloadSubmissionPreparer>();
            preparerMock
                .Setup(value => value.PrepareAsync(
                    It.IsAny<TrustedDownloadCandidate>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((TrustedDownloadCandidate candidate, string? _, CancellationToken _) =>
                    downloadType == "Torrent"
                        ? new PreparedTorrentSubmission(
                            candidate.Title,
                            candidate.Artist,
                            candidate.Album,
                            candidate.Source,
                            candidate.Quality,
                            candidate.Language,
                            candidate.Size,
                            "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12",
                            "ABCDEF1234567890ABCDEF1234567890ABCDEF12",
                            null,
                            "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12",
                            null,
                            [])
                        : new PreparedUsenetSubmission(
                            candidate.Title,
                            candidate.Artist,
                            candidate.Album,
                            candidate.Source,
                            candidate.Quality,
                            candidate.Language,
                            candidate.Size,
                            "https://indexer.local/file.nzb",
                            "<nzb />"u8.ToArray(),
                            "book.nzb"));
            _services.AddSingleton(preparerMock.Object);

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(downloadClient, It.IsAny<PreparedDownloadSubmission>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DownloadClientSubmissionResult($"{downloadType}-client-item-1"));
            gatewayMock
                .Setup(g => g.GetQueueItemAsync(downloadClient, It.IsAny<Download>(), It.IsAny<QueueItem>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueueItem { SourceFiles = files });
            gatewayMock
                .Setup(g => g.GetQueueAsync(downloadClient, It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new QueueItem {
                        SourceFiles = files
                    }
                ]);
            gatewayMock
                .Setup(g => g.FetchDownloadsAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<List<Download>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DownloadClientConfiguration client, List<Download> downloads, CancellationToken _) =>
                {
                    return [.. downloads.Select(download => {
                        _downloadRepository.UpdateAsync(download.Completed());
                        return download;
                    })];
                });
            gatewayMock
                .Setup(g => g.MarkItemAsImportedAsync(
                    It.IsAny<DownloadClientConfiguration>(),
                    It.IsAny<Download>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            await _audiobookRepository.AddAsync(audiobook);
            await _downloadClientConfigurationRepository.SaveAsync(downloadClient);
            await _applicationSettingsRepository.SaveAsync(settings);

            var searchResult = BuildIndexerResult(downloadType, isMultiFile);

            // Add to download client
            var downloadService = _provider.GetRequiredService<DownloadService>();
            var createdDownloadId = await downloadService.StartDownloadAsync(searchResult, downloadClient.Id, audiobook.Id);

            // Download exists and is queued
            var download = await _downloadRepository.GetByIdAsync(createdDownloadId);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Queued, download.Status);

            // No job created yet
            Assert.Empty(await _downloadProcessingJobRepository.GetRecentAsync(2));

            // Monitor download (gateway mock should make it complete)
            var downloadMonitorService = _provider.GetRequiredService<DownloadMonitorService>();
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            // Download should be completed
            download = await _downloadRepository.GetByIdAsync(createdDownloadId);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Completed, download.Status);

            // There should be a job for it now
            var jobs = await _downloadProcessingJobRepository.GetRecentAsync(2);
            Assert.Single(jobs);
            var job = jobs.First();
            Assert.NotNull(job);

            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);

            // Download should be imported
            download = await _downloadRepository.GetByIdAsync(createdDownloadId);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Moved, download.Status);

            // Audiobook still exists
            audiobook = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(audiobook);

            // Audiobook files are created
            var importedFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.True(importedFiles.Count >= 1, "Expected at least one imported file for single-file flow");
            if (isMultiFile)
            {
                Assert.True(importedFiles.Count >= 2, "Expected at least two imported files for multi-file flow");
            }
        }

        private static SearchResult BuildIndexerResult(string downloadType, bool isMultiFile)
        {
            var titleSuffix = isMultiFile ? "Multi" : "Single";
            var result = new SearchResult
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = $"Indexer Result {downloadType} {titleSuffix}",
                Artist = "Test Author",
                Source = "Test Indexer",
                Size = 10_000_000,
                DownloadType = downloadType,
                Quality = "Good"
            };

            if (downloadType.Equals("Torrent", StringComparison.OrdinalIgnoreCase))
            {
                result.MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890";
                result.TorrentUrl = "http://indexer.local/torrent/1";
            }
            else
            {
                result.NzbUrl = "http://indexer.local/nzb/1";
            }

            return result;
        }

        private async Task<List<string>> CreateSingleFileSourceAsync(string sourceRoot)
        {
            return [
                await FileService.GetFileAsync(sourceRoot, "single-book.m4b")
            ];
        }

        private async Task<List<string>> CreateMultiFileSourceAsync(string sourceRoot)
        {
            var dir = FileService.GetTempDirectory(Path.Join(sourceRoot, "multi-book"));

            return [
                await FileService.GetFileAsync(dir, "part1.mp3", "part-1"),
                await FileService.GetFileAsync(dir, "part2.mp3", "part-2")
            ];
        }
    }
}
