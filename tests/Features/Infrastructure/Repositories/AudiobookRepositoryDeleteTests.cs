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
}
