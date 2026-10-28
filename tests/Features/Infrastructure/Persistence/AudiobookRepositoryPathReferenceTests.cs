using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "AudiobookRepositoryPathReferenceTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookRepositoryPathReferenceTests : BaseTests
{
    [Fact]
    public async Task RewritePathReferencesAsync_SaveFailure_ThrowsPersistenceException()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var source = Path.GetFullPath("path-rewrite-source");
        var target = Path.GetFullPath("path-rewrite-target");
        var seedOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        int audiobookId;
        await using (var seed = new ListenArrDbContext(seedOptions))
        {
            var audiobook = new Audiobook
            {
                Title = "Path Rewrite Persistence",
                BasePath = source,
                FilePath = Path.Join(source, "book.m4b")
            };
            seed.Audiobooks.Add(audiobook);
            await seed.SaveChangesAsync();
            audiobookId = audiobook.Id;
        }

        var failingOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(new ThrowingSaveChangesInterceptor())
            .Options;
        await using var db = new ListenArrDbContext(failingOptions);
        var repository = new AudiobookRepository(db);

        var exception = await Assert.ThrowsAsync<PersistenceException>(() =>
            repository.RewritePathReferencesAsync(
                audiobookId,
                source,
                target,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault));

        Assert.Equal("persistence_failure", exception.Code);
        Assert.IsType<DbUpdateException>(exception.InnerException);
    }

    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException("Simulated path rewrite persistence failure."));
    }
}
