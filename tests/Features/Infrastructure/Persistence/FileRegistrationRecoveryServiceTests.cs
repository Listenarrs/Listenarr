using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Area", "Library")]
[Trait("Name", "FileRegistrationRecoveryServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRegistrationRecoveryServiceTests : BaseTests
{
    [Fact]
    public async Task ReconcileAsync_AnonymousRetainedTerminalPublication_RemainsTerminalWithoutChangingContent()
    {
        var root = FileService.GetTempDirectory("registration-anonymous-retained-terminal");
        var source = Path.Join(root, "source.m4b");
        var destination = Path.Join(root, "destination.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await File.WriteAllTextAsync(destination, "audio");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(root, "terminal.db")};Pooling=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        var operationId = Guid.NewGuid();
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = operationId,
                ProtocolVersion = FileMutationProtocol.OperationEvidence,
                Action = FileAction.Copy,
                SourcePath = source,
                DestinationPath = destination,
                SourceLength = new FileInfo(source).Length,
                SourceSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source))),
                State = FileMutationJournalState.CompletedSourceRetained
            });
            await setup.SaveChangesAsync();
        }
        var recovery = new FileRegistrationRecoveryService(factory,
            new FileMover(NullLogger<FileMover>.Instance, dbContextFactory: factory,
                timeProvider: TimeProvider.System),
            TimeProvider.System, NullLogger<FileRegistrationRecoveryService>.Instance);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await recovery.ReconcileAsync();
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            await using var verification = await factory.CreateDbContextAsync();
            var journal = await verification.FileMutationJournals.AsNoTracking().SingleAsync();
            Assert.Equal(operationId, journal.OperationId);
            Assert.Equal(FileMutationJournalState.CompletedSourceRetained, journal.State);
            Assert.Null(journal.AudiobookId);
            Assert.Null(journal.AudiobookFileId);
            Assert.Null(journal.Error);
            Assert.Equal(0, await verification.AudiobookFiles.CountAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_OwnerTransactionInterrupted_PreservesContentAndCommittedOwnership(
        bool afterCommit)
    {
        var root = FileService.GetTempDirectory("registration-owner-transaction");
        var sourceDirectory = Path.Join(root, "source");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "book.m4b");
        var destination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(root, "owner.db")};Pooling=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        int audiobookId;
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            var audiobook = new Audiobook { Title = "Interrupted Owner", BasePath = sourceDirectory };
            setup.Audiobooks.Add(audiobook);
            await setup.SaveChangesAsync();
            audiobookId = audiobook.Id;
        }

        var operationId = Guid.NewGuid();
        var mover = new FileMover(NullLogger<FileMover>.Instance,
            dbContextFactory: factory, timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory("registration-owner-locks")
        };
        using (var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move, source, destination, operationId))
        {
            Assert.NotNull(lease);
            var interruptedOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite($"Data Source={Path.Join(root, "owner.db")};Pooling=False")
                .AddInterceptors(new InterruptOwnerCommitInterceptor(afterCommit))
                .Options;
            await using var ownerDb = new ListenArrDbContext(interruptedOptions);
            var repository = new Listenarr.Infrastructure.Persistence.Repositories.EfAudiobookFileRepository(ownerDb);
            var file = AudiobookFile.CreateUnresolved(destination);
            file.AudiobookId = audiobookId;
            file.ApplyPathIdentity(destination, AudiobookFilePathIdentity.CreateValid(
                destination, FileSystemPathSemantics.CurrentHostDefault,
                FileSystemCaseSensitivityMode.Auto, destinationDirectory));
            await Assert.ThrowsAsync<IOException>(() => repository.ClaimWithBasePathAsync(
                file, new AudiobookBasePathMutation(audiobookId, sourceDirectory, destinationDirectory)));
        }

        await using (var verification = await factory.CreateDbContextAsync())
        {
            Assert.Equal(afterCommit ? destinationDirectory : sourceDirectory,
                (await verification.Audiobooks.AsNoTracking().SingleAsync()).BasePath);
            Assert.Equal(afterCommit ? 1 : 0, await verification.AudiobookFiles.CountAsync());
            Assert.Equal(FileMutationJournalState.TargetVerified,
                (await verification.FileMutationJournals.AsNoTracking().SingleAsync()).State);
        }

        var recovery = new FileRegistrationRecoveryService(factory,
            new FileMover(NullLogger<FileMover>.Instance, dbContextFactory: factory,
                timeProvider: TimeProvider.System)
            {
                FileMoveLockDirectoryForTest = FileService.GetTempDirectory("registration-owner-locks")
            }, TimeProvider.System, NullLogger<FileRegistrationRecoveryService>.Instance);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await recovery.ReconcileAsync();
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            await using var verification = await factory.CreateDbContextAsync();
            var journal = await verification.FileMutationJournals.AsNoTracking().SingleAsync();
            Assert.Equal(afterCommit ? FileMutationJournalState.CompletedSourceRetained
                : FileMutationJournalState.NeedsAttention, journal.State);
            Assert.Equal(afterCommit ? destinationDirectory : sourceDirectory,
                (await verification.Audiobooks.AsNoTracking().SingleAsync()).BasePath);
            Assert.Equal(afterCommit ? 1 : 0, await verification.AudiobookFiles.CountAsync());
            if (afterCommit)
            {
                var file = await verification.AudiobookFiles.AsNoTracking().SingleAsync();
                Assert.Equal(destination, file.Path);
                Assert.Equal(audiobookId, file.AudiobookId);
                Assert.Equal(audiobookId, journal.AudiobookId);
                Assert.Null(file.PhysicalObjectIdentity);
            }
        }
    }

    [WindowsTheory]
    [InlineData(null)]
    [InlineData(FileMutationJournalState.Completed)]
    [InlineData(FileMutationJournalState.CompletedSourceRetained)]
    public async Task ReconcileAsync_AnonymousVerifiedMoveWithCommittedTrackedPath_AdoptsAndRetainsSource(
        FileMutationJournalState? historicalState)
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
            if (historicalState.HasValue)
            {
                var historical = (FileMutationJournal)db.Entry(anonymous).CurrentValues.ToObject();
                historical.OperationId = Guid.NewGuid();
                historical.State = historicalState.Value;
                historical.AudiobookId = persisted.Id;
                historical.SourcePath = Path.Join(sourceDirectory, "historical.m4b");
                if (historicalState == FileMutationJournalState.CompletedSourceRetained)
                    await File.WriteAllTextAsync(historical.SourcePath, "audio");
                db.FileMutationJournals.Add(historical);
                await db.SaveChangesAsync();
            }
        }

        await new FileRegistrationRecoveryService(
                factory,
                mover,
                TimeProvider.System,
                NullLogger<FileRegistrationRecoveryService>.Instance)
            .ReconcileAsync();

        Assert.True(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var completed = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(
                FileMutationJournalState.CompletedSourceRetained,
                completed.State);
            Assert.Equal(persisted.Id, completed.AudiobookId);
            Assert.Null(completed.AudiobookFileId);
        }
    }

    [Fact]
    public async Task ReconcileAsync_LegacyNonterminalJournal_MarksNeedsAttentionWithoutBlockingRecovery()
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

        await recovery.ReconcileAsync();

        await using var verification = await factory.CreateDbContextAsync();
        var persisted = await verification.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(FileMutationJournalState.NeedsAttention, persisted.State);
        Assert.False(string.IsNullOrWhiteSpace(persisted.Error));
        Assert.Contains("registration publication", persisted.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(FileMutationJournalState.TargetVerified)]
    [InlineData(FileMutationJournalState.RegistrationCommitted)]
    [InlineData(FileMutationJournalState.SourceDeletionAuthorized)]
    [InlineData(FileMutationJournalState.SourceDeleted)]
    public async Task ReconcileAsync_MissingOwners_ScopesRepairAndContinues(
        FileMutationJournalState state)
    {
        var root = FileService.GetTempDirectory("registration-missing-owners");
        var source = Path.Join(root, "source.m4b");
        var destination = Path.Join(root, "target.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await File.WriteAllTextAsync(destination, "audio");
        var operationIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var operationId in operationIds)
            {
                db.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = operationId,
                    ProtocolVersion = FileMutationProtocol.Current,
                    Action = FileAction.Move,
                    SourcePath = source,
                    DestinationPath = destination,
                    SourcePhysicalObjectIdentity = "legacy-source-diagnostic",
                    TargetPhysicalObjectIdentity = "legacy-target-diagnostic",
                    SourceLength = 5,
                    SourceSha256 = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes("audio"))),
                    AudiobookId = int.MaxValue,
                    State = state
                });
            }
            await db.SaveChangesAsync();
        }

        var mover = new Mock<IFileMover>(MockBehavior.Strict);
        var recovery = new FileRegistrationRecoveryService(
            factory, mover.Object, TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        await recovery.ReconcileAsync();
        await recovery.ReconcileAsync();

        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        mover.VerifyNoOtherCalls();
        await using var verification = await factory.CreateDbContextAsync();
        var journals = await verification.FileMutationJournals
            .AsNoTracking()
            .Where(journal => operationIds.Contains(journal.OperationId))
            .ToListAsync();
        Assert.Equal(2, journals.Count);
        Assert.All(journals, journal =>
        {
            Assert.Equal(FileMutationJournalState.NeedsAttention, journal.State);
            Assert.Contains("missing audiobook", journal.Error);
        });
        await Assert.ThrowsAsync<Listenarr.Application.Common.Exceptions.ApplicationConflictException>(
            () => recovery.ReconcileAudiobookAsync(int.MaxValue));
    }

    [FileLinkFact]
    public async Task ReconcileAsync_LinkedTarget_DoesNotAdoptOrCompletePublication()
    {
        var root = FileService.GetTempDirectory("registration-linked-target");
        await AddAuthorizedRootAsync(root);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var mover = new Mock<IFileMover>(MockBehavior.Strict);
        var recovery = new FileRegistrationRecoveryService(
            factory, mover.Object, TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        foreach (var anonymous in new[] { true, false })
        {
            var directory = Path.Join(root, anonymous ? "anonymous" : "owned");
            Directory.CreateDirectory(directory);
            var source = Path.Join(directory, "source.m4b");
            var destination = Path.Join(directory, "target.m4b");
            var foreign = Path.Join(root, Guid.NewGuid().ToString("N") + ".m4b");
            await File.WriteAllTextAsync(source, "audio");
            await File.WriteAllTextAsync(destination, "audio");
            await File.WriteAllTextAsync(foreign, "audio");
            var audiobook = new AudiobookBuilder()
                .WithTitle("Linked Target")
                .WithBasePath(directory)
                .Build();
            var identity = await _provider
                .GetRequiredService<IAudiobookFilePathIdentityResolver>()
                .ResolveAsync(audiobook, destination);
            Assert.Equal(PathIdentityState.Valid, identity.State);
            var file = AudiobookFile.CreateUnresolved(destination);
            file.ApplyPathIdentity(destination, identity);
            audiobook.Files = [file];
            var persisted = await _audiobookRepository.AddAsync(audiobook);
            var operationId = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = operationId,
                    ProtocolVersion = FileMutationProtocol.Current,
                    Action = FileAction.Move,
                    SourcePath = source,
                    DestinationPath = destination,
                    SourcePhysicalObjectIdentity = string.Empty,
                    SourceLength = 5,
                    SourceSha256 = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes("audio"))),
                    AudiobookId = anonymous ? null : persisted.Id,
                    State = anonymous
                        ? FileMutationJournalState.TargetVerified
                        : FileMutationJournalState.RegistrationCommitted
                });
                await db.SaveChangesAsync();
            }
            File.Delete(destination);
            File.CreateSymbolicLink(destination, foreign);

            await recovery.ReconcileAsync();
            await recovery.ReconcileAsync();

            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(foreign));
            Assert.Equal(foreign, new FileInfo(destination).LinkTarget);
            await using var verification = await factory.CreateDbContextAsync();
            var journal = await verification.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(FileMutationJournalState.NeedsAttention, journal.State);
            Assert.Equal(anonymous ? (int?)null : persisted.Id, journal.AudiobookId);
        }
        mover.VerifyNoOtherCalls();
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

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_MultipleAnonymousMovesShareCommittedTargetGeneration_MarksAttentionWithoutRetiringSources(
        bool targetUnavailable)
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

        using (var firstLease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            firstSource,
            destination,
            firstOperationId))
        {
            Assert.NotNull(firstLease);
        }
        using (var secondLease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            secondSource,
            destination,
            secondOperationId))
        {
            Assert.NotNull(secondLease);
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
        audiobook.Files = [file];
        await _audiobookRepository.AddAsync(audiobook);

        var recovery = new FileRegistrationRecoveryService(
            factory,
            mover,
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);
        using (var blockedTarget = targetUnavailable
            ? File.Open(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            await recovery.ReconcileAsync();
        }

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
            Assert.Equal(FileMutationJournalState.NeedsAttention, journal.State));
        Assert.All(journals, journal => Assert.Contains(
            "same publication path and content proof",
            journal.Error,
            StringComparison.OrdinalIgnoreCase));
    }

    [WindowsFact]
    public async Task ReconcileAsync_AnonymousVerifiedMoveWithoutDurableOwner_PreservesTargetAndMarksNeedsAttention()
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
        Assert.True(File.Exists(destination));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await using var db = await factory.CreateDbContextAsync();
        var anonymous = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(FileMutationJournalState.NeedsAttention, anonymous.State);
        Assert.Null(anonymous.AudiobookId);
        Assert.Null(anonymous.AudiobookFileId);
        Assert.Contains(
            "cannot recreate delete authority",
            anonymous.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task ReconcileAsync_SourceDeletionAuthorityLostAfterLiveOperation_RetainsExistingSource()
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
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == operationId);
            journal.State = FileMutationJournalState.SourceDeletionAuthorized;
            await db.SaveChangesAsync();
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

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.False(await new FileRegistrationRecoveryProbe(factory)
            .HasBlockingAsync(persisted.Id));
    }

    [Theory]
    [InlineData(FileMutationJournalState.Planned)]
    [InlineData(FileMutationJournalState.TargetIdentityPersisted)]
    [InlineData(FileMutationJournalState.TargetVerified)]
    [InlineData(FileMutationJournalState.RegistrationCommitted)]
    [InlineData(FileMutationJournalState.SourceDeletionAuthorized)]
    [InlineData(FileMutationJournalState.SourceDeleted)]
    [InlineData(FileMutationJournalState.OwnerMetadataReconciled)]
    [InlineData(FileMutationJournalState.NeedsAttention)]
    [InlineData(FileMutationJournalState.RollbackAuthorized)]
    [InlineData(FileMutationJournalState.RolledBack)]
    [InlineData(FileMutationJournalState.Completed)]
    [InlineData(FileMutationJournalState.CompletedSourceRetained)]
    public async Task ReconcileAsync_AnyPersistedJournalState_NeverDeletesExistingMoveSource(
        FileMutationJournalState state)
    {
        var root = FileService.GetTempDirectory("registration-restart-state");
        var source = Path.Join(root, "source.m4b");
        var destination = Path.Join(root, "destination.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await File.WriteAllTextAsync(destination, "audio");

        var audiobook = new AudiobookBuilder()
            .WithTitle("Restart State Safety")
            .WithBasePath(root)
            .WithFilePath(destination)
            .Build();
        var identity = await _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>()
            .ResolveAsync(audiobook, destination);
        var trackedFile = AudiobookFile.CreateUnresolved(destination);
        trackedFile.ApplyPathIdentity(destination, identity);
        audiobook.Files = [trackedFile];
        var persisted = await _audiobookRepository.AddAsync(audiobook);

        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = FileAction.Move,
                SourcePath = source,
                DestinationPath = destination,
                SourceParentDirectoryObjectIdentity = "diagnostic-source-parent",
                DestinationParentDirectoryObjectIdentity = "diagnostic-target-parent",
                SourcePhysicalObjectIdentity = "diagnostic-source",
                SourceLength = 5,
                SourceSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("audio"))),
                State = state,
                AudiobookId = persisted.Id,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        var recovery = new FileRegistrationRecoveryService(
            factory,
            _provider.GetRequiredService<IFileMover>(),
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        try
        {
            await recovery.ReconcileAsync();
        }
        catch (InvalidOperationException) when (
            state == FileMutationJournalState.NeedsAttention)
        {
            // A scoped repair state may remain unresolved, but it cannot recreate
            // authority to delete the source after restart.
        }

        Assert.True(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task ReconcileAsync_RestartedMoveDoesNotReplaySourceRetirementThroughFileMover()
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
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == operationId);
            journal.State = FileMutationJournalState.SourceDeletionAuthorized;
            Assert.False(string.IsNullOrWhiteSpace(journal.SourceSha256));
            await db.SaveChangesAsync();
        }

        var mover = new Mock<IFileMover>(MockBehavior.Strict);
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
            Assert.Equal(
                FileMutationJournalState.CompletedSourceRetained,
                pending.State);
            Assert.Equal(persisted.Id, pending.AudiobookId);
        }
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
        mover.VerifyNoOtherCalls();
    }

    [WindowsFact]
    public async Task ReconcileAudiobookWithReceiptsAsync_RestartRetainsAllExistingMoveSources()
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

        IReadOnlyList<FileRegistrationRecoveryReceipt> firstPassReceipts;
        await using (var sourceLock = new FileStream(
            secondSource,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            firstPassReceipts = await recovery.ReconcileAudiobookWithReceiptsAsync(
                audiobookId,
                [firstSource, secondSource]);
        }

        Assert.Equal(2, firstPassReceipts.Count);
        Assert.All(firstPassReceipts, receipt => Assert.True(receipt.SourceRetained));
        Assert.True(File.Exists(firstSource));
        Assert.True(File.Exists(secondSource));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(
                FileMutationJournalState.CompletedSourceRetained,
                (await db.FileMutationJournals
                    .AsNoTracking()
                    .SingleAsync(journal =>
                        journal.OperationId == firstOperationId)).State);
            Assert.Equal(
                FileMutationJournalState.CompletedSourceRetained,
                (await db.FileMutationJournals
                    .AsNoTracking()
                    .SingleAsync(journal =>
                        journal.OperationId == secondOperationId)).State);
        }

        var receipts = await recovery.ReconcileAudiobookWithReceiptsAsync(
            audiobookId,
            [firstSource, secondSource]);

        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt => Assert.True(receipt.SourceRetained));
        Assert.True(File.Exists(firstSource));
        Assert.True(File.Exists(secondSource));
        Assert.Equal("first-audio", await File.ReadAllTextAsync(firstDestination));
        Assert.Equal("second-audio", await File.ReadAllTextAsync(secondDestination));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.All(
                await db.FileMutationJournals.AsNoTracking().ToListAsync(),
                journal => Assert.Equal(
                    FileMutationJournalState.CompletedSourceRetained,
                    journal.State));
        }
    }

    [WindowsFact]
    public async Task ReconcileAudiobookAsync_CommittedMoveWithPendingSourceRetirement_RetainsSourceAndClearsBlocker()
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

        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == operationId);
            journal.State = FileMutationJournalState.SourceDeletionAuthorized;
            await db.SaveChangesAsync();
        }
        lease.Dispose();

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
        Assert.True(receipt.SourceRetained);
        Assert.Equal(operationId, receipt.OperationId);
        Assert.True(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        Assert.False(await probe.HasBlockingAsync(persisted.Id));
        var recoveryStatus = await recovery.RetryAsync(operationId);
        Assert.Equal(
            FileMutationJournalState.CompletedSourceRetained,
            recoveryStatus.JournalState);
        Assert.Equal(
            FileRegistrationRecoveryDisposition.Cleared,
            recoveryStatus.Disposition);
        Assert.Contains(
            "source was retained",
            recoveryStatus.PublicReason,
            StringComparison.OrdinalIgnoreCase);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var completed = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.Equal(
                FileMutationJournalState.CompletedSourceRetained,
                completed.State);
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

    private sealed class InterruptOwnerCommitInterceptor(bool afterCommit) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (!afterCommit)
            {
                throw new IOException("Injected interruption before owner transaction commit.");
            }
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Injected interruption after owner transaction commit.");
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
