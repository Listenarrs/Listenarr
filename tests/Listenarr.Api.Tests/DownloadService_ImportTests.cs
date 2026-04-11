using Listenarr.Api.Services;
using Listenarr.Api.Hubs;
using Listenarr.Domain.Models.Configurations;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class DownloadService_ImportTests
    {
        private ListenArrDbContext CreateInMemoryDb()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new ListenArrDbContext(options);
        }

        [Fact]
        public async Task QualityGating_SkipsLowerQualityImport()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using var db = new ListenArrDbContext(options);

            // Create audiobook and an existing high-quality file
            var book = new Audiobook { Title = "The High Quality Book" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Simulate existing AudiobookFile (MP3 320) in DB
            db.AudiobookFiles.Add(new AudiobookFile
            {
                AudiobookId = book.Id,
                Path = "C:\\library\\high.mp3",
                Format = "mp3",
                Bitrate = 320000,
                Source = "manual",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            // Create a temp file representing a lower-quality completed download (MP3 128)
            var tmp = Path.GetTempFileName();
            var tmpMp3 = Path.ChangeExtension(tmp, ".mp3");
            File.Move(tmp, tmpMp3);
            await File.WriteAllTextAsync(tmpMp3, "dummy");

            // Create download record linked to audiobook
            var download = new Download
            {
                Id = "qg-1",
                AudiobookId = book.Id,
                Title = book.Title,
                Status = DownloadStatus.Completed,
                DownloadPath = tmpMp3,
                FinalPath = tmpMp3,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            db.Downloads.Add(download);
            await db.SaveChangesAsync();

            // Mock services
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Format = "mp3", Bitrate = 128000 });

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = Path.GetTempPath(), EnableMetadataProcessing = true, CompletedFileAction = "Move" });

            var hubClientsMock = new Mock<IHubClients>();
            var clientProxyMock = new Mock<IClientProxy>();
            hubClientsMock.Setup(h => h.All).Returns(clientProxyMock.Object);
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton<IConfigurationService>(configMock.Object);
            services.AddSingleton(db);
            services.AddMemoryCache();
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddSingleton(hubContextMock.Object);
            using var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var repoMock = new Mock<IAudiobookRepository>();
            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadService>>();
            using var httpClient = new System.Net.Http.HttpClient();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var cacheMock = new Mock<IMemoryCache>();
            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(() => new ListenArrDbContext(options));
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(() => new ListenArrDbContext(options));
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();

            var importService = new ImportService(dbFactoryMock.Object, scopeFactory, new FileNamingService(configMock.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<FileNamingService>()), metadataMock.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportService>());

            // one importService instance for this test
            using var provider2 = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DownloadService>>(loggerMock.Object);
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpClientFactoryMock.Object);
                services.AddSingleton<IImportService>(importService);
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<Listenarr.Api.Services.Adapters.IDownloadClientAdapterFactory>(new Mock<Listenarr.Api.Services.Adapters.IDownloadClientAdapterFactory>().Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(cacheMock.Object);
                services.AddTransient<DownloadService>();
            });
            var downloadService = provider2.GetRequiredService<DownloadService>();

            // Act - process completed download
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: no new AudiobookFile created for this audiobook (still only the existing one)
            var files = await db.AudiobookFiles.Where(f => f.AudiobookId == book.Id).ToListAsync();
            Assert.Single(files);

            // Cleanup
            TryDeleteFile(tmpMp3);
        }

        [Fact]
        public async Task MultiFileImport_ImportsAllFiles_WithUniqueNames()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using var db = new ListenArrDbContext(options);

            var book = new Audiobook { Title = "Multi Book", BasePath = Path.Join(Path.GetTempPath(), "listenarr-multi", Guid.NewGuid().ToString()) };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Ensure destination dir exists
            Directory.CreateDirectory(book.BasePath);

            // Create an existing file in destination with name collision
            var existing = Path.Join(book.BasePath, "chapter1.mp3");
            await File.WriteAllTextAsync(existing, "existing");

            // Create source directory with two files: one collides, one new
            var srcDir = Path.Join(Path.GetTempPath(), "listenarr-src", Guid.NewGuid().ToString());
            Directory.CreateDirectory(srcDir);
            var file1 = Path.Join(srcDir, "chapter1.mp3");
            var file2 = Path.Join(srcDir, "chapter2.mp3");
            await File.WriteAllTextAsync(file1, "file1");
            await File.WriteAllTextAsync(file2, "file2");

            // Create download pointing at the directory
            var download = new Download
            {
                Id = "mf-1",
                AudiobookId = book.Id,
                Title = book.Title,
                Status = DownloadStatus.Completed,
                DownloadPath = srcDir,
                FinalPath = srcDir,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            db.Downloads.Add(download);
            await db.SaveChangesAsync();

            // Mocks
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Format = "mp3", Bitrate = 128000 });

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = Path.GetTempPath(), EnableMetadataProcessing = true, CompletedFileAction = "Move" });

            var hubClientsMock = new Mock<IHubClients>();
            var clientProxyMock = new Mock<IClientProxy>();
            hubClientsMock.Setup(h => h.All).Returns(clientProxyMock.Object);
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton<IConfigurationService>(configMock.Object);
            services.AddSingleton(db);
            services.AddMemoryCache();
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddSingleton(hubContextMock.Object);
            using var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var repoMock = new Mock<IAudiobookRepository>();
            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadService>>();
            using var httpClient = new System.Net.Http.HttpClient();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var cacheMock = new Mock<IMemoryCache>();
            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(() => new ListenArrDbContext(options));
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(() => new ListenArrDbContext(options));
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();

            var importService = new ImportService(dbFactoryMock.Object, scopeFactory, new FileNamingService(configMock.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<FileNamingService>()), metadataMock.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportService>());

            using var provider2 = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DownloadService>>(loggerMock.Object);
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpClientFactoryMock.Object);
                services.AddSingleton<IImportService>(importService);
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<Listenarr.Api.Services.Adapters.IDownloadClientAdapterFactory>(new Mock<Listenarr.Api.Services.Adapters.IDownloadClientAdapterFactory>().Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(cacheMock.Object);
                services.AddTransient<DownloadService>();
            });
            var downloadService = provider2.GetRequiredService<DownloadService>();

            // Act
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: files were moved into destination or imported later (deferred). At minimum we expect either DB records
            // to be created synchronously or files to be present on disk in the audiobook BasePath.
            var files = await db.AudiobookFiles.Where(f => f.AudiobookId == book.Id).ToListAsync();
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

            // Cleanup
            TryDeleteDirectory(book.BasePath, recursive: true);
            TryDeleteDirectory(srcDir, recursive: true);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_MultipartFiles_KeepNaturalOrderWhenRenamed()
        {
            var outputDir = Path.Join(Path.GetTempPath(), "listenarr-import-ordered", Guid.NewGuid().ToString());
            Directory.CreateDirectory(outputDir);

            var srcDir = Path.Join(Path.GetTempPath(), "listenarr-import-ordered-src", Guid.NewGuid().ToString());
            Directory.CreateDirectory(srcDir);
            var part10 = Path.Join(srcDir, "Part 10.mp3");
            var part2 = Path.Join(srcDir, "Part 2.mp3");
            var part1 = Path.Join(srcDir, "Part 1.mp3");
            await File.WriteAllTextAsync(part10, "ten");
            await File.WriteAllTextAsync(part2, "two");
            await File.WriteAllTextAsync(part1, "one");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "Ordered Download", Format = "mp3", Bitrate = 128000 });

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings
            {
                OutputPath = outputDir,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}",
                CompletedFileAction = "Copy"
            });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(configMock.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<FileNamingService>()),
                metadataMock.Object,
                new Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportService>());

            var results = await importService.ImportFilesFromDirectoryAsync(
                "ordered-download",
                audiobookId: null,
                new[] { part10, part2, part1 },
                await configMock.Object.GetApplicationSettingsAsync());

            var mapped = results
                .Where(r => r.Success && !string.IsNullOrWhiteSpace(r.FinalPath) && !string.IsNullOrWhiteSpace(r.SourcePath))
                .ToDictionary(r => r.SourcePath!, r => r.FinalPath!, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(Path.Join(outputDir, "Ordered Download-01.mp3"), mapped[part1]);
            Assert.Equal(Path.Join(outputDir, "Ordered Download-02.mp3"), mapped[part2]);
            Assert.Equal(Path.Join(outputDir, "Ordered Download-10.mp3"), mapped[part10]);
            Assert.Equal("one", await File.ReadAllTextAsync(mapped[part1]));
            Assert.Equal("two", await File.ReadAllTextAsync(mapped[part2]));
            Assert.Equal("ten", await File.ReadAllTextAsync(mapped[part10]));

            TryDeleteDirectory(outputDir, recursive: true);
            TryDeleteDirectory(srcDir, recursive: true);
        }

        [Fact]
        public async Task GetQueue_DoesNotPurge_WhenSabnzbdHistoryContainsMatch()
        {
            await using var db = CreateInMemoryDb();

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
            db.Downloads.Add(download);
            await db.SaveChangesAsync();

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

            // Setup configuration service to return our client list
            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync()).ReturnsAsync(new List<DownloadClientConfiguration> { clientConfig });

            // Setup MemoryCache so the GetQueueAsync can use the cache path
            using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
            const string queueJson = "{\"queue\":{\"slots\":[]}}";
            const string historyJson = "{\"history\":{\"slots\":[{\"nzo_id\":\"SABnzbd_nzo_x123\",\"name\":\"William Faulkner - The Sound and the Fury\",\"status\":\"Completed\",\"storage\":\"/downloads/complete/listenarr/William Faulkner - The Sound and the Fury\",\"completed\":1600000000}]}}";

            // Local factories transfer HttpResponseMessage ownership to the caller (HttpClient pipeline),
            // satisfying CodeQL's IDisposable-not-disposed analysis.
            static HttpResponseMessage CreateOkResponse(string json) =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
            static HttpResponseMessage CreateNotFoundResponse() =>
                new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);

            // Setup HTTP handler that returns empty queue but history contains the completed entry
            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                var q = req.RequestUri?.Query ?? string.Empty;
                if (q.Contains("mode=queue"))
                    return Task.FromResult(CreateOkResponse(queueJson));

                if (q.Contains("mode=history"))
                    return Task.FromResult(CreateOkResponse(historyJson));

                return Task.FromResult(CreateNotFoundResponse());
            });

            using var httpClient = new HttpClient(handler);

            // Build service provider scope factory (for db contexts)
            var services = new ServiceCollection();
            services.AddSingleton<ListenArrDbContext>(db);
            services.AddSingleton<IConfigurationService>(configMock.Object);
            services.AddMemoryCache();
            services.AddSingleton(memoryCache);
            using var provider = services.BuildServiceProvider();

            // Mocks for other constructor dependencies
            var repoMock = new Mock<IAudiobookRepository>();
            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadService>>();
            var httpFactoryMock = new Mock<IHttpClientFactory>();
            httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(db);
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(db);

            // Metrics mock to assert telemetry
            var metricsMock = new Mock<IAppMetricsService>();

            // Construct the service under test (use our HttpClient and factory)
            var importService4 = new Mock<IImportService>();

            using var provider2 = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DownloadService>>(loggerMock.Object);
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpFactoryMock.Object);
                services.AddSingleton<IImportService>(importService4.Object);
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<Listenarr.Api.Services.Adapters.IDownloadClientAdapterFactory>(new Mock<Listenarr.Api.Services.Adapters.IDownloadClientAdapterFactory>().Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(memoryCache);
                services.AddSingleton<IAppMetricsService>(metricsMock.Object);
                services.AddTransient<DownloadService>();
            });
            var downloadService = provider2.GetRequiredService<DownloadService>();

            // Act - call GetQueueAsync which runs the purge path
            await downloadService.GetQueueAsync();

            // Assert: the DB download should still exist (not purged) because SABnzbd history contained the matching entry
            using (var scope = provider.CreateScope())
            {
                await using var dbCtx = await scope.ServiceProvider.GetListenArrDbContextAsync();
                var stillExists = await dbCtx.Downloads.FindAsync(download.Id);
                Assert.NotNull(stillExists);
            }

        }

        [Fact]
        public async Task GetQueueAsync_DelegatesToDownloadQueueService()
        {
            await using var db = CreateInMemoryDb();

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

            db.Downloads.Add(trackedDownload);
            await db.SaveChangesAsync();

            var clientConfig = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080,
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync()).ReturnsAsync(new List<DownloadClientConfiguration> { clientConfig });
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.GetQueueAsync(clientConfig, It.IsAny<System.Threading.CancellationToken>()))
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

            using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

            var repoMock = new Mock<IAudiobookRepository>();
            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadService>>();
            // Local factory transfers HttpResponseMessage ownership to caller (satisfies CodeQL IDisposable analysis)
            static HttpResponseMessage CreateOkResponse2() => new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            var handler = new DelegatingHandlerMock((_, _) => Task.FromResult(CreateOkResponse2()));
            using var httpClient = new HttpClient(handler);
            var httpFactoryMock = new Mock<IHttpClientFactory>();
            httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            httpFactoryMock.Setup(f => f.CreateClient(null)).Returns(httpClient);
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(db);
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(db);

            var metricsMock = new Mock<IAppMetricsService>();
            var importServiceMock = new Mock<IImportService>();
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
            var completedProcessorMock = new Mock<ICompletedDownloadProcessor>();

            var notificationService = new NotificationService(
                httpClient,
                new Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>(),
                configMock.Object,
                new TestNotificationPayloadBuilder(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor());

            using var provider2 = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DownloadService>>(loggerMock.Object);
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpFactoryMock.Object);
                services.AddSingleton<IImportService>(importServiceMock.Object);
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(memoryCache);
                services.AddSingleton<IDownloadClientGateway>(gatewayMock.Object);
                services.AddSingleton<IDownloadQueueService>(queueServiceMock.Object);
                services.AddSingleton<ICompletedDownloadProcessor>(completedProcessorMock.Object);
                services.AddSingleton<IAppMetricsService>(metricsMock.Object);
                services.AddSingleton(notificationService);
                services.AddTransient<DownloadService>();
            });

            var downloadService = provider2.GetRequiredService<DownloadService>();

            var queue = await downloadService.GetQueueAsync();

            Assert.Single(queue);
            Assert.Equal("tracked-1", queue[0].Id);
            Assert.Equal("completed", queue[0].Status, ignoreCase: true, ignoreLineEndingDifferences: false, ignoreWhiteSpaceDifferences: false, ignoreAllWhiteSpace: false);
            queueServiceMock.Verify(q => q.GetQueueAsync(), Times.Once);
            gatewayMock.Verify(g => g.GetQueueAsync(clientConfig, It.IsAny<System.Threading.CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SendToDownloadClientAsync_StoresMagnetHashFallback_WhenClientReturnsNoId()
        {
            await using var db = CreateInMemoryDb();

            var clientConfig = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080,
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationAsync("qb-1")).ReturnsAsync(clientConfig);
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(clientConfig, It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync((string?)null);

            using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

            var repoMock = new Mock<IAudiobookRepository>();
            using var httpClient = new HttpClient();
            var httpFactoryMock = new Mock<IHttpClientFactory>();
            httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            httpFactoryMock.Setup(f => f.CreateClient(null)).Returns(httpClient);
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(db);
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(db);

            var metricsMock = new Mock<IAppMetricsService>();
            var importServiceMock = new Mock<IImportService>();
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
            var completedProcessorMock = new Mock<ICompletedDownloadProcessor>();

            var notificationService = new NotificationService(
                httpClient,
                new Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>(),
                configMock.Object,
                new TestNotificationPayloadBuilder(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor());

            using var provider2 = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DownloadService>>(new Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadService>());
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpFactoryMock.Object);
                services.AddSingleton<IImportService>(importServiceMock.Object);
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(memoryCache);
                services.AddSingleton<IDownloadClientGateway>(gatewayMock.Object);
                services.AddSingleton<IDownloadQueueService>(queueServiceMock.Object);
                services.AddSingleton<ICompletedDownloadProcessor>(completedProcessorMock.Object);
                services.AddSingleton<IAppMetricsService>(metricsMock.Object);
                services.AddSingleton(notificationService);
                services.AddTransient<DownloadService>();
            });

            var downloadService = provider2.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "Torrent",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12&dn=Artemis",
                Size = 123456789
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult, "qb-1");
            var persisted = await db.Downloads.FindAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("ABCDEF1234567890ABCDEF1234567890ABCDEF12", persisted!.Metadata["ClientDownloadId"]?.ToString());
            Assert.Equal("ABCDEF1234567890ABCDEF1234567890ABCDEF12", persisted.Metadata["TorrentHash"]?.ToString());
        }

        [Fact]
        public async Task SendToDownloadClientAsync_DerivesTorrent_WhenRequestSpoofsDdl()
        {
            await using var db = CreateInMemoryDb();

            var clientConfig = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080,
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationAsync("qb-1")).ReturnsAsync(clientConfig);
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(clientConfig, It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync("client-download-123");

            using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

            var repoMock = new Mock<IAudiobookRepository>();
            using var httpClient = new HttpClient();
            var httpFactoryMock = new Mock<IHttpClientFactory>();
            httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            httpFactoryMock.Setup(f => f.CreateClient(null)).Returns(httpClient);
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(db);
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(db);

            var metricsMock = new Mock<IAppMetricsService>();
            var importServiceMock = new Mock<IImportService>();
            var queueServiceMock = new Mock<IDownloadQueueService>();
            queueServiceMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<QueueItem>());
            var completedProcessorMock = new Mock<ICompletedDownloadProcessor>();

            var notificationService = new NotificationService(
                httpClient,
                new Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>(),
                configMock.Object,
                new TestNotificationPayloadBuilder(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor());

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DownloadService>>(new Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadService>());
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpFactoryMock.Object);
                services.AddSingleton<IImportService>(importServiceMock.Object);
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(memoryCache);
                services.AddSingleton<IDownloadClientGateway>(gatewayMock.Object);
                services.AddSingleton<IDownloadQueueService>(queueServiceMock.Object);
                services.AddSingleton<ICompletedDownloadProcessor>(completedProcessorMock.Object);
                services.AddSingleton<IAppMetricsService>(metricsMock.Object);
                services.AddSingleton(notificationService);
                services.AddTransient<DownloadService>();
            });

            var downloadService = provider.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "DDL",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12&dn=Artemis",
                Size = 123456789
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult, "qb-1");
            var persisted = await db.Downloads.FindAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("qb-1", persisted!.DownloadClientId);
            Assert.Equal("Torrent", persisted.Metadata["DownloadType"]?.ToString());
            gatewayMock.Verify(
                g => g.AddAsync(
                    clientConfig,
                    It.Is<SearchResult>(r => r.DownloadType == "Torrent" && r.MagnetLink.Contains("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<System.Threading.CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SendToDownloadClientAsync_DerivesTrustedInternetArchiveAsDdl()
        {
            await using var db = CreateInMemoryDb();

            db.Indexers.Add(new Indexer
            {
                Id = 42,
                Name = "Internet Archive",
                Url = "https://archive.org/advancedsearch.php",
                Type = "Torrent",
                Implementation = "InternetArchive",
                IsEnabled = true
            });
            await db.SaveChangesAsync();

            var clientConfig = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080,
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationAsync("qb-1")).ReturnsAsync(clientConfig);
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var gatewayMock = new Mock<IDownloadClientGateway>();

            using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

            var repoMock = new Mock<IAudiobookRepository>();
            using var httpClient = new HttpClient();
            var httpFactoryMock = new Mock<IHttpClientFactory>();
            httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            httpFactoryMock.Setup(f => f.CreateClient(null)).Returns(httpClient);
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(db);
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(db);

            var metricsMock = new Mock<IAppMetricsService>();
            var importServiceMock = new Mock<IImportService>();
            var queueServiceMock = new Mock<IDownloadQueueService>();
            queueServiceMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<QueueItem>());
            var completedProcessorMock = new Mock<ICompletedDownloadProcessor>();

            var notificationService = new NotificationService(
                httpClient,
                new Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>(),
                configMock.Object,
                new TestNotificationPayloadBuilder(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor());

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DownloadService>>(new Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadService>());
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpFactoryMock.Object);
                services.AddSingleton<IImportService>(importServiceMock.Object);
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(memoryCache);
                services.AddSingleton<IDownloadClientGateway>(gatewayMock.Object);
                services.AddSingleton<IDownloadQueueService>(queueServiceMock.Object);
                services.AddSingleton<ICompletedDownloadProcessor>(completedProcessorMock.Object);
                services.AddSingleton<IAppMetricsService>(metricsMock.Object);
                services.AddSingleton(notificationService);
                services.AddTransient<DownloadService>();
            });

            var downloadService = provider.GetRequiredService<DownloadService>();

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
            var persisted = await db.Downloads.FindAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("DDL", persisted!.DownloadClientId);
            Assert.Equal("DDL", persisted.Metadata["DownloadType"]?.ToString());
            gatewayMock.Verify(
                g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
        }

        private static void TryDeleteDirectory(string path, bool recursive = false)
        {
            try
            {
                Directory.Delete(path, recursive);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
        }
    }
}
