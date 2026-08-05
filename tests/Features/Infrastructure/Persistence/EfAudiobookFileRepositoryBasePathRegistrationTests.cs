using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "EfAudiobookFileRepositoryBasePathRegistrationTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfAudiobookFileRepositoryBasePathRegistrationTests : BaseTests
{
    [Fact]
    public async Task ReplacePhysicalGenerationAsync_CancelledAtMutationCommand_CommitsUnambiguously()
    {
        await using var connection = await OpenDatabaseAsync();
        var interceptor = new CancelDuringMutationCommandInterceptor();
        var options = CreateOptions(connection, interceptor);
        var boundary = Path.GetFullPath(Path.Join("library", "PhysicalGeneration"));
        var filePath = Path.Join(boundary, "Book.m4b");
        await SeedAudiobooksAsync(options, new Audiobook { Id = 1, Title = "Book", BasePath = boundary });
        AudiobookFile persisted;
        await using (var seed = new ListenArrDbContext(options))
        {
            persisted = CreateFile(1, filePath, boundary, "generation-one");
            seed.AudiobookFiles.Add(persisted);
            await seed.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        interceptor.Arm(cancellation);
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);

        var updated = await repository.ReplacePhysicalGenerationAsync(
            persisted.Id,
            1,
            filePath,
            "generation-one",
            CreateFile(1, filePath, boundary, "generation-two"),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(updated);
        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            "generation-two",
            (await verification.AudiobookFiles.AsNoTracking().SingleAsync()).PhysicalObjectIdentity);
    }

    [Fact]
    public async Task DeletePhysicalGenerationAsync_CancelledAtMutationCommand_CommitsUnambiguously()
    {
        await using var connection = await OpenDatabaseAsync();
        var interceptor = new CancelDuringMutationCommandInterceptor();
        var options = CreateOptions(connection, interceptor);
        var boundary = Path.GetFullPath(Path.Join("library", "PhysicalGenerationDelete"));
        var filePath = Path.Join(boundary, "Book.m4b");
        await SeedAudiobooksAsync(options, new Audiobook { Id = 1, Title = "Book", BasePath = boundary });
        AudiobookFile persisted;
        await using (var seed = new ListenArrDbContext(options))
        {
            persisted = CreateFile(1, filePath, boundary, "generation-one");
            seed.AudiobookFiles.Add(persisted);
            await seed.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        interceptor.Arm(cancellation);
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);

        var deleted = await repository.DeletePhysicalGenerationAsync(
            persisted.Id,
            1,
            filePath,
            "generation-one",
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(deleted);
        await using var verification = new ListenArrDbContext(options);
        Assert.Empty(await verification.AudiobookFiles.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ApplyBasePathAsync_CancelledAtMutationCommand_CommitsUnambiguously()
    {
        await using var connection = await OpenDatabaseAsync();
        var interceptor = new CancelDuringMutationCommandInterceptor();
        var options = CreateOptions(connection, interceptor);
        var originalBasePath = Path.GetFullPath(Path.Join("library", "ApplyBaseOriginal"));
        var destination = Path.GetFullPath(Path.Join("library", "ApplyBaseDestination"));
        await SeedAudiobooksAsync(
            options,
            new Audiobook { Id = 1, Title = "Book", BasePath = originalBasePath });
        using var cancellation = new CancellationTokenSource();
        interceptor.Arm(cancellation);
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);

        var applied = await repository.ApplyBasePathAsync(
            new AudiobookBasePathMutation(1, originalBasePath, destination),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(applied);
        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            destination,
            (await verification.Audiobooks.AsNoTracking().SingleAsync()).BasePath);
    }

    [Fact]
    public async Task ClaimWithBasePathAsync_CommitsFileAndBasePathTogether()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = CreateOptions(connection);
        var destination = Path.GetFullPath(Path.Join("library", "Author", "Book"));
        var filePath = Path.Join(destination, "Book.m4b");
        await SeedAudiobooksAsync(options, new Audiobook { Id = 1, Title = "Book" });
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);
        var file = CreateFile(1, filePath, destination, "generation-one");

        var result = await repository.ClaimWithBasePathAsync(
            file,
            new AudiobookBasePathMutation(1, null, destination));

        Assert.True(result.Created, result.Reason);
        await using var verification = new ListenArrDbContext(options);
        var persistedAudiobook = await verification.Audiobooks.AsNoTracking().SingleAsync();
        var persistedFile = await verification.AudiobookFiles.AsNoTracking().SingleAsync();
        Assert.Equal(destination, persistedAudiobook.BasePath);
        Assert.Equal(filePath, persistedFile.Path);
        Assert.Equal("generation-one", persistedFile.PhysicalObjectIdentity);
    }

    [Fact]
    public async Task ClaimWithBasePathAsync_CancelledDuringCommit_CommitsUnambiguously()
    {
        await using var connection = await OpenDatabaseAsync();
        var interceptor = new CancelDuringCommitInterceptor();
        var options = CreateOptions(connection, interceptor);
        var destination = Path.GetFullPath(Path.Join("library", "CommitCancel", "Book"));
        var filePath = Path.Join(destination, "Book.m4b");
        await SeedAudiobooksAsync(options, new Audiobook { Id = 1, Title = "Book" });
        using var cancellation = new CancellationTokenSource();
        interceptor.Arm(cancellation);
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);

        var result = await repository.ClaimWithBasePathAsync(
            CreateFile(1, filePath, destination, "generation-one"),
            new AudiobookBasePathMutation(1, null, destination),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Created, result.Reason);
        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(destination, (await verification.Audiobooks.AsNoTracking().SingleAsync()).BasePath);
        Assert.Equal(
            "generation-one",
            (await verification.AudiobookFiles.AsNoTracking().SingleAsync()).PhysicalObjectIdentity);
    }

    [Fact]
    public async Task ClaimWithBasePathAsync_OwnershipConflict_PreservesBasePath()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = CreateOptions(connection);
        var originalBasePath = Path.GetFullPath(Path.Join("library", "Original"));
        var destination = Path.GetFullPath(Path.Join("library", "Destination"));
        var filePath = Path.Join(destination, "Book.m4b");
        await SeedAudiobooksAsync(
            options,
            new Audiobook { Id = 1, Title = "First", BasePath = originalBasePath },
            new Audiobook { Id = 2, Title = "Second", BasePath = destination });
        await using (var seed = new ListenArrDbContext(options))
        {
            seed.AudiobookFiles.Add(CreateFile(2, filePath, destination, "owner-generation"));
            await seed.SaveChangesAsync();
        }
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);

        var result = await repository.ClaimWithBasePathAsync(
            CreateFile(1, filePath, destination, "candidate-generation"),
            new AudiobookBasePathMutation(1, originalBasePath, destination));

        Assert.Equal(AudiobookFileClaimOutcome.OwnedByOtherAudiobook, result.Outcome);
        await using var verification = new ListenArrDbContext(options);
        var first = await verification.Audiobooks.AsNoTracking().SingleAsync(book => book.Id == 1);
        Assert.Equal(originalBasePath, first.BasePath);
        Assert.Single(await verification.AudiobookFiles.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ReplacePhysicalGenerationWithBasePathAsync_FileConflict_RollsBackBasePath()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = CreateOptions(connection);
        var originalBasePath = Path.GetFullPath(Path.Join("library", "Original"));
        var destination = Path.GetFullPath(Path.Join("library", "Destination"));
        var filePath = Path.Join(destination, "Book.m4b");
        await SeedAudiobooksAsync(
            options,
            new Audiobook { Id = 1, Title = "Book", BasePath = originalBasePath });
        AudiobookFile persisted;
        await using (var seed = new ListenArrDbContext(options))
        {
            persisted = CreateFile(1, filePath, destination, "generation-one");
            seed.AudiobookFiles.Add(persisted);
            await seed.SaveChangesAsync();
        }
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);
        var replacement = CreateFile(1, filePath, destination, "generation-two");

        var updated = await repository.ReplacePhysicalGenerationWithBasePathAsync(
            persisted.Id,
            1,
            filePath,
            expectedPhysicalObjectIdentity: "wrong-generation",
            replacement,
            new AudiobookBasePathMutation(1, originalBasePath, destination));

        Assert.False(updated);
        await using var verification = new ListenArrDbContext(options);
        var audiobook = await verification.Audiobooks.AsNoTracking().SingleAsync();
        var file = await verification.AudiobookFiles.AsNoTracking().SingleAsync();
        Assert.Equal(originalBasePath, audiobook.BasePath);
        Assert.Equal("generation-one", file.PhysicalObjectIdentity);
    }

    [Fact]
    public async Task ReplacePhysicalGenerationWithBasePathAsync_CancelledDuringCommit_CommitsUnambiguously()
    {
        await using var connection = await OpenDatabaseAsync();
        var interceptor = new CancelDuringCommitInterceptor();
        var options = CreateOptions(connection, interceptor);
        var originalBasePath = Path.GetFullPath(Path.Join("library", "CommitCancelOriginal"));
        var destination = Path.GetFullPath(Path.Join("library", "CommitCancelDestination"));
        var filePath = Path.Join(destination, "Book.m4b");
        await SeedAudiobooksAsync(
            options,
            new Audiobook { Id = 1, Title = "Book", BasePath = originalBasePath });
        AudiobookFile persisted;
        await using (var seed = new ListenArrDbContext(options))
        {
            persisted = CreateFile(1, filePath, destination, "generation-one");
            seed.AudiobookFiles.Add(persisted);
            await seed.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        interceptor.Arm(cancellation);
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);

        var updated = await repository.ReplacePhysicalGenerationWithBasePathAsync(
            persisted.Id,
            1,
            filePath,
            "generation-one",
            CreateFile(1, filePath, destination, "generation-two"),
            new AudiobookBasePathMutation(1, originalBasePath, destination),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(updated);
        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(destination, (await verification.Audiobooks.AsNoTracking().SingleAsync()).BasePath);
        Assert.Equal(
            "generation-two",
            (await verification.AudiobookFiles.AsNoTracking().SingleAsync()).PhysicalObjectIdentity);
    }

    [Fact]
    public async Task DeletePhysicalGenerationWithBasePathAsync_CancelledDuringCommit_CommitsUnambiguously()
    {
        await using var connection = await OpenDatabaseAsync();
        var interceptor = new CancelDuringCommitInterceptor();
        var options = CreateOptions(connection, interceptor);
        var destination = Path.GetFullPath(Path.Join("library", "CommitCancelDeleteDestination"));
        var previousBasePath = Path.GetFullPath(Path.Join("library", "CommitCancelDeletePrevious"));
        var filePath = Path.Join(destination, "Book.m4b");
        await SeedAudiobooksAsync(
            options,
            new Audiobook { Id = 1, Title = "Book", BasePath = destination });
        AudiobookFile persisted;
        await using (var seed = new ListenArrDbContext(options))
        {
            persisted = CreateFile(1, filePath, destination, "generation-one");
            seed.AudiobookFiles.Add(persisted);
            await seed.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        interceptor.Arm(cancellation);
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);

        var deleted = await repository.DeletePhysicalGenerationWithBasePathAsync(
            persisted.Id,
            1,
            filePath,
            "generation-one",
            new AudiobookBasePathMutation(1, destination, previousBasePath),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(deleted);
        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(previousBasePath, (await verification.Audiobooks.AsNoTracking().SingleAsync()).BasePath);
        Assert.Empty(await verification.AudiobookFiles.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DeletePhysicalGenerationWithBasePathAsync_RestoresBothClaims()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = CreateOptions(connection);
        var previousBasePath = Path.GetFullPath(Path.Join("library", "Previous"));
        var destination = Path.GetFullPath(Path.Join("library", "Destination"));
        var filePath = Path.Join(destination, "Book.m4b");
        await SeedAudiobooksAsync(
            options,
            new Audiobook { Id = 1, Title = "Book", BasePath = destination });
        AudiobookFile persisted;
        await using (var seed = new ListenArrDbContext(options))
        {
            persisted = CreateFile(1, filePath, destination, "generation-one");
            seed.AudiobookFiles.Add(persisted);
            await seed.SaveChangesAsync();
        }
        await using var context = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(context);

        var deleted = await repository.DeletePhysicalGenerationWithBasePathAsync(
            persisted.Id,
            1,
            filePath,
            "generation-one",
            new AudiobookBasePathMutation(1, destination, previousBasePath));

        Assert.True(deleted);
        await using var verification = new ListenArrDbContext(options);
        var audiobook = await verification.Audiobooks.AsNoTracking().SingleAsync();
        Assert.Equal(previousBasePath, audiobook.BasePath);
        Assert.Empty(await verification.AudiobookFiles.AsNoTracking().ToListAsync());
    }

    private static AudiobookFile CreateFile(
        int audiobookId,
        string filePath,
        string boundary,
        string physicalIdentity)
    {
        var file = AudiobookFile.CreateUnresolved(filePath);
        file.AudiobookId = audiobookId;
        file.ApplyPathIdentity(
            filePath,
            AudiobookFilePathIdentity.CreateValid(
                filePath,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemCaseSensitivityMode.Auto,
                boundary));
        file.ApplyPhysicalObjectIdentity(physicalIdentity, DateTime.UtcNow);
        return file;
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

    private sealed class CancelDuringMutationCommandInterceptor : DbCommandInterceptor
    {
        private CancellationTokenSource? _cancellation;

        public void Arm(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _cancellation, null)?.Cancel();
            return ValueTask.FromResult(result);
        }
    }

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

    private static async Task SeedAudiobooksAsync(
        DbContextOptions<ListenArrDbContext> options,
        params Audiobook[] audiobooks)
    {
        await using var context = new ListenArrDbContext(options);
        context.Audiobooks.AddRange(audiobooks);
        await context.SaveChangesAsync();
    }
}
