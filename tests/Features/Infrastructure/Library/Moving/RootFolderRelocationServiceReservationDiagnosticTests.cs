using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationServiceTests
{
    [Theory]
    [InlineData(DirectoryObjectIdentityFailureKind.Unknown)]
    [InlineData(DirectoryObjectIdentityFailureKind.IdentityUnstable)]
    [InlineData(DirectoryObjectIdentityFailureKind.AccessDenied)]
    public async Task StartRelocation_CurrentUsableTarget_DiagnosticFailureDoesNotBlock(
        DirectoryObjectIdentityFailureKind failureKind)
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        Directory.CreateDirectory(target);
        var resolver = new Mock<IDirectoryObjectIdentityResolver>();
        resolver.Setup(candidate => candidate.ResolveAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "The optional diagnostic cannot be captured.", failureKind));

        var result = await CreateService(directoryObjectIdentityResolver: resolver.Object)
            .StartAsync(rootId, BuildRelocationCommand(target));

        Assert.Equal(RootFolderRelocationStatus.Pending, result.Status);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.MoveJobs.ToListAsync());
        Assert.Equal(TargetIdentityEnrollmentState.NotRequired,
            (await db.RootFolderRelocations.SingleAsync()).TargetIdentityEnrollmentState);
    }

    [Theory]
    [InlineData(null, FileMutationJournalState.CompletedSourceRetained)]
    [InlineData(FileMutationOwner.CompanionFile, FileMutationJournalState.CompletedSourceRetained)]
    [InlineData(FileMutationOwner.RegistrationCompanionFile, FileMutationJournalState.CompletedSourceRetained)]
    [InlineData(null, FileMutationJournalState.RolledBack)]
    [InlineData(FileMutationOwner.CompanionFile, FileMutationJournalState.RolledBack)]
    [InlineData(FileMutationOwner.RegistrationCompanionFile, FileMutationJournalState.RolledBack)]
    [InlineData(0, FileMutationJournalState.RolledBack)]
    public async Task StartRelocation_TerminalPublication_DoesNotInventRecoveryBlocker(
        int? fileOwner, FileMutationJournalState state)
    {
        var (rootId, audiobookId, source, target) = await SeedRelocationScenarioAsync();
        var sourceFile = Path.Join(source, "Author", "Title", "book.m4b");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                Action = FileAction.Move,
                SourcePath = Path.Join(source, "old-download.m4b"),
                DestinationPath = sourceFile,
                SourceLength = 5,
                State = state,
                AudiobookId = audiobookId,
                AudiobookFileId = fileOwner
            });
            await db.SaveChangesAsync();
        }

        var result = await CreateService().StartAsync(rootId, BuildRelocationCommand(target));

        Assert.Equal(RootFolderRelocationStatus.Pending, result.Status);
        Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
        await using var current = await _factory.CreateDbContextAsync();
        Assert.Single(await current.MoveJobs.ToListAsync());
        Assert.Equal(state, (await current.FileMutationJournals.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileActive_ReservationDiagnosticChanged_DoesNotBlockCurrentPathRecovery(
        bool interruptPlannedParent)
    {
        var source = Path.Join(TempRoot, $"reservation-diagnostic-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"reservation-diagnostic-target-{Guid.NewGuid():N}", "nested");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }
        var interrupted = CreateService();
        Action<string> crash = _ => throw new IOException("Interrupted reservation publication.");
        if (interruptPlannedParent)
        {
            interrupted.AfterReservationParentIntentPersistedForTest = crash;
        }
        else
        {
            interrupted.AfterTargetReservationStatePersistedForTest = crash;
        }

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(rootId, BuildRelocationCommand(target)));
        Guid relocationId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            relocationId = (await db.RootFolderRelocations.SingleAsync()).Id;
            var reservations = await db.RootFolderRelocationCreatedDirectories.ToListAsync();
            Assert.NotEmpty(reservations);
            foreach (var reservation in reservations)
            {
                reservation.DirectoryObjectIdentity = "legacy-other-client-observation";
            }
            await db.SaveChangesAsync();
        }

        var restarted = CreateService();
        await restarted.ReconcileActiveAsync();
        await restarted.ReconcileActiveAsync();
        Assert.True(Directory.Exists(target));
        var result = await restarted.RetryAsync(relocationId);

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(target));
        await using var current = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await current.RootFolders.SingleAsync()).Path);
        Assert.All(await current.RootFolderRelocationCreatedDirectories.ToListAsync(),
            reservation => Assert.Equal(RootFolderRelocationCreatedDirectoryState.Retained, reservation.State));
    }
}
