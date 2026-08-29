using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    [Trait("Name", "MoveJobProcessorTests")]
    [Trait("Category", "BackgroundWorkers")]
    public partial class MoveJobProcessorTests : BaseTests
    {
        private const string LeaseOwner = "test-worker";
        [Fact]
        public async Task ProcessJobAsync_HappyPath_MovesFilesAndCompletesJob()
        {
            var src = FileService.GetTempDirectory("move-processor-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), "move-processor-dst");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.True(
                updatedJob.Status == MoveJobStatus.Completed,
                updatedJob.Error ?? $"Unexpected move status: {updatedJob.Status}");
            Assert.False(Directory.Exists(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));

            var history = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
            var moveEvent = Assert.Single(history, entry => entry.EventType == "Moved");
            Assert.True(moveEvent.NotificationSent);

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.move.job.started", It.IsAny<double>()), Times.Once);
            metricsMock.Verify(m => m.Increment("worker.move.job.completed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_ActiveDeletionRecovery_StopsBeforeFilesystemMutation()
        {
            var source = FileService.GetTempDirectory("move-processor-delete-owner-source");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-delete-owner-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Deletion Recovery Owner",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var dbFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.AudiobookDeletionIntents.Add(new AudiobookDeletionIntent
                {
                    Id = Guid.NewGuid(),
                    AudiobookId = audiobook.Id,
                    DeleteFolder = true,
                    State = AudiobookDeletionIntentState.Planned
                });
                await db.SaveChangesAsync();
            }

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var blocked = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, blocked.Status);
            Assert.Contains("deletion owns", blocked.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_ActiveRenameRecovery_StopsBeforeFilesystemMutation()
        {
            var source = FileService.GetTempDirectory("move-processor-rename-owner-source");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-rename-owner-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Rename Recovery Owner",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var dbFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = Guid.NewGuid(),
                    Action = FileAction.Move,
                    SourcePath = sourceFile,
                    DestinationPath = Path.Join(source, "renamed.m4b"),
                    SourcePhysicalObjectIdentity = "test-source-generation",
                    SourceLength = new FileInfo(sourceFile).Length,
                    State = FileMutationJournalState.Planned,
                    AudiobookId = audiobook.Id,
                    AudiobookFileId = 999_999
                });
                await db.SaveChangesAsync();
            }

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var blocked = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, blocked.Status);
            Assert.Contains("file organize operation owns", blocked.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_ActiveRegistrationRecovery_StopsBeforeFilesystemMutation()
        {
            var source = FileService.GetTempDirectory("move-processor-registration-owner-source");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-registration-owner-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Registration Recovery Owner",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var dbFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = Guid.NewGuid(),
                    Action = FileAction.Move,
                    SourcePath = Path.Join(source, "original-download.m4b"),
                    DestinationPath = sourceFile,
                    SourcePhysicalObjectIdentity = "test-download-generation",
                    TargetPhysicalObjectIdentity = "test-registered-generation",
                    SourceLength = new FileInfo(sourceFile).Length,
                    State = FileMutationJournalState.SourceDeletionAuthorized,
                    AudiobookId = audiobook.Id,
                    AudiobookFileId = null
                });
                await db.SaveChangesAsync();
            }

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var blocked = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, blocked.Status);
            Assert.Contains("committed file import", blocked.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_AnonymousRegistrationPublicationTouchesSource_StopsBeforeFilesystemMutation()
        {
            var source = FileService.GetTempDirectory("move-processor-anonymous-registration-source");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-anonymous-registration-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Anonymous Registration Recovery Owner",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            var anonymousPublishedFile = await FileService.GetFileAsync(
                source,
                "unregistered.m4b",
                "anonymous-audio");

            var dbFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = Guid.NewGuid(),
                    ProtocolVersion = FileMutationProtocol.Current,
                    Action = FileAction.Copy,
                    SourcePath = Path.Join(
                        FileService.GetTempPath(),
                        $"anonymous-download-{Guid.NewGuid():N}.m4b"),
                    DestinationPath = anonymousPublishedFile,
                    SourceParentDirectoryObjectIdentity = "source-parent",
                    DestinationParentDirectoryObjectIdentity = "destination-parent",
                    SourcePhysicalObjectIdentity = "anonymous-source-generation",
                    TargetPhysicalObjectIdentity = "anonymous-target-generation",
                    SourceLength = new FileInfo(anonymousPublishedFile).Length,
                    State = FileMutationJournalState.TargetVerified,
                    AudiobookId = null,
                    AudiobookFileId = null
                });
                await db.SaveChangesAsync();
            }

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var blocked = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, blocked.Status);
            Assert.Contains("unresolved file publication", blocked.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(anonymousPublishedFile));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_PersistedRelocation_AnonymousPublicationInSourceRootSibling_BlocksMove()
        {
            var relocationRoot = FileService.GetTempDirectory(
                "move-processor-relocation-anonymous-root");
            var source = Path.Join(relocationRoot, "Author", "Book");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-relocation-anonymous-target-{Guid.NewGuid():N}",
                "Author",
                "Book");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Persisted Relocation Recovery Owner",
                BasePath = source
            });

            var dbFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.RootFolders.Add(new RootFolder
                {
                    Name = "Relocation Root",
                    Path = relocationRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
                });
                await db.SaveChangesAsync();
            }

            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            job.RelocationId = Guid.NewGuid();
            job.SourceCleanupBoundary = relocationRoot;

            var anonymousDirectory = Path.Join(relocationRoot, "Untracked");
            Directory.CreateDirectory(anonymousDirectory);
            var anonymousPublishedFile = await FileService.GetFileAsync(
                anonymousDirectory,
                "unregistered.m4b",
                "anonymous-audio");
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = Guid.NewGuid(),
                    ProtocolVersion = FileMutationProtocol.Current,
                    Action = FileAction.Copy,
                    SourcePath = Path.Join(
                        FileService.GetTempPath(),
                        $"relocation-anonymous-download-{Guid.NewGuid():N}.m4b"),
                    DestinationPath = anonymousPublishedFile,
                    SourceParentDirectoryObjectIdentity = "source-parent",
                    DestinationParentDirectoryObjectIdentity = "destination-parent",
                    SourcePhysicalObjectIdentity = "anonymous-source-generation",
                    TargetPhysicalObjectIdentity = "anonymous-target-generation",
                    SourceLength = new FileInfo(anonymousPublishedFile).Length,
                    State = FileMutationJournalState.TargetVerified,
                    AudiobookId = null,
                    AudiobookFileId = null
                });
                await db.SaveChangesAsync();
            }

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var blocked = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, blocked.Status);
            Assert.Contains("unresolved file publication", blocked.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(anonymousPublishedFile));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_TransientTrackedManifestInspectionFailure_SchedulesRetryWithoutMutation()
        {
            var sourcePlan = new Mock<IMoveSourcePlanService>(MockBehavior.Strict);
            sourcePlan
                .Setup(service => service.BuildPlanAsync(
                    It.IsAny<AudiobookPathReferenceSnapshot>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ApplicationUnavailableException(
                    "move_source_temporarily_unavailable",
                    "Injected transient source storage outage."));
            Init(builder => builder.WithSingleton(sourcePlan.Object));

            var source = FileService.GetTempDirectory("move-processor-transient-manifest-source");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-transient-manifest-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Transient Manifest Inspection",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.RetryScheduled, updated.Status);
            Assert.Contains("will be retried", updated.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
            sourcePlan.VerifyAll();
        }

        [Fact]
        public async Task ProcessJobAsync_AutoSemanticsTemporarilyUnavailable_SchedulesRetryWithoutMutation()
        {
            var semanticsResolver = new ToggleUnavailableSemanticsResolver();
            Init(builder => builder.WithSingleton<IFileSystemSemanticsResolver>(
                semanticsResolver));

            var source = FileService.GetTempDirectory(
                "move-processor-transient-semantics-source");
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-transient-semantics-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Transient Filesystem Semantics",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                target,
                source);

            semanticsResolver.ReturnUnavailable = true;
            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.RetryScheduled, updated.Status);
            Assert.Contains(
                "identity verification will be retried",
                updated.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task ProcessJobAsync_AutoSemanticsOnlyBehavioral_BlocksBeforeFilesystemMutation()
        {
            var semanticsResolver = new BehavioralSemanticsResolver();
            Init(builder => builder.WithSingleton<IFileSystemSemanticsResolver>(
                semanticsResolver));

            var source = FileService.GetTempDirectory(
                "move-processor-behavioral-semantics-source");
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-behavioral-semantics-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Behavioral Filesystem Semantics",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                target,
                source);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updated.Status);
            Assert.Contains(
                "start a new move",
                updated.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(MoveRecoveryPolicy.HasFilesystemExecutionEvidence(updated));
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task ProcessJobAsync_AutoBehavioralSource_WithExecutionEvidence_NormalMoveStillBlocks()
        {
            var semanticsResolver = new BehavioralSemanticsResolver();
            Init(builder => builder.WithSingleton<IFileSystemSemanticsResolver>(
                semanticsResolver));

            var source = FileService.GetTempDirectory(
                "move-processor-behavioral-source-evidence");
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-behavioral-source-evidence-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Behavioral Source With Execution Evidence",
                BasePath = source
            });
            var explicitTargetMode = OperatingSystem.IsWindows()
                ? FileSystemCaseSensitivityMode.Insensitive
                : FileSystemCaseSensitivityMode.Sensitive;
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                target,
                source,
                targetCaseSensitivityMode: explicitTargetMode);
            var dbFactory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var persisted = await db.MoveJobs.SingleAsync(candidate =>
                    candidate.Id == job.Id);
                persisted.Phase = MoveJobPhase.Copying;
                await db.SaveChangesAsync();
            }
            job.Phase = MoveJobPhase.Copying;

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updated.Status);
            Assert.Contains(
                "start a new move",
                updated.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task ProcessJobAsync_AutoBehavioralSource_ForcedCopyRetain_CopiesAndRetainsSource()
        {
            var semanticsResolver = new BehavioralSemanticsResolver();
            Init(builder => builder.WithSingleton<IFileSystemSemanticsResolver>(
                semanticsResolver));

            var source = FileService.GetTempDirectory(
                "move-processor-behavioral-retain-source");
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-behavioral-retain-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Behavioral Copy Retain Source",
                BasePath = source
            });
            var explicitTargetMode = OperatingSystem.IsWindows()
                ? FileSystemCaseSensitivityMode.Insensitive
                : FileSystemCaseSensitivityMode.Sensitive;
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                target,
                source,
                deleteEmptySource: true,
                targetCaseSensitivityMode: explicitTargetMode,
                sourceCleanupMode: MoveSourceCleanupMode.RetainSource,
                forceCopyAndRetainSource: true);

            // Simulate a legacy/corrupted durable row that predates queue-level
            // normalization. Execution must still treat forced retention as
            // authoritative and never retire the source directory.
            var dbFactory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var persisted = await db.MoveJobs.SingleAsync(candidate =>
                    candidate.Id == job.Id);
                persisted.DeleteEmptySource = true;
                await db.SaveChangesAsync();
            }
            job.DeleteEmptySource = true;

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.True(
                updated.Status == MoveJobStatus.Completed,
                updated.Error ?? $"Unexpected move status: {updated.Status}");
            Assert.True(MoveJobPublicProjection.IsSourceRetained(updated));
            Assert.True(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
            using var verificationScope = _provider.CreateScope();
            var movedAudiobook = Assert.IsType<Audiobook>(
                await verificationScope.ServiceProvider
                    .GetRequiredService<IAudiobookRepository>()
                    .GetByIdAsync(audiobook.Id));
            Assert.Equal(target, movedAudiobook.BasePath);
        }

        [Fact]
        public async Task ProcessJobAsync_AutoBehavioralTarget_ForcedCopyRetain_BlocksBeforeFilesystemMutation()
        {
            var semanticsResolver = new BehavioralSemanticsResolver();
            Init(builder => builder.WithSingleton<IFileSystemSemanticsResolver>(
                semanticsResolver));

            var source = FileService.GetTempDirectory(
                "move-processor-behavioral-target-source");
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-behavioral-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Behavioral Target Copy Retain",
                BasePath = source
            });
            var explicitSourceMode = OperatingSystem.IsWindows()
                ? FileSystemCaseSensitivityMode.Insensitive
                : FileSystemCaseSensitivityMode.Sensitive;
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                target,
                source,
                deleteEmptySource: false,
                sourceCaseSensitivityMode: explicitSourceMode,
                sourceCleanupMode: MoveSourceCleanupMode.RetainSource,
                forceCopyAndRetainSource: true);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updated.Status);
            Assert.Contains(
                "start a new move",
                updated.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(MoveRecoveryPolicy.HasFilesystemExecutionEvidence(updated));
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task ProcessJobAsync_AutoBehavioralTarget_WithExecutionEvidence_StillBlocksBeforeNewMutation()
        {
            var semanticsResolver = new BehavioralSemanticsResolver();
            Init(builder => builder.WithSingleton<IFileSystemSemanticsResolver>(
                semanticsResolver));

            var source = FileService.GetTempDirectory(
                "move-processor-behavioral-target-evidence-source");
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-behavioral-target-evidence-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Behavioral Target With Execution Evidence",
                BasePath = source
            });
            var explicitSourceMode = OperatingSystem.IsWindows()
                ? FileSystemCaseSensitivityMode.Insensitive
                : FileSystemCaseSensitivityMode.Sensitive;
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                target,
                source,
                deleteEmptySource: false,
                sourceCaseSensitivityMode: explicitSourceMode,
                sourceCleanupMode: MoveSourceCleanupMode.RetainSource,
                forceCopyAndRetainSource: true);
            var dbFactory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var persisted = await db.MoveJobs.SingleAsync(candidate =>
                    candidate.Id == job.Id);
                persisted.Phase = MoveJobPhase.Copying;
                await db.SaveChangesAsync();
            }
            job.Phase = MoveJobPhase.Copying;

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updated.Status);
            Assert.Contains(
                "start a new move",
                updated.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [LinuxFact]
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public async Task ProcessJobAsync_ReadOnlyRemountDuringTargetScaffolding_SchedulesRetryWithoutMutation()
        {
            var source = FileService.GetTempDirectory("move-processor-erofs-source");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var targetParent = FileService.GetTempDirectory("move-processor-erofs-target-parent");
            var target = Path.Join(targetParent, "Book");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Transient Read Only Remount",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            var hookRan = false;
            using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(path =>
            {
                if (hookRan || !string.Equals(path, target, StringComparison.Ordinal))
                {
                    return;
                }

                hookRan = true;
                throw new System.ComponentModel.Win32Exception(
                    30,
                    "Injected read-only filesystem remount.");
            });

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            Assert.True(hookRan);
            var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.RetryScheduled, updated.Status);
            Assert.Contains("retried", updated.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [WindowsFact]
        public async Task ProcessJobAsync_SourceDeleteSharingViolation_SchedulesRetryWithoutRetiringSource()
        {
            var source = FileService.GetTempDirectory("move-processor-sharing-source");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-sharing-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Windows Sharing Retry",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            await using (var sourceLock = new FileStream(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                await _provider.GetRequiredService<IMoveJobProcessor>()
                    .ProcessJobAsync(job, CancellationToken.None);

                var updated = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
                Assert.Equal(MoveJobStatus.RetryScheduled, updated.Status);
                Assert.True(File.Exists(sourceFile));
                Assert.False(File.Exists(Path.Join(target, "book.m4b")));
            }
        }

        [Fact]
        public async Task ProcessJobAsync_UntrackedNonAudioCompanionInManagedAudiobookFolder_MovesWithTrackedAudio()
        {
            var sourceRoot = FileService.GetTempDirectory("move-processor-companion-source-root");
            var sourceRootFolder = await AddAuthorizedRootAsync(
                sourceRoot,
                "Companion Source Root");
            var source = Path.Join(sourceRoot, "Author", "Book");
            Directory.CreateDirectory(source);
            var audioPath = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var coverPath = await FileService.GetFileAsync(source, "cover.jpg", "cover");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Companion Move",
                BasePath = source
            });
            var sourceSemantics = FileSystemPathSemantics.CurrentHostDefault;
            var audioIdentity = AudiobookFilePathIdentity.CreateValid(
                audioPath,
                sourceSemantics,
                FileSystemCaseSensitivityMode.Auto,
                sourceRoot);
            var trackedAudio = AudiobookFile.CreateUnresolved(audioPath);
            trackedAudio.AudiobookId = audiobook.Id;
            trackedAudio.ApplyPathIdentity(audioPath, audioIdentity);
            ApplyTestPhysicalObjectIdentity(trackedAudio, audioPath);
            var audioClaim = await _audiobookFileRepository.ClaimAsync(trackedAudio);
            Assert.Equal(AudiobookFileClaimOutcome.Created, audioClaim.Outcome);

            var manifest = await _provider
                .GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook);
            Assert.Contains(manifest.Entries, entry =>
                entry.EntryType == MoveJobEntryType.File
                && string.Equals(entry.RelativePath, "cover.jpg", StringComparison.Ordinal));
            Assert.DoesNotContain(
                await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id),
                file => string.Equals(file.Path, coverPath, StringComparison.Ordinal));

            var targetRoot = FileService.GetTempDirectory("move-processor-companion-target-root");
            var targetRootFolder = await AddAuthorizedRootAsync(
                targetRoot,
                "Companion Target Root");
            var target = Path.Join(targetRoot, "Author", "Book");
            var targetResolution = await _provider
                .GetRequiredService<IFileSystemSemanticsResolver>()
                .ResolveAsync(targetRoot);
            Assert.Equal(PathIdentityState.Valid, targetResolution.State);
            var targetIdentity = PathIdentitySnapshot.FromResolution(
                targetResolution.Semantics,
                targetRootFolder.CaseSensitivityMode,
                targetRoot,
                target);
            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var jobId = await queue.EnqueueMoveAsync(
                new MoveEnqueueCommand(
                    audiobook.Id,
                    manifest.SourceRoot,
                    manifest.SourceIdentity,
                    manifest.Entries,
                    target,
                    targetIdentity,
                    sourceRootFolder.DirectoryObjectIdentityVersion!.Value,
                    sourceRootFolder.DirectoryObjectIdentity!,
                    targetRootFolder.DirectoryObjectIdentityVersion!.Value,
                    targetRootFolder.DirectoryObjectIdentity!,
                    true,
                    sourceRoot));
            var job = Assert.IsType<MoveJob>(await queue.GetJobAsync(jobId));
            await PrepareJobForProcessingAsync(queue, job);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(jobId));
            Assert.True(
                completed.Status == MoveJobStatus.Completed,
                completed.Error ?? $"Unexpected move status: {completed.Status}");
            Assert.False(File.Exists(coverPath));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.Equal("cover", await File.ReadAllTextAsync(Path.Join(target, "cover.jpg")));
            var persistedFiles = await _audiobookFileRepository
                .GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(persistedFiles);
            Assert.Equal(
                Path.Join(target, "book.m4b"),
                persistedFiles[0].CanonicalPath ?? persistedFiles[0].Path);
        }

        [Fact]
        public async Task ProcessJobAsync_PreservesUnownedEmptySourceAncestorsWithinConfiguredRoot()
        {
            var sourceRoot = FileService.GetTempDirectory("move-processor-cleanup-root");
            await AddAuthorizedRootAsync(sourceRoot, "Move Cleanup Source Root");
            var source = Path.Join(sourceRoot, "Author", "Series", "Title", "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-cleanup-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Cleanup Test", BasePath = source });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var completed = await queue.GetJobAsync(job.Id);
            Assert.True(
                completed?.Status == MoveJobStatus.Completed,
                completed?.Error ?? "The move job was not persisted.");
            Assert.True(Directory.Exists(sourceRoot));
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(Path.Join(sourceRoot, "Author")));
            Assert.True(Directory.Exists(Path.Join(sourceRoot, "Author", "Series")));
            Assert.True(Directory.Exists(Path.Join(sourceRoot, "Author", "Series", "Title")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_CustomSiblingMove_RemovesOldTitleFolderAndKeepsSeries()
        {
            var customRoot = FileService.GetTempDirectory("move-processor-sibling-root");
            var series = Path.Join(customRoot, "Matt Dinniman", "Dungeon Crawler Carl");
            var oldTitle = Path.Join(series, "A Parade of Horribles (20262)");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await RecordOwnedDirectoryHierarchyAsync(series, oldTitle);
            var target = Path.Join(series, "A Parade of Horribles (2026)", "test");
            Directory.CreateDirectory(target);
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "A Parade of Horribles",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Completed, updatedJob.Status);
            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(oldTitle));
            Assert.True(Directory.Exists(series));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_UnrelatedForeignSyntaxRoot_DoesNotBlockBoundedCleanup()
        {
            var sourceRoot = FileService.GetTempDirectory("move-processor-foreign-root");
            var source = Path.Join(sourceRoot, "Author", "Title");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await RecordOwnedDirectoryHierarchyAsync(
                sourceRoot,
                Path.GetDirectoryName(source)!);
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-foreign-dst-{Guid.NewGuid():N}");
            var rootFolderRepository = _provider.GetRequiredService<IRootFolderRepository>();
            await rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "Foreign Legacy Root",
                Path = OperatingSystem.IsWindows() ? "/legacy/library" : @"Z:\legacy\library"
            });
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Foreign Root Cleanup",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var completed = await queue.GetJobAsync(job.Id);
            Assert.True(
                completed?.Status == MoveJobStatus.Completed,
                completed?.Error ?? "The move job was not persisted.");
            Assert.True(Directory.Exists(sourceRoot));
            Assert.False(Directory.Exists(Path.Join(sourceRoot, "Author")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_CompletedStatusPersistenceFailure_PropagatesWithoutCompletedMetric()
        {
            var source = FileService.GetTempDirectory("move-processor-status-failure-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), "move-processor-status-failure-dst");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Status Failure",
                BasePath = source
            });
            var (durableQueue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            var handoffStore = new Mock<IMoveScanHandoffStore>();
            handoffStore.Setup(store => store.CommitMoveCompletionAsync(
                    It.IsAny<MoveCompletionCommit>(),
                    It.IsAny<Func<CancellationToken, Task<RegistrationPublicationMatchOutcome>>>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PersistenceException(
                    "Completion transaction failed.",
                    new InvalidOperationException("Database unavailable.")));
            var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
                _provider,
                handoffStore.Object);

            await Assert.ThrowsAsync<PersistenceException>(() => processor.ProcessJobAsync(
                job,
                CancellationToken.None));

            var metrics = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            Assert.Equal(
                MoveJobStatus.Running,
                (await durableQueue.GetJobAsync(job.Id))?.Status);
            metrics.Verify(
                service => service.Increment("worker.move.job.completed", It.IsAny<double>()),
                Times.Never);
            var persistedJob = Assert.IsType<MoveJob>(
                await durableQueue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobPhase.RecordingCompletion, persistedJob.Phase);
            Assert.Empty(await _historyRepository.GetByEventTypeAsync("MoveFailed"));
            Assert.Empty(
                await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));

            var retryProcessor = _provider.GetRequiredService<IMoveJobProcessor>();
            await retryProcessor.ProcessJobAsync(persistedJob, CancellationToken.None);

            var completedJob = await durableQueue.GetJobAsync(job.Id);
            Assert.True(
                completedJob?.Status == MoveJobStatus.Completed,
                $"Expected completed replay, but got {completedJob?.Status}: {completedJob?.Error}");
            Assert.Equal(MoveJobPhase.RecordingCompletion, completedJob?.Phase);
            Assert.Single(
                await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"),
                entry => entry.EventType == "Moved");
            metrics.Verify(
                service => service.Increment("worker.move.job.completed", It.IsAny<double>()),
                Times.Once);
        }

        [Fact]
        public async Task ProcessDurableJobAsync_TerminalStateNotificationCancellation_DoesNotEscapeCommit()
        {
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = int.MaxValue,
                RequestedPath = Path.Join(FileService.GetTempPath(), "missing-audiobook-target"),
                Status = MoveJobStatus.Running,
                LeaseOwner = LeaseOwner,
                LeaseGeneration = 1,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1)
            };
            var queue = new Mock<IMoveQueueService>(MockBehavior.Strict);
            queue.Setup(service => service.UpdateJobStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Running,
                    null,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            queue.Setup(service => service.UpdateJobStatusWithoutNotificationAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Failed,
                    "Audiobook not found",
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            queue.Setup(service => service.NotifyPersistedJobStateAsync(
                    job.Id,
                    MoveJobStatus.Failed,
                    "Audiobook not found",
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TaskCanceledException(
                    "Injected terminal-state notification cancellation."));
            var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
                _provider,
                queue.Object);

            var postCommit = await processor.ProcessDurableJobAsync(
                job,
                CancellationToken.None);

            Assert.Null(postCommit);
            Assert.Equal(MoveJobStatus.Failed, job.Status);
            Assert.Equal("Audiobook not found", job.Error);
            queue.Verify(service => service.UpdateJobStatusWithoutNotificationAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Failed,
                "Audiobook not found",
                It.IsAny<CancellationToken>()), Times.Once);
            queue.Verify(service => service.NotifyPersistedJobStateAsync(
                job.Id,
                MoveJobStatus.Failed,
                "Audiobook not found",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_TargetInsideSource_MovesSourceContentsIntoTarget()
        {
            var src = FileService.GetTempDirectory("move-processor-nested-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var extras = Path.Join(src, "extras");
            Directory.CreateDirectory(extras);
            await FileService.GetFileAsync(extras, "cover.jpg", "image");
            var dst = Path.Join(src, " test");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Nested", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Completed, updatedJob.Status);
            Assert.True(Directory.Exists(src));
            Assert.True(Directory.Exists(dst));
            Assert.False(File.Exists(Path.Join(src, "book.m4b")));
            Assert.False(Directory.Exists(extras));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
            Assert.True(File.Exists(Path.Join(dst, "extras", "cover.jpg")));

            using var verificationScope = _provider.CreateScope();
            var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var updatedAudiobook = Assert.IsType<Audiobook>(
                await verificationRepository.GetByIdAsync(audiobook.Id));
            Assert.Equal(dst, updatedAudiobook.BasePath);
        }

        [Fact]
        public async Task ProcessJobAsync_CustomMove_RemovesEmptySourceParentUsingFallbackBoundary()
        {
            var sourceParent = FileService.GetTempDirectory("move-processor-empty-parent");
            var src = Path.Join(sourceParent, " test");
            Directory.CreateDirectory(src);
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            await RecordOwnedDirectoryHierarchyAsync(
                Path.GetDirectoryName(sourceParent)!,
                sourceParent);
            var dst = Path.Join(FileService.GetTempPath(), "move-processor-cleaned-dst");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Empty Parent", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Completed, updatedJob.Status);
            Assert.False(Directory.Exists(src));
            Assert.False(Directory.Exists(sourceParent));
            Assert.True(Directory.Exists(Path.GetDirectoryName(sourceParent)!));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_SourceInsideDestination_DoesNotDeleteDestinationAncestor()
        {
            var dst = FileService.GetTempDirectory("move-processor-parent-target");
            var src = Path.Join(dst, " test");
            Directory.CreateDirectory(src);
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Parent Target", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Completed, updatedJob.Status);
            Assert.False(Directory.Exists(src));
            Assert.True(Directory.Exists(dst));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [LinuxFact]
        public async Task ProcessJobAsync_CaseOnlyMove_OnCaseSensitiveHost_MovesFiles()
        {

            var root = FileService.GetTempDirectory("move-processor-case-only-root");
            var src = Path.Join(root, "Title");
            Directory.CreateDirectory(src);
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(root, "title");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Case", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Completed, updatedJob.Status);
            Assert.False(Directory.Exists(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_DeleteEmptySourceFalse_KeepsEmptySourceDirectory()
        {
            var src = FileService.GetTempDirectory("move-processor-keep-source");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), $"move-processor-keep-source-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Keep Source",
                BasePath = src
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                dst,
                src,
                deleteEmptySource: false);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Completed, updatedJob.Status);
            Assert.True(Directory.Exists(src));
            Assert.Empty(Directory.EnumerateFileSystemEntries(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_DeleteEmptySourceFalse_WithNarrowSourceIdentityAndManagedAuthorization_CompletesAndRetainsSourceDirectory()
        {
            var managedRoot = FileService.GetTempDirectory(
                "move-processor-managed-auth-no-cleanup");
            var managedMode = OperatingSystem.IsWindows()
                ? FileSystemCaseSensitivityMode.Insensitive
                : FileSystemCaseSensitivityMode.Sensitive;
            await AddAuthorizedRootAsync(
                managedRoot,
                "Managed Source Root",
                managedMode);
            var source = Path.Join(managedRoot, "Author", "BookMoved");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(managedRoot, "Author", "BookReturned");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Managed authorization without cleanup",
                BasePath = source
            });

            var semanticsResolver = _provider
                .GetRequiredService<IFileSystemSemanticsResolver>();
            var sourceResolution = await semanticsResolver.ResolveAsync(
                source,
                managedMode);
            var targetResolution = await semanticsResolver.ResolveAsync(
                managedRoot,
                managedMode);
            Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
            Assert.Equal(PathIdentityState.Valid, targetResolution.State);
            var sourceIdentity = PathIdentitySnapshot.FromResolution(
                sourceResolution.Semantics,
                managedMode,
                source,
                source);
            var targetIdentity = PathIdentitySnapshot.FromResolution(
                targetResolution.Semantics,
                managedMode,
                managedRoot,
                target);
            var directoryIdentityResolver = _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>();
            var managedIdentity = await directoryIdentityResolver.ResolveAsync(managedRoot);
            Assert.True(managedIdentity.IsAvailable, managedIdentity.UnavailableReason);
            var manifest = await BuildMoveManifestAsync(source);
            await EnsureTrackedManifestRowsAsync(
                audiobook,
                source,
                sourceIdentity,
                manifest);

            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var jobId = await queue.EnqueueMoveAsync(new MoveEnqueueCommand(
                audiobook.Id,
                source,
                sourceIdentity,
                manifest,
                target,
                targetIdentity,
                managedIdentity.Version!.Value,
                managedIdentity.Value!,
                managedIdentity.Version!.Value,
                managedIdentity.Value!,
                DeleteEmptySource: false,
                SourceCleanupBoundary: managedRoot));
            var job = Assert.IsType<MoveJob>(await queue.GetJobAsync(jobId));
            await PrepareJobForProcessingAsync(queue, job);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Completed, completed.Status);
            Assert.True(Directory.Exists(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_CompletedMoveWithRetainedSource_CannotBeRequeued()
        {
            var src = FileService.GetTempDirectory("move-processor-requeue-retained-source");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), $"move-processor-requeue-retained-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Requeue Retained",
                BasePath = src
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                dst,
                src,
                deleteEmptySource: false);
            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var requeuedJobId = await queue.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            var completedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Completed, completedJob.Status);
            Assert.True(Directory.Exists(src));
            Assert.Empty(Directory.EnumerateFileSystemEntries(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_TargetContainsFiles_RequiresAttention()
        {
            var src = FileService.GetTempDirectory("move-processor-fail-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = FileService.GetTempDirectory("move-processor-fail-dst");
            await FileService.GetFileAsync(dst, "existing.txt", "blocked");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Fail", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob.Status);
            Assert.Equal(0, updatedJob.AttemptCount);
            Assert.True(Directory.Exists(src));

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.move.job.needs_attention", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_AttemptIncrementLosesLease_DoesNotPublishStaleFailure()
        {
            var src = FileService.GetTempDirectory("move-processor-stale-attempt-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-stale-attempt-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Stale Attempt",
                BasePath = src
            });
            var (_, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);
            var queue = new Mock<IMoveQueueService>();
            queue.Setup(service => service.UpdateJobStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Running,
                    null,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            queue.Setup(service => service.IncrementAttemptAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MoveLeaseLostException(job.Id, job.LeaseGeneration));
            var contentMoveService = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ThrowUnexpectedAfterPublish());
            var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
                _provider,
                queue.Object,
                contentMoveService);

            await Assert.ThrowsAsync<MoveLeaseLostException>(() => processor.ProcessJobAsync(
                job,
                CancellationToken.None));

            queue.Verify(service => service.UpdateJobStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Failed,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProcessJobAsync_LeaseExpiresAfterFilesystemCleanup_DoesNotRewriteAudiobookMetadata()
        {
            var source = FileService.GetTempDirectory(
                "move-processor-expired-before-rewrite-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-expired-before-rewrite-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Expired Before Rewrite",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            var factory = _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            var processor = _provider.GetRequiredService<MoveJobProcessor>();
            processor.AfterSourceCleanupBeforeMetadataRewriteForTest = async observedJob =>
            {
                await using var db = await factory.CreateDbContextAsync();
                var persisted = await db.MoveJobs.SingleAsync(candidate =>
                    candidate.Id == observedJob.Id);
                persisted.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            };

            await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
                processor.ProcessJobAsync(job, CancellationToken.None));

            await using var verification = await factory.CreateDbContextAsync();
            var audiobookAfter = await verification.Audiobooks
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id);
            Assert.Equal(source, audiobookAfter.BasePath);
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            var persistedJob = await verification.MoveJobs
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == job.Id);
            Assert.Equal(MoveJobStatus.Running, persistedJob.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_TargetReplacedAfterSourceCleanup_DoesNotRewriteAudiobookMetadata()
        {
            var source = FileService.GetTempDirectory(
                "move-processor-target-replaced-before-rewrite-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-target-replaced-before-rewrite-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Target Replaced Before Rewrite",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            var factory = _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            var processor = _provider.GetRequiredService<MoveJobProcessor>();
            processor.AfterSourceCleanupBeforeMetadataRewriteForTest = async _ =>
            {
                var targetFile = Path.Join(target, "book.m4b");
                File.Delete(targetFile);
                await File.WriteAllTextAsync(targetFile, "replacement");
            };

            await processor.ProcessJobAsync(job, CancellationToken.None);

            await using var verification = await factory.CreateDbContextAsync();
            var audiobookAfter = await verification.Audiobooks
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id);
            Assert.Equal(source, audiobookAfter.BasePath);
            Assert.False(File.Exists(Path.Join(source, "book.m4b")));
            Assert.Equal(
                "replacement",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
            var persistedJob = await verification.MoveJobs
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == job.Id);
            Assert.Equal(MoveJobStatus.NeedsAttention, persistedJob.Status);
            Assert.Contains(
                "target",
                persistedJob.Error,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ProcessJobAsync_CanceledToken_ThrowsBeforeStateChange()
        {
            var src = FileService.GetTempDirectory("move-processor-cancel");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-cancel-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Cancel", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, src);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessJobAsync(job, cts.Token));

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Running, updatedJob.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_LegacyIdenticalEndpoint_IsSupersededWithoutHistoryOrScanHandoff()
        {
            var src = FileService.GetTempDirectory("move-processor-identical-legacy");
            var sourceFile = await FileService.GetFileAsync(src, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Identical Legacy",
                BasePath = src
            });
            var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
            var identity = new PathIdentitySnapshot(
                syntax,
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                src);
            var legacyJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = audiobook.Id,
                SourcePath = src,
                RequestedPath = src,
                Status = MoveJobStatus.Queued,
                ActiveDeduplicationKey = $"legacy-identical:{Guid.NewGuid():N}"
            };
            legacyJob.SetSourceIdentity(identity);
            legacyJob.SetTargetIdentity(identity);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(legacyJob);
                await db.SaveChangesAsync();
            }

            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var job = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(legacyJob.Id));
            await PrepareJobForProcessingAsync(queue, job);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(legacyJob.Id));
            Assert.Equal(MoveJobStatus.Superseded, updatedJob.Status);
            Assert.Contains("identical", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(updatedJob.ActiveDeduplicationKey);
            Assert.Null(updatedJob.LeaseOwner);
            Assert.Null(updatedJob.LeaseExpiresAt);
            Assert.True(File.Exists(sourceFile));
            Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{legacyJob.Id:N}"));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.False(await verification.MoveScanHandoffs
                .AnyAsync(handoff => handoff.MoveJobId == legacyJob.Id));
        }

        [Fact]
        public async Task ProcessJobAsync_LegacyIdenticalEndpointWithExecutionState_PreservesForAttention()
        {
            var src = FileService.GetTempDirectory("move-processor-identical-evidence");
            var sourceFile = await FileService.GetFileAsync(src, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Identical Evidence",
                BasePath = src
            });
            var identity = new PathIdentitySnapshot(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                src);
            var legacyJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = audiobook.Id,
                SourcePath = src,
                RequestedPath = src,
                Status = MoveJobStatus.Queued,
                ActiveDeduplicationKey = $"legacy-identical-evidence:{Guid.NewGuid():N}"
            };
            legacyJob.SetSourceIdentity(identity);
            legacyJob.SetTargetIdentity(identity);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(legacyJob);
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = legacyJob.Id,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    CopyState = MoveJobEntryCopyState.Staged
                });
                await db.SaveChangesAsync();
            }

            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var job = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(legacyJob.Id));
            await PrepareJobForProcessingAsync(queue, job);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(legacyJob.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob.Status);
            Assert.Contains("durable move execution state", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(updatedJob.ActiveDeduplicationKey);
            Assert.Null(updatedJob.LeaseOwner);
            Assert.Null(updatedJob.LeaseExpiresAt);
            Assert.True(File.Exists(sourceFile));
            Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{legacyJob.Id:N}"));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.True(await verification.MoveJobEntries
                .AnyAsync(entry => entry.MoveJobId == legacyJob.Id));
            Assert.False(await verification.MoveScanHandoffs
                .AnyAsync(handoff => handoff.MoveJobId == legacyJob.Id));
        }

        [Fact]
        public async Task ProcessJobAsync_MissingSourceAndTargetWithTargetMetadata_MarksNeedsAttention()
        {
            var src = Path.Join(FileService.GetTempPath(), $"move-processor-missing-src-{Guid.NewGuid():N}");
            var dst = Path.Join(FileService.GetTempPath(), $"move-processor-missing-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Missing Paths",
                BasePath = dst
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_MetadataRewriteAfterEnqueue_SupersedesStaleExistingSourceWithoutMutation()
        {
            var queuedSource = FileService.GetTempDirectory("move-processor-queued-source");
            await FileService.GetFileAsync(queuedSource, "queued.m4b", "queued audio");
            var newerSource = FileService.GetTempDirectory("move-processor-newer-source");
            await FileService.GetFileAsync(newerSource, "current.m4b", "current audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-stale-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Metadata Race",
                BasePath = queuedSource
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, queuedSource);
            audiobook.BasePath = newerSource;
            await _audiobookRepository.UpdateAsync(audiobook);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Superseded, updatedJob.Status);
            Assert.True(File.Exists(Path.Join(queuedSource, "queued.m4b")));
            Assert.True(File.Exists(Path.Join(newerSource, "current.m4b")));
            Assert.False(Directory.Exists(target));
            var persistedAudiobook = Assert.IsType<Audiobook>(
                await _audiobookRepository.GetByIdAsync(audiobook.Id));
            Assert.Equal(newerSource, persistedAudiobook.BasePath);
            var history = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.DoesNotContain(history, entry => entry.EventType == "Moved");
        }

        [Fact]
        public async Task ProcessJobAsync_MetadataRewriteToRequestedTargetAfterEnqueue_SupersedesWithoutRecoveryArtifacts()
        {
            var queuedSource = FileService.GetTempDirectory("move-processor-same-target-source");
            await FileService.GetFileAsync(queuedSource, "queued.m4b", "queued audio");
            var target = FileService.GetTempDirectory("move-processor-same-target-destination");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Same Target Race",
                BasePath = queuedSource
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, queuedSource);
            audiobook.BasePath = target;
            await _audiobookRepository.UpdateAsync(audiobook);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Superseded, updatedJob.Status);
            Assert.True(File.Exists(Path.Join(queuedSource, "queued.m4b")));
            Assert.False(File.Exists(Path.Join(target, "queued.m4b")));
            var persistedAudiobook = Assert.IsType<Audiobook>(
                await _audiobookRepository.GetByIdAsync(audiobook.Id));
            Assert.Equal(target, persistedAudiobook.BasePath);
            var history = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.DoesNotContain(history, entry => entry.EventType == "Moved");
        }

        [Fact]
        public async Task ProcessJobAsync_MalformedCurrentBasePath_RequiresAttentionWithoutMutation()
        {
            var queuedSource = FileService.GetTempDirectory("move-processor-malformed-source");
            await FileService.GetFileAsync(queuedSource, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-malformed-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Malformed State",
                BasePath = queuedSource
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, queuedSource);
            audiobook.BasePath = "malformed\0path";
            await _audiobookRepository.UpdateAsync(audiobook);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob.Status);
            Assert.Contains("malformed", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(queuedSource, "book.m4b")));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task ProcessJobAsync_MissingPersistedSource_DoesNotMoveCurrentBasePath()
        {
            var missingSource = Path.Join(FileService.GetTempPath(), $"move-processor-stale-src-{Guid.NewGuid():N}");
            var currentBasePath = FileService.GetTempDirectory("move-processor-current-base");
            await FileService.GetFileAsync(currentBasePath, "current.m4b", "current audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-stale-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Stale Source",
                BasePath = currentBasePath
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, missingSource);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.Superseded, updatedJob.Status);
            Assert.Contains("source path changed", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(currentBasePath, "current.m4b")));
            Assert.False(Directory.Exists(target));
            using var verificationScope = _provider.CreateScope();
            var repository = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var persistedAudiobook = Assert.IsType<Audiobook>(
                await repository.GetByIdAsync(audiobook.Id));
            Assert.Equal(currentBasePath, persistedAudiobook.BasePath);
        }

        [Fact]
        public async Task ProcessJobAsync_SupersededState_PublishesAfterAudiobookLockRelease()
        {
            var queuedSource = FileService.GetTempDirectory("move-processor-publish-stale-source");
            await FileService.GetFileAsync(queuedSource, "queued.m4b", "audio");
            var currentBasePath = FileService.GetTempDirectory("move-processor-publish-current");
            await FileService.GetFileAsync(currentBasePath, "current.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-publish-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Deferred State Publication",
                BasePath = currentBasePath
            });
            var (_, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                target,
                queuedSource);
            using var coordinator = new TrackingAudiobookOperationCoordinator();
            var queue = new Mock<IMoveQueueService>(MockBehavior.Strict);
            queue.Setup(service => service.UpdateJobStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Running,
                    null,
                    It.IsAny<CancellationToken>()))
                .Callback(() => Assert.False(coordinator.IsExecuting))
                .Returns(Task.CompletedTask);
            queue.Setup(service => service.UpdateJobStatusWithoutNotificationAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Superseded,
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            queue.Setup(service => service.NotifyPersistedJobStateAsync(
                    job.Id,
                    MoveJobStatus.Superseded,
                    It.Is<string?>(error => error != null && error.Contains("source path changed", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<CancellationToken>()))
                .Callback(() => Assert.False(coordinator.IsExecuting))
                .Returns(Task.CompletedTask);
            var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
                _provider,
                queue.Object,
                coordinator);

            await processor.ProcessJobAsync(job, CancellationToken.None);

            queue.Verify(service => service.UpdateJobStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Running,
                    null,
                    It.IsAny<CancellationToken>()),
                Times.Once);
            queue.Verify(service => service.UpdateJobStatusWithoutNotificationAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Superseded,
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            queue.VerifyAll();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ProcessJobAsync_MalformedPersistedEndpoint_RequiresAttentionWithoutMutation(
            bool malformedSource)
        {
            var source = FileService.GetTempDirectory("move-processor-malformed-endpoint-source");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-malformed-endpoint-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Malformed Endpoint",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            if (malformedSource)
            {
                job.SourcePath = "malformed\0source";
            }
            else
            {
                job.RequestedPath = "malformed\0target";
            }

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob.Status);
            Assert.Contains("persisted", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(Directory.Exists(target));
        }

        [WindowsFact]
        public async Task ProcessJobAsync_ForeignPersistedSourceSyntax_CannotAliasWindowsSource()
        {
            var source = FileService.GetTempDirectory("move-processor-foreign-endpoint-source");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-foreign-endpoint-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Foreign Endpoint",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            job.SourcePath = "/" + Path.GetRelativePath(
                    Path.GetPathRoot(source)!,
                    source)
                .Replace('\\', '/');

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob.Status);
            Assert.Contains("persisted source path", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task ProcessJobAsync_LegacyJobWithoutSourcePath_RequiresAttentionWithoutMovingCurrentBasePath()
        {
            var source = FileService.GetTempDirectory("move-processor-legacy-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-legacy-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Legacy Source",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                target,
                source);
            var jobId = job.Id;
            job.SourcePath = null;
            job.SourcePathSyntax = null;
            job.SourceCaseSensitivity = null;
            job.SourceCaseSensitivityMode = null;
            job.SourceIdentityBoundary = null;

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(jobId));
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob.Status);
            Assert.Contains("persisted source path", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(Directory.Exists(target));
        }

        private sealed class ThrowUnexpectedAfterPublish : IMoveFaultInjector
        {
            public Task AfterPublishedAsync(
                Guid jobId,
                CancellationToken cancellationToken) =>
                Task.FromException(new InvalidOperationException(
                    "Simulated unexpected post-publication failure."));
        }

        private sealed class TrackingAudiobookOperationCoordinator : IAudiobookOperationCoordinator, IDisposable
        {
            private readonly AudiobookOperationCoordinator _inner = new();
            private int _executing;

            public bool IsExecuting => Volatile.Read(ref _executing) != 0;

            public Task ExecuteExclusiveAsync(
                int audiobookId,
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken = default) =>
                _inner.ExecuteExclusiveAsync(
                    audiobookId,
                    token => TrackAsync(operation, token),
                    cancellationToken);

            public Task<T> ExecuteExclusiveAsync<T>(
                int audiobookId,
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken = default) =>
                _inner.ExecuteExclusiveAsync(
                    audiobookId,
                    token => TrackAsync(operation, token),
                    cancellationToken);

            public Task ExecuteExclusiveAsync(
                IEnumerable<int> audiobookIds,
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken = default) =>
                _inner.ExecuteExclusiveAsync(
                    audiobookIds,
                    token => TrackAsync(operation, token),
                    cancellationToken);

            public Task<T> ExecuteExclusiveAsync<T>(
                IEnumerable<int> audiobookIds,
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken = default) =>
                _inner.ExecuteExclusiveAsync(
                    audiobookIds,
                    token => TrackAsync(operation, token),
                    cancellationToken);

            private async Task TrackAsync(
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _executing);
                try
                {
                    await operation(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _executing);
                }
            }

            private async Task<T> TrackAsync<T>(
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _executing);
                try
                {
                    return await operation(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _executing);
                }
            }

            public void Dispose() => _inner.Dispose();
        }

        private sealed class BehavioralSemanticsResolver : IFileSystemSemanticsResolver
        {
            private readonly FileSystemSemanticsResolver _inner = new();

            public async ValueTask<FileSystemSemanticsResolution> ResolveAsync(
                string path,
                FileSystemCaseSensitivityMode mode,
                CancellationToken cancellationToken = default)
            {
                var resolution = await _inner.ResolveAsync(path, mode, cancellationToken);
                return mode == FileSystemCaseSensitivityMode.Auto
                    && resolution.State == PathIdentityState.Valid
                    ? resolution with
                    {
                        EvidenceKind = FileSystemSemanticsEvidenceKind.BehavioralObservation
                    }
                    : resolution;
            }
        }

        private sealed class ToggleUnavailableSemanticsResolver : IFileSystemSemanticsResolver
        {
            private readonly FileSystemSemanticsResolver _inner = new();

            public bool ReturnUnavailable { get; set; }

            public async ValueTask<FileSystemSemanticsResolution> ResolveAsync(
                string path,
                FileSystemCaseSensitivityMode mode,
                CancellationToken cancellationToken = default)
            {
                if (!ReturnUnavailable)
                {
                    return await _inner.ResolveAsync(path, mode, cancellationToken);
                }

                var syntax = OperatingSystem.IsWindows()
                    ? FileSystemPathSyntax.Windows
                    : FileSystemPathSyntax.Unix;
                return new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        syntax,
                        FileSystemCaseSensitivity.Unknown),
                    PathIdentityState.Unavailable,
                    Path.GetFullPath(path),
                    "Injected transient filesystem semantics outage.",
                    Path.GetFullPath(path));
            }
        }

        private static async Task PrepareJobForProcessingAsync(IMoveQueueService queue, MoveJob job)
        {
            var leaseGeneration = Assert.IsType<int>(
                await queue.TryClaimJobAsync(job.Id, LeaseOwner));
            job.LeaseOwner = LeaseOwner;
            job.LeaseGeneration = leaseGeneration;
        }

        private async Task RecordOwnedDirectoryHierarchyAsync(
            string managedBoundary,
            string deepestOwnedDirectory)
        {
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var boundary = FileSystemPathIdentity.Canonicalize(
                managedBoundary,
                semantics.Syntax);
            await AddAuthorizedRootAsync(boundary, "Move Job Test Root");
            var current = FileSystemPathIdentity.Canonicalize(
                deepestOwnedDirectory,
                semantics.Syntax);
            var directories = new List<string>();
            while (!FileSystemPathIdentity.AreEquivalent(current, boundary, semantics))
            {
                if (!FileSystemPathIdentity.IsSameOrInside(current, boundary, semantics))
                {
                    throw new InvalidOperationException(
                        "The test-owned directory escaped its managed boundary.");
                }

                directories.Add(current);
                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidOperationException(
                        "The test-owned directory has no parent.");
            }

            directories.Reverse();
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var operationId = Guid.NewGuid();
            foreach (var directory in directories)
            {
                await ownershipStore.RecordCreatedAsync(
                    new LibraryDirectoryOwnershipClaim(
                        directory,
                        semantics,
                        "test-fixture",
                        operationId));
            }
        }

        private async Task<(IMoveQueueService Queue, MoveJob Job)> CreateQueuedMoveJobAsync(
            Audiobook audiobook,
            string requestedPath,
            string sourcePath,
            bool deleteEmptySource = true,
            int executionProtocolVersion = MoveExecutionProtocol.Current,
            FileSystemCaseSensitivityMode sourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            FileSystemCaseSensitivityMode targetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            MoveSourceCleanupMode sourceCleanupMode = MoveSourceCleanupMode.RetainSource,
            bool forceCopyAndRetainSource = false)
        {
            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var semanticsResolver = _provider
                .GetRequiredService<IFileSystemSemanticsResolver>();
            var sourceResolution = await semanticsResolver.ResolveAsync(
                sourcePath,
                sourceCaseSensitivityMode);
            var targetResolution = await semanticsResolver.ResolveAsync(
                requestedPath,
                targetCaseSensitivityMode);
            Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
            Assert.Equal(PathIdentityState.Valid, targetResolution.State);
            var sourceBoundary = await FindSourceBoundaryAsync(
                sourcePath,
                sourceResolution.Semantics);
            var sourceIdentity = PathIdentitySnapshot.FromResolution(
                sourceResolution.Semantics,
                sourceCaseSensitivityMode,
                sourceBoundary,
                sourcePath);
            var targetBoundary = FindTargetBoundary(
                requestedPath,
                targetResolution.Semantics);
            var targetIdentity = PathIdentitySnapshot.FromResolution(
                targetResolution.Semantics,
                targetCaseSensitivityMode,
                targetBoundary,
                requestedPath);
            var directoryIdentityResolver = _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>();
            var sourceDirectoryIdentity = await directoryIdentityResolver
                .ResolveAsync(sourceBoundary);
            Assert.True(
                sourceDirectoryIdentity.IsAvailable,
                sourceDirectoryIdentity.UnavailableReason);
            var targetDirectoryIdentity = await directoryIdentityResolver
                .ResolveAsync(targetBoundary);
            Assert.True(
                targetDirectoryIdentity.IsAvailable,
                targetDirectoryIdentity.UnavailableReason);
            var manifest = await BuildMoveManifestAsync(sourcePath);
            await EnsureTrackedManifestRowsAsync(
                audiobook,
                sourcePath,
                sourceIdentity,
                manifest);
            var jobId = await queue.EnqueueMoveAsync(
                new MoveEnqueueCommand(
                    audiobook.Id,
                    sourcePath,
                    sourceIdentity,
                    manifest,
                    requestedPath,
                    targetIdentity,
                    sourceDirectoryIdentity.Version!.Value,
                    sourceDirectoryIdentity.Value!,
                    targetDirectoryIdentity.Version!.Value,
                    targetDirectoryIdentity.Value!,
                    deleteEmptySource,
                    deleteEmptySource ? sourceBoundary : null,
                    SourceCleanupMode: sourceCleanupMode,
                    ForceCopyAndRetainSource: forceCopyAndRetainSource));
            var job = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(jobId));
            if (job.ExecutionProtocolVersion != executionProtocolVersion)
            {
                var factory = _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                var persisted = await db.MoveJobs.SingleAsync(
                    candidate => candidate.Id == job.Id);
                persisted.ExecutionProtocolVersion = executionProtocolVersion;
                await db.SaveChangesAsync();
                job.ExecutionProtocolVersion = executionProtocolVersion;
            }
            await PrepareJobForProcessingAsync(queue, job);
            return (queue, job);
        }

        private async Task<string> FindSourceBoundaryAsync(
            string sourcePath,
            FileSystemPathSemantics sourceSemantics)
        {
            var source = Path.GetFullPath(sourcePath);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var roots = await db.RootFolders.AsNoTracking().ToListAsync();
            var matchingRoot = roots
                .Select(root => FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        root.Path,
                        out var canonical,
                        out _)
                    ? canonical
                    : null)
                .Where(path => path != null
                    && FileSystemPathIdentity.IsSameOrInside(
                        source,
                        path,
                        sourceSemantics))
                .OrderByDescending(path => path!.Length)
                .FirstOrDefault();
            if (matchingRoot != null)
            {
                return matchingRoot;
            }

            var managedRoot = Path.GetFullPath(FileService.GetTempPath());
            if (FileSystemPathIdentity.IsSameOrInside(
                    source,
                    managedRoot,
                    sourceSemantics))
            {
                return managedRoot;
            }

            return Path.GetDirectoryName(source)
                ?? source;
        }

        private string FindTargetBoundary(
            string targetPath,
            FileSystemPathSemantics targetSemantics)
        {
            var target = Path.GetFullPath(targetPath);
            var managedRoot = Path.GetFullPath(FileService.GetTempPath());
            if (FileSystemPathIdentity.IsSameOrInside(
                    target,
                    managedRoot,
                    targetSemantics))
            {
                return managedRoot;
            }

            var current = Path.GetDirectoryName(target);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }
                current = Path.GetDirectoryName(current);
            }

            throw new InvalidOperationException(
                "Move test target has no existing authorization boundary.");
        }

        private async Task EnsureTrackedManifestRowsAsync(
            Audiobook audiobook,
            string sourcePath,
            PathIdentitySnapshot sourceIdentity,
            IReadOnlyCollection<MoveSourceManifestEntry> manifest)
        {
            var existing = await _audiobookFileRepository
                .GetByAudiobookIdAsync(audiobook.Id);
            foreach (var entry in manifest.Where(candidate =>
                candidate.EntryType == MoveJobEntryType.File))
            {
                Assert.True(FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    sourcePath,
                    entry.RelativePath,
                    sourceIdentity.Semantics,
                    out var fullPath));
                var identity = AudiobookFilePathIdentity.CreateValid(
                    fullPath,
                    sourceIdentity.Semantics,
                    sourceIdentity.RequestedMode,
                    sourceIdentity.BoundaryPath);
                var tracked = existing.FirstOrDefault(file =>
                    !string.IsNullOrWhiteSpace(file.Path)
                    && FileSystemPathIdentity.AreEquivalent(
                        file.Path,
                        fullPath,
                        sourceIdentity.Semantics));
                if (tracked != null)
                {
                    tracked.ApplyPathIdentity(fullPath, identity);
                    ApplyTestPhysicalObjectIdentity(tracked, fullPath);
                    await _audiobookFileRepository.UpdateAsync(tracked);
                    continue;
                }

                tracked = AudiobookFile.CreateUnresolved(fullPath);
                tracked.AudiobookId = audiobook.Id;
                tracked.ApplyPathIdentity(fullPath, identity);
                ApplyTestPhysicalObjectIdentity(tracked, fullPath);
                var claim = await _audiobookFileRepository.ClaimAsync(tracked);
                Assert.Equal(AudiobookFileClaimOutcome.Created, claim.Outcome);
                existing.Add(Assert.IsType<AudiobookFile>(claim.File));
            }
        }

        private static void ApplyTestPhysicalObjectIdentity(
            AudiobookFile file,
            string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return;
            }

            var parentPath = Path.GetDirectoryName(fullPath)!;
            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            using var entry = parent.OpenExistingFileForStableRead(
                Path.GetFileName(fullPath));
            file.ApplyPhysicalObjectIdentity(
                entry.GetObjectIdentity(),
                DateTime.UtcNow);
        }

        private static async Task<IReadOnlyList<MoveSourceManifestEntry>> BuildMoveManifestAsync(
            string sourcePath)
        {
            if (!Directory.Exists(sourcePath))
            {
                return
                [
                    new MoveSourceManifestEntry(
                        "book.m4b",
                        MoveJobEntryType.File,
                        1,
                        DateTime.UnixEpoch,
                        new string('A', 64))
                ];
            }

            var entries = new List<MoveSourceManifestEntry>();
            var pending = new Stack<string>();
            pending.Push(sourcePath);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(sourcePath, path);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        entries.Add(new MoveSourceManifestEntry(
                            relativePath,
                            MoveJobEntryType.Directory,
                            0,
                            Directory.GetLastWriteTimeUtc(path),
                            null));
                        pending.Push(path);
                        continue;
                    }

                    var bytes = await File.ReadAllBytesAsync(path);
                    entries.Add(new MoveSourceManifestEntry(
                        relativePath,
                        MoveJobEntryType.File,
                        bytes.LongLength,
                        File.GetLastWriteTimeUtc(path),
                        Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(bytes))));
                }
            }

            return entries.Count > 0
                ? entries
                :
                [
                    new MoveSourceManifestEntry(
                        "book.m4b",
                        MoveJobEntryType.File,
                        1,
                        DateTime.UnixEpoch,
                        new string('A', 64))
                ];
        }
    }
}
