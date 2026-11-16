using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationServiceTests
{
    [Fact]
    public async Task MetadataOnly_RewritesFuturePlacementWithoutMovingObservedFiles()
    {
        var source = Path.Join(TempRoot, $"future-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"future-target-{Guid.NewGuid():N}");
        var observed = Path.Join(TempRoot, $"future-observed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(observed);
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        int rootId, bookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Future placement root",
                Path = source,
                CaseSensitivityMode = semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivityMode.Sensitive : FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid
            };
            var book = new Audiobook { Title = "Future placement", BasePath = observed, NamingRootPath = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();
            rootId = root.Id;
            bookId = book.Id;
        }
        var result = await CreateService().StartAsync(rootId, new RootFolderPathChangeCommand(
            target, RootFolderRelocationMode.MetadataOnly, false, "Future placement root", false,
            semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivityMode.Sensitive : FileSystemCaseSensitivityMode.Insensitive, source));
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verify = await _factory.CreateDbContextAsync();
        var current = await verify.Audiobooks.SingleAsync(book => book.Id == bookId);
        Assert.Equal(target, current.NamingRootPath);
        Assert.Equal(observed, current.BasePath);
    }
}
