using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "FileRenameRecoveryReconcilerTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRenameRecoveryReconcilerTests : BaseTests
{
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

    private sealed record Scenario(
        int AudiobookId,
        int FileId,
        string Source,
        string Destination,
        string SourceIdentity,
        Guid OperationId);
}
