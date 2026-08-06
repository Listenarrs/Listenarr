using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileMoverMarkerlessRegistrationTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileMoverMarkerlessRegistrationTests : BaseTests
{
    [Fact]
    public async Task PrepareMove_RequiresRegistrationCommitBeforeSourceDeletion()
    {
        var scenario = await CreateScenarioAsync("move-authority");
        var mover = CreateMover();

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        Assert.False(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));
        Assert.True(File.Exists(scenario.Source));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        Assert.True(lease.PrepareCleanupRecovery(17));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.RegistrationCommitted,
            audiobookId: 17);

        Assert.True(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 17);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task PrepareCopy_CommittedRegistrationCompletesJournalWithoutSourceMutation(
        FileAction action)
    {
        var scenario = await CreateScenarioAsync($"registration-{action}");
        var mover = CreateMover();

        using var lease = await mover.PrepareActionForRegistrationAsync(
            action,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        Assert.True(lease.MatchesCurrentPublication());
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        Assert.True(lease.PrepareCleanupRecovery(23));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 23);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task PrepareMove_RetryAfterOwnershipCommitGapReusesVerifiedGeneration()
    {
        var scenario = await CreateScenarioAsync("registration-retry");
        var firstMover = CreateMover();
        string targetIdentity;
        using (var firstLease = await firstMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId))
        {
            Assert.NotNull(firstLease);
            targetIdentity = firstLease.PhysicalObjectIdentity;
        }

        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        var retryMover = CreateMover();
        using var retryLease = await retryMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            targetIdentity);

        Assert.NotNull(retryLease);
        Assert.Equal(targetIdentity, retryLease.PhysicalObjectIdentity);
        Assert.True(retryLease.PrepareCleanupRecovery(31));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            retryLease.CompletePublication());
        Assert.True(await retryMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            retryLease,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 31);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task CompleteMove_CrashAfterSourceDeletionResumesFromDatabaseAuthorization()
    {
        var scenario = await CreateScenarioAsync("registration-delete-crash");
        var crashingMover = CreateMover(
            afterSourceDeletedBeforeState: () =>
                throw new IOException("Injected crash after source deletion."));
        using var lease = await crashingMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(41));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.False(await crashingMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.SourceDeletionAuthorized,
            audiobookId: 41);

        var recoveryMover = CreateMover();
        using var recoveryLease = await recoveryMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            lease.PhysicalObjectIdentity);
        Assert.NotNull(recoveryLease);
        Assert.True(recoveryLease.PrepareCleanupRecovery(41));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            recoveryLease.CompletePublication());
        Assert.True(await recoveryMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            recoveryLease,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 41);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task CompleteMove_ReplacedSourceIsPreservedAndJournalNeedsAttention()
    {
        var scenario = await CreateScenarioAsync("registration-source-replaced");
        var mover = CreateMover();
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(53));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        File.Delete(scenario.Source);
        await File.WriteAllTextAsync(scenario.Source, "foreign-source");

        Assert.False(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));

        Assert.Equal("foreign-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 53);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    private FileMover CreateMover(
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
                "file-mover-markerless-registration-locks"),
            AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync =
                afterSourceDeletedBeforeState
        };
    }

    private async Task<Scenario> CreateScenarioAsync(string name)
    {
        var root = FileService.GetTempDirectory(name);
        var source = Path.Join(root, "source.m4b");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Join(destinationDirectory, "published.m4b");
        await File.WriteAllTextAsync(source, "audio");
        return new Scenario(root, source, destination, Guid.NewGuid());
    }

    private async Task AssertJournalStateAsync(
        Guid operationId,
        FileMutationJournalState state,
        int? audiobookId)
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(state, journal.State);
        Assert.Equal(audiobookId, journal.AudiobookId);
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
        Guid OperationId);
}
