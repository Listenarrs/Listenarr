using Listenarr.Application.Common.Exceptions;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "EfRootFolderRepositoryDefaultTransactionTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfRootFolderRepositoryDefaultTransactionTests : BaseTests
{
    [Fact]
    public async Task AddAndSetDefaultAsync_RejectedInsert_PreservesExistingDefault()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = CreateOptions(connection);
        await SeedAsync(
            options,
            new RootFolder { Id = 1, Name = "Current", Path = Path.GetFullPath("current"), IsDefault = true },
            new RootFolder { Id = 2, Name = "Conflict", Path = Path.GetFullPath("conflict") });
        var repository = CreateRepository(options);

        await Assert.ThrowsAnyAsync<Exception>(() => repository.AddAndSetDefaultAsync(
            new RootFolder
            {
                Name = "Replacement",
                Path = Path.GetFullPath("conflict"),
                IsDefault = true
            },
            expectedCurrentDefaultId: 1));

        await using var verification = new ListenArrDbContext(options);
        var roots = await verification.RootFolders.AsNoTracking().OrderBy(root => root.Id).ToListAsync();
        Assert.Equal(2, roots.Count);
        Assert.True(roots.Single(root => root.Id == 1).IsDefault);
        Assert.False(roots.Single(root => root.Id == 2).IsDefault);
    }

    [Fact]
    public async Task UpdateAndSetDefaultAsync_RejectedUpdate_PreservesExistingDefault()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = CreateOptions(connection);
        await SeedAsync(
            options,
            new RootFolder { Id = 1, Name = "Current", Path = Path.GetFullPath("current"), IsDefault = true },
            new RootFolder { Id = 2, Name = "Candidate", Path = Path.GetFullPath("candidate") });
        var repository = CreateRepository(options);

        await Assert.ThrowsAnyAsync<Exception>(() => repository.UpdateAndSetDefaultAsync(
            new RootFolder
            {
                Id = 2,
                Name = "Candidate",
                Path = Path.GetFullPath("current"),
                IsDefault = true
            },
            expectedCurrentDefaultId: 1));

        await using var verification = new ListenArrDbContext(options);
        var roots = await verification.RootFolders.AsNoTracking().OrderBy(root => root.Id).ToListAsync();
        Assert.True(roots.Single(root => root.Id == 1).IsDefault);
        Assert.False(roots.Single(root => root.Id == 2).IsDefault);
        Assert.Equal(Path.GetFullPath("candidate"), roots.Single(root => root.Id == 2).Path);
    }

    [Fact]
    public async Task AddAndSetDefaultAsync_CancelledDuringCommit_CommitsUnambiguously()
    {
        await using var connection = await OpenDatabaseAsync();
        var interceptor = new CancelDuringCommitInterceptor();
        var options = CreateOptions(connection, interceptor);
        await SeedAsync(
            options,
            new RootFolder
            {
                Id = 1,
                Name = "Current",
                Path = Path.GetFullPath("commit-cancel-current"),
                IsDefault = true
            });
        using var cancellation = new CancellationTokenSource();
        interceptor.Arm(cancellation);
        var repository = CreateRepository(options);
        var replacementPath = Path.GetFullPath("commit-cancel-replacement");

        await repository.AddAndSetDefaultAsync(
            new RootFolder
            {
                Name = "Replacement",
                Path = replacementPath,
                IsDefault = true
            },
            expectedCurrentDefaultId: 1,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        await using var verification = new ListenArrDbContext(options);
        var roots = await verification.RootFolders.AsNoTracking().ToListAsync();
        Assert.Equal(2, roots.Count);
        Assert.False(roots.Single(root => root.Id == 1).IsDefault);
        Assert.True(roots.Single(root => root.Path == replacementPath).IsDefault);
    }

    [Fact]
    public async Task AddAndSetDefaultAsync_StaleExpectedDefault_IsRejectedWithoutMutation()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = CreateOptions(connection);
        await SeedAsync(
            options,
            new RootFolder { Id = 1, Name = "Current", Path = Path.GetFullPath("current"), IsDefault = true });
        var repository = CreateRepository(options);

        await Assert.ThrowsAsync<ApplicationConflictException>(() => repository.AddAndSetDefaultAsync(
            new RootFolder
            {
                Name = "Replacement",
                Path = Path.GetFullPath("replacement"),
                IsDefault = true
            },
            expectedCurrentDefaultId: null));

        await using var verification = new ListenArrDbContext(options);
        var roots = await verification.RootFolders.AsNoTracking().ToListAsync();
        Assert.Single(roots);
        Assert.True(roots[0].IsDefault);
    }

    [Fact]
    public async Task UpdateAndSetDefaultAsync_ConcurrentSwitches_CommitExactlyOneWinner()
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"root-default-concurrency-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(
                    $"Data Source={databasePath};Pooling=False;Default Timeout=10")
                .Options;
            await using (var setup = new ListenArrDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.RootFolders.AddRange(
                    new RootFolder
                    {
                        Id = 1,
                        Name = "Current",
                        Path = Path.GetFullPath("concurrent-current"),
                        IsDefault = true
                    },
                    new RootFolder
                    {
                        Id = 2,
                        Name = "First",
                        Path = Path.GetFullPath("concurrent-first")
                    },
                    new RootFolder
                    {
                        Id = 3,
                        Name = "Second",
                        Path = Path.GetFullPath("concurrent-second")
                    });
                await setup.SaveChangesAsync();
            }

            var firstRepository = CreateRepository(options);
            var secondRepository = CreateRepository(options);
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(async () =>
            {
                await start.Task;
                return await Record.ExceptionAsync(() =>
                    firstRepository.UpdateAndSetDefaultAsync(
                        new RootFolder
                        {
                            Id = 2,
                            Name = "First",
                            Path = Path.GetFullPath("concurrent-first"),
                            IsDefault = true
                        },
                        expectedCurrentDefaultId: 1));
            });
            var second = Task.Run(async () =>
            {
                await start.Task;
                return await Record.ExceptionAsync(() =>
                    secondRepository.UpdateAndSetDefaultAsync(
                        new RootFolder
                        {
                            Id = 3,
                            Name = "Second",
                            Path = Path.GetFullPath("concurrent-second"),
                            IsDefault = true
                        },
                        expectedCurrentDefaultId: 1));
            });

            start.SetResult();
            var outcomes = await Task.WhenAll(first, second);

            Assert.Single(outcomes, exception => exception == null);
            Assert.Single(outcomes, exception =>
                exception is ApplicationConflictException);
            await using var verification = new ListenArrDbContext(options);
            var roots = await verification.RootFolders
                .AsNoTracking()
                .OrderBy(root => root.Id)
                .ToListAsync();
            Assert.False(roots.Single(root => root.Id == 1).IsDefault);
            var winningCandidates = roots
                .Where(root => root.Id is 2 or 3 && root.IsDefault)
                .ToList();
            Assert.Single(winningCandidates);
            Assert.Single(roots, root => root.IsDefault);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new ListenArrDbContext(CreateOptions(connection));
        await context.Database.EnsureCreatedAsync();
        return connection;
    }

    private static DbContextOptions<ListenArrDbContext> CreateOptions(
        SqliteConnection connection,
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection);
        if (interceptor != null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private static async Task SeedAsync(
        DbContextOptions<ListenArrDbContext> options,
        params RootFolder[] roots)
    {
        await using var context = new ListenArrDbContext(options);
        context.RootFolders.AddRange(roots);
        await context.SaveChangesAsync();
    }

    private static EfRootFolderRepository CreateRepository(
        DbContextOptions<ListenArrDbContext> options) =>
        new(
            new TestDbContextFactory(options),
            Mock.Of<ILogger<EfRootFolderRepository>>());

    private sealed class CancelDuringCommitInterceptor : IDbTransactionInterceptor
    {
        private CancellationTokenSource? _cancellation;

        public void Arm(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _cancellation, null)?.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListenArrDbContext(options));
    }
}
