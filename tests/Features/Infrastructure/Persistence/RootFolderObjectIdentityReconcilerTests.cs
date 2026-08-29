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
    public async Task ReconcileAsync_UnconfirmedRoot_DoesNotAuthorizeVisibleDirectory()
    {
        var rootPath = Path.GetFullPath("startup-unconfirmed-root");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            setup.RootFolders.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = rootPath
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
            "not been confirmed",
            root.DirectoryObjectIdentityUnavailableReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAsync_AuthorizedRootMissing_PreservesAuthorizedGeneration()
    {
        var rootPath = Path.GetFullPath("startup-missing-root");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            setup.RootFolders.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = rootPath,
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = "authorized"
            });
            await setup.SaveChangesAsync();
        }

        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                rootPath,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Directory not found.",
                DirectoryObjectIdentityFailureKind.Missing));
        var reconciler = new RootFolderObjectIdentityReconciler(
            new TestDbContextFactory(options),
            identityResolver.Object,
            new FilesystemMutationCoordinator(),
            NullLogger<RootFolderObjectIdentityReconciler>.Instance);

        await reconciler.ReconcileAsync();

        identityResolver.VerifyAll();
        await using var verification = new ListenArrDbContext(options);
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, root.DirectoryObjectIdentityVersion);
        Assert.Equal("authorized", root.DirectoryObjectIdentity);
        Assert.Contains(
            "not found",
            root.DirectoryObjectIdentityUnavailableReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAsync_AuthorizedRootMatches_ClearsObservedFailureWithoutReplacingAuthority()
    {
        var rootPath = FileService.GetTempDirectory("startup-healthy-root");
        var observed = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(rootPath);
        Assert.True(observed.IsAvailable, observed.UnavailableReason);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            setup.RootFolders.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = rootPath,
                DirectoryObjectIdentityVersion = observed.Version,
                DirectoryObjectIdentity = observed.Value,
                DirectoryObjectIdentityUnavailableReason = "previously missing"
            });
            await setup.SaveChangesAsync();
        }

        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                rootPath,
                observed.Version!.Value,
                observed.Value!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(observed);
        var reconciler = new RootFolderObjectIdentityReconciler(
            new TestDbContextFactory(options),
            identityResolver.Object,
            new FilesystemMutationCoordinator(),
            NullLogger<RootFolderObjectIdentityReconciler>.Instance);

        await reconciler.ReconcileAsync();

        identityResolver.VerifyAll();
        await using var verification = new ListenArrDbContext(options);
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(observed.Value, root.DirectoryObjectIdentity);
        Assert.Null(root.DirectoryObjectIdentityUnavailableReason);
    }

    [LinuxFact]
    public async Task ReconcileAsync_RootReplacedAfterAuthoritySave_DoesNotRestoreMutationAuthority()
    {
        var rootPath = FileService.GetTempDirectory(
            "root-object-identity-authority-race");
        var displacedRoot = rootPath + ".displaced";
        var databasePath = Path.Join(
            FileService.GetTempPath(),
            $"root-object-identity-authority-race-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        var identityResolver = new DirectoryObjectIdentityResolver();
        var identity = await identityResolver.ResolveAsync(rootPath);
        Assert.True(identity.IsAvailable, identity.UnavailableReason);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.RootFolders.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = rootPath,
                DirectoryObjectIdentityVersion = identity.Version,
                DirectoryObjectIdentity = identity.Value,
                DirectoryObjectIdentityUnavailableReason = "previously unavailable"
            });
            await setup.SaveChangesAsync();
        }

        var hookRan = false;
        var reconciler = new RootFolderObjectIdentityReconciler(
            factory,
            identityResolver,
            new FilesystemMutationCoordinator(),
            NullLogger<RootFolderObjectIdentityReconciler>.Instance)
        {
            AfterRootAuthoritySavedForTest = _ =>
            {
                hookRan = true;
                Directory.Move(rootPath, displacedRoot);
                Directory.CreateDirectory(rootPath);
            }
        };

        try
        {
            await reconciler.ReconcileAsync();

            Assert.True(hookRan);
            Assert.True(Directory.Exists(displacedRoot));
            Assert.True(Directory.Exists(rootPath));
            await using var verification = await factory.CreateDbContextAsync();
            var root = await verification.RootFolders.SingleAsync();
            Assert.Equal(identity.Version, root.DirectoryObjectIdentityVersion);
            Assert.Equal(identity.Value, root.DirectoryObjectIdentity);
            Assert.False(string.IsNullOrWhiteSpace(
                root.DirectoryObjectIdentityUnavailableReason));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
            if (Directory.Exists(displacedRoot))
            {
                Directory.Delete(displacedRoot, recursive: true);
            }
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
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
