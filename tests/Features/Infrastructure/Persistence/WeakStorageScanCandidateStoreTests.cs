using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "WeakStorageScanCandidateStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class WeakStorageScanCandidateStoreTests : BaseTests
{
    [Fact]
    public async Task ConfirmAsync_CurrentMissingCandidate_RemovesOnlyTrackedRow()
    {
        var scenario = await CreateScenarioAsync();
        var store = CreateStore(scenario.Factory);
        var token = await store.ReplaceAsync(
            scenario.AudiobookId,
            [scenario.Candidate]);
        var pending = Assert.Single(await store.GetPendingAsync(scenario.AudiobookId));

        var result = await store.ConfirmAsync(
            scenario.AudiobookId,
            token,
            [pending.Id]);

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.PreservedCount);
        await using var verification = await scenario.Factory.CreateDbContextAsync();
        Assert.False(await verification.AudiobookFiles.AnyAsync(
            file => file.Id == scenario.FileId));
    }

    [Fact]
    public async Task ConfirmAsync_FileReappeared_PreservesTrackedRow()
    {
        var scenario = await CreateScenarioAsync();
        var store = CreateStore(scenario.Factory);
        var token = await store.ReplaceAsync(
            scenario.AudiobookId,
            [scenario.Candidate]);
        var pending = Assert.Single(await store.GetPendingAsync(scenario.AudiobookId));
        Directory.CreateDirectory(Path.GetDirectoryName(scenario.ResolvedPath)!);
        await File.WriteAllTextAsync(scenario.ResolvedPath, "audio");

        var result = await store.ConfirmAsync(
            scenario.AudiobookId,
            token,
            [pending.Id]);

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.PreservedCount);
        await using var verification = await scenario.Factory.CreateDbContextAsync();
        Assert.True(await verification.AudiobookFiles.AnyAsync(
            file => file.Id == scenario.FileId));
    }

    [Fact]
    public async Task ConfirmAsync_PathOccupiedByDirectory_PreservesTrackedRow()
    {
        var scenario = await CreateScenarioAsync();
        var store = CreateStore(scenario.Factory);
        var token = await store.ReplaceAsync(
            scenario.AudiobookId,
            [scenario.Candidate]);
        var pending = Assert.Single(await store.GetPendingAsync(scenario.AudiobookId));
        Directory.CreateDirectory(scenario.ResolvedPath);

        var result = await store.ConfirmAsync(
            scenario.AudiobookId,
            token,
            [pending.Id]);

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.PreservedCount);
        await using var verification = await scenario.Factory.CreateDbContextAsync();
        Assert.True(await verification.AudiobookFiles.AnyAsync(
            file => file.Id == scenario.FileId));
    }

    [Fact]
    public async Task ConfirmAsync_ParentDirectoryDisappeared_PreservesTrackedRow()
    {
        var scenario = await CreateScenarioAsync();
        var store = CreateStore(scenario.Factory);
        var token = await store.ReplaceAsync(
            scenario.AudiobookId,
            [scenario.Candidate]);
        var pending = Assert.Single(await store.GetPendingAsync(scenario.AudiobookId));
        Directory.Delete(Path.GetDirectoryName(scenario.ResolvedPath)!);

        var result = await store.ConfirmAsync(
            scenario.AudiobookId,
            token,
            [pending.Id]);

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.PreservedCount);
        await using var verification = await scenario.Factory.CreateDbContextAsync();
        Assert.True(await verification.AudiobookFiles.AnyAsync(
            file => file.Id == scenario.FileId));
    }

    [Fact]
    public async Task ConfirmAsync_BasePathChangedSinceScan_PreservesTrackedRow()
    {
        var scenario = await CreateScenarioAsync();
        var store = CreateStore(scenario.Factory);
        var token = await store.ReplaceAsync(
            scenario.AudiobookId,
            [scenario.Candidate]);
        var pending = Assert.Single(await store.GetPendingAsync(scenario.AudiobookId));
        var replacementBasePath = FileService.GetTempDirectory(
            "weak-scan-candidate-remapped");
        var replacementPath = Path.Join(replacementBasePath, "missing.m4b");
        await File.WriteAllTextAsync(replacementPath, "audio");
        await using (var db = await scenario.Factory.CreateDbContextAsync())
        {
            var audiobook = await db.Audiobooks.SingleAsync(
                book => book.Id == scenario.AudiobookId);
            audiobook.BasePath = replacementBasePath;
            await db.SaveChangesAsync();
        }

        var result = await store.ConfirmAsync(
            scenario.AudiobookId,
            token,
            [pending.Id]);

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.PreservedCount);
        await using var verification = await scenario.Factory.CreateDbContextAsync();
        Assert.True(await verification.AudiobookFiles.AnyAsync(
            file => file.Id == scenario.FileId));
    }

    [Fact]
    public async Task ConfirmAsync_RelationalCommitFailure_RollsBackTrackedRowDeletion()
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"weak-scan-confirmation-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        var root = FileService.GetTempDirectory("weak-scan-transaction");
        var resolvedPath = Path.Join(root, "missing.m4b");
        const string storedPath = "missing.m4b";
        const string physicalIdentity = "durable:transaction-file";

        try
        {
            int audiobookId;
            int fileId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var audiobook = new AudiobookBuilder()
                    .WithTitle("Transactional weak scan")
                    .WithBasePath(root)
                    .Build();
                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                var file = AudiobookFile.CreateUnresolved(storedPath);
                file.AudiobookId = audiobook.Id;
                file.ApplyPhysicalObjectIdentity(physicalIdentity, DateTime.UtcNow);
                db.AudiobookFiles.Add(file);
                await db.SaveChangesAsync();
                audiobookId = audiobook.Id;
                fileId = file.Id;
            }

            var store = new WeakStorageScanCandidateStore(factory, TimeProvider.System);
            var token = await store.ReplaceAsync(
                audiobookId,
                [new WeakStorageMissingFileCandidate(
                    fileId,
                    storedPath,
                    resolvedPath,
                    physicalIdentity)]);
            var pending = Assert.Single(await store.GetPendingAsync(audiobookId));
            store.BeforeConfirmationCommitForTest = () =>
                throw new InvalidOperationException("Injected commit failure.");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ConfirmAsync(audiobookId, token, [pending.Id]));

            await using var verification = await factory.CreateDbContextAsync();
            Assert.True(await verification.AudiobookFiles.AnyAsync(file => file.Id == fileId));
            Assert.True(await verification.WeakStorageScanCandidates.AnyAsync(candidate =>
                candidate.Id == pending.Id && candidate.ConfirmedAt == null));
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task ReplaceAsync_NewScanPrunesPreviouslyConfirmedCandidates()
    {
        var scenario = await CreateScenarioAsync();
        var store = CreateStore(scenario.Factory);
        var token = await store.ReplaceAsync(
            scenario.AudiobookId,
            [scenario.Candidate]);
        var pending = Assert.Single(await store.GetPendingAsync(scenario.AudiobookId));
        var confirmation = await store.ConfirmAsync(
            scenario.AudiobookId,
            token,
            [pending.Id]);
        Assert.Equal(1, confirmation.RemovedCount);

        await store.ReplaceAsync(scenario.AudiobookId, []);

        await using var verification = await scenario.Factory.CreateDbContextAsync();
        Assert.False(await verification.WeakStorageScanCandidates.AnyAsync(candidate =>
            candidate.AudiobookId == scenario.AudiobookId));
    }

    [Fact]
    public async Task ConfirmAsync_StaleToken_FailsWithoutRemovingRow()
    {
        var scenario = await CreateScenarioAsync();
        var store = CreateStore(scenario.Factory);
        await store.ReplaceAsync(scenario.AudiobookId, [scenario.Candidate]);
        var pending = Assert.Single(await store.GetPendingAsync(scenario.AudiobookId));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            store.ConfirmAsync(
                scenario.AudiobookId,
                Guid.NewGuid(),
                [pending.Id]));

        await using var verification = await scenario.Factory.CreateDbContextAsync();
        Assert.True(await verification.AudiobookFiles.AnyAsync(
            file => file.Id == scenario.FileId));
    }

    private static WeakStorageScanCandidateStore CreateStore(
        IDbContextFactory<ListenArrDbContext> factory) =>
        new(factory, TimeProvider.System);

    private async Task<Scenario> CreateScenarioAsync()
    {
        var root = FileService.GetTempDirectory("weak-scan-candidate");
        var resolvedPath = Path.Join(root, "missing.m4b");
        var storedPath = "missing.m4b";
        const string physicalIdentity = "durable:test-file";
        var audiobook = new AudiobookBuilder()
            .WithTitle("Weak scan candidate")
            .WithBasePath(root)
            .Build();
        var file = AudiobookFile.CreateUnresolved(storedPath);
        file.ApplyPhysicalObjectIdentity(physicalIdentity, DateTime.UtcNow);
        audiobook.Files = [file];
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
        }

        return new Scenario(
            factory,
            audiobook.Id,
            file.Id,
            resolvedPath,
            new WeakStorageMissingFileCandidate(
                file.Id,
                storedPath,
                resolvedPath,
                physicalIdentity));
    }

    private sealed record Scenario(
        IDbContextFactory<ListenArrDbContext> Factory,
        int AudiobookId,
        int FileId,
        string ResolvedPath,
        WeakStorageMissingFileCandidate Candidate);

    private sealed class TestDbContextFactory(
        DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
