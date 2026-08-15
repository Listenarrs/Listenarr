using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Name", "EfAudiobookFileRepositoryTests")]
[Trait("Category", "Persistence")]
public sealed class EfAudiobookFileRepositoryTests : BaseTests
{
    [Fact]
    public async Task ClaimAsync_TwoAudiobooksClaimSameIdentityConcurrently_ExactlyOneWins()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"listenarr-file-ownership-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, DefaultTimeout = 30 }.ToString();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connectionString).Options;
        try
        {
            int firstAudiobookId;
            int secondAudiobookId;
            await using (var setupContext = new ListenArrDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync();
                var firstAudiobook = new Audiobook { Title = "First", BasePath = "C:\\library\\first" };
                var secondAudiobook = new Audiobook { Title = "Second", BasePath = "C:\\library\\second" };
                setupContext.Audiobooks.AddRange(firstAudiobook, secondAudiobook);
                await setupContext.SaveChangesAsync();
                firstAudiobookId = firstAudiobook.Id;
                secondAudiobookId = secondAudiobook.Id;
            }

            var saveBarrier = new SaveBarrier(2);
            await using var firstContext = new BarrierListenArrDbContext(options, saveBarrier);
            await using var secondContext = new BarrierListenArrDbContext(options, saveBarrier);
            var firstRepository = new EfAudiobookFileRepository(firstContext);
            var secondRepository = new EfAudiobookFileRepository(secondContext);
            var semantics = new FileSystemPathSemantics(FileSystemPathSyntax.Windows, FileSystemCaseSensitivity.Insensitive);
            var firstIdentity = AudiobookFilePathIdentity.CreateValid("C:\\library\\shared\\book.m4b", semantics, FileSystemCaseSensitivityMode.Insensitive, "C:\\library");
            var secondIdentity = AudiobookFilePathIdentity.CreateValid("c:/LIBRARY/shared/BOOK.m4b", semantics, FileSystemCaseSensitivityMode.Insensitive, "C:\\library");
            var firstFile = AudiobookFile.CreateUnresolved("C:\\library\\shared\\book.m4b");
            firstFile.AudiobookId = firstAudiobookId;
            firstFile.ApplyPathIdentity(firstFile.Path!, firstIdentity);
            var secondFile = AudiobookFile.CreateUnresolved("c:/LIBRARY/shared/BOOK.m4b");
            secondFile.AudiobookId = secondAudiobookId;
            secondFile.ApplyPathIdentity(secondFile.Path!, secondIdentity);

            var outcomes = await Task.WhenAll(firstRepository.ClaimAsync(firstFile), secondRepository.ClaimAsync(secondFile));
            Assert.Single(outcomes, outcome => outcome.Outcome == AudiobookFileClaimOutcome.Created);
            Assert.Single(outcomes, outcome => outcome.Outcome == AudiobookFileClaimOutcome.OwnedByOtherAudiobook);
            await using var verification = new ListenArrDbContext(options);
            Assert.Single(await verification.AudiobookFiles.Where(file => file.PathOwnershipKey == firstIdentity.OwnershipKey).ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CheckOwnershipAsync_CaseSensitiveConflict_DoesNotBlockDistinctCaseVariant()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var semantics = new FileSystemPathSemantics(FileSystemPathSyntax.Unix, FileSystemCaseSensitivity.Sensitive);
        var conflictedIdentity = AudiobookFilePathIdentity.CreateValid("/library/Book.m4b", semantics, FileSystemCaseSensitivityMode.Sensitive, "/library") with { OwnershipKey = null, State = PathIdentityState.Conflict, Reason = "Legacy duplicate." };
        await using var context = new ListenArrDbContext(options);
        var audiobook = new Audiobook { Title = "Conflict", BasePath = "/library" };
        var file = AudiobookFile.CreateUnresolved("/library/Book.m4b");
        file.Audiobook = audiobook;
        file.ApplyPathIdentity(file.Path!, conflictedIdentity);
        context.AudiobookFiles.Add(file);
        await context.SaveChangesAsync();
        var repository = new EfAudiobookFileRepository(context);
        var distinct = await repository.CheckOwnershipAsync(audiobook.Id, null, AudiobookFilePathIdentity.CreateValid("/library/book.m4b", semantics, FileSystemCaseSensitivityMode.Sensitive, "/library"));
        var exact = await repository.CheckOwnershipAsync(audiobook.Id, null, AudiobookFilePathIdentity.CreateValid("/library/Book.m4b", semantics, FileSystemCaseSensitivityMode.Sensitive, "/library"));
        Assert.Equal(AudiobookFileOwnershipCheckOutcome.Available, distinct.Outcome);
        Assert.Equal(AudiobookFileOwnershipCheckOutcome.IdentityConflict, exact.Outcome);
    }

    [Fact]
    public async Task GetMissingMetadataAsync_ForeignLegacyRowsBeyondFirstPage_DoNotStarveHostRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        var foreignPrefix = OperatingSystem.IsWindows()
            ? "/foreign/library"
            : "C:\\foreign\\library";
        var hostPath = OperatingSystem.IsWindows()
            ? "C:\\library\\host.m4b"
            : "/library/host.m4b";

        await using var context = new ListenArrDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook { Title = "Copied database metadata" };
        context.Audiobooks.Add(audiobook);
        for (var index = 0; index < 1025; index++)
        {
            var separator = OperatingSystem.IsWindows() ? "/" : "\\";
            var file = AudiobookFile.CreateUnresolved(
                $"{foreignPrefix}{separator}foreign-{index:D4}.m4b");
            file.Audiobook = audiobook;
            context.AudiobookFiles.Add(file);
        }

        var hostFile = AudiobookFile.CreateUnresolved(hostPath);
        hostFile.Audiobook = audiobook;
        context.AudiobookFiles.Add(hostFile);
        await context.SaveChangesAsync();

        var repository = new EfAudiobookFileRepository(context);
        var candidates = await repository.GetMissingMetadataAsync(1);

        var candidate = Assert.Single(candidates);
        Assert.Equal(hostFile.Id, candidate.Id);
        Assert.Equal(hostPath, candidate.Path);
    }

