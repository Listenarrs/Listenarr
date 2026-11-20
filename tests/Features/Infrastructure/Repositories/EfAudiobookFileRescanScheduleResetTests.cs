using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Name", "EfAudiobookFileRescanScheduleResetTests")]
[Trait("Category", "Persistence")]
public sealed class EfAudiobookFileRescanScheduleResetTests : BaseTests
{
    private const string SqliteProvider = "Sqlite";
    private const string InMemoryProvider = "InMemory";
    private const string Replace = nameof(EfAudiobookFileRepository.ReplacePhysicalGenerationAsync);
    private const string ReplaceWithBasePath = nameof(EfAudiobookFileRepository.ReplacePhysicalGenerationWithBasePathAsync);
    private const string Restore = nameof(EfAudiobookFileRepository.RestorePhysicalGenerationAsync);
    private const string RestoreWithBasePath = nameof(EfAudiobookFileRepository.RestorePhysicalGenerationWithBasePathAsync);
    private const string Reconcile = nameof(EfAudiobookFileRepository.ReconcilePathIdentityAsync);
    private const string ApplyBasePath = nameof(EfAudiobookFileRepository.ApplyBasePathAsync);
    private const string CurrentGeneration = "generation-two";

    private static readonly DateTime NowUtc = new(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc);

    // A file the rescan has already backed off on every ladder: due tomorrow,
    // with all three counters raised.
    private static readonly RescanSchedule BackedOff = new(
        NowUtc.AddDays(1),
        new AudiobookFileRescanCounts(3, 2, 1),
        nameof(MetadataRescanOutcome.Incomplete));

    private static readonly RescanSchedule ResetSchedule = BackedOff with
    {
        DueUtc = NowUtc,
        Counts = new AudiobookFileRescanCounts(0, 0, 0)
    };

    public static TheoryData<string, string> ReplaceWrites => Combine(Replace, ReplaceWithBasePath);

    public static TheoryData<string, string> NonGenerationWrites =>
        Combine(Restore, RestoreWithBasePath, Reconcile, ApplyBasePath);

    [Theory]
    [MemberData(nameof(ReplaceWrites))]
    public async Task ReplacedGeneration_IsDueNowWithZeroCountersAndKeepsItsLastOutcome(string provider, string write)
    {
        await using var store = await ResetStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(BackedOff);

        await using (var context = store.CreateContext())
        {
            Assert.True(await RunAsync(store.Repository(context), write, fileId));
        }

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal("generation-three", persisted.PhysicalObjectIdentity);
        Assert.Equal(ResetSchedule, RescanSchedule.Of(persisted));
    }

    [Theory]
    [MemberData(nameof(ReplaceWrites))]
    public async Task ReplacedGeneration_TrackedCopy_TakesTheResetThroughUpdateAsync(string provider, string write)
    {
        await using var store = await ResetStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(BackedOff);
        await using var context = store.CreateContext();
        var repository = store.Repository(context);
        var trackedFile = Assert.IsType<AudiobookFile>(await repository.GetByIdAsync(fileId));

        Assert.True(await RunAsync(repository, write, fileId));
        Assert.Equal(ResetSchedule, RescanSchedule.Of(trackedFile));
        trackedFile.Codec = "aac";
        await repository.UpdateAsync(trackedFile);

        var persisted = await store.ReadFileAsync(fileId);
        Assert.Equal("aac", persisted.Codec);
        Assert.Equal(ResetSchedule, RescanSchedule.Of(persisted));
    }

    [Theory]
    [MemberData(nameof(NonGenerationWrites))]
    public async Task WriteThatIsNotANewGeneration_KeepsTheSchedule(string provider, string write)
    {
        await using var store = await ResetStore.CreateAsync(provider);
        var fileId = await store.SeedFileAsync(BackedOff);

        await using (var context = store.CreateContext())
        {
            Assert.True(await RunAsync(store.Repository(context), write, fileId));
        }

        Assert.Equal(BackedOff, RescanSchedule.Of(await store.ReadFileAsync(fileId)));
    }

    private static TheoryData<string, string> Combine(params string[] writes)
    {
        var data = new TheoryData<string, string>();
        foreach (var provider in new[] { SqliteProvider, InMemoryProvider })
        {
            foreach (var write in writes)
            {
                data.Add(provider, write);
            }
        }

        return data;
    }

