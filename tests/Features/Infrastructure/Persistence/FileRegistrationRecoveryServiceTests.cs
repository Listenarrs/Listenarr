using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Area", "Library")]
[Trait("Name", "FileRegistrationRecoveryServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRegistrationRecoveryServiceTests : BaseTests
{
    [WindowsFact]
    public async Task ReconcileAsync_AnonymousVerifiedMoveWithCommittedTrackedGeneration_AdoptsAndCompletes()
    {
        var root = FileService.GetTempDirectory("registration-adoption");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "book.m4b");
        var destination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var mover = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "registration-adoption-locks")
        };

        var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            source,
            destination,
            operationId);
        Assert.NotNull(lease);
        var physicalObjectIdentity = lease.PhysicalObjectIdentity;

        var audiobook = new AudiobookBuilder()
            .WithTitle("Registration Adoption")
            .WithBasePath(destinationDirectory)
            .WithFilePath(destination)
            .Build();
        var identity = await _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>()
            .ResolveAsync(audiobook, destination);
        Assert.Equal(PathIdentityState.Valid, identity.State);
        var file = AudiobookFile.CreateUnresolved(destination);
        file.ApplyPathIdentity(destination, identity);
        file.ApplyPhysicalObjectIdentity(physicalObjectIdentity, DateTime.UtcNow);
        audiobook.Files = [file];
        var persisted = await _audiobookRepository.AddAsync(audiobook);
        lease.Dispose();

        await using (var db = await factory.CreateDbContextAsync())
        {
            var anonymous = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(FileMutationJournalState.TargetVerified, anonymous.State);
            Assert.Null(anonymous.AudiobookId);
            Assert.Null(anonymous.AudiobookFileId);
        }

        await new FileRegistrationRecoveryService(
                factory,
                mover,
                TimeProvider.System,
                NullLogger<FileRegistrationRecoveryService>.Instance)
            .ReconcileAsync();

        Assert.False(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var completed = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(FileMutationJournalState.Completed, completed.State);
            Assert.Equal(persisted.Id, completed.AudiobookId);
            Assert.Null(completed.AudiobookFileId);
        }
    }

    [Fact]
    public async Task ReconcileAsync_LegacyNonterminalJournal_MarksNeedsAttentionAndBlocksRecovery()
    {
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = operationId,
                ProtocolVersion = FileMutationProtocol.MarkerlessDatabaseState,
                Action = FileAction.Move,
                SourcePath = Path.Join(FileService.GetTempPath(), "legacy-recovery-source.m4b"),
                DestinationPath = Path.Join(FileService.GetTempPath(), "legacy-recovery-target.m4b"),
                SourcePhysicalObjectIdentity = "legacy-source",
                TargetPhysicalObjectIdentity = "legacy-target",
                SourceLength = 1,
                State = FileMutationJournalState.SourceDeleted
            });
            await db.SaveChangesAsync();
        }

        var recovery = new FileRegistrationRecoveryService(
            factory,
            _provider.GetRequiredService<IFileMover>(),
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.ReconcileAsync());

        Assert.Contains("legacy recovery protocol", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await factory.CreateDbContextAsync();
        var persisted = await verification.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(FileMutationJournalState.NeedsAttention, persisted.State);
        Assert.Contains("parent-directory generation", persisted.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAsync_StaleRepairWriter_DoesNotRegressCompletedJournal()
    {
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = operationId,
                Action = FileAction.Move,
                SourcePath = Path.Join(FileService.GetTempPath(), "stale-repair-source.m4b"),
                DestinationPath = Path.Join(FileService.GetTempPath(), "stale-repair-target.m4b"),
                SourcePhysicalObjectIdentity = "stale-repair-source",
                TargetPhysicalObjectIdentity = "stale-repair-target",
                SourceLength = 1,
                AudiobookId = int.MaxValue,
                State = FileMutationJournalState.TargetVerified
            });
            await db.SaveChangesAsync();
        }

        using var clock = new BlockingRecoveryTimeProvider();
        var recovery = new FileRegistrationRecoveryService(
            factory,
            _provider.GetRequiredService<IFileMover>(),
            clock,
            NullLogger<FileRegistrationRecoveryService>.Instance);
        var recoveryTask = Task.Run(() => recovery.ReconcileAsync());
        Assert.True(clock.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

        await using (var concurrent = await factory.CreateDbContextAsync())
        {
            var journal = await concurrent.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == operationId);
            journal.State = FileMutationJournalState.Completed;
            journal.Error = null;
            await concurrent.SaveChangesAsync();
        }

        clock.Release();
        await recoveryTask;

        await using var verification = await factory.CreateDbContextAsync();
        var persisted = await verification.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(FileMutationJournalState.Completed, persisted.State);
        Assert.Null(persisted.Error);
    }

    [Fact]
    public async Task ReconcileAsync_StaleRepairWriter_RelationalCasDoesNotRegressCompletedJournal()
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            $"registration-recovery-cas-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var factory = new TestDbContextFactory(options);
            await using (var setup = await factory.CreateDbContextAsync())
            {
                await setup.Database.EnsureCreatedAsync();
                setup.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = Guid.NewGuid(),
                    Action = FileAction.Move,
                    SourcePath = Path.Join(FileService.GetTempPath(), "relational-stale-source.m4b"),
                    DestinationPath = Path.Join(FileService.GetTempPath(), "relational-stale-target.m4b"),
                    SourcePhysicalObjectIdentity = "relational-stale-source",
                    TargetPhysicalObjectIdentity = "relational-stale-target",
                    SourceLength = 1,
                    AudiobookId = int.MaxValue,
                    State = FileMutationJournalState.TargetVerified
                });
                await setup.SaveChangesAsync();
            }

            Guid operationId;
            await using (var read = await factory.CreateDbContextAsync())
            {
                operationId = await read.FileMutationJournals
                    .Select(journal => journal.OperationId)
                    .SingleAsync();
            }

            using var clock = new BlockingRecoveryTimeProvider();
            var recovery = new FileRegistrationRecoveryService(
                factory,
                Mock.Of<IFileMover>(),
                clock,
                NullLogger<FileRegistrationRecoveryService>.Instance);
            var recoveryTask = Task.Run(() => recovery.ReconcileAsync());
            Assert.True(clock.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

            await using (var concurrent = await factory.CreateDbContextAsync())
            {
                var journal = await concurrent.FileMutationJournals
                    .SingleAsync(candidate => candidate.OperationId == operationId);
                journal.State = FileMutationJournalState.Completed;
                journal.Error = null;
                await concurrent.SaveChangesAsync();
            }

            clock.Release();
            await recoveryTask;

            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(FileMutationJournalState.Completed, persisted.State);
            Assert.Null(persisted.Error);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task ReconcileAudiobookAsync_UnrelatedAmbiguousAnonymousMoves_DoNotBlockScopedRecovery()
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Unrelated Scoped Recovery")
            .Build());
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.AddRange(
                new FileMutationJournal
                {
                    OperationId = Guid.NewGuid(),
                    Action = FileAction.Move,
                    SourcePath = Path.Join(FileService.GetTempPath(), "unrelated-a.m4b"),
                    DestinationPath = Path.Join(FileService.GetTempPath(), "unrelated-target.m4b"),
                    SourcePhysicalObjectIdentity = "unrelated-source-a",
                    TargetPhysicalObjectIdentity = "shared-unrelated-target",
                    SourceLength = 1,
                    State = FileMutationJournalState.TargetVerified
                },
                new FileMutationJournal
                {
                    OperationId = Guid.NewGuid(),
                    Action = FileAction.Move,
                    SourcePath = Path.Join(FileService.GetTempPath(), "unrelated-b.m4b"),
                    DestinationPath = Path.Join(FileService.GetTempPath(), "unrelated-target.m4b"),
                    SourcePhysicalObjectIdentity = "unrelated-source-b",
                    TargetPhysicalObjectIdentity = "shared-unrelated-target",
                    SourceLength = 1,
                    State = FileMutationJournalState.TargetVerified
                });
            await db.SaveChangesAsync();
        }
        var recovery = new FileRegistrationRecoveryService(
            factory,
            _provider.GetRequiredService<IFileMover>(),
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        await recovery.ReconcileAudiobookAsync(audiobook.Id);

        await using var verification = await factory.CreateDbContextAsync();
        var journals = await verification.FileMutationJournals.AsNoTracking().ToListAsync();
        Assert.Equal(2, journals.Count);
        Assert.All(journals, journal => Assert.Null(journal.AudiobookId));
        Assert.All(journals, journal =>
            Assert.Equal(FileMutationJournalState.TargetVerified, journal.State));
    }

    [WindowsFact]
    public async Task ReconcileAsync_MultipleAnonymousMovesShareCommittedTargetGeneration_FailsClosedWithoutRetiringSources()
    {
        var root = FileService.GetTempDirectory("registration-ambiguous-adoption");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var firstSource = Path.Join(sourceDirectory, "first.m4b");
        var secondSource = Path.Join(sourceDirectory, "second.m4b");
        var destination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(firstSource, "audio");
        await File.WriteAllTextAsync(secondSource, "audio");
        var firstOperationId = Guid.NewGuid();
        var secondOperationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var mover = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "registration-ambiguous-adoption-locks")
        };

        string targetIdentity;
        using (var firstLease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            firstSource,
            destination,
            firstOperationId))
        {
            Assert.NotNull(firstLease);
            targetIdentity = firstLease.PhysicalObjectIdentity;
        }
        using (var secondLease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            secondSource,
            destination,
            secondOperationId))
        {
            Assert.NotNull(secondLease);
            Assert.True(secondLease.MatchesPhysicalObjectIdentity(targetIdentity));
        }

        var audiobook = new AudiobookBuilder()
            .WithTitle("Ambiguous Registration Adoption")
            .WithBasePath(destinationDirectory)
            .WithFilePath(destination)
            .Build();
        var identity = await _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>()
            .ResolveAsync(audiobook, destination);
        var file = AudiobookFile.CreateUnresolved(destination);
        file.ApplyPathIdentity(destination, identity);
        file.ApplyPhysicalObjectIdentity(targetIdentity, DateTime.UtcNow);
        audiobook.Files = [file];
        await _audiobookRepository.AddAsync(audiobook);

        var recovery = new FileRegistrationRecoveryService(
            factory,
            mover,
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.ReconcileAsync());

        Assert.Contains("shares its published target generation", exception.Message);
        Assert.True(File.Exists(firstSource));
        Assert.True(File.Exists(secondSource));
        await using var db = await factory.CreateDbContextAsync();
        var journals = await db.FileMutationJournals
            .AsNoTracking()
            .Where(candidate => candidate.OperationId == firstOperationId
                || candidate.OperationId == secondOperationId)
            .ToListAsync();
        Assert.Equal(2, journals.Count);
        Assert.All(journals, journal => Assert.Null(journal.AudiobookId));
        Assert.All(journals, journal =>
            Assert.Equal(FileMutationJournalState.TargetVerified, journal.State));
    }

    [WindowsFact]
    public async Task ReconcileAsync_AnonymousVerifiedMoveWithoutCommittedTrackedGeneration_RemainsRetryable()
    {
        var root = FileService.GetTempDirectory("registration-anonymous-retry");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "book.m4b");
        var destination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var mover = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "registration-anonymous-retry-locks")
        };

        using (var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            source,
            destination,
            operationId))
        {
            Assert.NotNull(lease);
        }
        Assert.True(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));

        await new FileRegistrationRecoveryService(
                factory,
                mover,
                TimeProvider.System,
                NullLogger<FileRegistrationRecoveryService>.Instance)
            .ReconcileAsync();

        Assert.True(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await using var db = await factory.CreateDbContextAsync();
        var anonymous = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(FileMutationJournalState.TargetVerified, anonymous.State);
        Assert.Null(anonymous.AudiobookId);
        Assert.Null(anonymous.AudiobookFileId);
    }

    [WindowsFact]
    public async Task ReconcileAsync_RegisteredDestinationSharingViolation_LeavesRecoveryPendingWithoutFailingStartup()
    {
        var root = FileService.GetTempDirectory("registration-target-lock");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "book.m4b");
        var destination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var mover = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "registration-target-lock-files")
        };
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            source,
            destination,
            operationId);
        Assert.NotNull(lease);
        var audiobook = new AudiobookBuilder()
            .WithTitle("Registration Target Lock")
            .WithBasePath(destinationDirectory)
            .WithFilePath(destination)
            .Build();
        var identity = await _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>()
            .ResolveAsync(audiobook, destination);
        var file = AudiobookFile.CreateUnresolved(destination);
        file.ApplyPathIdentity(destination, identity);
        file.ApplyPhysicalObjectIdentity(lease.PhysicalObjectIdentity, DateTime.UtcNow);
        audiobook.Files = [file];
        var persisted = await _audiobookRepository.AddAsync(audiobook);
        Assert.True(lease.PrepareCleanupRecovery(persisted.Id));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());
        await using (var sourceLock = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            Assert.False(await mover.CompletePreparedMoveAsync(
                source,
                destination,
                lease,
                operationId));
        }
        lease.Dispose();

        var recovery = new FileRegistrationRecoveryService(
            factory,
            mover,
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);
        await using (var targetLock = new FileStream(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            await recovery.ReconcileAsync();

            await using var db = await factory.CreateDbContextAsync();
            var pending = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(FileMutationJournalState.SourceDeletionAuthorized, pending.State);
            Assert.True(File.Exists(source));
            Assert.True(await new FileRegistrationRecoveryProbe(factory)
                .HasBlockingAsync(persisted.Id));
        }

        await recovery.ReconcileAsync();

        Assert.False(File.Exists(source));
        Assert.False(await new FileRegistrationRecoveryProbe(factory)
            .HasBlockingAsync(persisted.Id));
    }

    [Fact]
    public async Task ReconcileAsync_ReadOnlyRemountDuringSourceRetirement_LeavesRecoveryPending()
    {
        var root = FileService.GetTempDirectory("registration-erofs-pending");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "book.m4b");
        var destination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var realMover = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "registration-erofs-pending-locks")
        };
        using var lease = await realMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            source,
            destination,
            operationId);
        Assert.NotNull(lease);

        var audiobook = new AudiobookBuilder()
            .WithTitle("Registration EROFS Pending")
            .WithBasePath(destinationDirectory)
            .WithFilePath(destination)
            .Build();
        var identity = await _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>()
            .ResolveAsync(audiobook, destination);
        Assert.Equal(PathIdentityState.Valid, identity.State);
        var file = AudiobookFile.CreateUnresolved(destination);
        file.ApplyPathIdentity(destination, identity);
        file.ApplyPhysicalObjectIdentity(lease.PhysicalObjectIdentity, DateTime.UtcNow);
        audiobook.Files = [file];
        var persisted = await _audiobookRepository.AddAsync(audiobook);
        Assert.True(lease.PrepareCleanupRecovery(persisted.Id));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());
        FilePublicationSourceProof sourceProof;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == operationId);
            journal.State = FileMutationJournalState.SourceDeletionAuthorized;
            sourceProof = new FilePublicationSourceProof(
                journal.SourcePhysicalObjectIdentity,
                journal.SourceLength,
                Assert.IsType<string>(journal.SourceSha256));
            await db.SaveChangesAsync();
        }

        var mover = new Mock<IFileMover>(MockBehavior.Strict);
        mover.Setup(service => service.PrepareActionForRegistrationAsync(
                FileAction.Move,
                source,
                destination,
                operationId,
                lease.PhysicalObjectIdentity,
                sourceProof))
            .ThrowsAsync(new InvalidOperationException(
                "Injected wrapped read-only filesystem failure.",
                new System.ComponentModel.Win32Exception(30)));
        var recovery = new FileRegistrationRecoveryService(
            factory,
            mover.Object,
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        await recovery.ReconcileAsync();

        await using (var db = await factory.CreateDbContextAsync())
        {
            var pending = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(FileMutationJournalState.SourceDeletionAuthorized, pending.State);
            Assert.Equal(persisted.Id, pending.AudiobookId);
        }
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
        mover.VerifyAll();
    }

    [WindowsFact]
    public async Task ReconcileAudiobookWithReceiptsAsync_PartialRecoveryFailure_ReconstructsEarlierCompletedReceiptOnRetry()
    {
        var root = FileService.GetTempDirectory(
            "registration-partial-recovery-receipts");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var firstSource = Path.Join(sourceDirectory, "first.m4b");
        var secondSource = Path.Join(sourceDirectory, "second.m4b");
        var firstDestination = Path.Join(destinationDirectory, "first.m4b");
        var secondDestination = Path.Join(destinationDirectory, "second.m4b");
        await File.WriteAllTextAsync(firstSource, "first-audio");
        await File.WriteAllTextAsync(secondSource, "second-audio");
        var firstOperationId = Guid.NewGuid();
        var secondOperationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        var mover = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "registration-partial-recovery-locks")
        };

        string firstTargetIdentity;
        string secondTargetIdentity;
        using (var firstLease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            firstSource,
            firstDestination,
            firstOperationId))
        using (var secondLease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            secondSource,
            secondDestination,
            secondOperationId))
        {
            Assert.NotNull(firstLease);
            Assert.NotNull(secondLease);
            firstTargetIdentity = firstLease.PhysicalObjectIdentity;
            secondTargetIdentity = secondLease.PhysicalObjectIdentity;

            var audiobook = new AudiobookBuilder()
                .WithTitle("Partial Recovery Receipts")
                .WithBasePath(destinationDirectory)
                .Build();
            var identityResolver = _provider
                .GetRequiredService<IAudiobookFilePathIdentityResolver>();
            var firstIdentity = await identityResolver.ResolveAsync(
                audiobook,
                firstDestination);
            var secondIdentity = await identityResolver.ResolveAsync(
                audiobook,
                secondDestination);
            Assert.Equal(PathIdentityState.Valid, firstIdentity.State);
            Assert.Equal(PathIdentityState.Valid, secondIdentity.State);
            var firstFile = AudiobookFile.CreateUnresolved(firstDestination);
            firstFile.ApplyPathIdentity(firstDestination, firstIdentity);
            firstFile.ApplyPhysicalObjectIdentity(
                firstTargetIdentity,
                DateTime.UtcNow);
            var secondFile = AudiobookFile.CreateUnresolved(secondDestination);
            secondFile.ApplyPathIdentity(secondDestination, secondIdentity);
            secondFile.ApplyPhysicalObjectIdentity(
                secondTargetIdentity,
                DateTime.UtcNow);
            audiobook.Files = [firstFile, secondFile];
            var persisted = await _audiobookRepository.AddAsync(audiobook);

            Assert.True(firstLease.PrepareCleanupRecovery(persisted.Id));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                firstLease.CompletePublication());
            Assert.True(secondLease.PrepareCleanupRecovery(persisted.Id));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                secondLease.CompletePublication());

            await using var orderingDb = await factory.CreateDbContextAsync();
            var firstJournal = await orderingDb.FileMutationJournals.SingleAsync(
                journal => journal.OperationId == firstOperationId);
            var secondJournal = await orderingDb.FileMutationJournals.SingleAsync(
                journal => journal.OperationId == secondOperationId);
            firstJournal.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
            secondJournal.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
            await orderingDb.SaveChangesAsync();
        }

        int audiobookId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            audiobookId = await db.Audiobooks
                .Select(audiobook => audiobook.Id)
                .SingleAsync();
        }
        var recovery = new FileRegistrationRecoveryService(
            factory,
            mover,
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        await using (var sourceLock = new FileStream(
            secondSource,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                recovery.ReconcileAudiobookWithReceiptsAsync(
                    audiobookId,
                    [firstSource, secondSource]));
            Assert.Equal("registration_recovery_pending", exception.Code);
        }

        Assert.False(File.Exists(firstSource));
        Assert.True(File.Exists(secondSource));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(
                FileMutationJournalState.Completed,
                (await db.FileMutationJournals
                    .AsNoTracking()
                    .SingleAsync(journal =>
                        journal.OperationId == firstOperationId)).State);
            Assert.NotEqual(
                FileMutationJournalState.Completed,
                (await db.FileMutationJournals
                    .AsNoTracking()
                    .SingleAsync(journal =>
                        journal.OperationId == secondOperationId)).State);
        }

        var receipts = await recovery.ReconcileAudiobookWithReceiptsAsync(
            audiobookId,
            [firstSource, secondSource]);

        Assert.Equal(2, receipts.Count);
        Assert.True(receipts
            .Select(receipt => receipt.OperationId)
            .ToHashSet()
            .SetEquals([firstOperationId, secondOperationId]));
        Assert.False(File.Exists(firstSource));
        Assert.False(File.Exists(secondSource));
        Assert.Equal("first-audio", await File.ReadAllTextAsync(firstDestination));
        Assert.Equal("second-audio", await File.ReadAllTextAsync(secondDestination));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.All(
                await db.FileMutationJournals.AsNoTracking().ToListAsync(),
                journal => Assert.Equal(
                    FileMutationJournalState.Completed,
                    journal.State));
        }
    }

    [WindowsFact]
    public async Task ReconcileAudiobookAsync_CommittedMoveWithPendingSourceRetirement_ResumesAndClearsBlocker()
    {
        var root = FileService.GetTempDirectory("registration-recovery");
        await AddAuthorizedRootAsync(root);
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "book.m4b");
        var destination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var mover = new FileMover(
            NullLogger<FileMover>.Instance,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "registration-recovery-locks")
        };

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            source,
            destination,
            operationId);
        Assert.NotNull(lease);

        var audiobook = new AudiobookBuilder()
            .WithTitle("Registration Recovery")
            .WithBasePath(destinationDirectory)
            .WithFilePath(destination)
            .Build();
        var identityResolver = _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>();
        var identity = await identityResolver.ResolveAsync(audiobook, destination);
        Assert.Equal(PathIdentityState.Valid, identity.State);
        var file = AudiobookFile.CreateUnresolved(destination);
        file.ApplyPathIdentity(destination, identity);
        file.ApplyPhysicalObjectIdentity(lease.PhysicalObjectIdentity, DateTime.UtcNow);
        audiobook.Files = [file];
        var persisted = await _audiobookRepository.AddAsync(audiobook);

        Assert.True(lease.PrepareCleanupRecovery(persisted.Id));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        await using (var sourceLock = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            Assert.False(await mover.CompletePreparedMoveAsync(
                source,
                destination,
                lease,
                operationId));
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var pending = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(FileMutationJournalState.SourceDeletionAuthorized, pending.State);
            Assert.Equal(persisted.Id, pending.AudiobookId);
            Assert.Null(pending.AudiobookFileId);
        }
        var probe = new FileRegistrationRecoveryProbe(factory);
        Assert.True(await probe.HasBlockingAsync(persisted.Id));

        var recovery = new FileRegistrationRecoveryService(
            factory,
            mover,
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);
        var receipts = await recovery.ReconcileAudiobookWithReceiptsAsync(
            persisted.Id,
            [source]);

        var receipt = Assert.Single(receipts);
        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal(persisted.Id, receipt.AudiobookId);
        Assert.Equal(source, receipt.SourcePath);
        Assert.Equal(destination, receipt.DestinationPath);
        Assert.False(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        Assert.False(await probe.HasBlockingAsync(persisted.Id));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var completed = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(FileMutationJournalState.Completed, completed.State);
        }

        lease.Dispose();
        var originalLastWriteTimeUtc = File.GetLastWriteTimeUtc(destination);
        await File.WriteAllTextAsync(destination, "other");
        File.SetLastWriteTimeUtc(destination, originalLastWriteTimeUtc);

        var mutatedReceipts = await recovery.ReconcileAudiobookWithReceiptsAsync(
            persisted.Id,
            [source]);

        Assert.Empty(mutatedReceipts);
        Assert.Equal("other", await File.ReadAllTextAsync(destination));

        File.Delete(destination);
        await File.WriteAllTextAsync(destination, "replacement");

        var staleReceipts = await recovery.ReconcileAudiobookWithReceiptsAsync(
            persisted.Id,
            [source]);

        Assert.Empty(staleReceipts);
        Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListenArrDbContext(options));
    }

    private sealed class BlockingRecoveryTimeProvider : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _blocked = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public override DateTimeOffset GetUtcNow()
        {
            _blocked.Set();
            _release.Wait();
            return DateTimeOffset.UtcNow;
        }

        public bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _blocked.Dispose();
            _release.Dispose();
        }
    }
}
