using Listenarr.Tests.Common;
using Microsoft.AspNetCore.SignalR;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "WorkerProcessorBoundaryTests")]
    [Trait("Category", "BackgroundWorkers")]
    public class WorkerProcessorBoundaryTests : BaseTests
    {
        [Fact]
        public async Task AuthorMonitoringProcessor_RunCycle_DelegatesToDueAuthorSync()
        {
            var monitoringService = new Mock<IAuthorMonitoringService>();
            monitoringService
                .Setup(s => s.SyncDueAuthorsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(3);

            _services.AddSingleton(monitoringService.Object);
            Init();

            var processor = new AuthorMonitoringProcessor(
                _provider.GetRequiredService<ILogger<AuthorMonitoringProcessor>>(),
                _provider.GetRequiredService<IServiceScopeFactory>());

            await processor.RunCycleAsync(CancellationToken.None);

            monitoringService.Verify(s => s.SyncDueAuthorsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SeriesMonitoringProcessor_RunCycle_DelegatesToDueSeriesSync()
        {
            var monitoringService = new Mock<ISeriesMonitoringService>();
            monitoringService
                .Setup(s => s.SyncDueSeriesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(2);

            _services.AddSingleton(monitoringService.Object);
            Init();

            var processor = new SeriesMonitoringProcessor(
                _provider.GetRequiredService<ILogger<SeriesMonitoringProcessor>>(),
                _provider.GetRequiredService<IServiceScopeFactory>());

            await processor.RunCycleAsync(CancellationToken.None);

            monitoringService.Verify(s => s.SyncDueSeriesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ImageCacheCleanupProcessor_RunCycle_IsReplaySafe()
        {
            var imageCacheService = new Mock<IImageCacheService>();
            imageCacheService
                .Setup(s => s.ClearTempCacheAsync())
                .Returns(Task.CompletedTask);

            _services.AddSingleton(imageCacheService.Object);
            Init();

            var processor = new ImageCacheCleanupProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<ImageCacheCleanupProcessor>>());

            await processor.RunCycleAsync(CancellationToken.None);
            await processor.RunCycleAsync(CancellationToken.None);

            imageCacheService.Verify(s => s.ClearTempCacheAsync(), Times.Exactly(2));
        }

        [Fact]
        public async Task MetadataRescanProcessor_NonAudioFile_RemovesFileRecord()
        {
            var file = new AudiobookFile
            {
                Id = 42,
                AudiobookId = 7,
                Path = "not-a-book.txt"
            };
            var fileRepository = new Mock<IAudiobookFileRepository>();
            fileRepository
                .Setup(r => r.GetMissingMetadataAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync([file]);
            fileRepository
                .Setup(r => r.GetByIdAsync(file.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(file);
            fileRepository
                .Setup(r => r.DeletePhysicalGenerationAsync(
                    file.Id,
                    file.AudiobookId,
                    file.Path,
                    file.PhysicalObjectIdentity,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var audiobookRepository = new Mock<IAudiobookRepository>();
            audiobookRepository
                .Setup(r => r.GetByIdAsync(file.AudiobookId))
                .ReturnsAsync((Audiobook?)null);
            var metadataService = new Mock<IMetadataService>();

            var services = new ServiceCollection();
            services.AddSingleton(fileRepository.Object);
            services.AddSingleton(audiobookRepository.Object);
            services.AddSingleton(metadataService.Object);
            using var provider = services.BuildServiceProvider();

            using var operationCoordinator = new AudiobookOperationCoordinator();
            var moveQueueService = new Mock<IMoveQueueService>();
            moveQueueService.Setup(service => service.GetRecoveryStateForAudiobookAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(MoveRecoveryState.None);
            moveQueueService.Setup(service => service.EnsureFilesystemMutationAllowedAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var processor = new MetadataRescanProcessor(
                provider.GetRequiredService<IServiceScopeFactory>(),
                operationCoordinator,
                moveQueueService.Object,
                Mock.Of<ILogger<MetadataRescanProcessor>>());

            await processor.RunCycleAsync(CancellationToken.None);

            fileRepository.Verify(r => r.DeletePhysicalGenerationAsync(
                file.Id,
                file.AudiobookId,
                file.Path,
                file.PhysicalObjectIdentity,
                It.IsAny<CancellationToken>()), Times.Once);
            metadataService.Verify(s => s.ExtractFileMetadataAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task FfmpegInstallProcessor_InstalledPath_BroadcastsInstalled()
        {
            var ffmpegService = new Mock<IFfmpegService>();
            ffmpegService
                .Setup(s => s.EnsureFfprobeInstalledAsync())
                .ReturnsAsync("C:\\ffmpeg\\ffprobe.exe");
            var clientProxy = CreateHubProxy<DownloadHub>(out var hubContext);

            var processor = new FfmpegInstallProcessor(
                ffmpegService.Object,
                hubContext.Object,
                _provider.GetRequiredService<ILogger<FfmpegInstallProcessor>>());

            await processor.EnsureInstalledAsync(CancellationToken.None);

            clientProxy.Verify(
                p => p.SendCoreAsync(
                    "FfmpegInstallStatus",
                    It.Is<object?[]>(args => args.Length == 1 && args[0]!.ToString()!.Contains("Installed")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task FfmpegInstallProcessor_MissingPath_BroadcastsNotInstalled()
        {
            var ffmpegService = new Mock<IFfmpegService>();
            ffmpegService
                .Setup(s => s.EnsureFfprobeInstalledAsync())
                .ReturnsAsync((string?)null);
            var clientProxy = CreateHubProxy<DownloadHub>(out var hubContext);

            var processor = new FfmpegInstallProcessor(
                ffmpegService.Object,
                hubContext.Object,
                _provider.GetRequiredService<ILogger<FfmpegInstallProcessor>>());

            await processor.EnsureInstalledAsync(CancellationToken.None);

            clientProxy.Verify(
                p => p.SendCoreAsync(
                    "FfmpegInstallStatus",
                    It.Is<object?[]>(args => args.Length == 1 && args[0]!.ToString()!.Contains("NotInstalled")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task QueueMonitorProcessor_BroadcastsOnlyWhenSnapshotChanges()
        {
            var snapshot = new QueueSnapshot
            {
                Items =
                [
                    new QueueItem { Id = "q1", Status = "downloading", Progress = 10 }
                ]
            };
            var queueService = new Mock<IDownloadQueueService>();
            queueService
                .Setup(s => s.GetQueueSnapshotAsync())
                .ReturnsAsync(snapshot);
            var clientProxy = CreateHubProxy<DownloadHub>(out var hubContext);

            _services.AddSingleton(queueService.Object);
            Init();

            var processor = new QueueMonitorProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                hubContext.Object,
                _provider.GetRequiredService<ILogger<QueueMonitorProcessor>>());

            var firstInterval = await processor.RunCycleAsync(CancellationToken.None);
            var secondInterval = await processor.RunCycleAsync(CancellationToken.None);

            Assert.Equal(TimeSpan.FromSeconds(5), firstInterval);
            Assert.Equal(TimeSpan.FromSeconds(5), secondInterval);
            clientProxy.Verify(
                p => p.SendCoreAsync("QueueUpdate", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task AutomaticSearchProcessor_NoEligibleBooks_DoesNotSearchOrDownload()
        {
            var audiobookRepository = new Mock<IAudiobookRepository>();
            audiobookRepository
                .Setup(r => r.GetMonitoredAudiobooksForSearchAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Audiobook>());
            var searchService = new Mock<ISearchService>();
            var downloadService = new Mock<IDownloadService>();

            _services.AddSingleton(audiobookRepository.Object);
            _services.AddSingleton(searchService.Object);
            _services.AddSingleton(downloadService.Object);
            Init();

            var processor = new AutomaticSearchProcessor(
                _provider.GetRequiredService<ILogger<AutomaticSearchProcessor>>(),
                _provider.GetRequiredService<IServiceScopeFactory>());

            await processor.RunCycleAsync(CancellationToken.None);

            searchService.Verify(
                s => s.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>()),
                Times.Never);
            downloadService.Verify(
                s => s.StartDownloadAsync(It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()),
                Times.Never);
        }

        [Fact]
        public async Task UnmatchedScanProcessor_ProcessJob_CompletesWithUntrackedAudio()
        {
            var root = FileService.GetTempDirectory("unmatched-processor-root");
            var file = await FileService.GetFileAsync(root, "Untracked Book.m4b", "audio");
            await AddAuthorizedRootAsync(root);
            await CreateApplicationSettings();
            var queue = new UnmatchedScanQueueService(
                _provider.GetRequiredService<ILogger<UnmatchedScanQueueService>>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            CreateHubProxy<SettingsHub>(out var hubContext);

            var processor = new UnmatchedScanProcessor(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<UnmatchedScanProcessor>>(),
                hubContext.Object,
                _provider.GetRequiredService<IFfmpegService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            await queue.EnqueueAsync(root);
            Assert.True(queue.Reader.TryRead(out var job));

            await processor.ProcessJobAsync(job, CancellationToken.None);

            Assert.True(queue.TryGetJob(job.Id, out var updatedJob));
            Assert.Equal("Completed", updatedJob!.Status);
            var result = Assert.Single(updatedJob.Results!);
            Assert.Equal(file, result.FullPath);
            Assert.Equal("M4B", result.Format);
        }

        [WindowsFact]
        public async Task UnmatchedScanProcessor_ForeignRootSyntax_DoesNotScanWindowsAlias()
        {
            var root = FileService.GetWindowsRootRelativeTempDirectory(
                "unmatched-processor-foreign-root");
            var file = await FileService.GetFileAsync(root, "Foreign Alias Book.m4b", "audio");
            await AddAuthorizedRootAsync(root);
            await CreateApplicationSettings();
            var foreignRoot = TempFileService
                .GetWindowsRootRelativeForeignAlias(root);
            var queue = new UnmatchedScanQueueService(
                _provider.GetRequiredService<ILogger<UnmatchedScanQueueService>>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            CreateHubProxy<SettingsHub>(out var hubContext);
            var processor = new UnmatchedScanProcessor(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<UnmatchedScanProcessor>>(),
                hubContext.Object,
                _provider.GetRequiredService<IFfmpegService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            await queue.EnqueueAsync(foreignRoot);
            Assert.True(queue.Reader.TryRead(out var job));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                processor.ProcessJobAsync(job, CancellationToken.None));

            Assert.True(File.Exists(file));
            Assert.True(queue.TryGetJob(job.Id, out var current));
            Assert.Equal("Processing", current!.Status);
        }

        [Fact]
        public async Task UnmatchedScanProcessor_AuthorizedRootMissingBeforeProcessing_FailsClosed()
        {
            var missingRoot = FileService.GetTempDirectory("missing-root");
            await AddAuthorizedRootAsync(missingRoot);
            await CreateApplicationSettings();
            var queue = new UnmatchedScanQueueService(
                _provider.GetRequiredService<ILogger<UnmatchedScanQueueService>>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            var clientProxy = CreateHubProxy<SettingsHub>(out var hubContext);

            var processor = new UnmatchedScanProcessor(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<UnmatchedScanProcessor>>(),
                hubContext.Object,
                _provider.GetRequiredService<IFfmpegService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            await queue.EnqueueAsync(missingRoot);
            Assert.True(queue.Reader.TryRead(out var job));
            Directory.Delete(missingRoot, recursive: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                processor.ProcessJobAsync(job, CancellationToken.None));

            Assert.True(queue.TryGetJob(job.Id, out var updatedJob));
            Assert.Equal("Processing", updatedJob!.Status);
            clientProxy.Verify(
                p => p.SendCoreAsync("UnmatchedScanComplete", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task UnmatchedScanProcessor_AuthorizedRootReplacedBeforeProcessing_FailsClosed()
        {
            var parent = FileService.GetTempDirectory("unmatched-root-replacement-parent");
            var root = Path.Join(parent, "library");
            var displaced = Path.Join(parent, "library-original");
            Directory.CreateDirectory(root);
            await AddAuthorizedRootAsync(root);
            await CreateApplicationSettings();
            var queue = new UnmatchedScanQueueService(
                _provider.GetRequiredService<ILogger<UnmatchedScanQueueService>>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            CreateHubProxy<SettingsHub>(out var hubContext);
            var processor = new UnmatchedScanProcessor(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<UnmatchedScanProcessor>>(),
                hubContext.Object,
                _provider.GetRequiredService<IFfmpegService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            await queue.EnqueueAsync(root);
            Assert.True(queue.Reader.TryRead(out var job));

            Directory.Move(root, displaced);
            Directory.CreateDirectory(root);
            var replacementFile = await FileService.GetFileAsync(
                root,
                "Replacement Book.m4b",
                "replacement audio");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                processor.ProcessJobAsync(job, CancellationToken.None));

            Assert.True(File.Exists(replacementFile));
            Assert.True(queue.TryGetJob(job.Id, out var updatedJob));
            Assert.Equal("Processing", updatedJob!.Status);
            Assert.Null(updatedJob.Results);
        }

        private static Mock<IClientProxy> CreateHubProxy<THub>(out Mock<IHubContext<THub>> hubContext)
            where THub : Hub
        {
            var clientProxy = new Mock<IClientProxy>();
            clientProxy
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.All).Returns(clientProxy.Object);

            hubContext = new Mock<IHubContext<THub>>();
            hubContext.Setup(h => h.Clients).Returns(hubClients.Object);
            return clientProxy;
        }
    }
}