    private static Task<bool> RunAsync(EfAudiobookFileRepository repository, string write, int fileId)
    {
        var replacement = AudiobookFile.CreateUnresolved(ResetStore.FilePath);
        replacement.Size = 2048;
        replacement.DurationSeconds = 60;
        replacement.Format = "m4b";
        replacement.SampleRate = 44_100;
        replacement.ApplyPhysicalObjectIdentity("generation-three", NowUtc);
        var predecessor = new AudiobookFilePhysicalGenerationSnapshot(
            1024, 30, "m4b", "mp4", "aac", 64_000, 44_100, 2, "scan", "generation-one", 1, NowUtc.AddDays(-1));
        var basePathMove = new AudiobookBasePathMutation(ResetStore.AudiobookId, ResetStore.BasePath, ResetStore.OtherBasePath);
        return write switch
        {
            Replace => repository.ReplacePhysicalGenerationAsync(
                fileId, ResetStore.AudiobookId, ResetStore.FilePath, CurrentGeneration, replacement),
            ReplaceWithBasePath => repository.ReplacePhysicalGenerationWithBasePathAsync(
                fileId, ResetStore.AudiobookId, ResetStore.FilePath, CurrentGeneration, replacement, basePathMove),
            Restore => repository.RestorePhysicalGenerationAsync(
                fileId, ResetStore.AudiobookId, ResetStore.FilePath, CurrentGeneration, predecessor),
            RestoreWithBasePath => repository.RestorePhysicalGenerationWithBasePathAsync(
                fileId, ResetStore.AudiobookId, ResetStore.FilePath, CurrentGeneration, predecessor, basePathMove),
            Reconcile => repository.ReconcilePathIdentityAsync(
                fileId,
                ResetStore.AudiobookId,
                ResetStore.SeededPathState,
                ResetStore.FilePath,
                ResetStore.CreateIdentity(FileSystemCaseSensitivityMode.Sensitive)),
            ApplyBasePath => repository.ApplyBasePathAsync(basePathMove),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, null)
        };
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

        public void WriteTo(AudiobookFile file)
        {
            file.MetadataRescanDueUtc = DueUtc;
            file.MetadataRescanFailureCount = Counts.Failure;
            file.MetadataRescanUnavailableCount = Counts.Unavailable;
            file.MetadataRescanBlockedCount = Counts.Blocked;
            file.MetadataRescanLastOutcome = LastOutcome;
        }
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class ResetStore : IAsyncDisposable
    {
        public const int AudiobookId = 1;
        public static readonly string BasePath = Path.GetFullPath(Path.Join("library", "RescanScheduleReset"));
        public static readonly string OtherBasePath = Path.GetFullPath(Path.Join("library", "RescanScheduleResetMoved"));
        public static readonly string FilePath = Path.Join(BasePath, "Book.m4b");
        public static readonly AudiobookFilePathState SeededPathState = SeededFile().CapturePathState();

        private readonly DbContextOptions<ListenArrDbContext> _options;
        private readonly string? _databasePath;
        private readonly FixedClock _clock = new(NowUtc);

        private ResetStore(DbContextOptions<ListenArrDbContext> options, string? databasePath)
        {
            _options = options;
            _databasePath = databasePath;
        }

        public static async Task<ResetStore> CreateAsync(string provider)
        {
            if (provider == InMemoryProvider)
            {
                return new ResetStore(
                    new DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
                    null);
            }

            var databasePath = Path.Combine(Path.GetTempPath(), $"listenarr-rescan-reset-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
            var store = new ResetStore(
                new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connectionString).Options,
                databasePath);
            await using var setupContext = store.CreateContext();
            await setupContext.Database.EnsureCreatedAsync();
            return store;
        }

        public static AudiobookFilePathIdentity CreateIdentity(FileSystemCaseSensitivityMode mode) =>
            AudiobookFilePathIdentity.CreateValid(FilePath, FileSystemPathSemantics.CurrentHostDefault, mode, BasePath);

        public ListenArrDbContext CreateContext() => new(_options);

        public EfAudiobookFileRepository Repository(ListenArrDbContext context) =>
            new(context, timeProvider: _clock);

        public async Task<int> SeedFileAsync(RescanSchedule schedule)
        {
            await using var context = CreateContext();
            context.Audiobooks.Add(new Audiobook { Id = AudiobookId, Title = "A Princess of Mars", BasePath = BasePath });
            var file = SeededFile();
            schedule.WriteTo(file);
            context.AudiobookFiles.Add(file);
            await context.SaveChangesAsync();
            Assert.Equal(schedule, RescanSchedule.Of(await ReadFileAsync(file.Id)));
            return file.Id;
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

        private static AudiobookFile SeededFile()
        {
            var file = AudiobookFile.CreateUnresolved(FilePath);
            file.AudiobookId = AudiobookId;
            file.ApplyPathIdentity(FilePath, CreateIdentity(FileSystemCaseSensitivityMode.Auto));
            file.ApplyPhysicalObjectIdentity(CurrentGeneration, NowUtc.AddHours(-3));
            return file;
        }
    }
}
