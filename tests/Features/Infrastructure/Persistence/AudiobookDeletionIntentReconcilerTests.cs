using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "AudiobookDeletionIntentReconcilerTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookDeletionIntentReconcilerTests : BaseTests
{
    [Fact]
    public async Task ReconcileAsync_PlannedIntent_CleansFilesystemBeforeDeletingDatabaseRow()
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Delete Recovery")
            .WithBasePath(FileService.GetTempDirectory("delete-recovery-planned"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        filesystem.Setup(service => service.DeleteAsync(
                It.Is<Audiobook>(candidate => candidate.Id == audiobook.Id),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudiobookFilesystemDeleteResult
            {
                TrackedFileCleanupComplete = true
            });
        var reconciler = new AudiobookDeletionIntentReconciler(
            store,
            _audiobookRepository,
            _provider.GetRequiredService<IAudiobookDeletionCommitService>(),
            filesystem.Object,
            NullLogger<AudiobookDeletionIntentReconciler>.Instance);

        await reconciler.ReconcileAsync();

        filesystem.VerifyAll();
        Assert.Null(await _audiobookRepository.GetByIdAsync(audiobook.Id));
        Assert.Equal(
            AudiobookDeletionIntentState.Completed,
            await GetIntentStateAsync(intent.Id));
    }

    [Fact]
    public async Task ReconcileAsync_CleanupAlreadyCommitted_DeletesDatabaseWithoutRepeatingFilesystemMutation()
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Delete Recovery Commit")
            .WithBasePath(FileService.GetTempDirectory("delete-recovery-commit"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: false);
        await store.MarkFilesystemCleanupCompletedAsync(intent.Id);
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        var reconciler = new AudiobookDeletionIntentReconciler(
            store,
            _audiobookRepository,
            _provider.GetRequiredService<IAudiobookDeletionCommitService>(),
            filesystem.Object,
            NullLogger<AudiobookDeletionIntentReconciler>.Instance);

        await reconciler.ReconcileAsync();

        filesystem.Verify(service => service.DeleteAsync(
            It.IsAny<Audiobook>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(await _audiobookRepository.GetByIdAsync(audiobook.Id));
        Assert.Equal(
            AudiobookDeletionIntentState.Completed,
            await GetIntentStateAsync(intent.Id));
    }

    [Fact]
    public async Task ReconcileAsync_DatabaseRowAlreadyDeletedAfterCleanup_CompletesIntentWithoutRepeatingCleanup()
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Delete Recovery Post Commit Crash")
            .WithBasePath(FileService.GetTempDirectory("delete-recovery-post-commit"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        await store.MarkFilesystemCleanupCompletedAsync(intent.Id);
        Assert.True(await _audiobookRepository.DeleteByIdAsync(audiobook.Id));
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        var reconciler = new AudiobookDeletionIntentReconciler(
            store,
            _audiobookRepository,
            _provider.GetRequiredService<IAudiobookDeletionCommitService>(),
            filesystem.Object,
            NullLogger<AudiobookDeletionIntentReconciler>.Instance);

        await reconciler.ReconcileAsync();

        filesystem.VerifyNoOtherCalls();
        Assert.Equal(
            AudiobookDeletionIntentState.Completed,
            await GetIntentStateAsync(intent.Id));
    }

    [Fact]
    public async Task ReconcileAsync_FilesystemFailure_PreservesDatabaseRowForRetry()
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Delete Recovery Failure")
            .WithBasePath(FileService.GetTempDirectory("delete-recovery-failure"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        filesystem.Setup(service => service.DeleteAsync(
                It.IsAny<Audiobook>(),
                true,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Injected cleanup failure."));
        var reconciler = new AudiobookDeletionIntentReconciler(
            store,
            _audiobookRepository,
            _provider.GetRequiredService<IAudiobookDeletionCommitService>(),
            filesystem.Object,
            NullLogger<AudiobookDeletionIntentReconciler>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconciler.ReconcileAsync());

        Assert.NotNull(await _audiobookRepository.GetByIdAsync(audiobook.Id));
        Assert.Equal(
            AudiobookDeletionIntentState.Planned,
            await GetIntentStateAsync(intent.Id));
    }

    private async Task<AudiobookDeletionIntentState> GetIntentStateAsync(Guid intentId)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.AudiobookDeletionIntents
            .Where(candidate => candidate.Id == intentId)
            .Select(candidate => candidate.State)
            .SingleAsync();
    }
}
