using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Hubs;
using Listenarr.Api.Repositories;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class EndToEndDownloadImportFlowTests
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
            var dbName = Guid.NewGuid().ToString("N");
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options;

            var outputRoot = Path.Join(Path.GetTempPath(), "listenarr-e2e-out", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputRoot);

            var sourceRoot = Path.Join(Path.GetTempPath(), "listenarr-e2e-src", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);

            var sourcePath = isMultiFile
                ? await CreateMultiFileSourceAsync(sourceRoot)
                : await CreateSingleFileSourceAsync(sourceRoot);

            await using (var seed = new ListenArrDbContext(options))
            {
                var audiobook = new Audiobook
                {
                    Title = $"E2E {downloadType} {(isMultiFile ? "Multi" : "Single")}",
                    Authors = new List<string> { "Test Author" },
                    BasePath = Path.Join(outputRoot, "library", Guid.NewGuid().ToString("N"))
                };
                seed.Audiobooks.Add(audiobook);
                await seed.SaveChangesAsync();
            }

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContext())
                .Returns(() => new ListenArrDbContext(options));
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

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
                    : new Dictionary<string, object>()
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

            var configMock = new Mock<IConfigurationService>();
            configMock
                .Setup(c => c.GetDownloadClientConfigurationAsync(downloadClient.Id))
                .ReturnsAsync(downloadClient);
            configMock
                .Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { downloadClient });
            configMock
                .Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(settings);

            var metadataMock = new Mock<IMetadataService>();
            metadataMock
                .Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync((string path) => new AudioMetadata
                {
                    Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    Bitrate = 128000,
                    Duration = TimeSpan.FromMinutes(5)
                });

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(downloadClient, It.IsAny<SearchResult>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync($"{downloadType}-client-item-1");
            gatewayMock
                .Setup(g => g.GetQueueAsync(downloadClient, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>());

            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(new Mock<IHubClients>().Object);

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddScoped(_ => new ListenArrDbContext(options));
            serviceCollection.AddSingleton<IConfigurationService>(configMock.Object);
            serviceCollection.AddSingleton<IMetadataService>(metadataMock.Object);
            using var provider = serviceCollection.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var fileNamingService = new FileNamingService(configMock.Object, NullLogger<FileNamingService>.Instance);
            var importService = new ImportService(dbFactoryMock.Object, scopeFactory, fileNamingService, metadataMock.Object, NullLogger<ImportService>.Instance);

            var downloadRepository = new EfDownloadRepository(dbFactoryMock.Object, NullLogger<EfDownloadRepository>.Instance);
            var fileFinalizer = new FileFinalizer(importService, downloadRepository, scopeFactory, NullLogger<FileFinalizer>.Instance);
            var archiveExtractor = new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance);

            var queueServiceMock = new Mock<IDownloadQueueService>();
            queueServiceMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<QueueItem>());

            var completedProcessor = new CompletedDownloadProcessor(
                downloadRepository,
                fileFinalizer,
                configMock.Object,
                scopeFactory,
                importService,
                archiveExtractor,
                queueServiceMock.Object,
                hubContextMock.Object,
                NullLogger<CompletedDownloadProcessor>.Instance);

            var httpFactoryMock = new Mock<IHttpClientFactory>();
            using var factoryHttpClient = new HttpClient();
            httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(factoryHttpClient);
            httpFactoryMock.Setup(f => f.CreateClient((string?)null)).Returns(factoryHttpClient);

            var pathMappingMock = new Mock<IRemotePathMappingService>();
            pathMappingMock
                .Setup(p => p.TranslatePathAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string _, string path) => path);

            using var notificationHttpClient = new HttpClient();
            var notificationService = new NotificationService(
                notificationHttpClient,
                NullLogger<NotificationService>.Instance,
                configMock.Object,
                new TestNotificationPayloadBuilder(),
                new HttpContextAccessor());

            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var downloadService = new DownloadService(
                hubContextMock.Object,
                Mock.Of<IAudiobookRepository>(),
                configMock.Object,
                dbFactoryMock.Object,
                NullLogger<DownloadService>.Instance,
                httpFactoryMock.Object,
                scopeFactory,
                pathMappingMock.Object,
                importService,
                Mock.Of<ISearchService>(),
                gatewayMock.Object,
                memoryCache,
                queueServiceMock.Object,
                completedProcessor,
                new NoopAppMetricsService(),
                notificationService,
                new NoopHubBroadcaster());

            int audiobookId;
            await using (var verifyCtx = new ListenArrDbContext(options))
            {
                audiobookId = await verifyCtx.Audiobooks.Select(a => a.Id).SingleAsync();
            }

            var searchResult = BuildIndexerResult(downloadType, isMultiFile);

            var createdDownloadId = await downloadService.StartDownloadAsync(searchResult, downloadClient.Id, audiobookId);
            await downloadService.ProcessCompletedDownloadAsync(createdDownloadId, sourcePath);

            await using var assertCtx = new ListenArrDbContext(options);
            var download = await assertCtx.Downloads.FindAsync(createdDownloadId);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Moved, download!.Status);

            var persistedAudiobook = await assertCtx.Audiobooks.FindAsync(audiobookId);
            Assert.NotNull(persistedAudiobook);

            var importedFiles = await assertCtx.AudiobookFiles.Where(f => f.AudiobookId == audiobookId).ToListAsync();

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

        private static async Task<string> CreateSingleFileSourceAsync(string sourceRoot)
        {
            var file = Path.Join(sourceRoot, "single-book.m4b");
            await File.WriteAllTextAsync(file, "single-file-content");
            return file;
        }

        private static async Task<string> CreateMultiFileSourceAsync(string sourceRoot)
        {
            var dir = Path.Join(sourceRoot, "multi-book");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Join(dir, "part1.mp3"), "part-1");
            await File.WriteAllTextAsync(Path.Join(dir, "part2.mp3"), "part-2");
            return dir;
        }
    }
}
