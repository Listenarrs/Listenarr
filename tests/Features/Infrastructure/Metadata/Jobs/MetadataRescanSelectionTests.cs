/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.Library.Files;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Listenarr.Tests.Features.Infrastructure.Persistence.LargeLibrary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Jobs;

/// <summary>
/// Drives the rescan selection loop against SQLite. Rows read are counted where
/// the repository returns them and probes where the filesystem answers them,
/// never by the loop's own counters.
/// </summary>
[Trait("Area", "Metadata")]
[Trait("Name", "MetadataRescanSelectionTests")]
[Trait("Category", "Infrastructure")]
public sealed class MetadataRescanSelectionTests : BaseTests
{
    private static readonly DateTime ClockUtc = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(5);
    private static readonly FileSystemPathSyntax HostSyntax = LargeLibraryFixtureBuilder.HostSyntax;
    private static readonly FileSystemPathSyntax ForeignSyntax = LargeLibraryFixtureBuilder.ForeignSyntax;
    private const string MissingMetadataSql =
        "(\"DurationSeconds\" IS NULL OR \"Format\" IS NULL OR \"SampleRate\" IS NULL OR \"Size\" IS NULL OR \"Size\" = 64)";

    private readonly ITestOutputHelper _output;

    public MetadataRescanSelectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(LargeLibraryVariant.ForeignSyntaxOldest)]
    [InlineData(LargeLibraryVariant.ForeignBaseOldest)]
    public async Task SelectAsync_ForeignRowsAtTheOldestDueTimes_AreBoundedAndTheHostRowIsReached(LargeLibraryVariant variant)
    {
        var builder = new LargeLibraryFixtureBuilder(ClockUtc);
        await using var fixture = await builder.BuildAsync();
        await builder.StampRescanScheduleAsync(fixture, variant);
        var harness = new SelectionHarness(() => fixture.CreateOptions());
        var hostRowId = (int)await ScalarAsync(
            fixture,
            $"SELECT \"Id\" FROM \"AudiobookFiles\" WHERE \"PathSyntax\" = '{HostSyntax}' AND {MissingMetadataSql} "
            + $"AND \"MetadataRescanDueUtc\" <= '{ClockUtc:yyyy-MM-dd HH:mm:ss}' ORDER BY \"MetadataRescanDueUtc\", \"Id\" LIMIT 1");
        var selected = new List<int>();

        for (var cycle = 0; cycle < 2; cycle++)
        {
            var nowUtc = ClockUtc + (cycle * CycleInterval);
            var dueForeignBefore = await DueForeignSyntaxCountAsync(fixture, nowUtc);

            var result = await harness.RunCycleAsync(nowUtc);

            var dueForeignAfter = await DueForeignSyntaxCountAsync(fixture, nowUtc);
            _output.WriteLine(
                $"{variant} cycle {cycle + 1}: rows read {harness.CycleRowsRead}, pages {result.PagesRead}, "
                + $"probes {harness.CycleProbedPaths.Count}, due foreign {dueForeignBefore} -> {dueForeignAfter}, "
                + $"host row selected {result.Batch.Any(candidate => candidate.File.Id == hostRowId)}");
            Assert.InRange(harness.CycleRowsRead, 1, MetadataRescanSchedulePolicy.PageSize * MetadataRescanSchedulePolicy.MaxPages);
            if (variant == LargeLibraryVariant.ForeignBaseOldest && cycle == 0)
            {
                // The foreign-base rows hold the oldest due times and still pass the page filter,
                // so the first cycle must walk past them; one page means the variant setup was lost.
                Assert.True(result.PagesRead > 1, $"cycle 1 read {result.PagesRead} page(s); the foreign-base rows are not the oldest");
            }

            Assert.Equal(Math.Min(MetadataRescanSchedulePolicy.MaxForeignRows, dueForeignBefore), dueForeignBefore - dueForeignAfter);
            Assert.Equal(dueForeignBefore - dueForeignAfter, result.ForeignRowsDeferred);
            selected.AddRange(result.Batch.Select(candidate => candidate.File.Id));
        }

        Assert.Contains(hostRowId, selected);
    }

    [Fact]
    public async Task SelectAsync_ManyMissingFiles_ChecksAtMostTheProbeBudgetAndDefersEveryProbedRow()
    {
        await using var store = await SmallStore.CreateAsync();
        for (var book = 0; book < 50; book++)
        {
            await store.SeedAsync(
                LibraryPath($"book-{book:D2}"),
                Enumerable.Range(0, 100).Select(index => new SeedFile(
                    LibraryPath($"book-{book:D2}", $"part-{index:D3}.m4b"),
                    HostSyntax,
                    ClockUtc.AddMinutes(-1 - (book * 100) - index))));
        }

        var harness = new SelectionHarness(store.CreateOptions) { FileExists = _ => false };
        var probedIds = new HashSet<int>();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var nowUtc = ClockUtc + (cycle * CycleInterval);
            var result = await harness.RunCycleAsync(nowUtc);

            Assert.Equal(MetadataRescanSchedulePolicy.MaxProbes, harness.CycleProbedPaths.Count);
            Assert.Empty(result.Batch);
            foreach (var path in harness.CycleProbedPaths)
            {
                var file = await store.ReadByPathAsync(path);
                Assert.True(probedIds.Add(file.Id), $"file {file.Id} was probed in an earlier cycle");
                Assert.True(file.MetadataRescanDueUtc > nowUtc, $"file {file.Id} is still due after it was probed");
                Assert.Equal(nameof(MetadataRescanOutcome.FileMissing), file.MetadataRescanLastOutcome);
                Assert.Equal(1, file.MetadataRescanUnavailableCount);
            }
        }
    }

    [Fact]
    public async Task SelectAsync_BlockedAudiobookWithMissingFile_IsNotProbedAndRecordsMoveRecoveryBlocked()
    {
        await using var store = await SmallStore.CreateAsync();
        var blocked = await store.SeedAsync(
            LibraryPath("blocked"),
            [
                new SeedFile(LibraryPath("blocked", "one.m4b"), HostSyntax, ClockUtc.AddHours(-3)),
                new SeedFile(LibraryPath("blocked", "two.m4b"), HostSyntax, ClockUtc.AddHours(-2))
            ]);
        var healthy = await store.SeedAsync(
            LibraryPath("healthy"),
            [new SeedFile(LibraryPath("healthy", "book.m4b"), HostSyntax, ClockUtc.AddHours(-1))]);
        var harness = new SelectionHarness(store.CreateOptions)
        {
            FileExists = path => !path.Contains("blocked", StringComparison.Ordinal)
        };
        harness.BlockedAudiobookIds.Add(blocked.AudiobookId);

        var result = await harness.RunCycleAsync(ClockUtc);

        Assert.Equal(new[] { LibraryPath("healthy", "book.m4b") }, harness.CycleProbedPaths);
        Assert.Equal(healthy.FileIds, result.Batch.Select(candidate => candidate.File.Id));
        foreach (var fileId in blocked.FileIds)
        {
            var file = await store.ReadAsync(fileId);
            Assert.Equal(nameof(MetadataRescanOutcome.MoveRecoveryBlocked), file.MetadataRescanLastOutcome);
            Assert.Equal(0, file.MetadataRescanUnavailableCount);
            Assert.Equal(1, file.MetadataRescanBlockedCount);
            Assert.Equal(ClockUtc + MetadataRescanSchedulePolicy.BlockedDelay(1), file.MetadataRescanDueUtc);
        }

        Assert.Equal(1, harness.RecoveryLookups[blocked.AudiobookId]);
    }

    [Fact]
    public async Task SelectAsync_ResolverThrowsForOneRow_RecordsSelectionFailedAndSelectsLaterRows()
    {
        await using var store = await SmallStore.CreateAsync();
        var seeded = await store.SeedAsync(
            LibraryPath("book"),
            [
                new SeedFile(LibraryPath("book", "first.m4b"), HostSyntax, ClockUtc.AddHours(-3)),
                // A relative path that escapes the base path makes the real resolver throw.
                new SeedFile("../../outside/escape.m4b", HostSyntax, ClockUtc.AddHours(-2)),
                new SeedFile(LibraryPath("book", "last.m4b"), HostSyntax, ClockUtc.AddHours(-1))
            ]);
        var harness = new SelectionHarness(store.CreateOptions);

        var result = await harness.RunCycleAsync(ClockUtc);

        Assert.Equal(
            new[] { seeded.FileIds[0], seeded.FileIds[2] },
            result.Batch.Select(candidate => candidate.File.Id));
        var failed = await store.ReadAsync(seeded.FileIds[1]);
        Assert.Equal(nameof(MetadataRescanOutcome.SelectionFailed), failed.MetadataRescanLastOutcome);
        Assert.Equal(1, failed.MetadataRescanBlockedCount);
        Assert.Equal(ClockUtc + MetadataRescanSchedulePolicy.BlockedDelay(1), failed.MetadataRescanDueUtc);
    }

    [Fact]
    public async Task SelectAsync_SelectionFailedCannotBeStored_SkipsTheRowForTheRestOfTheCycle()
    {
        await using var store = await SmallStore.CreateAsync();
        var failing = await store.SeedAsync(
            LibraryPath("book"),
            [new SeedFile("../../outside/escape.m4b", HostSyntax, ClockUtc.AddHours(-3))]);
        // Relative rows with no stored syntax under a foreign base fill the first
        // page without a probe, so the failing row would head the second page.
        await store.SeedAsync(
            ForeignLibraryPath("foreign"),
            Enumerable.Range(0, MetadataRescanSchedulePolicy.PageSize + 2).Select(index => new SeedFile(
                $"part-{index:D3}.m4b",
                null,
                ClockUtc.AddHours(-2).AddSeconds(index))));
        var healthy = await store.SeedAsync(
            LibraryPath("healthy"),
            [new SeedFile(LibraryPath("healthy", "book.m4b"), HostSyntax, ClockUtc.AddHours(-1))]);
        var harness = new SelectionHarness(store.CreateOptions);
        harness.FailingWrites.Add(failing.FileIds[0]);

        var result = await harness.RunCycleAsync(ClockUtc);

        Assert.Equal(healthy.FileIds, result.Batch.Select(candidate => candidate.File.Id));
        Assert.Equal(1, harness.CycleRowsSeen[failing.FileIds[0]]);
        Assert.Equal(ClockUtc.AddHours(-3), (await store.ReadAsync(failing.FileIds[0])).MetadataRescanDueUtc);
    }

    private static string LibraryPath(params string[] names) => RootedPath(HostSyntax, names);

    private static string ForeignLibraryPath(params string[] names) => RootedPath(ForeignSyntax, names);

    private static string RootedPath(FileSystemPathSyntax syntax, string[] names) =>
        syntax == FileSystemPathSyntax.Windows
            ? "C:\\library\\" + string.Join('\\', names)
            : "/library/" + string.Join('/', names);

    private static Task<long> DueForeignSyntaxCountAsync(LargeLibraryFixture fixture, DateTime nowUtc) =>
        ScalarAsync(
            fixture,
            $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE \"PathSyntax\" = '{ForeignSyntax}' AND {MissingMetadataSql} "
            + $"AND \"MetadataRescanDueUtc\" <= '{nowUtc:yyyy-MM-dd HH:mm:ss}'");

    private static async Task<long> ScalarAsync(LargeLibraryFixture fixture, string sql)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed record SeedFile(string Path, FileSystemPathSyntax? Syntax, DateTime DueUtc);

    private sealed record SeededBook(int AudiobookId, IReadOnlyList<int> FileIds);

    // Runs one selection per call on a fresh context, as the processor's cycle scope will.
    private sealed class SelectionHarness(Func<DbContextOptions<ListenArrDbContext>> createOptions)
    {
        public Func<string, bool> FileExists { get; init; } = _ => true;

        public HashSet<int> BlockedAudiobookIds { get; } = [];

        public HashSet<int> FailingWrites { get; } = [];

        public Dictionary<int, int> RecoveryLookups { get; } = [];

        public int CycleRowsRead { get; private set; }

        public Dictionary<int, int> CycleRowsSeen { get; } = [];

        public List<string> CycleProbedPaths { get; } = [];

        public async Task<MetadataRescanSelectionResult> RunCycleAsync(DateTime nowUtc)
        {
            CycleRowsRead = 0;
            CycleRowsSeen.Clear();
            CycleProbedPaths.Clear();
            await using var context = new ListenArrDbContext(createOptions());
            var selection = new MetadataRescanSelection(
                CountingRepository(new EfAudiobookFileRepository(context)),
                MoveQueue(),
                new AudiobookFilePathIdentityResolver(EmptyRootFolders(), ValidSemantics()),
                CountingFileSystem(),
                NullLogger<MetadataRescanSelection>.Instance);
            return await selection.SelectAsync(nowUtc, CancellationToken.None);
        }

        private IAudiobookFileRepository CountingRepository(EfAudiobookFileRepository repository)
        {
            var proxy = new Mock<IAudiobookFileRepository>(MockBehavior.Strict);
            proxy.Setup(r => r.GetMetadataRescanDuePageAsync(
                    It.IsAny<DateTime>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(async (DateTime now, IReadOnlyCollection<int> excluded, int size, CancellationToken ct) =>
                {
                    var page = await repository.GetMetadataRescanDuePageAsync(now, excluded, size, ct);
                    CycleRowsRead += page.Count;
                    foreach (var candidate in page)
                    {
                        CycleRowsSeen[candidate.File.Id] = CycleRowsSeen.GetValueOrDefault(candidate.File.Id) + 1;
                    }

                    return page;
                });
            proxy.Setup(r => r.ApplyMetadataRescanScheduleAsync(
                    It.IsAny<int>(), It.IsAny<AudiobookFileRescanCounts>(), It.IsAny<AudiobookFileRescanScheduleWrite>(), It.IsAny<CancellationToken>()))
                .Returns((int fileId, AudiobookFileRescanCounts expected, AudiobookFileRescanScheduleWrite write, CancellationToken ct) =>
                    FailingWrites.Contains(fileId)
                        ? throw new InvalidOperationException("The schedule write failed.")
                        : repository.ApplyMetadataRescanScheduleAsync(fileId, expected, write, ct));
            proxy.Setup(r => r.GetDueForeignSyntaxRescanRowsAsync(
                    It.IsAny<DateTime>(), It.IsAny<FileSystemPathSyntax>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((DateTime now, FileSystemPathSyntax host, int bound, CancellationToken ct) =>
                    repository.GetDueForeignSyntaxRescanRowsAsync(now, host, bound, ct));
            proxy.Setup(r => r.DeferForeignSyntaxRescanRowsAsync(
                    It.IsAny<IReadOnlyCollection<AudiobookFileRescanForeignRow>>(), It.IsAny<DateTime>(), It.IsAny<FileSystemPathSyntax>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyCollection<AudiobookFileRescanForeignRow> rows, DateTime now, FileSystemPathSyntax host, CancellationToken ct) =>
                    repository.DeferForeignSyntaxRescanRowsAsync(rows, now, host, ct));
            return proxy.Object;
        }

        private IMoveQueueService MoveQueue()
        {
            var moveQueue = new Mock<IMoveQueueService>(MockBehavior.Strict);
            moveQueue.Setup(service => service.GetRecoveryStateForAudiobookAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int audiobookId, CancellationToken _) =>
                {
                    RecoveryLookups[audiobookId] = RecoveryLookups.GetValueOrDefault(audiobookId) + 1;
                    return BlockedAudiobookIds.Contains(audiobookId)
                        ? MoveRecoveryState.None with { Disposition = MoveRecoveryDisposition.InProgress }
                        : MoveRecoveryState.None;
                });
            return moveQueue.Object;
        }

        private IFileSystem CountingFileSystem()
        {
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            fileSystem.Setup(fs => fs.FileExists(It.IsAny<string>()))
                .Returns((string path) =>
                {
                    CycleProbedPaths.Add(path);
                    return FileExists(path);
                });
            return fileSystem.Object;
        }

        private static IRootFolderRepository EmptyRootFolders()
        {
            var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
            roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([]);
            return roots.Object;
        }

        // Every host path resolves as a valid case-sensitive location, so a row's
        // fate depends on the probe alone.
        private static IFileSystemSemanticsResolver ValidSemantics()
        {
            var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
            semantics.Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<string>(), It.IsAny<FileSystemCaseSensitivityMode>(), It.IsAny<CancellationToken>()))
                .Returns((string path, FileSystemCaseSensitivityMode _, CancellationToken _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(HostSyntax, FileSystemCaseSensitivity.Sensitive),
                        PathIdentityState.Valid,
                        path)));
            return semantics.Object;
        }
    }

    // A file-backed SQLite database with the current model, for hand-seeded rows.
    private sealed class SmallStore : IAsyncDisposable
    {
        private readonly DbContextOptions<ListenArrDbContext> _options;
        private readonly string _databasePath;
        private int _seededBooks;

        private SmallStore(DbContextOptions<ListenArrDbContext> options, string databasePath)
        {
            _options = options;
            _databasePath = databasePath;
        }

        public static async Task<SmallStore> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"listenarr-rescan-selection-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
            var store = new SmallStore(new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connectionString).Options, databasePath);
            await using var context = store.CreateContext();
            await context.Database.EnsureCreatedAsync();
            return store;
        }

        public DbContextOptions<ListenArrDbContext> CreateOptions() => _options;

        public async Task<SeededBook> SeedAsync(string basePath, IEnumerable<SeedFile> files)
        {
            var books = LargeLibraryFixtureBuilder.LoadBooks();
            var book = books[_seededBooks++ % books.Count];
            await using var context = CreateContext();
            var audiobook = new Audiobook { Title = book.Title, Authors = [.. book.Authors], BasePath = basePath };
            context.Audiobooks.Add(audiobook);
            var seeded = new List<AudiobookFile>();
            foreach (var seed in files)
            {
                var file = AudiobookFile.CreateUnresolved(seed.Path);
                file.Audiobook = audiobook;
                file.MetadataRescanDueUtc = seed.DueUtc;
                context.AudiobookFiles.Add(file);
                context.Entry(file).Property(nameof(AudiobookFile.PathSyntax)).CurrentValue = seed.Syntax;
                seeded.Add(file);
            }

            await context.SaveChangesAsync();
            return new SeededBook(audiobook.Id, seeded.Select(file => file.Id).ToList());
        }

        public async Task<AudiobookFile> ReadAsync(int fileId)
        {
            await using var context = CreateContext();
            return await context.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Id == fileId);
        }

        public async Task<AudiobookFile> ReadByPathAsync(string path)
        {
            await using var context = CreateContext();
            return await context.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Path == path);
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
