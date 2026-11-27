using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files;

// One test per return path of the guarded refresh: each asserts the reported reason and how far
// the refresh got (extractor and guarded update calls), so a path reporting another path's reason fails.
[Trait("Name", "AudiobookFileServiceMetadataRefreshResultTests")]
[Trait("Category", "Application")]
public sealed class AudiobookFileServiceMetadataRefreshResultTests : BaseTests
{
    [Fact]
    public async Task RefreshMetadataWithResultAsync_AudiobookMissing_ReportsRowGone()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        var missing = new AudiobookBuilder().WithTitle("The Time Machine")
            .WithBasePath(fixture.Audiobook.BasePath!).Build();

        var result = await fixture.Service.RefreshMetadataWithResultAsync(missing, fixture.File.Id, fixture.Lease.Object);

        Assert.Equal(MetadataRefreshResult.RowGone, result);
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_FileMissing_ReportsRowGone()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        await fixture.ChangeRowAsync((context, row) => context.AudiobookFiles.Remove(row));

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.RowGone, result);
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_FileOwnedByAnotherAudiobook_ReportsRowGone()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        var other = new AudiobookBuilder().WithTitle("Treasure Island").WithBasePath(fixture.Audiobook.BasePath!).Build();
        // A fresh context: the shared one tracks the fixture graph, whose file row exists only in SQLite.
        await using (var context = await _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            context.Audiobooks.Add(other);
            await context.SaveChangesAsync();
        }

        var result = await fixture.Service.RefreshMetadataWithResultAsync(other, fixture.File.Id, fixture.Lease.Object);

        Assert.Equal(MetadataRefreshResult.RowGone, result);
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_BlankStoredPath_ReportsGuardRejected()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        await fixture.ChangeRowAsync((_, row) => row.Path = "   ");

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.GuardRejected, result);
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_StoredPathIdentityNotValid_ReportsGuardRejected()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        fixture.Resolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<Audiobook>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (Audiobook audiobook, string path, CancellationToken token) =>
                (await fixture.RealResolver.ResolveAsync(audiobook, path, token)) with
                {
                    State = PathIdentityState.Unavailable
                });

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.GuardRejected, result);
        fixture.Repository.Verify(repository => repository.CheckOwnershipAsync(
            It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<AudiobookFilePathIdentity>(), It.IsAny<CancellationToken>()),
            Times.Never);
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Theory]
    [InlineData("ownership")]
    [InlineData("reconcile")]
    public async Task RefreshMetadataWithResultAsync_OwnershipOrReconcileRefused_ReportsGuardRejected(string refusal)
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        if (refusal == "ownership")
        {
            fixture.Repository.Setup(repository => repository.CheckOwnershipAsync(
                    It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<AudiobookFilePathIdentity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook));
        }
        else
        {
            fixture.Repository.Setup(repository => repository.ReconcilePathIdentityAsync(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<AudiobookFilePathState>(), It.IsAny<string>(),
                    It.IsAny<AudiobookFilePathIdentity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        }

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.GuardRejected, result);
        fixture.Repository.Verify(repository => repository.GetByIdAsync(fixture.File.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_RowDeletedBeforeFreshRead_ReportsRowGone()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        fixture.ChangeRowAfterReconcile((context, row) => context.AudiobookFiles.Remove(row));

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.RowGone, result);
        fixture.Repository.Verify(repository => repository.GetByIdAsync(fixture.File.Id, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_FreshReadIdentityNotValid_ReportsGuardRejected()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        fixture.ChangeRowAfterReconcile((_, row) => row.PreparePathIdentityReconciliation("Concurrent repair"));

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.GuardRejected, result);
        fixture.Repository.Verify(repository => repository.GetByIdAsync(fixture.File.Id, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_OwnershipRefusedBeforeExtraction_ReportsGuardRejected()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        fixture.Lease.Setup(lease => lease.MatchesCurrentPublication()).Returns(false);

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.GuardRejected, result);
        fixture.Lease.Verify(lease => lease.MatchesCurrentPublication(), Times.Once);
        fixture.VerifyStoppedBeforeExtraction();
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_ExtractorReturnsNoMetadata_ReportsNoMetadata()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this, returnMetadata: false);

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.NoMetadata, result);
        fixture.VerifyExtractedWithoutGuardedUpdate();
        Assert.Null((await fixture.ReloadAsync())!.DurationSeconds);
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_OwnershipRefusedAfterExtraction_ReportsGuardRejected()
    {
        var publicationMatches = true;
        await using var fixture = await RefreshFixture.CreateAsync(this, duringExtraction: () => publicationMatches = false);
        fixture.Lease.Setup(lease => lease.MatchesCurrentPublication()).Returns(() => publicationMatches);

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.GuardRejected, result);
        fixture.VerifyExtractedWithoutGuardedUpdate();
        Assert.Null((await fixture.ReloadAsync())!.DurationSeconds);
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_GuardedUpdateMatchesNoRow_ReportsGuardRejected()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);
        fixture.Repository.Setup(repository => repository.RefreshMetadataAsync(
                It.IsAny<AudiobookFileMetadataRefreshSnapshot>(), It.IsAny<AudioMetadata>(), It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (AudiobookFileMetadataRefreshSnapshot expected, AudioMetadata metadata, long? size, CancellationToken token) =>
            {
                await fixture.ChangeRowAsync((_, row) => row.Path += ".changed.m4b");
                return await fixture.RealRepository.RefreshMetadataAsync(expected, metadata, size, token);
            });

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.GuardRejected, result);
        fixture.VerifyGuardedUpdateCalled();
        Assert.Null((await fixture.ReloadAsync())!.DurationSeconds);
    }

    [Fact]
    public async Task RefreshMetadataWithResultAsync_GuardedUpdateWritesRow_ReportsUpdated()
    {
        await using var fixture = await RefreshFixture.CreateAsync(this);

        var result = await fixture.RefreshAsync();

        Assert.Equal(MetadataRefreshResult.Updated, result);
        fixture.VerifyGuardedUpdateCalled();
        var persisted = await fixture.ReloadAsync();
        Assert.Equal(222, persisted!.DurationSeconds);
        Assert.Equal("refreshed-format", persisted.Format);
    }

    // A SQLite-backed service whose repository and resolver are strict forwarding mocks, so a test
    // can refuse or interleave one call while every other call reaches the real implementation.
    private sealed class RefreshFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ListenArrDbContext> _options;
        private readonly ListenArrDbContext _context;

        private RefreshFixture(
            SqliteConnection connection,
            DbContextOptions<ListenArrDbContext> options,
            ListenArrDbContext context)
        {
            _connection = connection;
            _options = options;
            _context = context;
            RealRepository = new EfAudiobookFileRepository(context);
        }

        public Audiobook Audiobook { get; private set; } = null!;
        public AudiobookFile File { get; private set; } = null!;
        public IAudiobookFileService Service { get; private set; } = null!;
        public EfAudiobookFileRepository RealRepository { get; }
        public IAudiobookFilePathIdentityResolver RealResolver { get; private set; } = null!;
        public Mock<IAudiobookFileRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IAudiobookFilePathIdentityResolver> Resolver { get; } = new(MockBehavior.Strict);
        public Mock<IAudiobookFileRegistrationLease> Lease { get; } = new(MockBehavior.Strict);
        public Mock<IMetadataService> Extractor { get; } = new(MockBehavior.Strict);

        public static async Task<RefreshFixture> CreateAsync(
            AudiobookFileServiceMetadataRefreshResultTests test,
            bool returnMetadata = true,
            Action? duringExtraction = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connection).Options;
            var context = new ListenArrDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var fixture = new RefreshFixture(connection, options, context);
            await fixture.InitializeAsync(test, returnMetadata, duringExtraction);
            return fixture;
        }

        public Task<MetadataRefreshResult> RefreshAsync()
        {
            return Service.RefreshMetadataWithResultAsync(Audiobook, File.Id, Lease.Object);
        }

        public async Task ChangeRowAsync(Action<ListenArrDbContext, AudiobookFile> change)
        {
            await using var competing = new ListenArrDbContext(_options);
            change(competing, await competing.AudiobookFiles.SingleAsync(row => row.Id == File.Id));
            await competing.SaveChangesAsync();
        }

        public void ChangeRowAfterReconcile(Action<ListenArrDbContext, AudiobookFile> change)
        {
            Repository.Setup(repository => repository.ReconcilePathIdentityAsync(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<AudiobookFilePathState>(), It.IsAny<string>(),
                    It.IsAny<AudiobookFilePathIdentity>(), It.IsAny<CancellationToken>()))
                .Returns(async (int fileId, int audiobookId, AudiobookFilePathState state, string path,
                    AudiobookFilePathIdentity identity, CancellationToken token) =>
                {
                    var reconciled = await RealRepository.ReconcilePathIdentityAsync(
                        fileId, audiobookId, state, path, identity, token);
                    _context.ChangeTracker.Clear();
                    await ChangeRowAsync(change);
                    return reconciled;
                });
        }

        public async Task<AudiobookFile?> ReloadAsync()
        {
            await using var reader = new ListenArrDbContext(_options);
            return await reader.AudiobookFiles.AsNoTracking().SingleOrDefaultAsync(row => row.Id == File.Id);
        }

        public void VerifyStoppedBeforeExtraction()
        {
            Extractor.Verify(extractor => extractor.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()), Times.Never);
            VerifyGuardedUpdateCalled(Times.Never());
        }

        public void VerifyExtractedWithoutGuardedUpdate()
        {
            Extractor.Verify(extractor => extractor.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()), Times.Once);
            VerifyGuardedUpdateCalled(Times.Never());
        }

        public void VerifyGuardedUpdateCalled(Times? times = null)
        {
            Repository.Verify(repository => repository.RefreshMetadataAsync(
                    It.IsAny<AudiobookFileMetadataRefreshSnapshot>(), It.IsAny<AudioMetadata>(), It.IsAny<long?>(),
                    It.IsAny<CancellationToken>()),
                times ?? Times.Once());
        }

        public async ValueTask DisposeAsync()
        {
            Lease.Object.Dispose();
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private async Task InitializeAsync(
            AudiobookFileServiceMetadataRefreshResultTests test,
            bool returnMetadata,
            Action? duringExtraction)
        {
            Extractor.Setup(extractor => extractor.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
                .ReturnsAsync(() =>
                {
                    duringExtraction?.Invoke();
                    return returnMetadata
                        ? new AudioMetadata { Duration = TimeSpan.FromSeconds(222), Format = "refreshed-format" }
                        : null;
                });
            test.Init(builder => builder.WithSingleton(Extractor.Object));
            var provider = test._provider;
            var path = await test.FileService.GetFileAsync(
                test.FileService.GetTempDirectory("metadata-refresh-result"), "book.m4b", "metadata test audio");
            Audiobook = await test._audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A Princess of Mars").WithBasePath(Path.GetDirectoryName(path)!).Build());
            RealResolver = provider.GetRequiredService<IAudiobookFilePathIdentityResolver>();
            var identity = await RealResolver.ResolveAsync(Audiobook, path);
            Assert.Equal(PathIdentityState.Valid, identity.State);
            File = new AudiobookFileBuilder().WithAudiobook(Audiobook).WithPath(path).Build();
            File.ApplyPathIdentity(path, identity);
            _context.Audiobooks.Add(Audiobook);
            _context.AudiobookFiles.Add(File);
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            ForwardToRealImplementations();
            Lease.SetupGet(lease => lease.PublicPath).Returns(path);
            Lease.SetupGet(lease => lease.MetadataPath).Returns(path);
            Lease.Setup(lease => lease.MatchesCurrentPublication()).Returns(true);
            Lease.Setup(lease => lease.Dispose());
            Service = ActivatorUtilities.CreateInstance<AudiobookFileService>(
                provider, Repository.Object, Resolver.Object);
        }

        private void ForwardToRealImplementations()
        {
            Resolver.Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<Audiobook>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Audiobook audiobook, string path, CancellationToken token) =>
                    RealResolver.ResolveAsync(audiobook, path, token));
            Repository.Setup(repository => repository.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int id, CancellationToken token) => RealRepository.GetByIdAsync(id, token));
            Repository.Setup(repository => repository.CheckOwnershipAsync(
                    It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<AudiobookFilePathIdentity>(), It.IsAny<CancellationToken>()))
                .Returns((int audiobookId, int? fileId, AudiobookFilePathIdentity identity, CancellationToken token) =>
                    RealRepository.CheckOwnershipAsync(audiobookId, fileId, identity, token));
            Repository.Setup(repository => repository.ReconcilePathIdentityAsync(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<AudiobookFilePathState>(), It.IsAny<string>(),
                    It.IsAny<AudiobookFilePathIdentity>(), It.IsAny<CancellationToken>()))
                .Returns((int fileId, int audiobookId, AudiobookFilePathState state, string path,
                    AudiobookFilePathIdentity identity, CancellationToken token) =>
                    RealRepository.ReconcilePathIdentityAsync(fileId, audiobookId, state, path, identity, token));
            Repository.Setup(repository => repository.RefreshMetadataAsync(
                    It.IsAny<AudiobookFileMetadataRefreshSnapshot>(), It.IsAny<AudioMetadata>(), It.IsAny<long?>(),
                    It.IsAny<CancellationToken>()))
                .Returns((AudiobookFileMetadataRefreshSnapshot expected, AudioMetadata metadata, long? size,
                    CancellationToken token) => RealRepository.RefreshMetadataAsync(expected, metadata, size, token));
        }
    }
}
