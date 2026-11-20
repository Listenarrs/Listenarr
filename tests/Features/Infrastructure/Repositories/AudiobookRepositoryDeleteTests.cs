using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Area", "Library")]
[Trait("Name", "AudiobookRepositoryDeleteTests")]
[Trait("Category", "Persistence")]
public sealed class AudiobookRepositoryDeleteTests : BaseTests
{
    [Fact]
    public async Task DeleteByIdAsync_LargeFileGraph_UsesDatabaseCascadeWithoutMaterializingChildren()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new ListenArrDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Copied Linux Database",
            BasePath = "/server/mnt/drive/Audiobooks/Imported"
        };
        context.Audiobooks.Add(audiobook);
        await context.SaveChangesAsync();

        var files = Enumerable.Range(0, 2500)
            .Select(index =>
            {
                var file = AudiobookFile.CreateUnresolved(
                    $"/server/mnt/drive/Audiobooks/Imported/book-{index:D4}.mp3");
                file.AudiobookId = audiobook.Id;
                return file;
            })
            .ToArray();
        context.AudiobookFiles.AddRange(files);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var repository = new AudiobookRepository(context);

        var deleted = await repository.DeleteByIdAsync(audiobook.Id);

        Assert.True(deleted);
        Assert.Empty(context.ChangeTracker.Entries<AudiobookFile>());
        Assert.False(await context.Audiobooks.AnyAsync(candidate => candidate.Id == audiobook.Id));
        Assert.False(await context.AudiobookFiles.AnyAsync(candidate => candidate.AudiobookId == audiobook.Id));
    }

    [Fact]
    public async Task DeleteByIdAsync_SweepsTheBlocklistRowsTheDatabaseDoesNotCascade()
    {
        // BlockedReleases carries an AudiobookId but no foreign key, so nothing in the database
        // removes its rows when a book goes. Left behind, they are unreachable: the delete
        // endpoints are keyed on a book that no longer exists, and a book re-added later takes
        // a new id and never sees them. They would sit there for the life of the install.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new ListenArrDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var deleted = new Audiobook { Title = "Deleted Book" };
        var kept = new Audiobook { Title = "Kept Book" };
        context.Audiobooks.AddRange(deleted, kept);
        await context.SaveChangesAsync();
        context.BlockedReleases.AddRange(
            new BlockedRelease
            {
                AudiobookId = deleted.Id,
                ReleaseIdentifier = "btih:abcdef1234567890abcdef1234567890abcdef12",
                Title = "First",
                Reason = "simulated failure"
            },
            new BlockedRelease
            {
                AudiobookId = deleted.Id,
                ReleaseIdentifier = "btih:1111111111111111111111111111111111111111",
                Title = "Second",
                Reason = "simulated failure"
            },
            new BlockedRelease
            {
                AudiobookId = kept.Id,
                ReleaseIdentifier = "btih:2222222222222222222222222222222222222222",
                Title = "Somebody else's",
                Reason = "simulated failure"
            });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var repository = new AudiobookRepository(context);

        Assert.True(await repository.DeleteByIdAsync(deleted.Id));

        Assert.False(await context.BlockedReleases.AnyAsync(entry => entry.AudiobookId == deleted.Id));
        // The control: a sweep that took the whole table would satisfy the assertion above.
        Assert.True(await context.BlockedReleases.AnyAsync(entry => entry.AudiobookId == kept.Id));
    }
}
