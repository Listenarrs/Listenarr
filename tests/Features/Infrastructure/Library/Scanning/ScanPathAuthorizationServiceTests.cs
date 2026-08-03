using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Area", "Library")]
[Trait("Name", "ScanPathAuthorizationServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class ScanPathAuthorizationServiceTests : BaseTests
{
    [DirectoryLinkFact]
    public async Task AuthorizeAsync_LinkedAncestorOutsideConfiguredRoot_IsRejected()
    {
        var parent = FileService.GetTempDirectory("scan-authorization-linked-ancestor");
        var configuredRoot = Path.Join(parent, "library");
        var outsideRoot = Path.Join(parent, "outside");
        var outsideBook = Path.Join(outsideRoot, "Book");
        var linkedAncestor = Path.Join(configuredRoot, "alias");
        Directory.CreateDirectory(configuredRoot);
        Directory.CreateDirectory(outsideBook);
        Directory.CreateSymbolicLink(linkedAncestor, outsideRoot);
        await AddAuthorizedRootAsync(configuredRoot);
        var service = _provider.GetRequiredService<IScanPathAuthorizationService>();

        var result = await service.AuthorizeAsync(
            Path.Join(linkedAncestor, "Book"));

        Assert.False(result.IsAuthorized);
        Assert.Equal(
            ScanPathAuthorizationFailure.IdentityUnavailable,
            result.Failure);
        Assert.Null(result.PhysicalIdentity);
        Assert.True(Directory.Exists(outsideBook));
    }

    [WindowsFact]
    public async Task AuthorizeAsync_ForeignPersistedRootSyntax_CannotAliasWindowsRoot()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-foreign-root");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        await AddAuthorizedRootAsync(configuredRoot);
        var foreignRoot = "/" + Path.GetRelativePath(
                Path.GetPathRoot(configuredRoot)!,
                configuredRoot)
            .Replace('\\', '/');
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync();
            root.Path = foreignRoot;
            await db.SaveChangesAsync();
        }
        var foreignScanRoot = foreignRoot + "/Book";

        var result = await _provider
            .GetRequiredService<IScanPathAuthorizationService>()
            .AuthorizeAsync(foreignScanRoot);

        Assert.False(result.IsAuthorized);
        Assert.NotEqual(
            ScanPathAuthorizationFailure.None,
            result.Failure);
        Assert.True(Directory.Exists(scanRoot));
    }

    [Fact]
    public async Task AuthorizeAsync_ReplacedRoot_ProducesDifferentPhysicalIdentity()
    {
        var parent = FileService.GetTempDirectory("scan-authorization-root-replacement");
        var configuredRoot = Path.Join(parent, "library");
        var scanRoot = Path.Join(configuredRoot, "Book");
        var displacedRoot = Path.Join(parent, "library-original");
        Directory.CreateDirectory(scanRoot);
        await AddAuthorizedRootAsync(configuredRoot);
        var service = _provider.GetRequiredService<IScanPathAuthorizationService>();
        var original = await service.AuthorizeAsync(scanRoot);
        Assert.True(original.IsAuthorized, original.Error);

        Directory.Move(configuredRoot, displacedRoot);
        Directory.CreateDirectory(scanRoot);
        var replacement = await service.AuthorizeAsync(scanRoot);

        Assert.True(replacement.IsAuthorized, replacement.Error);
        Assert.NotEqual(
            original.PhysicalIdentity,
            replacement.PhysicalIdentity);
        Assert.True(Directory.Exists(Path.Join(displacedRoot, "Book")));
        Assert.True(Directory.Exists(scanRoot));
    }
}
