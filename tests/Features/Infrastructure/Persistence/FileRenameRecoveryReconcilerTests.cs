using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "FileRenameRecoveryReconcilerTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRenameRecoveryReconcilerTests : BaseTests
{
    [Fact]
    public async Task ReconcileAsync_CommittedCompanionPublication_RetiresExactSource()
    {
        var root = FileService.GetTempDirectory("companion-registration-recovery");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "library");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "cover.jpg");
        var destination = Path.Join(destinationDirectory, "cover.jpg");
        await File.WriteAllTextAsync(source, "cover");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Companion Recovery")
                .WithBasePath(destinationDirectory)
                .Build());
        var operationId = Guid.NewGuid();
        var mover = _provider.GetRequiredService<FileMover>();
        var capability = await mover.CheckAsync(source);
        Assert.True(capability.IsSupported, capability.Reason);

        var preparation = await mover.PrepareActionForRegistrationDetailedAsync(
            FilePublicationPlan.Durable(FileAction.Move),
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            capability.SourceProof!.Value,
            isCompanionFile: true,
            companionAudiobookId: audiobook.Id);
        using (var lease = Assert.IsAssignableFrom<
            IAudiobookFileRegistrationLease>(preparation.RegistrationLease))
        {
            Assert.True(lease.PrepareCleanupRecovery(audiobook.Id));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                lease.CompletePublication());
        }

        Assert.True(File.Exists(source));
        Assert.Equal("cover", await File.ReadAllTextAsync(destination));

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        Assert.False(File.Exists(source));
        Assert.Equal("cover", await File.ReadAllTextAsync(destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals.SingleAsync(candidate =>
            candidate.OperationId == operationId);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Equal(audiobook.Id, journal.AudiobookId);
        Assert.Equal(
            FileMutationOwner.RegistrationCompanionFile,
            journal.AudiobookFileId);
    }

    [Fact]
    public async Task ReconcileAsync_CompletedFilesystemRenameBeforeMetadataCommit_RepairsTrackedPath()
    {
        var scenario = await CreateScenarioAsync("completed-before-metadata");
        var mover = _provider.GetRequiredService<FileMover>();

        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            scenario.AudiobookId,
            scenario.FileId));
        Assert.False(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        await AssertRecoveredAsync(scenario);
    }

    [Fact]
    public async Task ReconcileAsync_LegacyCompletedFilesystemRename_StillRepairsOwnerMetadata()
    {
        var scenario = await CreateScenarioAsync("legacy-completed-before-metadata");
        var mover = _provider.GetRequiredService<FileMover>();

        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            scenario.AudiobookId,
            scenario.FileId));

        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
            Assert.Equal(FileMutationJournalState.Completed, journal.State);
            journal.ProtocolVersion = FileMutationProtocol.MarkerlessDatabaseState;
            journal.SourceParentDirectoryObjectIdentity = string.Empty;
            journal.DestinationParentDirectoryObjectIdentity = string.Empty;
            await db.SaveChangesAsync();
        }

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        await AssertRecoveredAsync(scenario);
    }

    [LinuxFact]
    public async Task ReconcileAsync_TargetReplacedAfterRecoveryProbe_DoesNotCommitOwnerMetadata()
    {
        var scenario = await CreateScenarioAsync("recovery-target-replaced-before-metadata");
        var mover = _provider.GetRequiredService<FileMover>();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            scenario.AudiobookId,
            scenario.FileId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);

        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var reconciler = new FileRenameRecoveryReconciler(
            factory,
            mover,
            _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>(),
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            TimeProvider.System,
            NullLogger<FileRenameRecoveryReconciler>.Instance)
        {
            BeforeOwnerMetadataCommitForTestAsync = operationId =>
            {
                Assert.Equal(scenario.OperationId, operationId);
                File.Delete(scenario.Destination);
                File.WriteAllText(scenario.Destination, "foreign-target");
                return Task.CompletedTask;
            }
        };

        await reconciler.ReconcileAsync();

        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
    }

    [LinuxFact]
    public async Task ReconcileAsync_TargetReplacedAfterOwnerMetadataSave_RollsBackRecoveryCommit()
    {
        var root = FileService.GetTempDirectory("rename-recovery-relational-post-save");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "Old Folder");
        var destinationDirectory = Path.Join(root, "New Folder");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "Source.m4b");
        var destination = Path.Join(destinationDirectory, "Renamed.m4b");
        await File.WriteAllTextAsync(source, "audio");

        var audiobook = new AudiobookBuilder()
            .WithTitle("Relational Recovery Book")
            .WithBasePath(sourceDirectory)
            .WithFilePath(source)
            .Build();
        var identityResolver = _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>();
        var identity = await identityResolver.ResolveAsync(audiobook, source);
        Assert.Equal(PathIdentityState.Valid, identity.State);
        var file = AudiobookFile.CreateUnresolved(source);
        file.ApplyPathIdentity(source, identity);
        var sourceIdentity = GetFileIdentity(source);
        file.ApplyPhysicalObjectIdentity(sourceIdentity, DateTime.UtcNow);
        audiobook.Files = [file];

        var operationId = Guid.NewGuid();
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Audiobooks.Add(audiobook);
            await setup.SaveChangesAsync();

            File.Move(source, destination);
            Assert.Equal(sourceIdentity, GetFileIdentity(destination));
            setup.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = operationId,
                Action = FileAction.Move,
                SourcePath = source,
                DestinationPath = destination,
                SourcePhysicalObjectIdentity = sourceIdentity,
                SourceLength = new FileInfo(destination).Length,
                TargetPhysicalObjectIdentity = sourceIdentity,
                AudiobookId = audiobook.Id,
                AudiobookFileId = file.Id,
                State = FileMutationJournalState.Completed
            });
            await setup.SaveChangesAsync();
        }

        var factory = new TestDbContextFactory(options);
        var reconciler = new FileRenameRecoveryReconciler(
            factory,
            _provider.GetRequiredService<FileMover>(),
            identityResolver,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            TimeProvider.System,
            NullLogger<FileRenameRecoveryReconciler>.Instance)
        {
            AfterOwnerMetadataSaveBeforeCommitForTestAsync = candidateOperationId =>
            {
                Assert.Equal(operationId, candidateOperationId);
                File.Delete(destination);
                File.WriteAllText(destination, "foreign-target");
                return Task.CompletedTask;
            }
        };

        await reconciler.ReconcileAsync();

        await using var verification = new ListenArrDbContext(options);
        var persistedFile = await verification.AudiobookFiles
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == file.Id);
        Assert.Equal(source, persistedFile.Path);
        Assert.Equal(
            FileMutationJournalState.NeedsAttention,
            (await verification.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId)).State);
        Assert.False(File.Exists(source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(destination));
    }

    [WindowsFact]
    public async Task ReconcileAsync_CompletedDestinationSharingViolation_LeavesJournalPendingUntilRetry()
    {
        var scenario = await CreateScenarioAsync("completed-destination-sharing");
        var mover = _provider.GetRequiredService<FileMover>();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            scenario.AudiobookId,
            scenario.FileId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed);

        await using (var destinationLock = new FileStream(
            scenario.Destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
                .ReconcileAsync();

            await AssertJournalStateAsync(
                scenario.OperationId,
                FileMutationJournalState.Completed);
            await AssertStoredPathAsync(scenario.FileId, scenario.Source);
            Assert.True(await _provider.GetRequiredService<IFileRenameRecoveryProbe>()
                .HasBlockingAsync(scenario.AudiobookId));
        }

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        await AssertRecoveredAsync(scenario);
    }

    [Fact]
    public async Task ReconcileAsync_OwnerBindingChangesAfterInitialRead_MarksJournalNeedsAttention()
    {
        var scenario = await CreateScenarioAsync("owner-binding-changed");
        var mover = _provider.GetRequiredService<FileMover>();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            scenario.AudiobookId,
            scenario.FileId));

        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var reconciler = new FileRenameRecoveryReconciler(
            factory,
            mover,
            _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>(),
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            TimeProvider.System,
            NullLogger<FileRenameRecoveryReconciler>.Instance)
        {
            AfterInitialOwnerBindingLoadedForTestAsync = async operationId =>
            {
                await using var db = await factory.CreateDbContextAsync();
                var journal = await db.FileMutationJournals
                    .SingleAsync(candidate => candidate.OperationId == operationId);
                journal.AudiobookFileId = null;
                await db.SaveChangesAsync();
            }
        };

        await reconciler.ReconcileAsync();

        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);
        Assert.False(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
    }

    [Fact]
    public async Task ReconcileAsync_CompletedForwardRenameThatWasRolledBack_RecognizesCompensation()
    {
        var scenario = await CreateScenarioAsync("completed-then-rolled-back");
        var mover = _provider.GetRequiredService<FileMover>();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            scenario.AudiobookId,
            scenario.FileId));
        var rollbackOperationId = Guid.NewGuid();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Destination,
            scenario.Source,
            scenario.SourceIdentity,
            rollbackOperationId,
            scenario.AudiobookId,
            scenario.FileId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        Assert.True(File.Exists(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.OwnerMetadataReconciled);
        await AssertJournalStateAsync(
            rollbackOperationId,
            FileMutationJournalState.OwnerMetadataReconciled);
    }

    [LinuxFact]
    public async Task ReconcileAsync_CompensationSourceReplacedBeforeTerminalCommit_DoesNotReconcileOwnerMetadata()
    {
        var scenario = await CreateScenarioAsync("compensation-source-replaced-before-terminal");
        var mover = _provider.GetRequiredService<FileMover>();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            scenario.AudiobookId,
            scenario.FileId));
        var rollbackOperationId = Guid.NewGuid();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Destination,
            scenario.Source,
            scenario.SourceIdentity,
            rollbackOperationId,
            scenario.AudiobookId,
            scenario.FileId));
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);

        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var reconciler = new FileRenameRecoveryReconciler(
            factory,
            mover,
            _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>(),
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            TimeProvider.System,
            NullLogger<FileRenameRecoveryReconciler>.Instance)
        {
            BeforeOwnerMetadataCommitForTestAsync = operationId =>
            {
                if (operationId == scenario.OperationId)
                {
                    File.Delete(scenario.Source);
                    File.WriteAllText(scenario.Source, "foreign-source");
                }
                return Task.CompletedTask;
            }
        };

        await reconciler.ReconcileAsync();

        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);
        Assert.Equal("foreign-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
    }

    [Fact]
    public async Task ReconcileAsync_CrashDuringOwnerBoundRollback_ResumesRollbackAndReconcilesBothJournals()
    {
        var scenario = await CreateScenarioAsync("crash-during-rollback");
        var mover = _provider.GetRequiredService<FileMover>();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            scenario.AudiobookId,
            scenario.FileId));

        var rollbackOperationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var interruptedRollback = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "rename-rollback-recovery-locks"),
            AfterMarkerlessRenamePublishedBeforeTargetStateForTestAsync = () =>
                throw new IOException("Injected process crash during organize rollback.")
        };
        await Assert.ThrowsAsync<IOException>(() =>
            interruptedRollback.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Destination,
                scenario.Source,
                scenario.SourceIdentity,
                rollbackOperationId,
                scenario.AudiobookId,
                scenario.FileId));

        Assert.True(File.Exists(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed);
        await AssertJournalStateAsync(
            rollbackOperationId,
            FileMutationJournalState.Planned);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        Assert.True(File.Exists(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.OwnerMetadataReconciled);
        await AssertJournalStateAsync(
            rollbackOperationId,
            FileMutationJournalState.OwnerMetadataReconciled);
    }

    [WindowsFact]
    public async Task ReconcileAsync_SourceSharingViolation_LeavesJournalPendingInsteadOfFailingStartup()
    {
        var scenario = await CreateScenarioAsync("sharing-violation-pending");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var interrupted = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "rename-sharing-recovery-locks"),
            AfterMarkerlessRenameJournalPlannedForTestAsync = () =>
                throw new IOException("Injected crash after organize journal planning.")
        };
        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId,
                scenario.AudiobookId,
                scenario.FileId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned);

        await using (var sourceLock = new FileStream(
            scenario.Source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
                .ReconcileAsync();

            await AssertJournalStateAsync(
                scenario.OperationId,
                FileMutationJournalState.Planned);
            Assert.True(File.Exists(scenario.Source));
            Assert.False(File.Exists(scenario.Destination));
            Assert.True(await _provider.GetRequiredService<IFileRenameRecoveryProbe>()
                .HasBlockingAsync(scenario.AudiobookId));
        }

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        await AssertRecoveredAsync(scenario);
    }

    [Fact]
    public async Task ReconcileAsync_ReadOnlyRemountDuringOwnedMove_LeavesJournalPending()
    {
        var scenario = await CreateScenarioAsync("erofs-pending");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var interrupted = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "rename-erofs-recovery-locks"),
            AfterMarkerlessRenameJournalPlannedForTestAsync = () =>
                throw new IOException("Injected crash after organize journal planning.")
        };
        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId,
                scenario.AudiobookId,
                scenario.FileId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned);

        var mover = new Mock<IFileMover>(MockBehavior.Strict);
        mover.Setup(service => service.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId,
                scenario.AudiobookId,
                scenario.FileId))
            .ThrowsAsync(new InvalidOperationException(
                "Injected wrapped read-only filesystem failure.",
                new System.ComponentModel.Win32Exception(30)));
        var reconciler = new FileRenameRecoveryReconciler(
            factory,
            mover.Object,
            _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>(),
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            TimeProvider.System,
            NullLogger<FileRenameRecoveryReconciler>.Instance);

        await reconciler.ReconcileAsync();

        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);
        Assert.True(File.Exists(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        mover.VerifyAll();
    }

    [Fact]
    public async Task ReconcileAsync_OwnerBoundNeedsAttentionJournal_FailsStartupRecovery()
    {
        var scenario = await CreateScenarioAsync("needs-attention");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = scenario.OperationId,
                Action = FileAction.Move,
                SourcePath = scenario.Source,
                DestinationPath = scenario.Destination,
                SourcePhysicalObjectIdentity = scenario.SourceIdentity,
                SourceLength = new FileInfo(scenario.Source).Length,
                AudiobookId = scenario.AudiobookId,
                AudiobookFileId = scenario.FileId,
                State = FileMutationJournalState.NeedsAttention,
                Error = "Injected unresolved organize state."
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
                .ReconcileAsync());

        Assert.Contains("requires operator repair", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);
        Assert.True(File.Exists(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
    }

    [Fact]
    public async Task ReconcileAsync_CrashAfterNativeRenameBeforeJournalTargetState_ResumesAndRepairsMetadata()
    {
        var scenario = await CreateScenarioAsync("published-before-target-state");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var interrupted = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "rename-recovery-locks"),
            AfterMarkerlessRenamePublishedBeforeTargetStateForTestAsync = () =>
                throw new IOException("Injected process crash after native rename publication.")
        };

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId,
                scenario.AudiobookId,
                scenario.FileId));
        Assert.False(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned);
        await AssertStoredPathAsync(scenario.FileId, scenario.Source);

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        await AssertRecoveredAsync(scenario);
    }

    [Fact]
    public async Task ReconcileAsync_InterruptedCompanionMove_ResumesWithoutRewritingAudiobookMetadata()
    {
        var root = FileService.GetTempDirectory("companion-recovery");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "incoming");
        var destinationDirectory = Path.Join(root, "library", "book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = await FileService.GetFileAsync(sourceDirectory, "cover.jpg", "cover");
        var destination = Path.Join(destinationDirectory, "cover.jpg");
        var primaryFile = await FileService.GetFileAsync(destinationDirectory, "book.m4b", "audio");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Companion Recovery")
            .WithBasePath(destinationDirectory)
            .WithFilePath(primaryFile)
            .Build());
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var interrupted = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "companion-recovery-locks"),
            AfterMarkerlessMovePublishedBeforeTargetStateForTestAsync = () =>
                throw new IOException("Injected crash after companion publication.")
        };

        var interruption = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interrupted.PerformActionOn(
                FileAction.Move,
                source,
                destination,
                operationId,
                audiobook.Id,
                FileMutationOwner.CompanionFile));
        Assert.IsType<IOException>(interruption.InnerException);
        Assert.False(File.Exists(source));
        Assert.Equal("cover", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(operationId, FileMutationJournalState.Planned);
        Assert.True(await _provider.GetRequiredService<IFileRenameRecoveryProbe>()
            .HasBlockingAsync(audiobook.Id));

        await _provider.GetRequiredService<IFileRenameRecoveryReconciler>()
            .ReconcileAsync();

        Assert.False(File.Exists(source));
        Assert.Equal("cover", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(operationId, FileMutationJournalState.Completed);
        Assert.False(await _provider.GetRequiredService<IFileRenameRecoveryProbe>()
            .HasBlockingAsync(audiobook.Id));
        var persisted = Assert.IsType<Audiobook>(
            await _audiobookRepository.GetByIdAsync(audiobook.Id));
        Assert.Equal(destinationDirectory, persisted.BasePath);
        Assert.Equal(primaryFile, persisted.FilePath);
    }

    private async Task<Scenario> CreateScenarioAsync(string name)
    {
        var root = FileService.GetTempDirectory($"rename-recovery-{name}");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "Old Folder");
        var destinationDirectory = Path.Join(root, "New Folder");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "Source.m4b");
        var destination = Path.Join(destinationDirectory, "Renamed.m4b");
        await File.WriteAllTextAsync(source, "audio");

        var audiobook = new AudiobookBuilder()
            .WithTitle("Recovery Book")
            .WithBasePath(sourceDirectory)
            .WithFilePath(source)
            .Build();
        var identityResolver = _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>();
        var identity = await identityResolver.ResolveAsync(audiobook, source);
        Assert.Equal(PathIdentityState.Valid, identity.State);
        var file = AudiobookFile.CreateUnresolved(source);
        file.ApplyPathIdentity(source, identity);
        var sourceIdentity = GetFileIdentity(source);
        file.ApplyPhysicalObjectIdentity(sourceIdentity, DateTime.UtcNow);
        audiobook.Files = [file];
        var persisted = await _audiobookRepository.AddAsync(audiobook);
        var persistedFile = Assert.Single(persisted.Files!);

        return new Scenario(
            persisted.Id,
            persistedFile.Id,
            source,
            destination,
            sourceIdentity,
            Guid.NewGuid());
    }

    private async Task AssertRecoveredAsync(Scenario scenario)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var audiobook = await db.Audiobooks
            .AsNoTracking()
            .Include(candidate => candidate.Files)
            .SingleAsync(candidate => candidate.Id == scenario.AudiobookId);
        var file = Assert.Single(audiobook.Files!);
        Assert.Equal(Path.GetFullPath(scenario.Destination), file.Path);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(scenario.Destination)), audiobook.BasePath);
        Assert.Equal(Path.GetFullPath(scenario.Destination), audiobook.FilePath);
        Assert.Equal(scenario.SourceIdentity, file.PhysicalObjectIdentity);
        Assert.Equal(
            FileMutationJournalState.OwnerMetadataReconciled,
            (await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == scenario.OperationId)).State);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
    }

    private async Task AssertStoredPathAsync(int fileId, string expected)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var file = await db.AudiobookFiles
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == fileId);
        Assert.Equal(expected, file.Path);
    }

    private async Task AssertJournalStateAsync(
        Guid operationId,
        FileMutationJournalState expected)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(expected, journal.State);
    }

    private static string GetFileIdentity(string path)
    {
        using var lease = PinnedAudiobookFileRegistrationLease.Open(path);
        return lease.PhysicalObjectIdentity;
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<ListenArrDbContext> options) :
        IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync() =>
            Task.FromResult(new ListenArrDbContext(options));
    }

    private sealed record Scenario(
        int AudiobookId,
        int FileId,
        string Source,
        string Destination,
        string SourceIdentity,
        Guid OperationId);
}
