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

    private FileMover CreateMover(
        bool disableNativeRename = false,
        bool forceCrossVolume = false,
        Func<Task>? afterJournalPlanned = null,
        Func<Task>? afterPublishedBeforeTargetState = null,
        Func<Task>? afterTargetCreatedBeforeState = null,
        Func<Task>? afterTargetState = null,
        Func<Task>? afterTargetWrittenBeforeVerifiedState = null,
        Func<Task>? afterSourceDeletedBeforeState = null)
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
                afterSourceDeletedBeforeState
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
