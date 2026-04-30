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
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Moq;
using Xunit;
using Listenarr.Api.Services.Metadata;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Tests.Features.Api.Services
{
    public class EndToEndDownloadImportFlowTests : BaseTests
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
        public async Task IndexerToClientToImport_EndToEnd_Works_ForSingleAndMultiFile(
            string clientType,
            string downloadType,
            bool isMultiFile)
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-e2e-out");
            var sourceRoot = FileService.GetTempDirectory("listenarr-e2e-src");

            var sourcePath = isMultiFile
                ? await CreateMultiFileSourceAsync(sourceRoot)
                : await CreateSingleFileSourceAsync(sourceRoot);

            var audiobook = new Audiobook
            {
                Id = 1,
                Title = $"E2E {downloadType} {(isMultiFile ? "Multi" : "Single")}",
                Authors = new List<string> { "Test Author" },
                BasePath = Path.Join(outputRoot, "library", Guid.NewGuid().ToString("N"))
            };

            var downloadClient = new DownloadClientConfiguration
            {
                Id = $"client-{clientType}",
                Name = clientType,
                Type = clientType,
                Host = "localhost",
                Port = 8080,
                IsEnabled = true,
                DownloadPath = sourceRoot,
                Settings = clientType.Equals("sabnzbd", StringComparison.OrdinalIgnoreCase)
                    ? new Dictionary<string, object> { ["apiKey"] = "apikey" }
                    : []
            };

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                EnableMetadataProcessing = true,
                CompletedFileAction = "Move",
                AllowedFileExtensions = new List<string> { ".m4b", ".mp3" },
                EnabledNotificationTriggers = new List<string>(),
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

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(downloadClient, It.IsAny<SearchResult>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync($"{downloadType}-client-item-1");
            gatewayMock
                .Setup(g => g.GetQueueAsync(downloadClient, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>());
            _services.AddSingleton(gatewayMock.Object);

            Init();

            await _audiobookRepository.AddAsync(audiobook);
            await _downloadClientConfigurationRepository.SaveAsync(downloadClient);
            await _applicationSettingsRepository.SaveAsync(settings);

            var searchResult = BuildIndexerResult(downloadType, isMultiFile);

            var downloadService = _provider.GetRequiredService<DownloadService>();
            var createdDownloadId = await downloadService.StartDownloadAsync(searchResult, downloadClient.Id, audiobook.Id);
            await downloadService.ProcessCompletedDownloadAsync(createdDownloadId, sourcePath);

            var download = await _downloadRepository.GetByIdAsync(createdDownloadId);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Moved, download!.Status);

            var persistedAudiobook = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(persistedAudiobook);

            var importedFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            if (importedFiles.Count == 0)
            {
                var basePath = persistedAudiobook!.BasePath ?? string.Empty;
                var diskFiles = Directory.Exists(basePath)
                    ? Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)
                    : Array.Empty<string>();

                if (isMultiFile)
                {
                    Assert.True(diskFiles.Length >= 2, "Expected at least two imported files on disk for multi-file flow");
                }
                else
                {
                    Assert.True(diskFiles.Length >= 1, "Expected at least one imported file on disk for single-file flow");
                }
            }
            else
            {
                if (isMultiFile)
                {
                    Assert.True(importedFiles.Count >= 2, "Expected at least two imported files for multi-file flow");
                }
                else
                {
                    Assert.True(importedFiles.Count >= 1, "Expected at least one imported file for single-file flow");
                }
            }

            gatewayMock.Verify(g => g.AddAsync(downloadClient, It.IsAny<SearchResult>(), It.IsAny<CancellationToken>()), Times.Once);
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

        private async Task<string> CreateSingleFileSourceAsync(string sourceRoot)
        {
            return await FileService.GetFileAsync(sourceRoot, "single-book.m4b");
        }

        private async Task<string> CreateMultiFileSourceAsync(string sourceRoot)
        {
            var dir = FileService.GetTempDirectory(Path.Join(sourceRoot, "multi-book"));
            await FileService.GetFileAsync(dir, "part1.mp3", "part-1");
            await FileService.GetFileAsync(dir, "part2.mp3", "part-2");
            return dir;
        }
    }
}
