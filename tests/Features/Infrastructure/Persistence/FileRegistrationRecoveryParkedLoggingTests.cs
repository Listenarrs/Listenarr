using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Area", "Library")]
[Trait("Name", "FileRegistrationRecoveryParkedLoggingTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRegistrationRecoveryParkedLoggingTests : BaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_UnsupportedProtocolJournals_WarnsPerParkedOperationWithReachedState(
        bool relational)
    {
        var root = FileService.GetTempDirectory("registration-parked-protocol");
        var options = relational
            ? new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite($"Data Source={Path.Join(root, "protocol.db")};Pooling=False")
                .Options
            : new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        var factory = new TestDbContextFactory(options);
        var seeded = new Dictionary<Guid, FileMutationJournalState>
        {
            [Guid.NewGuid()] = FileMutationJournalState.Planned,
            [Guid.NewGuid()] = FileMutationJournalState.SourceDeleted
        };
        var versions = new[] { 0, FileMutationProtocol.Current + 1 };
        await SeedAsync(factory, seeded, versions);

        var logger = new CapturingLogger();
        await CreateService(factory, logger).ReconcileAsync();

        await AssertParkedAndLoggedAsync(factory, logger, seeded);
    }

    [Fact]
    public async Task ReconcileAsync_OwnerlessSupportedJournal_WarnsParkedOperationWithReachedState()
    {
        var root = FileService.GetTempDirectory("registration-parked-orphan");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(root, "orphan.db")};Pooling=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        var seeded = new Dictionary<Guid, FileMutationJournalState>
        {
            [Guid.NewGuid()] = FileMutationJournalState.SourceDeleted
        };
        await SeedAsync(factory, seeded, [FileMutationProtocol.MarkerlessDatabaseState]);

        var logger = new CapturingLogger();
        await CreateService(factory, logger).ReconcileAsync();

        await AssertParkedAndLoggedAsync(factory, logger, seeded);
    }

    private static FileRegistrationRecoveryService CreateService(
        TestDbContextFactory factory,
        CapturingLogger logger) =>
        new(
            factory,
            new FileMover(NullLogger<FileMover>.Instance, dbContextFactory: factory,
                timeProvider: TimeProvider.System),
            TimeProvider.System,
            logger);

    private async Task SeedAsync(
        TestDbContextFactory factory,
        Dictionary<Guid, FileMutationJournalState> seeded,
        int[] versions)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        var index = 0;
        foreach (var (operationId, state) in seeded)
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = operationId,
                ProtocolVersion = versions[index++ % versions.Length],
                Action = FileAction.Move,
                SourcePath = Path.Join(FileService.GetTempPath(), $"parked-{operationId:N}-source.m4b"),
                DestinationPath = Path.Join(FileService.GetTempPath(), $"parked-{operationId:N}-target.m4b"),
                SourcePhysicalObjectIdentity = "parked-source",
                TargetPhysicalObjectIdentity = "parked-target",
                SourceLength = 1,
                State = state
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task AssertParkedAndLoggedAsync(
        TestDbContextFactory factory,
        CapturingLogger logger,
        Dictionary<Guid, FileMutationJournalState> seeded)
    {
        await using var verification = await factory.CreateDbContextAsync();
        var persisted = await verification.FileMutationJournals.AsNoTracking().ToListAsync();
        Assert.Equal(seeded.Count, persisted.Count);
        Assert.All(persisted, journal => Assert.Equal(FileMutationJournalState.NeedsAttention, journal.State));

        var warnings = logger.Warnings;
        foreach (var (operationId, state) in seeded)
        {
            var warning = Assert.Single(warnings, message => message.Contains(operationId.ToString()));
            Assert.Contains(state.ToString(), warning);
        }
    }

    private sealed class CapturingLogger : ILogger<FileRegistrationRecoveryService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Warnings =>
            _entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
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
