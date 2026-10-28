using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "AudiobookDeletionIntentStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookDeletionIntentStoreTests : BaseTests
{
    [Fact]
    public async Task GetOrCreateAsync_RetryReusesActiveIntentAndPreservesDeleteFolderContract()
    {
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();

        var created = await store.GetOrCreateAsync(4101, deleteFolder: true);
        var retried = await store.GetOrCreateAsync(4101, deleteFolder: true);

        Assert.Equal(created.Id, retried.Id);
        Assert.Equal(AudiobookDeletionIntentState.Planned, retried.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetOrCreateAsync(4101, deleteFolder: false));
    }

    [Fact]
    public async Task StateTransitions_CannotSkipCleanupOrReopenTerminalIntent()
    {
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(4102, deleteFolder: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkCompletedAsync(intent.Id));

        await store.MarkFilesystemCleanupCompletedAsync(intent.Id);
        await store.MarkCompletedAsync(intent.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkNeedsAttentionAsync(intent.Id, "late failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkFilesystemCleanupCompletedAsync(intent.Id));

        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var persisted = await db.AudiobookDeletionIntents
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == intent.Id);
        Assert.Equal(AudiobookDeletionIntentState.Completed, persisted.State);
        Assert.Null(persisted.Error);
    }

    [Fact]
    public async Task NeedsAttention_IsTerminalAndExcludedFromRetry()
    {
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(4103, deleteFolder: false);
        await store.MarkNeedsAttentionAsync(intent.Id, "recovery authority lost");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetOrCreateAsync(4103, deleteFolder: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkFilesystemCleanupCompletedAsync(intent.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordErrorAsync(intent.Id, "retryable error"));
    }
}
