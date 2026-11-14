using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Metadata;

[Trait("Name", "AdminMetadataController_RescanScheduleTests")]
[Trait("Category", "AdminMetadataController")]
public sealed class AdminMetadataController_RescanScheduleTests : BaseTests
{
    private const string FilePath = "/library/source/book.m4b";
    private const string ReextractEndpoint = "reextract-file";
    private const string TriggerEndpoint = "trigger-rescan";

    private static readonly DateTime NowUtc = new(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DueInAWeekUtc = NowUtc.AddDays(7);

    public static TheoryData<string> Endpoints => new() { ReextractEndpoint, TriggerEndpoint };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ManualRescan_FileDueInAWeek_IsReextractedAndLeavesTheMissingPredicate(string endpoint)
    {
        await using var store = await AdminStore.CreateAsync();
        var fileId = await store.SeedFileAsync(new StoredSchedule(DueInAWeekUtc, 2, 0, 0, "Incomplete"));

        var extractions = await store.RunAsync(endpoint, fileId, CompleteMetadata());

        Assert.Equal(1, extractions);
        var file = await store.ReadFileAsync(fileId);
        Assert.Equal(60, file.DurationSeconds);
        Assert.Equal(new StoredSchedule(DueInAWeekUtc, 0, 0, 0, "Completed"), StoredSchedule.Of(file));
        Assert.Empty(await store.MissingMetadataAsync());
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ManualRescan_IncompleteResult_IncrementsFailureCount(string endpoint)
    {
        await using var store = await AdminStore.CreateAsync();
        var fileId = await store.SeedFileAsync(new StoredSchedule(DueInAWeekUtc, 2, 0, 0, "Incomplete"));
        var withoutSampleRate = CompleteMetadata();
        withoutSampleRate.SampleRate = null;

        await store.RunAsync(endpoint, fileId, withoutSampleRate);

        Assert.Equal(
            new StoredSchedule(NowUtc.AddHours(2), 3, 0, 0, "Incomplete"),
            StoredSchedule.Of(await store.ReadFileAsync(fileId)));
        Assert.Single(await store.MissingMetadataAsync());
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ManualRescan_NoMetadata_RecordsNoMetadata(string endpoint)
    {
        await using var store = await AdminStore.CreateAsync();
        var fileId = await store.SeedFileAsync(new StoredSchedule(DueInAWeekUtc, 0, 1, 1, "FileMissing"));

        await store.RunAsync(endpoint, fileId, null);

        Assert.Equal(
            new StoredSchedule(NowUtc.AddMinutes(30), 1, 0, 0, "NoMetadata"),
            StoredSchedule.Of(await store.ReadFileAsync(fileId)));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ManualRescan_ExtractionThrows_RecordsExtractionFailed(string endpoint)
    {
        await using var store = await AdminStore.CreateAsync();
        var fileId = await store.SeedFileAsync(new StoredSchedule(DueInAWeekUtc, 1, 0, 0, "Incomplete"));

        await store.RunAsync(endpoint, fileId, extraction: new InvalidOperationException("probe failed"));

        Assert.Equal(
            new StoredSchedule(NowUtc.AddHours(1), 2, 0, 0, "ExtractionFailed"),
            StoredSchedule.Of(await store.ReadFileAsync(fileId)));
    }

    [Fact]
    public async Task TriggerRescan_FileNotOnDisk_RecordsNothing()
    {
        await using var store = await AdminStore.CreateAsync();
        var seeded = new StoredSchedule(DueInAWeekUtc, 2, 1, 1, "GuardRejected");
        var fileId = await store.SeedFileAsync(seeded);

        var extractions = await store.RunAsync(TriggerEndpoint, fileId, CompleteMetadata(), fileExists: false);

        Assert.Equal(0, extractions);
        Assert.Equal(seeded, StoredSchedule.Of(await store.ReadFileAsync(fileId)));
    }

    private static AudioMetadata CompleteMetadata() => new()
    {
        Duration = TimeSpan.FromSeconds(60),
        Format = "m4b",
        SampleRate = 44100,
        BitRate = 64000,
        Channels = 2
    };

    private sealed record StoredSchedule(DateTime DueUtc, int Failure, int Unavailable, int Blocked, string? LastOutcome)
    {
        public static StoredSchedule Of(AudiobookFile file) => new(
            file.MetadataRescanDueUtc,
            file.MetadataRescanFailureCount,
            file.MetadataRescanUnavailableCount,
            file.MetadataRescanBlockedCount,
            file.MetadataRescanLastOutcome);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class AdminStore : IAsyncDisposable
    {
        private readonly DbContextOptions<ListenArrDbContext> _options;
        private readonly string _databasePath;

        private AdminStore(DbContextOptions<ListenArrDbContext> options, string databasePath)
        {
            _options = options;
            _databasePath = databasePath;
        }

        public static async Task<AdminStore> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"listenarr-admin-rescan-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
            var store = new AdminStore(new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connectionString).Options, databasePath);
            await using var setupContext = store.CreateContext();
            await setupContext.Database.EnsureCreatedAsync();
            return store;
        }

        public async Task<int> SeedFileAsync(StoredSchedule schedule)
        {
            await using var context = CreateContext();
            var file = new AudiobookFile
            {
                Audiobook = new Audiobook { Title = "A Princess of Mars", BasePath = "/library/source" },
                Path = FilePath,
                MetadataRescanDueUtc = schedule.DueUtc,
                MetadataRescanFailureCount = schedule.Failure,
                MetadataRescanUnavailableCount = schedule.Unavailable,
                MetadataRescanBlockedCount = schedule.Blocked,
                MetadataRescanLastOutcome = schedule.LastOutcome
            };
            context.AudiobookFiles.Add(file);
            await context.SaveChangesAsync();
            return file.Id;
        }

        // Returns how many times the extraction ran.
        public async Task<int> RunAsync(
            string endpoint,
            int fileId,
            AudioMetadata? metadata = null,
            Exception? extraction = null,
            bool fileExists = true)
        {
            var antiforgery = new Mock<IAntiforgery>();
            var metadataService = new Mock<IMetadataService>();
            var setup = metadataService.Setup(service => service.ExtractFileMetadataAsync(FilePath));
            if (extraction != null)
            {
                setup.ThrowsAsync(extraction);
            }
            else
            {
                setup.ReturnsAsync(metadata);
            }

            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(system => system.FileExists(FilePath)).Returns(fileExists);
            fileSystem.Setup(system => system.GetFileLength(FilePath)).Returns(4096);

            await using var context = CreateContext();
            var controller = new AdminMetadataController(
                new EfAudiobookFileRepository(context),
                antiforgery.Object,
                metadataService.Object,
                fileSystem.Object,
                new FixedTimeProvider(NowUtc),
                NullLogger<AdminMetadataController>.Instance);

            if (endpoint == ReextractEndpoint)
            {
                await controller.ReextractFile(fileId);
            }
            else
            {
                await controller.TriggerRescan();
            }

            return metadataService.Invocations.Count(call => call.Method.Name == nameof(IMetadataService.ExtractFileMetadataAsync));
        }

        public async Task<AudiobookFile> ReadFileAsync(int fileId)
        {
            await using var context = CreateContext();
            return await context.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Id == fileId);
        }

        public async Task<List<AudiobookFile>> MissingMetadataAsync()
        {
            await using var context = CreateContext();
            return await new EfAudiobookFileRepository(context).GetMissingMetadataAsync(10);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_databasePath);
            return ValueTask.CompletedTask;
        }

        private ListenArrDbContext CreateContext() => new(_options);
    }
}
