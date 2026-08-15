using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileMoverMarkerlessRenameTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileMoverMarkerlessRenameTests : BaseTests
{
    [Fact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_MarkerlessRenameCompletesWithoutArtifacts()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover();

        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        Assert.Equal(
            scenario.SourceIdentity,
            GetFileIdentity(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [WindowsFact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_CaseAliasRetryUsesSameDurableJournal()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            afterJournalPlanned: () =>
                throw new IOException("Injected crash after markerless rename journal plan."));

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId));

        Assert.True(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source.ToUpperInvariant(),
            scenario.Destination.ToUpperInvariant(),
            scenario.SourceIdentity,
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
    public async Task MoveFilePreservingPhysicalIdentityAsync_CrashAfterJournalPlanResumes()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            afterJournalPlanned: () =>
                throw new IOException("Injected crash after markerless journal plan."));

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId));

        Assert.True(File.Exists(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            targetIdentity: null);
        AssertNoLibraryArtifacts(scenario.Root);

        Assert.True(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
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
    public async Task MoveFilePreservingPhysicalIdentityAsync_CrashAfterNativeRenameResumesFromIdentity()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            afterPublishedBeforeTargetState: () =>
                throw new IOException("Injected crash after markerless native rename."));

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        Assert.Equal(
            scenario.SourceIdentity,
            GetFileIdentity(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            targetIdentity: null);
        AssertNoLibraryArtifacts(scenario.Root);

        Assert.True(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_CrashAfterTargetStateResumes()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            afterTargetState: () =>
                throw new IOException("Injected crash after markerless target state."));

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetIdentityPersisted,
            scenario.SourceIdentity);

        Assert.True(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_CompletedRetryIsIdempotent()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover();

        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));
        Assert.True(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        Assert.Equal(
            scenario.SourceIdentity,
            GetFileIdentity(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_OwnerMetadataReconciledRetryIgnoresLaterSourceReuse()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover();

        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            audiobookId: 42,
            audiobookFileId: 420));

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
            Assert.Equal(42, journal.AudiobookId);
            Assert.Equal(420, journal.AudiobookFileId);
            journal.State = FileMutationJournalState.OwnerMetadataReconciled;
            await db.SaveChangesAsync();
        }

        await File.WriteAllTextAsync(scenario.Source, "recreated-source");

        Assert.True(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId,
            audiobookId: 42,
            audiobookFileId: 420));
        Assert.Equal("recreated-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.OwnerMetadataReconciled,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_CompletedJournalWithRecreatedSourcePreservesSourceAndBlocks()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover();
        Assert.True(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));

        await File.WriteAllTextAsync(scenario.Source, "recreated-source");

        Assert.False(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));
        Assert.Equal("recreated-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_TargetReplacedAfterUncommittedRenameIsPreservedAndBlocked()
    {
        var scenario = await CreateScenarioAsync();
        var interrupted = CreateMover(
            afterPublishedBeforeTargetState: () =>
                throw new IOException("Injected crash after markerless native rename."));
        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.MoveFilePreservingPhysicalIdentityAsync(
                scenario.Source,
                scenario.Destination,
                scenario.SourceIdentity,
                scenario.OperationId));

        File.Delete(scenario.Destination);
        await File.WriteAllTextAsync(scenario.Destination, "foreign");
        var replacementIdentity = GetFileIdentity(scenario.Destination);
        Assert.NotEqual(scenario.SourceIdentity, replacementIdentity);

        Assert.False(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));

        Assert.Equal("foreign", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            targetIdentity: null);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    private FileMover CreateMover(
        Func<Task>? afterJournalPlanned = null,
        Func<Task>? afterPublishedBeforeTargetState = null,
        Func<Task>? afterTargetState = null)
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
            AfterMarkerlessRenameJournalPlannedForTestAsync =
                afterJournalPlanned,
            AfterMarkerlessRenamePublishedBeforeTargetStateForTestAsync =
                afterPublishedBeforeTargetState,
            AfterMarkerlessRenameTargetStateForTestAsync = afterTargetState
        };
    }

    private async Task<Scenario> CreateScenarioAsync()
    {
        var root = FileService.GetTempDirectory(
            "file-mover-markerless-rename");
        var source = Path.Join(root, "source.m4b");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Join(destinationDirectory, "renamed.m4b");
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
