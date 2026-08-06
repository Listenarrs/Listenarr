using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.SignalR;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning
{
    [Trait("Name", "ScanJobProcessorTests")]
    [Trait("Category", "BackgroundWorkers")]
    public class ScanJobProcessorTests : BaseTests
    {
        public override async Task InitializeAsync()
        {
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Duration = TimeSpan.FromSeconds(120),
                    Format = "m4b",
                    BitRate = 64000,
                    SampleRate = 32000,
                    Channels = 1
                });

            _services.AddSingleton(metadataMock.Object);
            Init();
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(FileService.GetTempPath())
                    .Build());
        }

        [Fact]
        public async Task ProcessJobAsync_UnresolvedMoveExecution_BlocksBeforeScanReconciliation()
        {
            var basePath = FileService.GetTempDirectory("scan-processor-unresolved-move");
            _ = await FileService.GetFileAsync(basePath, "Scan Book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Scan Move Fence")
                .WithBasePath(basePath)
                .Build());
            await MoveJobTestFactory.SeedUnresolvedExecutionAsync(
                _provider,
                audiobook.Id,
                basePath,
                Path.Join(FileService.GetTempPath(), $"scan-move-target-{Guid.NewGuid():N}"));
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Failed", updatedJob.Status);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        public async Task ProcessJobAsync_HappyPath_ReconcilesFilesAndCompletesJob()
        {
            var basePath = FileService.GetTempDirectory("scan-processor-happy");
            var audioPath = await FileService.GetFileAsync(basePath, "Scan Book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Scan Book")
                .WithBasePath(basePath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Completed", updatedJob.Status);

            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            var file = Assert.Single(files);
            Assert.Equal(audioPath, file.Path);

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.scan.job.started", It.IsAny<double>()), Times.Once);
            metricsMock.Verify(m => m.Increment("worker.scan.job.completed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_PathlessAuthoritativeScan_RemovesVerifiedMissingTrackedFile()
        {
            var basePath = FileService.GetTempDirectory("scan-processor-pathless-authoritative");
            var missingPath = Path.Join(basePath, "Missing Book.m4b");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Missing Book")
                .WithBasePath(basePath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(missingPath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);
            Assert.Null(job.Path);
            Assert.True(job.IsAuthoritativeScope);

            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Completed", updatedJob.Status);
            Assert.Empty(
                await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        public async Task ProcessJobAsync_ExplicitQueuedPath_IsNotOverriddenByStoredBasePath()
        {
            var storedBasePath = FileService.GetTempDirectory("scan-processor-stored-base");
            var requestedPath = FileService.GetTempDirectory("scan-processor-requested");
            var requestedFile = await FileService.GetFileAsync(
                requestedPath,
                "Requested Book.m4b",
                "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Requested Book")
                .WithBasePath(storedBasePath)
                .Build());
            var queue = Assert.IsType<ScanQueueService>(
                _provider.GetRequiredService<IScanQueueService>());
            var authorization = await _provider
                .GetRequiredService<IScanPathAuthorizationService>()
                .AuthorizeAsync(requestedPath);
            Assert.True(authorization.IsAuthorized, authorization.Error);
            var jobId = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                requestedPath,
                authorization.Identity,
                authorization.PhysicalIdentity,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(jobId, job.Id);

            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var file = Assert.Single(
                await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
            Assert.Equal(requestedFile, file.Path);
            var persisted = Assert.IsType<Audiobook>(
                await _audiobookRepository.GetByIdSnapshotAsync(audiobook.Id));
            Assert.Equal(requestedPath, persisted.BasePath);
        }

        [Fact]
        public async Task ProcessJobAsync_ConfiguredRootChangedAfterEnqueue_RejectsQueuedAuthority()
        {
            var originalRoot = FileService.GetTempDirectory(
                "scan-processor-original-root");
            var replacementRoot = FileService.GetTempDirectory(
                "scan-processor-replacement-root");
            var bookPath = Path.Join(originalRoot, "Author", "Book");
            Directory.CreateDirectory(bookPath);
            _ = await FileService.GetFileAsync(bookPath, "Book.m4b", "audio");
            var settings = await _applicationSettingsRepository.GetAsync()
                ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                    new ApplicationSettingsBuilder().Build());
            settings.OutputPath = originalRoot;
            settings = await _applicationSettingsRepository.SaveAsync(settings);
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Book")
                    .Build());
            var authorization = await _provider
                .GetRequiredService<IScanPathAuthorizationService>()
                .AuthorizeAsync(bookPath);
            Assert.True(authorization.IsAuthorized, authorization.Error);
            var queue = Assert.IsType<ScanQueueService>(
                _provider.GetRequiredService<IScanQueueService>());
            var jobId = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                bookPath,
                authorization.Identity,
                authorization.PhysicalIdentity,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(jobId, job.Id);
            settings.OutputPath = replacementRoot;
            await _applicationSettingsRepository.SaveAsync(settings);

            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updated = GetRequiredJob(queue, job.Id);
            Assert.Equal("Failed", updated.Status);
            Assert.Contains(
                "not within a configured root folder",
                updated.Error ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(
                await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [DirectoryLinkFact]
        public async Task ProcessJobAsync_LinkedChildDirectory_DoesNotImportOutsideFiles()
        {

            var basePath = FileService.GetTempDirectory("scan-processor-link-root");
            var outsidePath = FileService.GetTempDirectory("scan-processor-link-outside");
            await FileService.GetFileAsync(outsidePath, "Linked Book.m4b", "outside");
            Directory.CreateSymbolicLink(Path.Join(basePath, "linked"), outsidePath);

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Linked Book")
                .WithBasePath(basePath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Completed", updatedJob.Status);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
            var persistedAudiobook = Assert.IsType<Audiobook>(
                await _audiobookRepository.GetByIdAsync(audiobook.Id));
            Assert.Equal(basePath, persistedAudiobook.BasePath);
        }

        [DirectoryLinkFact]
        public async Task ProcessJobAsync_LinkedScanRoot_FailsWithoutDeletingTrackedFiles()
        {

            var actualRoot = FileService.GetTempDirectory("scan-processor-linked-root-target");
            var linkParent = FileService.GetTempDirectory("scan-processor-linked-root-parent");
            var linkedRoot = Path.Join(linkParent, "linked-root");
            Directory.CreateSymbolicLink(linkedRoot, actualRoot);
            var trackedPath = Path.Join(linkedRoot, "Tracked Book.m4b");
            await File.WriteAllTextAsync(Path.Join(actualRoot, "Tracked Book.m4b"), "audio");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Tracked Book")
                .WithBasePath(linkedRoot)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(trackedPath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Failed", updatedJob.Status);
            Assert.False(string.IsNullOrWhiteSpace(updatedJob.Error));
            Assert.Contains(
                "link",
                updatedJob.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(
                    actualRoot,
                    "Tracked Book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_BroadcastFailure_DoesNotChangeDurableCompletion()
        {
            var failingProxy = new Mock<IClientProxy>();
            failingProxy
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("hub down"));

            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.All).Returns(failingProxy.Object);
            var hubContext = new Mock<IHubContext<DownloadHub>>();
            hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

            _services.AddSingleton(hubContext.Object);
            Init();
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(FileService.GetTempPath())
                    .Build());

            var basePath = FileService.GetTempDirectory("scan-processor-failure");
            await FileService.GetFileAsync(basePath, "Broken Broadcast.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Broken Broadcast")
                .WithBasePath(basePath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Completed", updatedJob.Status);

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.scan.job.completed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_ReleasesAudiobookLockBeforeOptionalCompletionEffects()
        {
            var broadcastEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBroadcast = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var blockingProxy = new Mock<IClientProxy>();
            blockingProxy
                .Setup(proxy => proxy.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string method, object?[] _, CancellationToken _) =>
                {
                    if (!string.Equals(method, "AudiobookUpdate", StringComparison.Ordinal))
                    {
                        return Task.CompletedTask;
                    }

                    broadcastEntered.TrySetResult();
                    return releaseBroadcast.Task;
                });
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(clients => clients.All).Returns(blockingProxy.Object);
            var hubContext = new Mock<IHubContext<DownloadHub>>();
            hubContext.Setup(context => context.Clients).Returns(hubClients.Object);
            _services.AddSingleton(hubContext.Object);
            Init();
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(FileService.GetTempPath())
                    .Build());

            var basePath = FileService.GetTempDirectory("scan-processor-post-effects");
            await FileService.GetFileAsync(basePath, "Post Effects.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Scan post effects",
                BasePath = basePath,
                Monitored = false
            });
            var (_, job) = await CreateQueuedScanJobAsync(audiobook);
            var processor = _provider.GetRequiredService<IScanJobProcessor>();

            var processing = processor.ProcessJobAsync(job, CancellationToken.None);
            await broadcastEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var coordinator = _provider.GetRequiredService<IAudiobookOperationCoordinator>();
            var concurrentEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var concurrentOperation = coordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                _ =>
                {
                    concurrentEntered.TrySetResult();
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            await concurrentEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseBroadcast.TrySetResult();
            await Task.WhenAll(processing, concurrentOperation);
        }

        [Fact]
        public async Task ProcessJobAsync_MissingBasePath_FailsWithoutClearingMetadataOrFiles()
        {
            var missingBasePath = Path.Join(
                FileService.GetTempPath(),
                $"scan-processor-missing-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Missing Scan Book")
                .WithBasePath(missingBasePath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(Path.Join(missingBasePath, "Missing Scan Book.m4b"))
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Failed", updatedJob.Status);
            Assert.Equal("BasePath unavailable", updatedJob.Error);

            var persistedAudiobook = Assert.IsType<Audiobook>(
                await _audiobookRepository.GetByIdAsync(audiobook.Id));
            Assert.Equal(missingBasePath, persistedAudiobook.BasePath);
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(files);
        }

        [Fact]
        public async Task ProcessJobAsync_MoveScanWithMissingBasePath_RecordsTerminalFailure()
        {
            var missingBasePath = Path.Join(
                Path.GetTempPath(),
                $"scan-processor-move-missing-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Missing Move Scan")
                .WithBasePath(missingBasePath)
                .Build());
            var queue = Assert.IsType<ScanQueueService>(
                _provider.GetRequiredService<IScanQueueService>());
            const string correlationId = "move:missing-base-path";
            var jobId = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(jobId, job.Id);

            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Failed", updatedJob.Status);
            var correlated = await _historyRepository.GetByCorrelationIdAsync(correlationId);
            Assert.Single(correlated, entry =>
                entry.EventType == HistoryEvents.ScanFailed
                && entry.Outcome == HistoryOutcome.Failed);
        }

        [Fact]
        public async Task ProcessJobAsync_CanceledToken_ThrowsBeforeStateChange()
        {
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(FileService.GetTempDirectory("scan-processor-cancel"))
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessJobAsync(job, cts.Token));

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Queued", updatedJob.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_ReplayedMoveScan_DoesNotDuplicateTerminalHistory()
        {
            var basePath = FileService.GetTempDirectory("scan-processor-move-replay");
            await FileService.GetFileAsync(basePath, "Move Replay Book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Move Replay Book")
                .WithBasePath(basePath)
                .Build());
            var (_, job) = await CreateQueuedScanJobAsync(
                audiobook,
                correlationId: "move:scan-replay");

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var history = await _historyRepository.GetByCorrelationIdAsync("move:scan-replay");
            Assert.Single(history, entry =>
                entry.EventType == HistoryEvents.ScanCompleted
                && entry.Outcome == HistoryOutcome.Succeeded);
        }

        [Fact]
        public async Task ProcessJobAsync_ReplayedMoveScanFailure_DoesNotDuplicateTerminalHistory()
        {
            var missingBasePath = Path.Join(
                Path.GetTempPath(),
                $"scan-processor-move-failure-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Move Failure Replay")
                .WithBasePath(missingBasePath)
                .Build());
            var (_, job) = await CreateQueuedScanJobAsync(
                audiobook,
                correlationId: "move:scan-failure-replay");

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var history = await _historyRepository.GetByCorrelationIdAsync(
                "move:scan-failure-replay");
            Assert.Single(history, entry =>
                entry.EventType == HistoryEvents.ScanFailed
                && entry.Outcome == HistoryOutcome.Failed);
        }

        [Fact]
        public async Task ProcessJobAsync_ReplayedCompletedJob_DoesNotDuplicateFileRows()
        {
            var basePath = FileService.GetTempDirectory("scan-processor-replay");
            await FileService.GetFileAsync(basePath, "Replay Book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Replay Book")
                .WithBasePath(basePath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Completed", updatedJob.Status);
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(files);
        }

        [WindowsFact]
        public async Task ProcessJobAsync_ForeignPersistedBasePath_IsAuthorizedBeforeAnyNativeProbe()
        {
            var audiobook = new AudiobookBuilder()
                .WithId(809)
                .WithTitle("Foreign Scan Base")
                .WithBasePath($"/listenarr-foreign-scan-{Guid.NewGuid():N}")
                .Build();
            Assert.False(Directory.Exists(Path.GetFullPath(audiobook.BasePath!)));
            var audiobookRepository = new Mock<IAudiobookRepository>();
            audiobookRepository.Setup(repository => repository.GetForScanAsync(
                    audiobook.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(audiobook);
            var historyRepository = new Mock<IHistoryRepository>();
            historyRepository.Setup(repository => repository.AddAsync(
                    It.IsAny<History>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((History entry, CancellationToken _) => entry);
            var authorizationService = new Mock<IScanPathAuthorizationService>();
            authorizationService.Setup(service => service.ResolveDefaultAsync(
                    audiobook.BasePath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(ScanPathAuthorizationResult.Rejected(
                    ScanPathAuthorizationFailure.InvalidPath,
                    "Foreign persisted scan path rejected."));
            await using var services = new ServiceCollection()
                .AddSingleton(audiobookRepository.Object)
                .AddSingleton(historyRepository.Object)
                .AddSingleton(new Mock<IAudiobookScanService>().Object)
                .AddSingleton(authorizationService.Object)
                .BuildServiceProvider();
            var queue = Assert.IsType<ScanQueueService>(
                _provider.GetRequiredService<IScanQueueService>());
            var jobId = await queue.EnqueueScanAsync(audiobook);
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(jobId, job.Id);
            var processor = new ScanJobProcessor(
                queue,
                services.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<ScanJobProcessor>>(),
                _provider.GetRequiredService<IHubContext<DownloadHub>>(),
                _provider.GetRequiredService<IAppMetricsService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                _provider.GetRequiredService<IFilesystemMutationCoordinator>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>());

            await processor.ProcessJobAsync(job, CancellationToken.None);

            authorizationService.Verify(service => service.ResolveDefaultAsync(
                audiobook.BasePath,
                It.IsAny<CancellationToken>()), Times.Once);
            var updated = GetRequiredJob(queue, job.Id);
            Assert.Equal("Failed", updated.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_AudiobookDeletedBeforeCompletion_MarksMoveScanFailed()
        {
            var basePath = FileService.GetTempDirectory("scan-processor-deleted-during-scan");
            var audiobook = new AudiobookBuilder()
                .WithId(808)
                .WithTitle("Deleted During Scan")
                .WithBasePath(basePath)
                .Build();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            audiobookRepository.Setup(repository => repository.GetForScanAsync(
                    audiobook.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(audiobook);
            var fileRepository = new Mock<IAudiobookFileRepository>();
            fileRepository.Setup(repository => repository.GetByAudiobookIdAsync(
                    audiobook.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var historyRepository = new Mock<IHistoryRepository>();
            historyRepository.Setup(repository => repository.GetByCorrelationIdAsync(
                    "move:deleted-during-scan",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            historyRepository.Setup(repository => repository.AddAsync(
                    It.IsAny<History>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((History entry, CancellationToken _) => entry);
            var scanService = new Mock<IAudiobookScanService>();
            scanService.Setup(service => service.ScanAsync(
                    It.IsAny<AudiobookScanCommand>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException(
                    "Audiobook disappeared before scan completion"));
            var pathIdentity = PathIdentitySnapshot.FromResolution(
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemCaseSensitivityMode.Auto,
                FileService.GetTempPath(),
                basePath);
            var authorizationService = new Mock<IScanPathAuthorizationService>();
            authorizationService.Setup(service => service.ResolveDefaultAsync(
                    basePath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(ScanPathAuthorizationResult.Authorized(
                    basePath,
                    pathIdentity,
                    new ScanPathPhysicalIdentity(
                        "processor-test-boundary",
                        "processor-test-root")));
            await using var services = new ServiceCollection()
                .AddSingleton(audiobookRepository.Object)
                .AddSingleton(fileRepository.Object)
                .AddSingleton(historyRepository.Object)
                .AddSingleton(scanService.Object)
                .AddSingleton(authorizationService.Object)
                .BuildServiceProvider();
            var queue = Assert.IsType<ScanQueueService>(
                _provider.GetRequiredService<IScanQueueService>());
            var jobId = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: "move:deleted-during-scan");
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(jobId, job.Id);
            var processor = new ScanJobProcessor(
                queue,
                services.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<ILogger<ScanJobProcessor>>(),
                _provider.GetRequiredService<IHubContext<DownloadHub>>(),
                _provider.GetRequiredService<IAppMetricsService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                _provider.GetRequiredService<IFilesystemMutationCoordinator>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>());

            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = GetRequiredJob(queue, job.Id);
            Assert.Equal("Failed", updatedJob.Status);
            Assert.Equal("Audiobook disappeared before scan completion", updatedJob.Error);
            historyRepository.Verify(repository => repository.AddAsync(
                It.Is<History>(entry =>
                    entry.EventType == HistoryEvents.ScanFailed
                    && entry.Outcome == HistoryOutcome.Failed
                    && entry.CorrelationId == "move:deleted-during-scan"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        private static ScanJob GetRequiredJob(
            ScanQueueService queue,
            Guid jobId)
        {
            Assert.True(queue.TryGetJob(jobId, out var job));
            return Assert.IsType<ScanJob>(job);
        }

        private async Task<(ScanQueueService Queue, ScanJob Job)> CreateQueuedScanJobAsync(
            Audiobook audiobook,
            string? correlationId = null)
        {
            var queue = Assert.IsType<ScanQueueService>(_provider.GetRequiredService<IScanQueueService>());
            var jobId = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(jobId, job.Id);
            return (queue, job);
        }
    }
}
