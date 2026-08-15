using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "EfLibraryAddCommitStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfLibraryAddCommitStoreTests : BaseTests
{
    [Fact]
    public async Task CommitAsync_CancelledBetweenWrites_RollsBackAudiobookAndHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelAfterFirstSaveInterceptor(cancellation);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new EfLibraryAddCommitStore(db);
        var audiobook = new Audiobook { Title = "Cancelled Atomic Add" };
        var history = new History
        {
            AudiobookTitle = audiobook.Title,
            EventType = "Added",
            Message = "Added",
            Source = "Test",
            Timestamp = DateTime.UtcNow
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CommitAsync(audiobook, history, cancellation.Token));

        Assert.Empty(await db.Audiobooks.AsNoTracking().ToListAsync());
        Assert.Empty(await db.History.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CommitAsync_AssignsGeneratedAudiobookIdToHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new EfLibraryAddCommitStore(db);
        var audiobook = new Audiobook { Title = "Atomic Add" };
        var history = new History
        {
            AudiobookTitle = audiobook.Title,
            EventType = "Added",
            Message = "Added",
            Source = "Test",
            Timestamp = DateTime.UtcNow
        };

        await store.CommitAsync(audiobook, history);

        Assert.True(audiobook.Id > 0);
        var storedHistory = Assert.Single(await db.History.AsNoTracking().ToListAsync());
        Assert.Equal(audiobook.Id, storedHistory.AudiobookId);
    }

    private sealed class CancelAfterFirstSaveInterceptor(CancellationTokenSource cancellation)
        : SaveChangesInterceptor
    {
        private int _completedSaves;

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _completedSaves) == 1)
            {
                cancellation.Cancel();
            }

            return ValueTask.FromResult(result);
        }
    }
}
