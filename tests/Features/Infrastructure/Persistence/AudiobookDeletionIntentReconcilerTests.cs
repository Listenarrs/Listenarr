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
    public async Task ReconcileAsync_PlannedIntent_RetainsFilesystemAndDatabaseUntilExplicitRetry()
    {
        var root = FileService.GetTempDirectory("delete-recovery-planned");
        var file = await FileService.GetFileAsync(root, "book.m4b", "audio");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Delete Recovery")
            .WithBasePath(root)
            .WithFilePath(file)
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        var reconciler = BuildReconciler(store);

        await reconciler.ReconcileAsync();

        Assert.True(File.Exists(file));
        Assert.NotNull(await _audiobookRepository.GetByIdAsync(audiobook.Id));
        var persisted = await GetIntentAsync(intent.Id);
        Assert.Equal(AudiobookDeletionIntentState.Planned, persisted.State);
        Assert.Contains(
            "live delete proof",
            persisted.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
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
        var reconciler = BuildReconciler(store);

        await reconciler.ReconcileAsync();

        Assert.Null(await _audiobookRepository.GetByIdAsync(audiobook.Id));
        Assert.Equal(
            AudiobookDeletionIntentState.Completed,
            (await GetIntentAsync(intent.Id)).State);
    }

    [Fact]
    public async Task ReconcileAsync_DatabaseRowAlreadyDeletedAfterCleanup_CompletesIntent()
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Delete Recovery Post Commit Crash")
            .WithBasePath(FileService.GetTempDirectory("delete-recovery-post-commit"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        await store.MarkFilesystemCleanupCompletedAsync(intent.Id);
        Assert.True(await _audiobookRepository.DeleteByIdAsync(audiobook.Id));
        var reconciler = BuildReconciler(store);

        await reconciler.ReconcileAsync();

        Assert.Equal(
            AudiobookDeletionIntentState.Completed,
            (await GetIntentAsync(intent.Id)).State);
    }

    [Fact]
    public async Task ReconcileAsync_NeedsAttentionIntent_DoesNotBlockIndependentCommittedIntent()
    {
        var blockedBook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Scoped Delete Repair")
            .WithBasePath(FileService.GetTempDirectory("delete-recovery-scoped-repair"))
            .Build());
        var committedBook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Independent Delete Commit")
            .WithBasePath(FileService.GetTempDirectory("delete-recovery-independent"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var blockedIntent = await store.GetOrCreateAsync(
            blockedBook.Id,
            deleteFolder: true);
        await store.MarkNeedsAttentionAsync(
            blockedIntent.Id,
            "Injected scoped repair state.");
        var committedIntent = await store.GetOrCreateAsync(
            committedBook.Id,
            deleteFolder: false);
        await store.MarkFilesystemCleanupCompletedAsync(committedIntent.Id);
        var reconciler = BuildReconciler(store);

        await reconciler.ReconcileAsync();

        Assert.NotNull(await _audiobookRepository.GetByIdAsync(blockedBook.Id));
        Assert.Equal(
            AudiobookDeletionIntentState.NeedsAttention,
            (await GetIntentAsync(blockedIntent.Id)).State);
        Assert.Null(await _audiobookRepository.GetByIdAsync(committedBook.Id));
        Assert.Equal(
            AudiobookDeletionIntentState.Completed,
            (await GetIntentAsync(committedIntent.Id)).State);
    }

    [Fact]
    public async Task ReconcileAsync_PlannedIntentWithMissingDatabaseRow_BecomesScopedNeedsAttention()
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Delete Recovery Missing Row")
            .WithBasePath(FileService.GetTempDirectory("delete-recovery-missing-row"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        Assert.True(await _audiobookRepository.DeleteByIdAsync(audiobook.Id));
        var reconciler = BuildReconciler(store);

        await reconciler.ReconcileAsync();

        var persisted = await GetIntentAsync(intent.Id);
        Assert.Equal(AudiobookDeletionIntentState.NeedsAttention, persisted.State);
        Assert.Contains(
            "audiobook row disappeared",
            persisted.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    private AudiobookDeletionIntentReconciler BuildReconciler(
        IAudiobookDeletionIntentStore store) =>
        new(
            store,
            _audiobookRepository,
            _provider.GetRequiredService<IAudiobookDeletionCommitService>(),
            NullLogger<AudiobookDeletionIntentReconciler>.Instance);

    private async Task<AudiobookDeletionIntent> GetIntentAsync(Guid intentId)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.AudiobookDeletionIntents
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == intentId);
    }
}
