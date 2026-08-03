using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "RootFolderObjectIdentityReconcilerTests")]
[Trait("Category", "Infrastructure")]
public sealed class RootFolderObjectIdentityReconcilerTests : BaseTests
{
    [WindowsFact]
    public async Task ReconcileAsync_AmbiguousPersistedRoot_DoesNotEnrollWindowsDeviceAlias()
    {
        var nativeRoot = FileService.GetTempDirectory("root-object-identity-ambiguous");
        var ambiguousRoot = "//?/" + Path.GetFullPath(nativeRoot).Replace('\\', '/');
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousRoot,
            out _));
        Assert.True(Directory.Exists(ambiguousRoot));

        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            setup.RootFolders.Add(new RootFolder
            {
                Id = 1,
                Name = "Legacy Root",
                Path = ambiguousRoot
            });
            await setup.SaveChangesAsync();
        }

        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        var reconciler = new RootFolderObjectIdentityReconciler(
            new TestDbContextFactory(options),
            identityResolver.Object,
            new FilesystemMutationCoordinator(),
            NullLogger<RootFolderObjectIdentityReconciler>.Instance);

        await reconciler.ReconcileAsync();

        identityResolver.VerifyNoOtherCalls();
        await using var verification = new ListenArrDbContext(options);
        var root = await verification.RootFolders.SingleAsync();
        Assert.Null(root.DirectoryObjectIdentityVersion);
        Assert.Null(root.DirectoryObjectIdentity);
        Assert.Contains(
            "unambiguous",
            root.DirectoryObjectIdentityUnavailableReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListenArrDbContext(options));
    }
}