    [Fact]
    public async Task GetMissingMetadataAsync_RelativeRowsUnderForeignBase_DoNotStarveHostRows()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var foreignBase = OperatingSystem.IsWindows()
            ? "/foreign/library"
            : "C:\\foreign\\library";
        var hostPath = OperatingSystem.IsWindows()
            ? "C:\\library\\host-relative-test.m4b"
            : "/library/host-relative-test.m4b";

        await using var context = new ListenArrDbContext(options);
        var foreignAudiobook = new Audiobook
        {
            Title = "Copied database relative metadata",
            BasePath = foreignBase
        };
        context.Audiobooks.Add(foreignAudiobook);
        for (var index = 0; index < 1025; index++)
        {
            var file = AudiobookFile.CreateUnresolved($"foreign-{index:D4}.m4b");
            file.Audiobook = foreignAudiobook;
            context.AudiobookFiles.Add(file);
        }

        var hostAudiobook = new Audiobook { Title = "Current host metadata" };
        var hostFile = AudiobookFile.CreateUnresolved(hostPath);
        hostFile.Audiobook = hostAudiobook;
        context.AudiobookFiles.Add(hostFile);
        await context.SaveChangesAsync();

        var repository = new EfAudiobookFileRepository(context);
        var candidates = await repository.GetMissingMetadataAsync(1);

