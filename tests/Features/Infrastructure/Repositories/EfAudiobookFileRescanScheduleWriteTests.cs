using System.Data.Common;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Name", "EfAudiobookFileRescanScheduleWriteTests")]
[Trait("Category", "Persistence")]
public sealed class EfAudiobookFileRescanScheduleWriteTests : BaseTests
{
    private const string SqliteProvider = "Sqlite";
    private const string InMemoryProvider = "InMemory";
    private const FileSystemPathSyntax HostSyntax = FileSystemPathSyntax.Unix;
    private const FileSystemPathSyntax ForeignSyntax = FileSystemPathSyntax.Windows;

    private static readonly DateTime NowUtc = new(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly RescanSchedule SeededSchedule = new(
        NowUtc.AddHours(-2),
        new AudiobookFileRescanCounts(2, 0, 1),
        nameof(MetadataRescanOutcome.GuardRejected));

    public static TheoryData<string> Providers => new() { SqliteProvider, InMemoryProvider };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ApplyMetadataRescanScheduleAsync_CountsUnchanged_WritesTheOutcome(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(SeededSchedule);
        var write = PolicyWrite(MetadataRescanOutcome.FileMissing, SeededSchedule.Counts, NowUtc);

        await using (var context = store.CreateContext())
        {
            Assert.True(await new EfAudiobookFileRepository(context)
                .ApplyMetadataRescanScheduleAsync(fileId, SeededSchedule.Counts, write));
        }

        Assert.Equal(new AudiobookFileRescanCounts(2, 1, 0), write.Counts);
        Assert.Equal(RescanSchedule.Of(write), await store.ReadScheduleAsync(fileId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ApplyMetadataRescanScheduleAsync_CountsChangedSinceRead_WritesNothing(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(SeededSchedule);
        var staleCounts = new AudiobookFileRescanCounts(0, 0, 0);

        await using (var context = store.CreateContext())
        {
            Assert.False(await new EfAudiobookFileRepository(context).ApplyMetadataRescanScheduleAsync(
                fileId,
                staleCounts,
                PolicyWrite(MetadataRescanOutcome.ExtractionFailed, staleCounts, NowUtc)));
        }

        Assert.Equal(SeededSchedule, await store.ReadScheduleAsync(fileId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RecordMetadataRescanRefreshedAsync_StillMissingMetadata_RecordsIncomplete(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(SeededSchedule);
        var write = PolicyWrite(MetadataRescanOutcome.Incomplete, SeededSchedule.Counts, NowUtc);

        await using (var context = store.CreateContext())
        {
            Assert.Equal(
                AudiobookFileRescanRefreshRecord.Incomplete,
                await new EfAudiobookFileRepository(context)
                    .RecordMetadataRescanRefreshedAsync(fileId, SeededSchedule.Counts, write));
        }

        Assert.Equal(new AudiobookFileRescanCounts(3, 0, 0), write.Counts);
        Assert.Equal(RescanSchedule.Of(write), await store.ReadScheduleAsync(fileId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RecordMetadataRescanRefreshedAsync_MetadataComplete_RecordsCompletedAndKeepsDueTime(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(SeededSchedule, hasMetadata: true);
        AudiobookFileRescanRefreshRecord recorded;

        await using (var context = store.CreateContext())
        {
            recorded = await new EfAudiobookFileRepository(context).RecordMetadataRescanRefreshedAsync(
                fileId,
                SeededSchedule.Counts,
                PolicyWrite(MetadataRescanOutcome.Incomplete, SeededSchedule.Counts, NowUtc));
        }

        Assert.Equal(
            new RescanSchedule(
                SeededSchedule.DueUtc,
                new AudiobookFileRescanCounts(0, 0, 0),
                nameof(MetadataRescanOutcome.Completed)),
            await store.ReadScheduleAsync(fileId));
        Assert.Equal(AudiobookFileRescanRefreshRecord.Completed, recorded);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RecordMetadataRescanRefreshedAsync_LostCompareAndSet_KeepsTheConcurrentIncompleteWrite(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(SeededSchedule);
        var concurrentWrite = PolicyWrite(MetadataRescanOutcome.Incomplete, SeededSchedule.Counts, NowUtc);
        await using (var concurrentContext = store.CreateContext())
        {
            Assert.Equal(
                AudiobookFileRescanRefreshRecord.Incomplete,
                await new EfAudiobookFileRepository(concurrentContext)
                    .RecordMetadataRescanRefreshedAsync(fileId, SeededSchedule.Counts, concurrentWrite));
        }

        AudiobookFileRescanRefreshRecord recorded;
        await using (var context = store.CreateContext())
        {
            recorded = await new EfAudiobookFileRepository(context).RecordMetadataRescanRefreshedAsync(
                fileId,
                SeededSchedule.Counts,
                PolicyWrite(MetadataRescanOutcome.Incomplete, SeededSchedule.Counts, NowUtc.AddMinutes(5)));
        }

        var persisted = await store.ReadScheduleAsync(fileId);
        Assert.Equal(3, persisted.Counts.Failure);
        Assert.Equal(nameof(MetadataRescanOutcome.Incomplete), persisted.LastOutcome);
        Assert.Equal(RescanSchedule.Of(concurrentWrite), persisted);
        Assert.Equal(AudiobookFileRescanRefreshRecord.NotRecorded, recorded);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task GetDueForeignSyntaxRescanRowsAsync_ReturnsDueForeignRowsInDueOrderUpToTheBound(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var dueLast = await store.SeedFileAsync(Due(-1, unavailable: 3), syntax: ForeignSyntax);
        var dueFirst = await store.SeedFileAsync(Due(-3, unavailable: 0), syntax: ForeignSyntax);
        var dueThird = await store.SeedFileAsync(Due(-2, unavailable: 1), syntax: ForeignSyntax);
        await store.SeedFileAsync(Due(1, unavailable: 0), syntax: ForeignSyntax);
        await store.SeedFileAsync(Due(-5, unavailable: 0), syntax: ForeignSyntax, hasMetadata: true);
        await store.SeedFileAsync(Due(-5, unavailable: 0), syntax: HostSyntax);
        await store.SeedFileAsync(Due(-5, unavailable: 0));
        var dueSecond = await store.SeedFileAsync(Due(-3, unavailable: 2), syntax: ForeignSyntax);

        await using var context = store.CreateContext();
        var repository = new EfAudiobookFileRepository(context);

        Assert.Equal(
            new AudiobookFileRescanForeignRow[] { new(dueFirst, 0), new(dueSecond, 2), new(dueThird, 1) },
            await repository.GetDueForeignSyntaxRescanRowsAsync(NowUtc, HostSyntax, 3));
        Assert.Equal(
            new AudiobookFileRescanForeignRow[] { new(dueFirst, 0), new(dueSecond, 2), new(dueThird, 1), new(dueLast, 3) },
            await repository.GetDueForeignSyntaxRescanRowsAsync(NowUtc, HostSyntax, 256));
        Assert.Empty(await repository.GetDueForeignSyntaxRescanRowsAsync(NowUtc, HostSyntax, 0));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeferForeignSyntaxRescanRowsAsync_MovesEachRowOutOnTheUnavailableLadder(string provider)
    {
        var updates = new UpdateCommandCounter();
        await using var store = await ScheduleStore.CreateAsync(provider, updates);
        int[] unavailableCounts = [0, 1, 2, 3, 4, 9];
        var seeded = new Dictionary<int, RescanSchedule>();
        foreach (var unavailable in unavailableCounts)
        {
            var schedule = new RescanSchedule(
                NowUtc.AddMinutes(-unavailable),
                new AudiobookFileRescanCounts(2, unavailable, 1),
                nameof(MetadataRescanOutcome.NotOnThisHost));
            seeded.Add(await store.SeedFileAsync(schedule, syntax: ForeignSyntax), schedule);
        }

        await using (var context = store.CreateContext())
        {
            var repository = new EfAudiobookFileRepository(context);
            var rows = await repository.GetDueForeignSyntaxRescanRowsAsync(NowUtc, HostSyntax, 256);
            Assert.Equal(unavailableCounts.Length, rows.Count);
            updates.Reset();

            Assert.Equal(rows.Count, await repository.DeferForeignSyntaxRescanRowsAsync(rows, NowUtc, HostSyntax));
        }

        if (provider == SqliteProvider)
        {
            // Six counts, five ladder steps: 4 and 9 share the 6 h cap.
            Assert.Equal(5, updates.Count);
        }

        foreach (var (fileId, schedule) in seeded)
        {
            Assert.Equal(
                RescanSchedule.Of(PolicyWrite(MetadataRescanOutcome.NotOnThisHost, schedule.Counts, NowUtc)),
                await store.ReadScheduleAsync(fileId));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeferForeignSyntaxRescanRowsAsync_RowChangedAfterRead_IsLeftAsWritten(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var unchanged = await store.SeedFileAsync(Due(-3, unavailable: 2), syntax: ForeignSyntax);
        var reset = await store.SeedFileAsync(Due(-2, unavailable: 2), syntax: ForeignSyntax);
        var completed = await store.SeedFileAsync(Due(-1, unavailable: 0), syntax: ForeignSyntax);
        await using var context = store.CreateContext();
        var repository = new EfAudiobookFileRepository(context);
        var rows = await repository.GetDueForeignSyntaxRescanRowsAsync(NowUtc, HostSyntax, 256);
        Assert.Equal(3, rows.Count);

        var resetSchedule = new RescanSchedule(NowUtc, new AudiobookFileRescanCounts(0, 0, 0), null);
        await store.OverwriteAsync(reset, file => RescanSchedule.WriteTo(file, resetSchedule));
        await store.OverwriteAsync(completed, file =>
        {
            file.Size = 4096;
            file.DurationSeconds = 100;
            file.Format = "m4b";
            file.SampleRate = 44100;
        });

        Assert.Equal(1, await repository.DeferForeignSyntaxRescanRowsAsync(rows, NowUtc, HostSyntax));

        Assert.Equal(
            RescanSchedule.Of(PolicyWrite(
                MetadataRescanOutcome.NotOnThisHost,
                Due(-3, unavailable: 2).Counts,
                NowUtc)),
            await store.ReadScheduleAsync(unchanged));
        Assert.Equal(resetSchedule, await store.ReadScheduleAsync(reset));
        Assert.Equal(Due(-1, unavailable: 0), await store.ReadScheduleAsync(completed));
    }

    // A tracked copy loaded before the write must reflect it: a later compare-and-set
    // that reads its counters from that copy has to win, and UpdateAsync on the copy
    // must keep the written schedule.
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ApplyMetadataRescanScheduleAsync_StaleTrackedCopy_TakesTheWriteThroughUpdateAsync(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(SeededSchedule);
        await using var context = store.CreateContext();
        var repository = new EfAudiobookFileRepository(context);
        var trackedFile = Assert.IsType<AudiobookFile>(await repository.GetByIdAsync(fileId));
        var firstWrite = PolicyWrite(MetadataRescanOutcome.FileMissing, SeededSchedule.Counts, NowUtc);

        Assert.True(await repository.ApplyMetadataRescanScheduleAsync(fileId, SeededSchedule.Counts, firstWrite));

        var trackedCounts = RescanSchedule.Of(trackedFile).Counts;
        var secondWrite = PolicyWrite(MetadataRescanOutcome.NoMetadata, trackedCounts, NowUtc.AddMinutes(30));
        Assert.True(await repository.ApplyMetadataRescanScheduleAsync(fileId, trackedCounts, secondWrite));
        Assert.Equal(RescanSchedule.Of(secondWrite), RescanSchedule.Of(trackedFile));
        trackedFile.DurationSeconds = 321;
        await repository.UpdateAsync(trackedFile);

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal(321, persisted.DurationSeconds);
        Assert.Equal(RescanSchedule.Of(secondWrite), RescanSchedule.Of(persisted));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RecordMetadataRescanRefreshedAsync_StaleTrackedCopy_KeepsCompletedThroughUpdateAsync(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(SeededSchedule, hasMetadata: true);
        await using var context = store.CreateContext();
        var repository = new EfAudiobookFileRepository(context);
        var trackedFile = Assert.IsType<AudiobookFile>(await repository.GetByIdAsync(fileId));
        var completed = new RescanSchedule(
            SeededSchedule.DueUtc,
            new AudiobookFileRescanCounts(0, 0, 0),
            nameof(MetadataRescanOutcome.Completed));

        Assert.Equal(
            AudiobookFileRescanRefreshRecord.Completed,
            await repository.RecordMetadataRescanRefreshedAsync(
                fileId,
                SeededSchedule.Counts,
                PolicyWrite(MetadataRescanOutcome.Incomplete, SeededSchedule.Counts, NowUtc)));
        Assert.Equal(completed, RescanSchedule.Of(trackedFile));
        trackedFile.DurationSeconds = 654;
        await repository.UpdateAsync(trackedFile);

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal(654, persisted.DurationSeconds);
        Assert.Equal(completed, RescanSchedule.Of(persisted));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeferForeignSyntaxRescanRowsAsync_StaleTrackedCopy_TakesTheWriteThroughUpdateAsync(string provider)
    {
        await using var store = await ScheduleStore.CreateAsync(provider);
        var seeded = Due(-1, unavailable: 1);
        var fileId = await store.SeedFileAsync(seeded, syntax: ForeignSyntax);
        await using var context = store.CreateContext();
        var repository = new EfAudiobookFileRepository(context);
        var trackedFile = Assert.IsType<AudiobookFile>(await repository.GetByIdAsync(fileId));
        var deferred = RescanSchedule.Of(PolicyWrite(MetadataRescanOutcome.NotOnThisHost, seeded.Counts, NowUtc));

        var rows = await repository.GetDueForeignSyntaxRescanRowsAsync(NowUtc, HostSyntax, 256);
        Assert.Equal(1, await repository.DeferForeignSyntaxRescanRowsAsync(rows, NowUtc, HostSyntax));
        Assert.Equal(deferred, RescanSchedule.Of(trackedFile));
        trackedFile.DurationSeconds = 987;
        await repository.UpdateAsync(trackedFile);

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal(987, persisted.DurationSeconds);
        Assert.Equal(deferred, RescanSchedule.Of(persisted));
    }

    private static RescanSchedule Due(int hoursFromNow, int unavailable) => new(
        NowUtc.AddHours(hoursFromNow),
        new AudiobookFileRescanCounts(1, unavailable, 0),
        nameof(MetadataRescanOutcome.FileMissing));

    // The write the processor will persist: the policy's counters and due time for the outcome.
    private static AudiobookFileRescanScheduleWrite PolicyWrite(
        MetadataRescanOutcome outcome,
        AudiobookFileRescanCounts current,
        DateTime nowUtc)
    {
        var change = MetadataRescanSchedulePolicy.Apply(
            outcome,
            new MetadataRescanCounters(current.Failure, current.Unavailable, current.Blocked),
            nowUtc)!.Value;
        return new AudiobookFileRescanScheduleWrite(
            new AudiobookFileRescanCounts(change.Counters.Failure, change.Counters.Unavailable, change.Counters.Blocked),
            change.DueUtc!.Value,
            outcome.ToString());
    }

    private sealed record RescanSchedule(DateTime DueUtc, AudiobookFileRescanCounts Counts, string? LastOutcome)
    {
        public static RescanSchedule Of(AudiobookFile file) => new(
            file.MetadataRescanDueUtc,
            new AudiobookFileRescanCounts(
                file.MetadataRescanFailureCount,
                file.MetadataRescanUnavailableCount,
                file.MetadataRescanBlockedCount),
            file.MetadataRescanLastOutcome);

        public static RescanSchedule Of(AudiobookFileRescanScheduleWrite write) =>
            new(write.DueUtc, write.Counts, write.LastOutcome);

        public static void WriteTo(AudiobookFile file, RescanSchedule schedule)
        {
            file.MetadataRescanDueUtc = schedule.DueUtc;
            file.MetadataRescanFailureCount = schedule.Counts.Failure;
            file.MetadataRescanUnavailableCount = schedule.Counts.Unavailable;
            file.MetadataRescanBlockedCount = schedule.Counts.Blocked;
            file.MetadataRescanLastOutcome = schedule.LastOutcome;
        }
    }

    private sealed class UpdateCommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public void Reset() => _count = 0;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _count);
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
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

        public static async Task<ScheduleStore> CreateAsync(string provider, UpdateCommandCounter? updates = null)
        {
            if (provider == InMemoryProvider)
            {
                return new ScheduleStore(
                    new DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
                    null);
            }

            var databasePath = Path.Combine(Path.GetTempPath(), $"listenarr-rescan-writes-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
            var builder = new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connectionString);
            if (updates != null)
            {
                builder.AddInterceptors(updates);
            }

            var store = new ScheduleStore(builder.Options, databasePath);
            await using var setupContext = store.CreateContext();
            await setupContext.Database.EnsureCreatedAsync();
            return store;
        }

        public ListenArrDbContext CreateContext() => new(_options);

        public async Task<int> SeedFileAsync(
            RescanSchedule schedule,
            FileSystemPathSyntax? syntax = null,
            bool hasMetadata = false)
        {
            await using var context = CreateContext();
            var file = new AudiobookFile
            {
                Audiobook = new Audiobook { Title = "A Princess of Mars", BasePath = "/library/source" },
                Path = "/library/source/book.m4b"
            };
            if (hasMetadata)
            {
                file.Size = 4096;
                file.DurationSeconds = 100;
                file.Format = "m4b";
                file.SampleRate = 44100;
            }

            RescanSchedule.WriteTo(file, schedule);
            context.AudiobookFiles.Add(file);
            context.Entry(file).Property(nameof(AudiobookFile.PathSyntax)).CurrentValue = syntax;
            await context.SaveChangesAsync();
            Assert.Equal(schedule, await ReadScheduleAsync(file.Id));
            return file.Id;
        }

        public async Task OverwriteAsync(int fileId, Action<AudiobookFile> change)
        {
            await using var context = CreateContext();
            var file = await context.AudiobookFiles.SingleAsync(candidate => candidate.Id == fileId);
            change(file);
            await context.SaveChangesAsync();
        }

        public async Task<AudiobookFile> ReadFileAsync(int fileId)
        {
            await using var context = CreateContext();
            return await context.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Id == fileId);
        }

        public async Task<RescanSchedule> ReadScheduleAsync(int fileId) =>
            RescanSchedule.Of(await ReadFileAsync(fileId));

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
