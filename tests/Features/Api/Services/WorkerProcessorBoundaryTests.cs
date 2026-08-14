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

        [Fact]
        public async Task UnmatchedScanProcessor_PinnedPathOnly_DoesNotReopenFilesForMetadataEnrichment()
        {
            var root = FileService.GetTempDirectory("unmatched-processor-limited-root");
            var bookDirectory = Path.Join(root, "Author", "2026 - Limited Book");
            Directory.CreateDirectory(bookDirectory);
            var file = await FileService.GetFileAsync(
                bookDirectory,
                "Limited Book.m4b",
                "audio");
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "desc.txt"),
                "filesystem-sidecar-description");
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "reader.txt"),
                "filesystem-sidecar-narrator");
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "cover.jpg"),
                "filesystem-cover");
            var expectedSize = new FileInfo(file).Length;
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var pathIdentity = new PathIdentitySnapshot(
                semantics.Syntax,
                semantics.CaseSensitivity,
                semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivityMode.Sensitive
                    : FileSystemCaseSensitivityMode.Insensitive,
                root);
            var authorization = new Mock<IScanPathAuthorizationService>(MockBehavior.Strict);
            authorization
                .Setup(service => service.AuthorizeAsync(
                    root,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(ScanPathAuthorizationResult.Authorized(
                    root,
                    pathIdentity,
                    ScanPathPhysicalIdentity.PinnedPathOnly()));
            _services.AddSingleton(authorization.Object);
            Init();
            await CreateApplicationSettings();

            var queue = new UnmatchedScanQueueService(
                _provider.GetRequiredService<ILogger<UnmatchedScanQueueService>>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            CreateHubProxy<SettingsHub>(out var hubContext);
            var ffmpeg = new Mock<IFfmpegService>(MockBehavior.Strict);
            var processor = new UnmatchedScanProcessor(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<UnmatchedScanProcessor>>(),
                hubContext.Object,
                ffmpeg.Object,
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            await queue.EnqueueAsync(root);
            Assert.True(queue.Reader.TryRead(out var job));

            await processor.ProcessJobAsync(job, CancellationToken.None);

            Assert.True(queue.TryGetJob(job.Id, out var updatedJob));
            Assert.Equal("Completed", updatedJob!.Status);
            var result = Assert.Single(updatedJob.Results!);
            Assert.Equal(file, result.FullPath);
            Assert.Equal(expectedSize, result.Size);
            Assert.Equal("Limited Book", result.Title);
            Assert.Equal("Author", result.Author);
            Assert.Null(result.Description);
            Assert.Null(result.Narrator);
            Assert.Null(result.CoverPath);
            ffmpeg.VerifyNoOtherCalls();
            authorization.VerifyAll();
        }

        [Fact]
        public async Task UnmatchedScanProcessor_PinnedFolderMetadata_ReadsAuthorizedSidecarsAndCover()
        {
            var root = FileService.GetTempDirectory("unmatched-pinned-folder-metadata-positive");
            var bookDirectory = Path.Join(root, "Author", "2026 - Pinned Book");
            Directory.CreateDirectory(bookDirectory);
            var audioPath = await FileService.GetFileAsync(
                bookDirectory,
                "Pinned Book.m4b",
                "audio");
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "desc.txt"),
                "authorized description");
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "reader.txt"),
                "Authorized Narrator");
            var coverPath = await FileService.GetFileAsync(
                bookDirectory,
                "cover.jpg",
                "cover");
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            string directoryIdentity;
            string fileIdentity;
            using (var folder = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                bookDirectory,
                createMissing: false))
            {
                directoryIdentity = folder.GetDirectoryObjectIdentity();
                using var audio = folder.OpenExistingFile(
                    Path.GetFileName(audioPath),
                    requireDeleteAccess: false);
                fileIdentity = audio.GetObjectIdentity();
            }

            var canonicalBookDirectory = FileSystemPathIdentity.Canonicalize(
                bookDirectory,
                semantics.Syntax);
            var canonicalAudioPath = FileSystemPathIdentity.Canonicalize(
                audioPath,
                semantics.Syntax);
            var enumeration = new ScanFileDiscovery.EnumerationResult(
                [canonicalAudioPath],
                [canonicalBookDirectory],
                new Dictionary<string, string>(semantics.Comparer)
                {
                    [canonicalBookDirectory] = directoryIdentity
                },
                new Dictionary<string, string>(semantics.Comparer)
                {
                    [canonicalAudioPath] = fileIdentity
                },
                new Dictionary<string, long>(semantics.Comparer)
                {
                    [canonicalAudioPath] = 5
                },
                []);
            var parsed = new PathParsedMetadata();

            await UnmatchedScanProcessor.ApplyPinnedFolderMetadataAsync(
                parsed,
                bookDirectory,
                enumeration,
                semantics,
                CancellationToken.None);

            Assert.Equal("authorized description", parsed.Description);
            Assert.Equal("Authorized Narrator", parsed.Narrator);
            Assert.Equal(coverPath, parsed.CoverPath);
        }

        [Fact]
        public async Task UnmatchedScanProcessor_PinnedFolderMetadata_NestedDiscUsesMatchedBookDirectory()
        {
            var root = FileService.GetTempDirectory("unmatched-pinned-folder-metadata-nested");
            var bookDirectory = Path.Join(
                root,
                "Author",
                "Series",
                "2026 - Nested Book");
            var discDirectory = Path.Join(bookDirectory, "CD1");
            Directory.CreateDirectory(discDirectory);
            var audioPath = await FileService.GetFileAsync(
                discDirectory,
                "01.m4b",
                "audio");
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "desc.txt"),
                "book-level description");
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "reader.txt"),
                "Book Level Narrator");
            var coverPath = await FileService.GetFileAsync(
                bookDirectory,
                "cover.jpg",
                "cover");
            var semantics = FileSystemPathSemantics.CurrentHostDefault;

            string bookDirectoryIdentity;
            string discDirectoryIdentity;
            string fileIdentity;
            using (var book = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                bookDirectory,
                createMissing: false))
            using (var disc = book.OpenExistingChild("CD1"))
            using (var audio = disc.OpenExistingFile(
                Path.GetFileName(audioPath),
                requireDeleteAccess: false))
            {
                bookDirectoryIdentity = book.GetDirectoryObjectIdentity();
                discDirectoryIdentity = disc.GetDirectoryObjectIdentity();
                fileIdentity = audio.GetObjectIdentity();
            }

            var canonicalBookDirectory = FileSystemPathIdentity.Canonicalize(
                bookDirectory,
                semantics.Syntax);
            var canonicalDiscDirectory = FileSystemPathIdentity.Canonicalize(
                discDirectory,
                semantics.Syntax);
            var canonicalAudioPath = FileSystemPathIdentity.Canonicalize(
                audioPath,
                semantics.Syntax);
            var enumeration = new ScanFileDiscovery.EnumerationResult(
                [canonicalAudioPath],
                [canonicalBookDirectory, canonicalDiscDirectory],
                new Dictionary<string, string>(semantics.Comparer)
                {
                    [canonicalBookDirectory] = bookDirectoryIdentity,
                    [canonicalDiscDirectory] = discDirectoryIdentity
                },
                new Dictionary<string, string>(semantics.Comparer)
                {
                    [canonicalAudioPath] = fileIdentity
                },
                new Dictionary<string, long>(semantics.Comparer)
                {
                    [canonicalAudioPath] = 5
                },
                []);
            var parsed = PathMetadataParser.ParsePathOnly(
                audioPath,
                root,
                semantics);

            await UnmatchedScanProcessor.ApplyPinnedFolderMetadataAsync(
                parsed,
                parsed.BookFolderPath ?? string.Empty,
                enumeration,
                semantics,
                CancellationToken.None);

            Assert.Equal(bookDirectory, parsed.BookFolderPath);
            Assert.Equal("book-level description", parsed.Description);
            Assert.Equal("Book Level Narrator", parsed.Narrator);
            Assert.Equal(coverPath, parsed.CoverPath);
        }

        [Fact]
        public async Task UnmatchedScanProcessor_PinnedFolderMetadata_ReplacementGenerationIsRejected()
        {
            var root = FileService.GetTempDirectory("unmatched-pinned-folder-metadata");
            var bookDirectory = Path.Join(root, "Author", "2026 - Pinned Book");
            Directory.CreateDirectory(bookDirectory);
            var audioPath = await FileService.GetFileAsync(
                bookDirectory,
                "Pinned Book.m4b",
                "audio");
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "desc.txt"),
                "original description");
            var displaced = Path.Join(root, "original-book-directory");
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            string directoryIdentity;
            string fileIdentity;
            using (var folder = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                bookDirectory,
                createMissing: false))
            {
                directoryIdentity = folder.GetDirectoryObjectIdentity();
                using var audio = folder.OpenExistingFile(
                    Path.GetFileName(audioPath),
                    requireDeleteAccess: false);
                fileIdentity = audio.GetObjectIdentity();
            }

            var canonicalBookDirectory = FileSystemPathIdentity.Canonicalize(
                bookDirectory,
                semantics.Syntax);
            var canonicalAudioPath = FileSystemPathIdentity.Canonicalize(
                audioPath,
                semantics.Syntax);
            var enumeration = new ScanFileDiscovery.EnumerationResult(
                [canonicalAudioPath],
                [canonicalBookDirectory],
                new Dictionary<string, string>(semantics.Comparer)
                {
                    [canonicalBookDirectory] = directoryIdentity
                },
                new Dictionary<string, string>(semantics.Comparer)
                {
                    [canonicalAudioPath] = fileIdentity
                },
                new Dictionary<string, long>(semantics.Comparer)
                {
                    [canonicalAudioPath] = 5
                },
                []);

            Directory.Move(bookDirectory, displaced);
            Directory.CreateDirectory(bookDirectory);
            await File.WriteAllTextAsync(
                Path.Join(bookDirectory, "desc.txt"),
                "replacement description");
            var parsed = new PathParsedMetadata();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                UnmatchedScanProcessor.ApplyPinnedFolderMetadataAsync(
                    parsed,
                    bookDirectory,
                    enumeration,
                    semantics,
                    CancellationToken.None));

            Assert.Null(parsed.Description);
            Assert.Equal(
                "original description",
                await File.ReadAllTextAsync(Path.Join(displaced, "desc.txt")));
            Assert.Equal(
                "replacement description",
                await File.ReadAllTextAsync(Path.Join(bookDirectory, "desc.txt")));
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
