using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "MoveSourceCleanupPolicyResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class MoveSourceCleanupPolicyResolverTests : BaseTests
{
    [Theory]
    [InlineData(WeakStorageSourceCleanupPolicy.RetainSource)]
    [InlineData(WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy)]
    public async Task ResolveAsync_HealthyManagedRoots_AuthorizesRegardlessOfLegacyPolicy(
        WeakStorageSourceCleanupPolicy legacyPolicy)
    {
        var sourcePath = FileService.GetTempDirectory("move-policy-source");
        var targetPath = FileService.GetTempDirectory("move-policy-target");
        var sourceRoot = await AddAuthorizedRootAsync(
            sourcePath,
            "Source",
            FileSystemCaseSensitivityMode.Sensitive);
        var targetRoot = await AddAuthorizedRootAsync(
            targetPath,
            "Target",
            FileSystemCaseSensitivityMode.Sensitive);
        await SetLegacyPolicyAsync(legacyPolicy, sourceRoot.Id, targetRoot.Id);
        var resolver = _provider.GetRequiredService<IMoveSourceCleanupPolicyResolver>();

        var authorization = await resolver.ResolveAsync(
            sourcePath,
            Path.Join(targetPath, "Author", "Book"));

        Assert.True(authorization.DeletesSourceAfterVerifiedCopy);
        Assert.True(authorization.SourceIsManagedRoot);
        Assert.Equal(sourceRoot.Id, authorization.SourceRootFolderId);
        Assert.Equal(targetRoot.Id, authorization.TargetRootFolderId);
        Assert.True(await resolver.IsCurrentAsync(authorization));
    }

    [Fact]
    public async Task IsCurrentAsync_LegacyPolicyChanged_DoesNotRevokeCurrentCapability()
    {
        var sourcePath = FileService.GetTempDirectory("move-policy-drift-source");
        var targetPath = FileService.GetTempDirectory("move-policy-drift-target");
        var sourceRoot = await AddAuthorizedRootAsync(
            sourcePath,
            "Source",
            FileSystemCaseSensitivityMode.Sensitive);
        var targetRoot = await AddAuthorizedRootAsync(
            targetPath,
            "Target",
            FileSystemCaseSensitivityMode.Sensitive);
        var resolver = _provider.GetRequiredService<IMoveSourceCleanupPolicyResolver>();
        var authorization = await resolver.ResolveAsync(sourcePath, targetPath);

        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            var target = await db.RootFolders.SingleAsync(root => root.Id == targetRoot.Id);
            target.WeakStorageSourceCleanupPolicy =
                WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy;
            target.WeakStoragePolicyRevision++;
            await db.SaveChangesAsync();
        }

        Assert.True(await resolver.IsCurrentAsync(authorization));
    }

    [Fact]
    public async Task IsCurrentAsync_StorageContractRevisionChanged_FailsClosed()
    {
        var sourcePath = FileService.GetTempDirectory("move-storage-contract-drift-source");
        var targetPath = FileService.GetTempDirectory("move-storage-contract-drift-target");
        var sourceRoot = await AddAuthorizedRootAsync(
            sourcePath,
            "Source",
            FileSystemCaseSensitivityMode.Sensitive);
        var targetRoot = await AddAuthorizedRootAsync(
            targetPath,
            "Target",
            FileSystemCaseSensitivityMode.Sensitive);
        var resolver = _provider.GetRequiredService<IMoveSourceCleanupPolicyResolver>();
        var authorization = await resolver.ResolveAsync(sourcePath, targetPath);

        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            var target = await db.RootFolders.SingleAsync(root => root.Id == targetRoot.Id);
            target.StorageContractRevision++;
            await db.SaveChangesAsync();
        }

        Assert.False(await resolver.IsCurrentAsync(authorization));
    }

    [Fact]
    public async Task IsCurrentAsync_TargetStorageAuthorityLost_FailsClosed()
    {
        var sourcePath = FileService.GetTempDirectory("move-target-health-source");
        var targetPath = FileService.GetTempDirectory("move-target-health-target");
        var sourceRoot = await AddAuthorizedRootAsync(
            sourcePath,
            "Source",
            FileSystemCaseSensitivityMode.Sensitive);
        var targetRoot = await AddAuthorizedRootAsync(
            targetPath,
            "Target",
            FileSystemCaseSensitivityMode.Sensitive);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(resolver => resolver.ResolveAsync(
                It.Is<RootFolder>(root => root.Id == targetRoot.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RootFolderStorageObservation(
                RootFolderStorageState.Changed,
                RootFolderStorageReason.IdentityMismatch,
                "Target changed",
                CanConfirmCurrentFolder: true,
                CanChangePath: true,
                CanMutateFilesystem: false,
                ConfirmationToken: "refresh"));
        var resolver = new MoveSourceCleanupPolicyResolver(
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            health.Object);
        var authorization = new MoveSourceCleanupAuthorization(
            MoveSourceCleanupMode.DeleteAfterVerifiedCopy,
            sourceRoot.Id,
            SourcePolicyRevision: 1,
            targetRoot.Id,
            TargetPolicyRevision: 1,
            SourceIsManagedRoot: true,
            Message: string.Empty,
            SourceStorageContractRevision: sourceRoot.StorageContractRevision,
            TargetStorageContractRevision: targetRoot.StorageContractRevision);

        Assert.False(await resolver.IsCurrentAsync(authorization));
        health.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_SourceMayOverlapUnresolvedManagedRoot_ForcesRetention()
    {
        var sourcePath = FileService.GetTempDirectory("move-policy-unresolved-source");
        var targetPath = FileService.GetTempDirectory("move-policy-unresolved-target");
        var targetRoot = await AddAuthorizedRootAsync(
            targetPath,
            "Target",
            FileSystemCaseSensitivityMode.Sensitive);
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            db.RootFolders.Add(new RootFolder
            {
                Name = "Unresolved Source",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown,
                PathIdentityState = PathIdentityState.Unavailable,
                WeakStorageSourceCleanupPolicy =
                    WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy,
                WeakStoragePolicyRevision = 1
            });
            var persistedTarget = await db.RootFolders.SingleAsync(root => root.Id == targetRoot.Id);
            persistedTarget.WeakStorageSourceCleanupPolicy =
                WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy;
            persistedTarget.WeakStoragePolicyRevision = 1;
            await db.SaveChangesAsync();
        }
        var resolver = _provider.GetRequiredService<IMoveSourceCleanupPolicyResolver>();

        var authorization = await resolver.ResolveAsync(
            Path.Join(sourcePath, "Author", "Book"),
            Path.Join(targetPath, "Author", "Book"));

        Assert.False(authorization.DeletesSourceAfterVerifiedCopy);
        Assert.True(authorization.ForceCopyAndRetainSource);
        Assert.Null(authorization.SourceRootFolderId);
        Assert.Contains("not authoritative", authorization.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_ReadOnlyManagedSource_ForcesCopyAndRetention()
    {
        var sourcePath = FileService.GetTempDirectory("move-policy-readonly-source");
        var targetPath = FileService.GetTempDirectory("move-policy-readonly-target");
        var sourceRoot = await AddAuthorizedRootAsync(
            sourcePath,
            "Source",
            FileSystemCaseSensitivityMode.Sensitive);
        var targetRoot = await AddAuthorizedRootAsync(
            targetPath,
            "Target",
            FileSystemCaseSensitivityMode.Sensitive);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(resolver => resolver.ResolveAsync(
                It.Is<RootFolder>(root => root.Id == targetRoot.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthyStorage());
        health.Setup(resolver => resolver.ResolveAsync(
                It.Is<RootFolder>(root => root.Id == sourceRoot.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RootFolderStorageObservation(
                RootFolderStorageState.Limited,
                RootFolderStorageReason.ReadOnlyFilesystem,
                "Read-only",
                CanConfirmCurrentFolder: false,
                CanChangePath: true,
                CanMutateFilesystem: false,
                ConfirmationToken: null));
        var resolver = new MoveSourceCleanupPolicyResolver(
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            health.Object);

        var authorization = await resolver.ResolveAsync(sourcePath, targetPath);

        Assert.False(authorization.DeletesSourceAfterVerifiedCopy);
        Assert.True(authorization.ForceCopyAndRetainSource);
        Assert.Contains("retained", authorization.Message, StringComparison.OrdinalIgnoreCase);
        health.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_TargetStorageAuthorityLost_RetainsSource()
    {
        var sourcePath = FileService.GetTempDirectory("move-policy-target-lost-source");
        var targetPath = FileService.GetTempDirectory("move-policy-target-lost-target");
        var sourceRoot = await AddAuthorizedRootAsync(
            sourcePath,
            "Source",
            FileSystemCaseSensitivityMode.Sensitive);
        var targetRoot = await AddAuthorizedRootAsync(
            targetPath,
            "Target",
            FileSystemCaseSensitivityMode.Sensitive);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(resolver => resolver.ResolveAsync(
                It.Is<RootFolder>(root => root.Id == targetRoot.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RootFolderStorageObservation(
                RootFolderStorageState.Changed,
                RootFolderStorageReason.IdentityMismatch,
                "Target changed",
                CanConfirmCurrentFolder: true,
                CanChangePath: true,
                CanMutateFilesystem: false,
                ConfirmationToken: "refresh"));
        var resolver = new MoveSourceCleanupPolicyResolver(
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            health.Object);

        var authorization = await resolver.ResolveAsync(sourcePath, targetPath);

        Assert.False(authorization.DeletesSourceAfterVerifiedCopy);
        Assert.Contains("destination storage", authorization.Message, StringComparison.OrdinalIgnoreCase);
        health.VerifyAll();
    }

    [DirectoryLinkTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_LinkedConfiguredBoundary_AllowsBoundaryButRejectsLinkedDescendant(bool linkedDescendant)
    {
        var root = FileService.GetTempDirectory("move-policy-linked-boundary");
        var physical = Directory.CreateDirectory(Path.Join(root, "physical")).FullName;
        var linked = Path.Join(root, "linked");
        Directory.CreateSymbolicLink(linked, physical);
        var source = Directory.CreateDirectory(Path.Join(linked, "Source")).FullName;
        var target = Path.Join(linked, "Destination", "Book");
        var foreign = Directory.CreateDirectory(Path.Join(root, "foreign")).FullName;
        await File.WriteAllTextAsync(Path.Join(source, "source.m4b"), "source");
        await File.WriteAllTextAsync(Path.Join(foreign, "foreign.m4b"), "foreign");
        var configured = await AddAuthorizedRootAsync(linked, "Linked Policy Root", FileSystemCaseSensitivityMode.Sensitive);
        if (linkedDescendant) Directory.CreateSymbolicLink(Path.Join(linked, "Destination"), foreign);
        var resolver = _provider.GetRequiredService<IMoveSourceCleanupPolicyResolver>();

        if (linkedDescendant)
        {
            var error = await Record.ExceptionAsync(() => resolver.ResolveAsync(source, target));
            Assert.NotNull(error);
            Assert.True(error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException,
                error.ToString());
        }
        else
        {
            var authorization = await resolver.ResolveAsync(source, target);
            Assert.Equal(configured.Id, authorization.SourceRootFolderId);
            Assert.Equal(configured.Id, authorization.TargetRootFolderId);
            Assert.True(authorization.DeletesSourceAfterVerifiedCopy || authorization.ForceCopyAndRetainSource);
        }
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Join(source, "source.m4b")));
        Assert.Equal("foreign", await File.ReadAllTextAsync(Path.Join(foreign, "foreign.m4b")));
        Assert.False(Directory.Exists(Path.Join(foreign, "Book")));
    }
    private static RootFolderStorageObservation HealthyStorage() =>
        new(
            RootFolderStorageState.Healthy,
            RootFolderStorageReason.None,
            Message: null,
            CanConfirmCurrentFolder: false,
            CanChangePath: true,
            CanMutateFilesystem: true,
            ConfirmationToken: null);

    private async Task SetLegacyPolicyAsync(
        WeakStorageSourceCleanupPolicy policy, params int[] rootFolderIds)
    {
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var roots = await db.RootFolders
            .Where(root => rootFolderIds.Contains(root.Id))
            .ToListAsync();
        foreach (var root in roots)
        {
            root.WeakStorageSourceCleanupPolicy = policy;
            root.WeakStoragePolicyRevision = 1;
        }
        await db.SaveChangesAsync();
    }
}
