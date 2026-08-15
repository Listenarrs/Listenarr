using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Area", "Persistence")]
[Trait("Name", "EfAudiobookFileTrackedPathTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfAudiobookFileTrackedPathTests : BaseTests
{
    [Fact]
    public async Task GetAllFilePathsAsync_ResolvesRelativeFileAndLegacyFilePath()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new ListenArrDbContext(options);
        var basePath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"tracked-repository-{Guid.NewGuid():N}"));
        var audiobook = new Audiobook
        {
            BasePath = basePath,
            FilePath = Path.Join("legacy", "single.m4b")
        };
        var file = AudiobookFile.CreateUnresolved(Path.Join("disc-1", "chapter.m4b"));
        audiobook.Files = [file];
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        var repository = new EfAudiobookFileRepository(db);

        var paths = await repository.GetAllFilePathsAsync(
            FileSystemPathSemantics.CurrentHostDefault);

        Assert.Contains(Path.Join(basePath, "disc-1", "chapter.m4b"), paths);
        Assert.Contains(Path.Join(basePath, "legacy", "single.m4b"), paths);
        Assert.DoesNotContain(Path.GetFullPath(Path.Join("disc-1", "chapter.m4b")), paths);
    }
}
