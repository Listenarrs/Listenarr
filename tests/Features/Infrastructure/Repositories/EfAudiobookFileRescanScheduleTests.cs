using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Name", "EfAudiobookFileRescanScheduleTests")]
[Trait("Category", "Persistence")]
public sealed class EfAudiobookFileRescanScheduleTests : BaseTests
{
    private const string SqliteProvider = "Sqlite";
    private const string InMemoryProvider = "InMemory";

    private static readonly RescanSchedule WrittenSchedule = new(
        new DateTime(2026, 12, 1, 10, 15, 30, DateTimeKind.Utc),
        FailureCount: 3,
        UnavailableCount: 2,
        BlockedCount: 1,
        LastOutcome: "ExtractionFailed");

    public static TheoryData<string> Providers => new() { SqliteProvider, InMemoryProvider };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpdateAsync_StaleDetachedCopy_KeepsRescanSchedule(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync();
        AudiobookFile staleCopy;
        await using (var readContext = store.CreateContext())
        {
            staleCopy = await readContext.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Id == fileId);
        }

        await store.WriteScheduleAsync(fileId, WrittenSchedule);
        staleCopy.DurationSeconds = 456;
        await using (var updateContext = store.CreateContext())
        {
            await new EfAudiobookFileRepository(updateContext).UpdateAsync(staleCopy);
        }

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal(456, persisted.DurationSeconds);
        Assert.Equal(WrittenSchedule, RescanSchedule.Of(persisted));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpdateAsync_StaleTrackedEntity_KeepsRescanSchedule(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync();
        await using var updateContext = store.CreateContext();
        var repository = new EfAudiobookFileRepository(updateContext);
        var trackedFile = Assert.IsType<AudiobookFile>(await repository.GetByIdAsync(fileId));

        await store.WriteScheduleAsync(fileId, WrittenSchedule);
        trackedFile.DurationSeconds = 321;
        await repository.UpdateAsync(trackedFile);

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal(321, persisted.DurationSeconds);
        Assert.Equal(WrittenSchedule, RescanSchedule.Of(persisted));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpdateAsync_TrackedEntityScheduleEdits_AreNotSaved(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync();
        await store.WriteScheduleAsync(fileId, WrittenSchedule);
        await using var updateContext = store.CreateContext();
        var repository = new EfAudiobookFileRepository(updateContext);
        var trackedFile = Assert.IsType<AudiobookFile>(await repository.GetByIdAsync(fileId));

        trackedFile.DurationSeconds = 654;
        trackedFile.MetadataRescanDueUtc = DateTime.UnixEpoch;
        trackedFile.MetadataRescanFailureCount = 0;
        trackedFile.MetadataRescanUnavailableCount = 0;
        trackedFile.MetadataRescanBlockedCount = 0;
        trackedFile.MetadataRescanLastOutcome = null;
        await repository.UpdateAsync(trackedFile);

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal(654, persisted.DurationSeconds);
        Assert.Equal(WrittenSchedule, RescanSchedule.Of(persisted));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TriggerRescanFlow_NoTrackingCandidateThenUpdate_KeepsRescanSchedule(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync();
        await using var adminContext = store.CreateContext();
        var repository = new EfAudiobookFileRepository(adminContext);
        var candidate = Assert.Single(await repository.GetMissingMetadataAsync(50));
        Assert.Equal(fileId, candidate.Id);

        await store.WriteScheduleAsync(fileId, WrittenSchedule);
        candidate.Size = 4096;
        candidate.DurationSeconds = 789;
        candidate.Format = "m4b";
        candidate.SampleRate = 44100;
        await repository.UpdateAsync(candidate);

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal(789, persisted.DurationSeconds);
        Assert.Equal("m4b", persisted.Format);
        Assert.Equal(WrittenSchedule, RescanSchedule.Of(persisted));
    }

    private sealed record RescanSchedule(
        DateTime DueUtc,
        int FailureCount,
        int UnavailableCount,
        int BlockedCount,
        string? LastOutcome)
    {
        public static RescanSchedule Of(AudiobookFile file) => new(
            file.MetadataRescanDueUtc,
            file.MetadataRescanFailureCount,
            file.MetadataRescanUnavailableCount,
            file.MetadataRescanBlockedCount,
            file.MetadataRescanLastOutcome);
    }

    private sealed class ScheduleStore : IAsyncDisposable
    {
        private readonly DbContextOptions<ListenArrDbContext> _options;
        private readonly string? _databasePath;

        private ScheduleStore(DbContextOptions<ListenArrDbContext> options, string? databasePath)
        {
            _options = options;
            _databasePath = databasePath;
        }

        public static async Task<ScheduleStore> CreateAsync(string provider)
        {
            if (provider == InMemoryProvider)
            {
                return new ScheduleStore(
                    new DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
                    null);
            }

            var databasePath = Path.Combine(Path.GetTempPath(), $"listenarr-rescan-schedule-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
            var store = new ScheduleStore(
                new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connectionString).Options,
                databasePath);
            await using var setupContext = store.CreateContext();
            await setupContext.Database.EnsureCreatedAsync();
            return store;
        }

        public ListenArrDbContext CreateContext() => new(_options);

        public async Task<int> SeedFileAsync()
        {
            await using var context = CreateContext();
            var file = new AudiobookFile
            {
                Audiobook = new Audiobook { Title = "A Princess of Mars", BasePath = "/library/source" },
                Path = "/library/source/book.m4b"
            };
            context.AudiobookFiles.Add(file);
            await context.SaveChangesAsync();
            return file.Id;
        }

        // Stands in for the schedule writes: a set-based UPDATE on a relational
        // store, and a tracked load-and-save where ExecuteUpdate is unavailable.
        public async Task WriteScheduleAsync(int fileId, RescanSchedule schedule)
        {
            await using var context = CreateContext();
            if (context.Database.IsRelational())
            {
                var updated = await context.AudiobookFiles
                    .Where(file => file.Id == fileId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(file => file.MetadataRescanDueUtc, schedule.DueUtc)
                        .SetProperty(file => file.MetadataRescanFailureCount, schedule.FailureCount)
                        .SetProperty(file => file.MetadataRescanUnavailableCount, schedule.UnavailableCount)
                        .SetProperty(file => file.MetadataRescanBlockedCount, schedule.BlockedCount)
                        .SetProperty(file => file.MetadataRescanLastOutcome, schedule.LastOutcome));
                Assert.Equal(1, updated);
            }
            else
            {
                var file = await context.AudiobookFiles.SingleAsync(candidate => candidate.Id == fileId);
                file.MetadataRescanDueUtc = schedule.DueUtc;
                file.MetadataRescanFailureCount = schedule.FailureCount;
                file.MetadataRescanUnavailableCount = schedule.UnavailableCount;
                file.MetadataRescanBlockedCount = schedule.BlockedCount;
                file.MetadataRescanLastOutcome = schedule.LastOutcome;
                await context.SaveChangesAsync();
            }

            Assert.Equal(schedule, RescanSchedule.Of(await ReadFileAsync(fileId)));
        }

        public async Task<AudiobookFile> ReadFileAsync(int fileId)
        {
            await using var context = CreateContext();
            return await context.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Id == fileId);
        }

        public ValueTask DisposeAsync()
        {
            if (_databasePath != null)
            {
                SqliteConnection.ClearAllPools();
                File.Delete(_databasePath);
            }

            return ValueTask.CompletedTask;
        }
    }
}
