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

    [Fact]
    public async Task ReconcileAsync_LegacyVersionTwoIdentityWithoutMarker_RemainsAuthorized()
    {
        var rootPath = FileService.GetTempDirectory("root-object-identity-markerless-v2");
        using var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(rootPath);
        var nativeIdentity = anchor.GetDirectoryObjectIdentity();
        var persistedIdentity = ManagedDirectoryIdentity.Create(
            Guid.NewGuid().ToString("N"),
            nativeIdentity);
        Assert.False(File.Exists(Path.Join(
            rootPath,
            ManagedDirectoryEnrollment.FileName)));

        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            setup.RootFolders.Add(new RootFolder
            {
                Id = 1,
                Name = "Library",
                Path = rootPath,
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = persistedIdentity
            });
            await setup.SaveChangesAsync();
        }

        var reconciler = new RootFolderObjectIdentityReconciler(
            new TestDbContextFactory(options),
            new DirectoryObjectIdentityResolver(),
            new FilesystemMutationCoordinator(),
            NullLogger<RootFolderObjectIdentityReconciler>.Instance);

        await reconciler.ReconcileAsync();

        await using var verification = new ListenArrDbContext(options);
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, root.DirectoryObjectIdentityVersion);
        Assert.Equal(persistedIdentity, root.DirectoryObjectIdentity);
        Assert.Null(root.DirectoryObjectIdentityUnavailableReason);
        Assert.False(File.Exists(Path.Join(
            rootPath,
            ManagedDirectoryEnrollment.FileName)));
    }

    [Fact]
    public async Task ReconcileAsync_MatchingLegacyEnrollmentMarker_RetiresMarkerAndKeepsDatabaseIdentity()
    {
        var rootPath = FileService.GetTempDirectory("root-object-identity-retire-marker");
        string nativeIdentity;
        using (var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(rootPath))
        {
            nativeIdentity = anchor.GetDirectoryObjectIdentity();
        }
        var token = Guid.NewGuid().ToString("N");
        var legacyIdentity = new DirectoryObjectIdentityResolution(
            ManagedDirectoryIdentity.CurrentVersion,
            ManagedDirectoryIdentity.Create(token, nativeIdentity),
            null);
        var markerPath = Path.Join(rootPath, ManagedDirectoryEnrollment.FileName);
        await File.WriteAllTextAsync(
            markerPath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 1,
                token,
                nativeIdentity,
                createdAtUtc = DateTimeOffset.UtcNow
            }));
        Assert.True(File.Exists(markerPath));

        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            setup.RootFolders.Add(new RootFolder
            {
                Id = 1,
                Name = "Library",
                Path = rootPath,
                DirectoryObjectIdentityVersion = legacyIdentity.Version,
                DirectoryObjectIdentity = legacyIdentity.Value
            });
            await setup.SaveChangesAsync();
        }

        var reconciler = new RootFolderObjectIdentityReconciler(
            new TestDbContextFactory(options),
            new DirectoryObjectIdentityResolver(),
            new FilesystemMutationCoordinator(),
            NullLogger<RootFolderObjectIdentityReconciler>.Instance);

        await reconciler.ReconcileAsync();

        await using var verification = new ListenArrDbContext(options);
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(legacyIdentity.Version, root.DirectoryObjectIdentityVersion);
        Assert.Equal(legacyIdentity.Value, root.DirectoryObjectIdentity);
        Assert.Null(root.DirectoryObjectIdentityUnavailableReason);
        Assert.False(File.Exists(markerPath));
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
