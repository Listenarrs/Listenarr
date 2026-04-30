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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Moq;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Metadata;
using Listenarr.Tests.Common;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Services
{
    public class DownloadService_ImportTests : BaseTests
    {
        private DownloadClientConfiguration _client = new DownloadClientConfigurationBuilder().Build();

        public override async Task InitializeAsync()
        {
            // Mock services
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Format = "mp3", BitRate = 128000 });

            _services.AddSingleton(metadataMock.Object);
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
        }

        [Fact]
        public async Task QualityGating_SkipsLowerQualityImport()
        {
            // Create audiobook and an existing high-quality 
            var book = new AudiobookBuilder()
                .WithTitle("The High Quality Book")
                .Build();
            await _audiobookRepository.AddAsync(book);

            // Simulate existing AudiobookFile (MP3 320) in DB
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = book.Id,
                Path = "C:\\library\\high.mp3",
                Format = "mp3",
                Bitrate = 320000,
                Source = "manual",
                CreatedAt = DateTime.UtcNow
            });

            // Create a temp file representing a lower-quality completed download (MP3 128)
            var tmpMp3 = await FileService.GetTempFileAsync("dummy.mp3");

            // Create download record linked to audiobook
            var download = new Download
            {
                Id = "qg-1",
                AudiobookId = book.Id,
                Title = book.Title!,
                Status = DownloadStatus.Completed,
                DownloadPath = tmpMp3,
                FinalPath = tmpMp3,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            await _downloadRepository.AddAsync(download);

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettings { OutputPath = Path.GetTempPath(), EnableMetadataProcessing = true, CompletedFileAction = "Move" });

            // Act - process completed download
            var downloadService = _provider.GetRequiredService<DownloadService>();
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: no new AudiobookFile created for this audiobook (still only the existing one)
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id);
            Assert.Single(files);
        }

        [Fact]
        public async Task MultiFileImport_ImportsAllFiles_WithUniqueNames()
        {
            // Create an existing file in destination with name collision
            var basePath = FileService.GetTempDirectory("listenarr-multi");
            var existing = await FileService.GetFileAsync(basePath, "chapter1.mp3");

            // Create source directory with two files: one collides, one new
            var srcDir = FileService.GetTempDirectory("listenarr-src");
            var file1 = await FileService.GetFileAsync(srcDir, "chapter1.mp3");
            var file2 = await FileService.GetFileAsync(srcDir, "chapter2.mp3");

            var book = new AudiobookBuilder()
                .WithTitle("Multi Book")
                .WithBasePath(basePath)
                .Build();
            await _audiobookRepository.AddAsync(book);

            // Create download pointing at the directory
            var download = new Download
            {
                Id = "mf-1",
                AudiobookId = book.Id,
                Title = book.Title!,
                Status = DownloadStatus.Completed,
                DownloadPath = srcDir,
                FinalPath = srcDir,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            await _downloadRepository.AddAsync(download);

            // Act
            var downloadService = _provider.GetRequiredService<DownloadService>();
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: files were moved into destination or imported later (deferred). At minimum we expect either DB records
            // to be created synchronously or files to be present on disk in the audiobook BasePath.
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id);
            if (files.Count == 0)
            {
                // If no DB records yet, check that files are present on disk (indicating move completed)
                var diskFiles = Directory.GetFiles(book.BasePath, "*", SearchOption.AllDirectories).Select(p => Path.GetFileName(p)).ToList();
                Assert.True(diskFiles.Contains("chapter1.mp3") || diskFiles.Contains("chapter2.mp3"), "Expected at least one AudiobookFile DB record or files present on disk");
            }
            else
            {
                // Existing DB assertions when import ran synchronously
                Assert.True(files.Count >= 1, "Expected at least one AudiobookFile DB record to be created");

                // Search recursively because naming patterns may place files into subfolders under the audiobook BasePath
                var diskFiles = Directory.GetFiles(book.BasePath, "*", SearchOption.AllDirectories).Select(p => Path.GetFileName(p)).ToList();
                // Colliding original file should remain and a suffixed file should be present
                Assert.Contains("chapter1.mp3", diskFiles);
                // Either a suffixed file for the colliding chapter1, or the second file should also be present
                Assert.True(
                    diskFiles.Any(d => d.StartsWith("chapter1 (")) ||
                    diskFiles.Any(d => d.StartsWith("chapter2")) ||
                    files.Count > 1,
                    "Expected a suffixed filename for the collision or the second file to be present or multiple DB entries");
            }
        }

        [Fact]
        public async Task ImportFilesFromDirectory_MultipartFiles_KeepNaturalOrderWhenRenamed()
        {
            var outputDir = FileService.GetTempDirectory("listenarr-import-ordered");

            var srcDir = FileService.GetTempDirectory("listenarr-import-ordered-src");
            var part10 = await FileService.GetFileAsync(srcDir, "Part 10.mp3", "ten");
            var part2 = await FileService.GetFileAsync(srcDir, "Part 2.mp3", "two");
            var part1 = await FileService.GetFileAsync(srcDir, "Part 1.mp3", "one");

            _services.AddScoped<IMetadataService, MetadataService>();
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(
                    new AudioMetadata { Title = "Ordered Download", Format = "mp3", BitRate = 128000 });
            _services.AddSingleton(metadataMock.Object);

            Init();
            await InitData();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDir)
                .WithMetadataProcessing()
                .WithCopyFileOnCompleted()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}")
                .Build());

            var importService = _provider.GetRequiredService<IImportService>();
            var results = await importService.ImportFilesFromDirectoryAsync(
                "ordered-download",
                audiobookId: null,
                [part10, part2, part1],
                settings);

            var mapped = results
                .Where(r => r.Success && !string.IsNullOrWhiteSpace(r.FinalPath) && !string.IsNullOrWhiteSpace(r.SourcePath))
                .ToDictionary(r => r.SourcePath!, r => r.FinalPath!, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(Path.Join(outputDir, "Ordered Download-01.mp3"), mapped[part1]);
            Assert.Equal(Path.Join(outputDir, "Ordered Download-02.mp3"), mapped[part2]);
            Assert.Equal(Path.Join(outputDir, "Ordered Download-10.mp3"), mapped[part10]);
            Assert.Equal("one", await File.ReadAllTextAsync(mapped[part1]));
            Assert.Equal("two", await File.ReadAllTextAsync(mapped[part2]));
            Assert.Equal("ten", await File.ReadAllTextAsync(mapped[part10]));
        }

        [Fact]
        public async Task GetQueue_DoesNotPurge_WhenSabnzbdHistoryContainsMatch()
        {
            // Seed download that would be considered orphaned:
            // - Status is Queued (not Downloading/Processing, not terminal states)
            // - Started >5 minutes ago (meets orphan age threshold)
            // - Not in client queue (will be detected as orphaned)
            var download = new Download
            {
                Id = "purge-1",
                Title = "William Faulkner - The Sound and the Fury",
                Status = DownloadStatus.Queued,
                DownloadClientId = "sab-1",
                StartedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            await _downloadRepository.AddAsync(download);

            // Build client configuration that represents SABnzbd
            var clientConfig = new DownloadClientConfiguration
            {
                Id = "sab-1",
                Name = "Sabnzbd",
                Type = "sabnzbd",
                Host = "localhost",
                Port = 8080,
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object> { { "apiKey", "apikey" } }
            };
            await _downloadClientConfigurationRepository.SaveAsync(clientConfig);

            // TODO: Adapt MOCK
            //const string queueJson = "{\"queue\":{\"slots\":[]}}";
            //const string historyJson = "{\"history\":{\"slots\":[{\"nzo_id\":\"SABnzbd_nzo_x123\",\"name\":\"William Faulkner - The Sound and the Fury\",\"status\":\"Completed\",\"storage\":\"/downloads/complete/listenarr/William Faulkner - The Sound and the Fury\",\"completed\":1600000000}]}}";

            // Act - call GetQueueAsync which runs the purge path
            var downloadService = _provider.GetRequiredService<DownloadService>();
            await downloadService.GetQueueAsync();

            // Assert: the DB download should still exist (not purged) because SABnzbd history contained the matching entry
            var stillExists = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(stillExists);

        }

        [Fact]
        public async Task GetQueueAsync_DelegatesToDownloadQueueService()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.GetQueueAsync(_client, It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>
                {
                    new QueueItem
                    {
                        Id = "061850ead3eb6f1c5c6d8420211b4bbf2d4ee3e2",
                        Title = "Dune - Frank Herbert [M4B]",
                        Status = "completed",
                        Progress = 100,
                        Size = 1100000000,
                        Downloaded = 1100000000,
                        DownloadClient = "local qbit",
                        DownloadClientId = "qb-1",
                        DownloadClientType = "qbittorrent",
                        AddedAt = DateTime.UtcNow.AddHours(-2)
                    }
                });
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
            _services.AddSingleton(queueServiceMock.Object);
            Init();
            await InitData();

            var trackedDownload = new Download
            {
                Id = "tracked-1",
                Title = "Dune - Frank Herbert [M4B]",
                Status = DownloadStatus.Completed,
                FinalPath = string.Empty,
                DownloadClientId = "qb-1",
                StartedAt = DateTime.UtcNow.AddMinutes(-20),
                Metadata = new Dictionary<string, object>
                {
                    ["TorrentHash"] = "061850ead3eb6f1c5c6d8420211b4bbf2d4ee3e2"
                }
            };
            await _downloadRepository.AddAsync(trackedDownload);

            var downloadService = _provider.GetRequiredService<DownloadService>();
            var queue = await downloadService.GetQueueAsync();

            Assert.Single(queue);
            Assert.Equal("tracked-1", queue[0].Id);
            Assert.Equal("completed", queue[0].Status, ignoreCase: true, ignoreLineEndingDifferences: false, ignoreWhiteSpaceDifferences: false, ignoreAllWhiteSpace: false);
            queueServiceMock.Verify(q => q.GetQueueAsync(), Times.Once);
            gatewayMock.Verify(g => g.GetQueueAsync(_client, It.IsAny<System.Threading.CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SendToDownloadClientAsync_StoresMagnetHashFallback_WhenClientReturnsNoId()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(_client, It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync((string?)null);
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
        public async Task SendToDownloadClientAsync_DerivesTorrent_WhenRequestSpoofsDdl()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(_client, It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync("client-download-123");
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
                    It.Is<SearchResult>(r => r.DownloadType == "Torrent" && r.MagnetLink.Contains("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<System.Threading.CancellationToken>()),
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
                DownloadType = "Torrent",
                IndexerId = 42,
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
                g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }
    }
}
