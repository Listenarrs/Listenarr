using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "RootFolderWeakStoragePolicyServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class RootFolderWeakStoragePolicyServiceTests : BaseTests
{
    [Theory]
    [InlineData(WeakStorageSourceCleanupPolicy.RetainSource, WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy)]
    [InlineData(WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy, WeakStorageSourceCleanupPolicy.RetainSource)]
    public async Task UpdateAsync_PreservesLegacyValueAndExplainsCompatibilityInHistory(
        WeakStorageSourceCleanupPolicy previousPolicy, WeakStorageSourceCleanupPolicy policy)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var rootId = await AddRootAsync(factory, previousPolicy);
        var service = CreateService(factory);

        var updated = await service.UpdateAsync(
            rootId,
            new RootFolderWeakStoragePolicyUpdate(
                policy,
                ExpectedRevision: 0));

        Assert.Equal(
            policy,
            updated.WeakStorageSourceCleanupPolicy);
        Assert.Equal(1, updated.WeakStoragePolicyRevision);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Contains(
            verification.History,
            history => history.EventType == "Root Folder Policy Changed"
                && history.Message == "Legacy weak-storage setting updated for compatibility. "
                    + "Source deletion remains governed by current storage capabilities and verified operation evidence.");
        var persisted = await verification.RootFolders.SingleAsync(root => root.Id == rootId);
        Assert.Equal(policy, persisted.WeakStorageSourceCleanupPolicy);
        Assert.Equal(1, persisted.WeakStoragePolicyRevision);
        Assert.Equal(0, persisted.StorageContractRevision);
    }

    [Fact]
    public async Task UpdateAsync_StaleRevision_DoesNotChangePolicy()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var rootId = await AddRootAsync(factory);
        var service = CreateService(factory);
        await service.UpdateAsync(
            rootId,
            new RootFolderWeakStoragePolicyUpdate(
                WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy,
                ExpectedRevision: 0));

        await Assert.ThrowsAsync<RootFolderWeakStoragePolicyConflictException>(() =>
            service.UpdateAsync(
                rootId,
                new RootFolderWeakStoragePolicyUpdate(
                    WeakStorageSourceCleanupPolicy.RetainSource,
                    ExpectedRevision: 0)));

        await using var verification = await factory.CreateDbContextAsync();
        var root = await verification.RootFolders.SingleAsync(item => item.Id == rootId);
        Assert.Equal(
            WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy,
            root.WeakStorageSourceCleanupPolicy);
        Assert.Equal(1, root.WeakStoragePolicyRevision);
    }

    private RootFolderWeakStoragePolicyService CreateService(
        IDbContextFactory<ListenArrDbContext> factory) =>
        new(
            factory,
            _provider.GetRequiredService<IFilesystemMutationCoordinator>(),
            TimeProvider.System);

    private static async Task<int> AddRootAsync(
        IDbContextFactory<ListenArrDbContext> factory,
        WeakStorageSourceCleanupPolicy policy = WeakStorageSourceCleanupPolicy.RetainSource)
    {
        await using var db = await factory.CreateDbContextAsync();
        var root = new RootFolder
        {
            Name = "Weak storage",
            WeakStorageSourceCleanupPolicy = policy,
            Path = Path.GetFullPath(Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
        };
        db.RootFolders.Add(root);
        await db.SaveChangesAsync();
        return root.Id;
    }
}
