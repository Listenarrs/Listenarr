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
    public async Task MoveFilePreservingPhysicalIdentityAsync_SourceParentReplacedAfterTargetState_DoesNotComplete()
    {
        var root = FileService.GetTempDirectory(
            "file-mover-markerless-rename-source-parent-race");
        var sourceParent = Path.Join(root, "source");
        var displacedSourceParent = sourceParent + ".original";
        var destinationParent = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceParent);
        Directory.CreateDirectory(destinationParent);
        var source = Path.Join(sourceParent, "book.m4b");
        var destination = Path.Join(destinationParent, "renamed.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var sourceIdentity = GetFileIdentity(source);
        var operationId = Guid.NewGuid();
        var mover = CreateMover(
            afterTargetState: () =>
            {
                Directory.Move(sourceParent, displacedSourceParent);
                Directory.CreateDirectory(sourceParent);
                File.WriteAllText(source, "replacement");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFilePreservingPhysicalIdentityAsync(
            source,
            destination,
            sourceIdentity,
            operationId));

        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            sourceIdentity);
        AssertNoLibraryArtifacts(root);
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task MoveFilePreservingPhysicalIdentityAsync_DestinationParentReplacedAfterTargetState_DoesNotComplete()
    {
        var root = FileService.GetTempDirectory(
            "file-mover-markerless-rename-target-parent-race");
        var sourceParent = Path.Join(root, "source");
        var destinationParent = Path.Join(root, "destination");
        var displacedDestinationParent = destinationParent + ".original";
        Directory.CreateDirectory(sourceParent);
        Directory.CreateDirectory(destinationParent);
        var source = Path.Join(sourceParent, "book.m4b");
        var destination = Path.Join(destinationParent, "renamed.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var sourceIdentity = GetFileIdentity(source);
        var operationId = Guid.NewGuid();
        var mover = CreateMover(
            afterTargetState: () =>
            {
                Assert.False(File.Exists(source));
                Assert.True(File.Exists(destination));
                Directory.Move(destinationParent, displacedDestinationParent);
                Assert.False(File.Exists(source));
                Assert.True(File.Exists(
                    Path.Join(displacedDestinationParent, "renamed.m4b")));
                Directory.CreateDirectory(destinationParent);
                File.WriteAllText(
                    Path.Join(destinationParent, "foreign.txt"),
                    "replacement");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFilePreservingPhysicalIdentityAsync(
            source,
            destination,
            sourceIdentity,
            operationId));

        Assert.False(File.Exists(source));
        Assert.False(File.Exists(destination));
        Assert.Equal(
            "audio",
            await File.ReadAllTextAsync(
                Path.Join(displacedDestinationParent, "renamed.m4b")));
        Assert.Equal(
            "replacement",
            await File.ReadAllTextAsync(
                Path.Join(destinationParent, "foreign.txt")));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            sourceIdentity);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_SourceRecreatedAfterSourceDeletedState_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover(
            afterSourceDeletedState: () =>
            {
                File.WriteAllText(scenario.Source, "replacement");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));

        Assert.Equal("replacement", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task MoveFilePreservingPhysicalIdentityAsync_TargetReplacedAfterSourceDeletedState_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover(
            afterSourceDeletedState: () =>
            {
                File.Delete(scenario.Destination);
                File.WriteAllText(scenario.Destination, "foreign-target");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            scenario.SourceIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task MoveFilePreservingPhysicalIdentityAsync_SourceParentReplacedAfterSourceDeletedState_DoesNotComplete()
    {
        var root = FileService.GetTempDirectory(
            "file-mover-markerless-rename-source-parent-after-state-race");
        var sourceParent = Path.Join(root, "source");
        var displacedSourceParent = sourceParent + ".original";
        var destinationParent = Path.Join(root, "destination");
        Directory.CreateDirectory(sourceParent);
        Directory.CreateDirectory(destinationParent);
        var source = Path.Join(sourceParent, "book.m4b");
        var destination = Path.Join(destinationParent, "renamed.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var sourceIdentity = GetFileIdentity(source);
        var operationId = Guid.NewGuid();
        var mover = CreateMover(
            afterSourceDeletedState: () =>
            {
                Directory.Move(sourceParent, displacedSourceParent);
                Directory.CreateDirectory(sourceParent);
                File.WriteAllText(source, "replacement");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFilePreservingPhysicalIdentityAsync(
            source,
            destination,
            sourceIdentity,
            operationId));

        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            sourceIdentity);
        AssertNoLibraryArtifacts(root);
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

    [LinuxFact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_CompatibleExpectedSourceToken_PersistsSameDurableTargetToken()
    {
        var scenario = await CreateScenarioAsync();
        Assert.StartsWith(
            "linux-generation:",
            scenario.SourceIdentity,
            StringComparison.Ordinal);
        var durableSourceIdentity =
            LinuxIdentityTestHelper.ToMergedV1AugmentedIdentity(
                scenario.SourceIdentity);

        Assert.True(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            durableSourceIdentity,
            scenario.OperationId));

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Equal(durableSourceIdentity, journal.SourcePhysicalObjectIdentity);
        Assert.Equal(durableSourceIdentity, journal.TargetPhysicalObjectIdentity);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_CompatiblePersistedTargetToken_RemainsDurableAcrossRecovery()
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

        Assert.StartsWith(
            "linux-generation:",
            scenario.SourceIdentity,
            StringComparison.Ordinal);
        var persistedCompatibleTargetIdentity =
            LinuxIdentityTestHelper.ToMergedV1AugmentedIdentity(
                scenario.SourceIdentity);
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals.SingleAsync(
                candidate => candidate.OperationId == scenario.OperationId);
            Assert.Equal(
                FileMutationJournalState.TargetIdentityPersisted,
                journal.State);
            journal.TargetPhysicalObjectIdentity =
                persistedCompatibleTargetIdentity;
            await db.SaveChangesAsync();
        }

        Assert.True(await CreateMover().MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));

        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            persistedCompatibleTargetIdentity);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
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

    [LinuxFact]
    public async Task MoveFilePreservingPhysicalIdentityAsync_TargetReplacedDuringCompletedCommit_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync();
        var mover = CreateMover(
            beforeCompletedJournalCommit: () =>
            {
                File.Delete(scenario.Destination);
                File.WriteAllText(scenario.Destination, "foreign-target");
                return Task.CompletedTask;
            });

        Assert.False(await mover.MoveFilePreservingPhysicalIdentityAsync(
            scenario.Source,
            scenario.Destination,
            scenario.SourceIdentity,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
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
        Func<Task>? afterTargetState = null,
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
            AfterMarkerlessRenameJournalPlannedForTestAsync =
                afterJournalPlanned,
            AfterMarkerlessRenamePublishedBeforeTargetStateForTestAsync =
                afterPublishedBeforeTargetState,
            AfterMarkerlessRenameTargetStateForTestAsync = afterTargetState,
            AfterMarkerlessRenameSourceDeletedStateForTestAsync =
                afterSourceDeletedState,
            BeforeMarkerlessCompletedJournalCommitForTestAsync =
                beforeCompletedJournalCommit
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