        var candidate = Assert.Single(candidates);
        Assert.Equal(hostFile.Id, candidate.Id);
        Assert.Equal(hostPath, candidate.Path);
    }

    [Fact]
    public async Task ReplacePhysicalGenerationAsync_RelationalUpdate_SynchronizesTrackedEntity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new ListenArrDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var observedAt = DateTime.UtcNow.AddMinutes(-1);
        var file = AudiobookFile.CreateUnresolved("/library/book.m4b");
        file.Audiobook = new Audiobook
        {
            Title = "Physical Generation",
            BasePath = "/library"
        };
        file.DurationSeconds = 10;
        file.ApplyPhysicalObjectIdentity("old-generation", observedAt);
        context.AudiobookFiles.Add(file);
        await context.SaveChangesAsync();

        var repository = new EfAudiobookFileRepository(context);
        var tracked = Assert.IsType<AudiobookFile>(
            await repository.GetByIdAsync(file.Id));
        var replacementObservedAt = DateTime.UtcNow;
        var replacement = AudiobookFile.CreateUnresolved(tracked.Path);
        replacement.AudiobookId = tracked.AudiobookId;
        replacement.Size = 1234;
        replacement.DurationSeconds = 25;
        replacement.Format = "M4B";
        replacement.ApplyPhysicalObjectIdentity(
            "new-generation",
            replacementObservedAt);

        var updated = await repository.ReplacePhysicalGenerationAsync(
            tracked.Id,
            tracked.AudiobookId,
            tracked.Path,
            "old-generation",
            replacement);

        Assert.True(updated);
        Assert.Equal(1234, tracked.Size);
        Assert.Equal(25, tracked.DurationSeconds);
        Assert.Equal("M4B", tracked.Format);
        Assert.Equal("new-generation", tracked.PhysicalObjectIdentity);
        Assert.Equal(
            replacementObservedAt,
            tracked.PhysicalIdentityObservedAtUtc);
        var trackedEntry = context.Entry(tracked);
        Assert.False(trackedEntry.Property(candidate => candidate.Size).IsModified);
        Assert.False(trackedEntry.Property(candidate => candidate.PhysicalObjectIdentity).IsModified);
        var persisted = await context.AudiobookFiles
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == tracked.Id);
        Assert.Equal("new-generation", persisted.PhysicalObjectIdentity);
    }

    [Fact]
    public async Task DeletePhysicalGenerationAsync_NullLegacyIdentity_DeletesOnlyExpectedRow()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var fileId = await SeedFileAsync(options);
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);
        var file = await context.AudiobookFiles.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == fileId);

        var deleted = await repository.DeletePhysicalGenerationAsync(
            file.Id,
            file.AudiobookId,
            file.Path,
            expectedPhysicalObjectIdentity: null);

        Assert.True(deleted);
        Assert.Empty(await context.AudiobookFiles.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DeletePhysicalGenerationAsync_WrongIdentity_PreservesNewerRow()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        int fileId;
        int audiobookId;
        await using (var setup = new ListenArrDbContext(options))
        {
            var file = AudiobookFile.CreateUnresolved(
                "/library/source/book.m4b");
            file.Audiobook = new Audiobook
            {
                Title = "Physical Generation",
                BasePath = "/library/source"
            };
            file.ApplyPhysicalObjectIdentity(
                "current-generation",
                DateTime.UtcNow);
            setup.AudiobookFiles.Add(file);
            await setup.SaveChangesAsync();
            fileId = file.Id;
            audiobookId = file.AudiobookId;
        }

        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);
        var deleted = await repository.DeletePhysicalGenerationAsync(
            fileId,
            audiobookId,
            "/library/source/book.m4b",
            "stale-generation");

        Assert.False(deleted);
        var persisted = await context.AudiobookFiles.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == fileId);
        Assert.Equal("current-generation", persisted.PhysicalObjectIdentity);
    }

    [Fact]
    public async Task UpdateAsync_TrackedMetadataChange_DoesNotOverwriteNewerPath()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var fileId = await SeedFileAsync(options);
        await using var metadataContext = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(metadataContext);
        var staleFile = await repository.GetByIdAsync(fileId);
        await MoveFileReferenceAsync(options, fileId);
        staleFile!.DurationSeconds = 123;
        await repository.UpdateAsync(staleFile);
        await using var verification = new ListenArrDbContext(options);
        var persisted = await verification.AudiobookFiles.SingleAsync(file => file.Id == fileId);
        Assert.Equal("/library/target/book.m4b", persisted.Path);
        Assert.Equal(123, persisted.DurationSeconds);
    }

    [Fact]
    public async Task UpdateAsync_AttachedEntityCannotMutatePathOrAudiobookOwnership()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var fileId = await SeedFileAsync(options);
        await using var metadataContext = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(metadataContext);
        var trackedFile = Assert.IsType<AudiobookFile>(
            await repository.GetByIdAsync(fileId));
        var originalAudiobookId = trackedFile.AudiobookId;
        trackedFile.Path = "/library/foreign/book.m4b";
        trackedFile.AudiobookId = originalAudiobookId + 1000;
        trackedFile.DurationSeconds = 321;

        await repository.UpdateAsync(trackedFile);

        await using var verification = new ListenArrDbContext(options);
        var persisted = await verification.AudiobookFiles.SingleAsync(
            file => file.Id == fileId);
        Assert.Equal("/library/source/book.m4b", persisted.Path);
        Assert.Equal(originalAudiobookId, persisted.AudiobookId);
        Assert.Equal(321, persisted.DurationSeconds);
    }

    [Fact]
    public async Task UpdateAsync_DetachedMetadataChange_DoesNotOverwriteNewerPath()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var fileId = await SeedFileAsync(options);
        AudiobookFile staleFile;
        await using (var staleContext = new ListenArrDbContext(options)) staleFile = await staleContext.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Id == fileId);
        await MoveFileReferenceAsync(options, fileId);
        staleFile.DurationSeconds = 456;
        await using (var metadataContext = new ListenArrDbContext(options)) await new EfAudiobookFileRepository(metadataContext).UpdateAsync(staleFile);
        await using var verification = new ListenArrDbContext(options);
        var persisted = await verification.AudiobookFiles.SingleAsync(file => file.Id == fileId);
        Assert.Equal("/library/target/book.m4b", persisted.Path);
        Assert.Equal(456, persisted.DurationSeconds);
    }

    private sealed class BarrierListenArrDbContext(DbContextOptions<ListenArrDbContext> options, SaveBarrier saveBarrier) : ListenArrDbContext(options)
    {
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ChangeTracker.Entries<AudiobookFile>().Any(entry => entry.State == EntityState.Added)) await saveBarrier.SignalAndWaitAsync(cancellationToken);
            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class SaveBarrier(int participantCount)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == participantCount) _release.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private static async Task<int> SeedFileAsync(DbContextOptions<ListenArrDbContext> options)
    {
        await using var context = new ListenArrDbContext(options);
        var file = new AudiobookFile { Audiobook = new Audiobook { Title = "Repository File", BasePath = "/library/source" }, Path = "/library/source/book.m4b" };
        context.AudiobookFiles.Add(file);
        await context.SaveChangesAsync();
        return file.Id;
    }

    private static async Task MoveFileReferenceAsync(DbContextOptions<ListenArrDbContext> options, int fileId)
    {
        await using var context = new ListenArrDbContext(options);
        (await context.AudiobookFiles.SingleAsync(candidate => candidate.Id == fileId)).Path = "/library/target/book.m4b";
        await context.SaveChangesAsync();
    }
}
