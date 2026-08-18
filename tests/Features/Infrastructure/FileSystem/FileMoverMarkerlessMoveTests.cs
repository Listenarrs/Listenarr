using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileMoverMarkerlessMoveTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileMoverMarkerlessMoveTests : BaseTests
{
    [Fact]
    public async Task MoveFileAsync_NativeRename_PersistsHashlessSourceProof()
    {
        var scenario = await CreateScenarioAsync();

        Assert.True(await CreateMover().MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Null(journal.SourceSha256);
        Assert.Equal(scenario.SourceIdentity, journal.SourcePhysicalObjectIdentity);
        Assert.Equal(scenario.SourceIdentity, journal.TargetPhysicalObjectIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_CopyFallback_PersistsContentHash()
    {
        var scenario = await CreateScenarioAsync();

        Assert.True(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Matches("^[0-9A-F]{64}$", journal.SourceSha256 ?? string.Empty);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_CopyFallback_WithExpectedSourceGeneration_RemainsSupported()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover(disableNativeRename: true);
        var sourceCapability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(mover);
        var capability = await sourceCapability.CheckAsync(scenario.Source);
        Assert.True(capability.IsSupported, capability.Reason);
        Assert.True(capability.SourceProof.HasValue);

        Assert.True(await mover.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedSourceProof: capability.SourceProof.Value));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Equal(scenario.SourceIdentity, journal.SourcePhysicalObjectIdentity);
        Assert.Matches("^[0-9A-F]{64}$", journal.SourceSha256 ?? string.Empty);
    }

    [LinuxFact]
    public async Task MoveFileAsync_ForcedCrossVolumeRejectsBeforePublicationOrJournalCreation()
    {
        var scenario = await CreateScenarioAsync();

        Assert.False(await CreateMover(forceCrossVolume: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.FileMutationJournals
            .AsNoTracking()
            .AnyAsync(candidate => candidate.OperationId == scenario.OperationId));
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task MoveFileAsync_CaseDistinctRetryDoesNotAdoptJournal()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            afterJournalPlanned: () =>
                throw new IOException("Injected crash after markerless move journal creation."));

        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));
        var distinctSource = Path.Join(scenario.Root, "SOURCE.m4b");
        await File.WriteAllTextAsync(distinctSource, "different source");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateMover().MoveFileAsync(
                distinctSource,
                scenario.Destination,
                scenario.OperationId));

        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("different source", await File.ReadAllTextAsync(distinctSource));
        Assert.False(File.Exists(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            targetIdentity: null);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [WindowsFact]
    public async Task MoveFileAsync_CaseAliasRetryUsesSameDurableJournal()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            afterJournalPlanned: () =>
                throw new IOException("Injected crash after markerless move journal creation."));

        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.True(await CreateMover().MoveFileAsync(
            scenario.Source.ToUpperInvariant(),
            scenario.Destination.ToUpperInvariant(),
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task MoveFileAsync_CompatiblePersistedSourceToken_NativeRenameCrashRecoveryPreservesDurableToken()
    {
        var scenario = await CreateScenarioAsync();
        Assert.StartsWith(
            "linux-generation:",
            scenario.SourceIdentity,
            StringComparison.Ordinal);
        var durableSourceIdentity =
            LinuxIdentityTestHelper.ToMergedV1AugmentedIdentity(
                scenario.SourceIdentity);
        var planned = CreateMover(
            afterJournalPlanned: () =>
                throw new IOException("Injected crash after markerless move journal creation."));
        await Assert.ThrowsAsync<IOException>(() => planned.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals.SingleAsync(
                candidate => candidate.OperationId == scenario.OperationId);
            journal.SourcePhysicalObjectIdentity = durableSourceIdentity;
            await db.SaveChangesAsync();
        }

        var published = CreateMover(
            afterPublishedBeforeTargetState: () =>
                throw new IOException("Injected crash after markerless native rename."));
        await Assert.ThrowsAsync<IOException>(() => published.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            targetIdentity: null);

        Assert.True(await CreateMover().MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
            Assert.Equal(FileMutationJournalState.Completed, journal.State);
            Assert.Equal(durableSourceIdentity, journal.SourcePhysicalObjectIdentity);
            Assert.Equal(durableSourceIdentity, journal.TargetPhysicalObjectIdentity);
        }
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_NativeRenameBeforeTargetStateCommitResumes()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            afterPublishedBeforeTargetState: () =>
                throw new IOException("Injected crash after markerless native rename."));

        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        Assert.Equal(scenario.SourceIdentity, GetFileIdentity(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            targetIdentity: null);
        AssertNoLibraryArtifacts(scenario.Root);

        Assert.True(await CreateMover().MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_FinalFileCreatedBeforeTargetIdentityBecomesNeedsAttention()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            disableNativeRename: true,
            afterTargetCreatedBeforeState: () =>
                throw new IOException("Injected crash after markerless target creation."));

        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.True(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        Assert.Equal(0, new FileInfo(scenario.Destination).Length);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            targetIdentity: null);
        AssertNoLibraryArtifacts(scenario.Root);

        Assert.False(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal(0, new FileInfo(scenario.Destination).Length);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            targetIdentity: null);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_TargetIdentityPersistedBeforeBytesResumes()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            disableNativeRename: true,
            afterTargetState: () =>
                throw new IOException("Injected crash after markerless target identity persistence."));

        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.True(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        Assert.Equal(0, new FileInfo(scenario.Destination).Length);
        var targetIdentity = GetFileIdentity(scenario.Destination);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetIdentityPersisted,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);

        Assert.True(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_BytesWrittenBeforeVerificationStateResumes()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            disableNativeRename: true,
            afterTargetWrittenBeforeVerifiedState: () =>
                throw new IOException("Injected crash after markerless target write."));

        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        var targetIdentity = GetFileIdentity(scenario.Destination);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetIdentityPersisted,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);

        Assert.True(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [WindowsFact]
    public async Task MoveFileAsync_SourceSharingViolationDoesNotAdvanceDeletionState()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            disableNativeRename: true,
            afterTargetWrittenBeforeVerifiedState: () =>
                throw new IOException("Injected crash after markerless target write."));

        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));
        var targetIdentity = GetFileIdentity(scenario.Destination);

        await using (var sourceLock = new FileStream(
            scenario.Source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            Assert.False(await CreateMover(disableNativeRename: true).MoveFileAsync(
                scenario.Source,
                scenario.Destination,
                scenario.OperationId));

            Assert.True(File.Exists(scenario.Source));
            Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
            await AssertJournalStateAsync(
                scenario.OperationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                targetIdentity);
        }

        Assert.True(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_SourceDeletedBeforeDeletionStateCommitResumes()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            disableNativeRename: true,
            afterSourceDeletedBeforeState: () =>
                throw new IOException("Injected crash after markerless source deletion."));

        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        var targetIdentity = GetFileIdentity(scenario.Destination);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.SourceDeletionAuthorized,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);

        Assert.True(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_TargetReplacedAfterIdentityPersistenceIsPreservedAndBlocked()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            disableNativeRename: true,
            afterTargetState: () =>
                throw new IOException("Injected crash after markerless target identity persistence."));
        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        var originalTargetIdentity = GetFileIdentity(scenario.Destination);
        File.Delete(scenario.Destination);
        await File.WriteAllTextAsync(scenario.Destination, "foreign-target");
        Assert.NotEqual(originalTargetIdentity, GetFileIdentity(scenario.Destination));

        Assert.False(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            originalTargetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_SourceReplacedAfterTargetWriteIsPreservedAndBlocked()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            disableNativeRename: true,
            afterTargetWrittenBeforeVerifiedState: () =>
                throw new IOException("Injected crash after markerless target write."));
        await Assert.ThrowsAsync<IOException>(() => interrupted.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        File.Delete(scenario.Source);
        await File.WriteAllTextAsync(scenario.Source, "foreign-source");
        Assert.NotEqual(scenario.SourceIdentity, GetFileIdentity(scenario.Source));
        var targetIdentity = GetFileIdentity(scenario.Destination);

        Assert.False(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.Equal("foreign-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_OwnerMetadataReconciledRetryIgnoresLaterSourceReuse()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover();

        Assert.True(await mover.PerformActionOn(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            audiobookId: 42,
            audiobookFileId: 0));

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
            Assert.Equal(42, journal.AudiobookId);
            Assert.Equal(0, journal.AudiobookFileId);
            journal.State = FileMutationJournalState.OwnerMetadataReconciled;
            await db.SaveChangesAsync();
        }

        await File.WriteAllTextAsync(scenario.Source, "recreated-source");

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            audiobookId: 42,
            audiobookFileId: 0));
        Assert.Equal("recreated-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.OwnerMetadataReconciled,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_CompletedJournalWithRecreatedSourcePreservesSourceAndBlocks()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover(disableNativeRename: true);

        Assert.True(await mover.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));
        var targetIdentity = GetFileIdentity(scenario.Destination);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            targetIdentity);

        await File.WriteAllTextAsync(scenario.Source, "recreated-source");

        Assert.False(await CreateMover(disableNativeRename: true).MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.Equal("recreated-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            targetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_SourceRecreatedAfterSourceDeletedState_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover(
            disableNativeRename: true,
            afterSourceDeletedState: () =>
            {
                File.WriteAllText(scenario.Source, "replacement");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.Equal("replacement", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            GetFileIdentity(scenario.Destination));
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFileAsync_TargetReplacedAfterSourceDeletedState_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync();
        string? originalTargetIdentity = null;
        var mover = CreateMover(
            disableNativeRename: true,
            afterSourceDeletedState: () =>
            {
                originalTargetIdentity = GetFileIdentity(scenario.Destination);
                File.Delete(scenario.Destination);
                File.WriteAllText(scenario.Destination, "foreign-target");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.NotNull(originalTargetIdentity);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            originalTargetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task MoveFileAsync_TargetReplacedAfterFinalProbeBeforeCompletedCommit_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync();
        string? originalTargetIdentity = null;
        var mover = CreateMover(
            disableNativeRename: true,
            beforeCompletedJournalCommit: () =>
            {
                originalTargetIdentity = GetFileIdentity(scenario.Destination);
                File.Delete(scenario.Destination);
                File.WriteAllText(scenario.Destination, "foreign-target");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFileAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.NotNull(originalTargetIdentity);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            originalTargetIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task MoveFileAsync_SourceParentReplacedAfterSourceDeletedState_DoesNotComplete()
    {
        var sourceParent = FileService.GetTempDirectory(
            "file-mover-markerless-parent-replaced-after-state-source");
        var displacedSourceParent = sourceParent + "-displaced";
        var destinationParent = FileService.GetTempDirectory(
            "file-mover-markerless-parent-replaced-after-state-destination");
        var source = Path.Join(sourceParent, "source.m4b");
        var destination = Path.Join(destinationParent, "moved.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();

        var mover = CreateMover(
            disableNativeRename: true,
            afterSourceDeletedState: async () =>
            {
                Directory.Move(sourceParent, displacedSourceParent);
                Directory.CreateDirectory(sourceParent);
                await File.WriteAllTextAsync(source, "replacement");
            });

        Assert.False(await mover.MoveFileAsync(source, destination, operationId));

        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            GetFileIdentity(destination));
        AssertNoLibraryArtifacts(sourceParent);
        AssertNoLibraryArtifacts(displacedSourceParent);
        AssertNoLibraryArtifacts(destinationParent);
    }

    [Fact]
    public async Task MoveFileAsync_RetryAfterSourceDeletedState_SourceParentReplacedWhileStopped_DoesNotComplete()
    {
        var sourceParent = FileService.GetTempDirectory(
            "file-mover-markerless-parent-replaced-after-restart-source");
        var displacedSourceParent = sourceParent + "-displaced";
        var destinationParent = FileService.GetTempDirectory(
            "file-mover-markerless-parent-replaced-after-restart-destination");
        var source = Path.Join(sourceParent, "source.m4b");
        var destination = Path.Join(destinationParent, "moved.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();

        var interruptedMover = CreateMover(
            disableNativeRename: true,
            afterSourceDeletedState: () =>
                throw new IOException("Simulated process interruption."));

        await Assert.ThrowsAsync<IOException>(() =>
            interruptedMover.MoveFileAsync(source, destination, operationId));

        Assert.False(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.SourceDeleted,
            GetFileIdentity(destination));
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.False(string.IsNullOrWhiteSpace(
                journal.SourceParentDirectoryObjectIdentity));
            Assert.False(string.IsNullOrWhiteSpace(
                journal.DestinationParentDirectoryObjectIdentity));
        }

        Directory.Move(sourceParent, displacedSourceParent);
        Directory.CreateDirectory(sourceParent);

        var recoveredMover = CreateMover(disableNativeRename: true);
        Assert.False(await recoveredMover.MoveFileAsync(
            source,
            destination,
            operationId));

        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            GetFileIdentity(destination));
        AssertNoLibraryArtifacts(sourceParent);
        AssertNoLibraryArtifacts(displacedSourceParent);
        AssertNoLibraryArtifacts(destinationParent);
    }

    [Fact]
    public async Task MoveFileAsync_RetryAfterSourceDeletedState_DestinationParentReplacedWhileStopped_DoesNotComplete()
    {
        var sourceParent = FileService.GetTempDirectory(
            "file-mover-markerless-destination-parent-replaced-after-restart-source");
        var destinationParent = FileService.GetTempDirectory(
            "file-mover-markerless-destination-parent-replaced-after-restart-destination");
        var displacedDestinationParent = destinationParent + "-displaced";
        var source = Path.Join(sourceParent, "source.m4b");
        var destination = Path.Join(destinationParent, "moved.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();

        var interruptedMover = CreateMover(
            disableNativeRename: true,
            afterSourceDeletedState: () =>
                throw new IOException("Simulated process interruption."));

        await Assert.ThrowsAsync<IOException>(() =>
            interruptedMover.MoveFileAsync(source, destination, operationId));

        Assert.False(File.Exists(source));
        var targetIdentity = GetFileIdentity(destination);
        Directory.Move(destinationParent, displacedDestinationParent);
        Directory.CreateDirectory(destinationParent);
        File.Move(
            Path.Join(displacedDestinationParent, "moved.m4b"),
            destination);
        Assert.Equal(targetIdentity, GetFileIdentity(destination));

        var recoveredMover = CreateMover(disableNativeRename: true);
        Assert.False(await recoveredMover.MoveFileAsync(
            source,
            destination,
            operationId));

        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            targetIdentity);
        AssertNoLibraryArtifacts(sourceParent);
        AssertNoLibraryArtifacts(destinationParent);
        AssertNoLibraryArtifacts(displacedDestinationParent);
    }

    [LinuxFact]
    public async Task MoveFileAsync_SourceParentReplacedAfterDeleteBeforeState_DoesNotComplete()
    {
        var sourceParent = FileService.GetTempDirectory(
            "file-mover-markerless-parent-replaced-source");
        var displacedSourceParent = sourceParent + "-displaced";
        var destinationParent = FileService.GetTempDirectory(
            "file-mover-markerless-parent-replaced-destination");
        var source = Path.Join(sourceParent, "source.m4b");
        var destination = Path.Join(destinationParent, "moved.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();
        var replacementAttempted = false;
        Exception? replacementFailure = null;

        var mover = CreateMover(
            disableNativeRename: true,
            afterSourceDeletedBeforeState: async () =>
            {
                replacementAttempted = true;
                try
                {
                    Directory.Move(sourceParent, displacedSourceParent);
                    Directory.CreateDirectory(sourceParent);
                    await File.WriteAllTextAsync(source, "replacement");
                }
                catch (Exception exception)
                {
                    replacementFailure = exception;
                    throw;
                }
            });

        Assert.False(await mover.MoveFileAsync(source, destination, operationId));

        Assert.True(replacementAttempted);
        Assert.Null(replacementFailure);
        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            GetFileIdentity(destination));
        AssertNoLibraryArtifacts(sourceParent);
        AssertNoLibraryArtifacts(displacedSourceParent);
        AssertNoLibraryArtifacts(destinationParent);
    }

    private FileMover CreateMover(
        bool disableNativeRename = false,
        bool forceCrossVolume = false,
        Func<Task>? afterJournalPlanned = null,
        Func<Task>? afterPublishedBeforeTargetState = null,
        Func<Task>? afterTargetCreatedBeforeState = null,
        Func<Task>? afterTargetState = null,
        Func<Task>? afterTargetWrittenBeforeVerifiedState = null,
        Func<Task>? afterSourceDeletedBeforeState = null,
        Func<Task>? afterSourceDeletedState = null,
        Func<Task>? beforeCompletedJournalCommit = null)
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        return new FileMover(
            new NullLogger<FileMover>(),
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "file-mover-markerless-locks"),
            DisableNativeFileRenameForTest = disableNativeRename,
            ForceCrossVolumeForTest = forceCrossVolume,
            AfterMarkerlessMoveJournalPlannedForTestAsync = afterJournalPlanned,
            AfterMarkerlessMovePublishedBeforeTargetStateForTestAsync =
                afterPublishedBeforeTargetState,
            AfterMarkerlessMoveTargetCreatedBeforeStateForTestAsync =
                afterTargetCreatedBeforeState,
            AfterMarkerlessMoveTargetStateForTestAsync = afterTargetState,
            AfterMarkerlessMoveTargetWrittenBeforeVerifiedStateForTestAsync =
                afterTargetWrittenBeforeVerifiedState,
            AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync =
                afterSourceDeletedBeforeState,
            AfterMarkerlessMoveSourceDeletedStateForTestAsync =
                afterSourceDeletedState,
            BeforeMarkerlessCompletedJournalCommitForTestAsync =
                beforeCompletedJournalCommit
        };
    }

    private async Task<Scenario> CreateScenarioAsync()
    {
        var root = FileService.GetTempDirectory("file-mover-markerless-move");
        var source = Path.Join(root, "source.m4b");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Join(destinationDirectory, "moved.m4b");
        await File.WriteAllTextAsync(source, "audio");
        return new Scenario(
            root,
            source,
            destination,
            GetFileIdentity(source),
            Guid.NewGuid());
    }

    private async Task AssertJournalStateAsync(
        Guid operationId,
        FileMutationJournalState state,
        string? targetIdentity)
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(state, journal.State);
        Assert.Equal(targetIdentity, journal.TargetPhysicalObjectIdentity);
    }

    private static string GetFileIdentity(string path)
    {
        using var lease = PinnedAudiobookFileRegistrationLease.Open(path);
        return lease.PhysicalObjectIdentity;
    }

    private static void AssertNoLibraryArtifacts(string root)
    {
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories),
            path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains(".listenarr-", StringComparison.Ordinal)
                    || name.EndsWith(".partial", StringComparison.Ordinal)
                    || name.Contains("quarantine", StringComparison.OrdinalIgnoreCase);
            });
    }

    private sealed record Scenario(
        string Root,
        string Source,
        string Destination,
        string SourceIdentity,
        Guid OperationId);
}
