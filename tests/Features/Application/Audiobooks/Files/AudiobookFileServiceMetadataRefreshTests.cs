using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files;

[Trait("Name", "AudiobookFileServiceMetadataRefreshTests")]
[Trait("Category", "Application")]
public sealed class AudiobookFileServiceMetadataRefreshTests : BaseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("legacy-physical-evidence")]
    public async Task RefreshMetadataAsync_ReadOnlyLease_PreservesAllOwnershipAndPhysicalEvidence(string? physicalIdentity)
    {
        // Given a valid tracked path with partial metadata and a read-only lease.
        var (audiobook, file, lease) = await CreateFixtureAsync(physicalIdentity);
        using (lease)
        {
            // When metadata is refreshed without durable-generation authority.
            var service = _provider.GetRequiredService<IAudiobookFileService>();
            Assert.True(await service.RefreshMetadataAsync(audiobook, file.Id, lease));

            // Then the ownership/physical identity snapshot is unchanged.
            var persisted = await ReloadFileAsync(file.Id);
            Assert.Equal(file.CapturePathState(), persisted.CapturePathState());
            Assert.Equal(file.PhysicalObjectIdentity, persisted.PhysicalObjectIdentity);
            Assert.Equal(file.PhysicalIdentityVersion, persisted.PhysicalIdentityVersion);
            Assert.Equal(file.PhysicalIdentityObservedAtUtc, persisted.PhysicalIdentityObservedAtUtc);
            Assert.Equal(file.Source, persisted.Source);
            Assert.Equal(file.Size, persisted.Size);
            Assert.Equal(222, persisted.DurationSeconds);
            Assert.Equal("refreshed-format", persisted.Format);
            Assert.Equal(48000, persisted.SampleRate);
            Assert.Equal("existing-codec", persisted.Codec);
            Assert.Equal(64000, persisted.Bitrate);
            Assert.Equal(2, persisted.Channels);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshPhysicalGenerationAsync(
                audiobook, file.Id, physicalIdentity, lease));
        }
    }

    [Theory]
    [InlineData("path")]
    [InlineData("base-path")]
    [InlineData("owner")]
    [InlineData("identity-state")]
    [InlineData("physical-identity")]
    [InlineData("publication")]
    [InlineData("cancellation")]
    public async Task RefreshMetadataAsync_StateChangesDuringExtraction_DoesNotApplyStaleMetadata(string mutation)
    {
        // Given extraction interrupted by a change outside the operation lock.
        using var cancellation = new CancellationTokenSource();
        var publicationMatches = true;
        Audiobook? audiobook = null;
        AudiobookFile? file = null;
        var fixture = await CreateFixtureAsync(null, async () =>
        {
            if (mutation == "publication")
            {
                publicationMatches = false;
                return;
            }
            if (mutation == "cancellation")
            {
                cancellation.Cancel();
                return;
            }
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            var row = await context.AudiobookFiles.SingleAsync(candidate => candidate.Id == file!.Id);
            switch (mutation)
            {
                case "path":
                    row.Path = Path.Join(audiobook!.BasePath!, "changed.m4b");
                    break;
                case "base-path":
                    var owner = await context.Audiobooks.SingleAsync(candidate => candidate.Id == audiobook!.Id);
                    owner.BasePath = Path.Join(audiobook!.BasePath!, "changed");
                    break;
                case "owner":
                    var other = new AudiobookBuilder().WithTitle("Other Owner").Build();
                    context.Audiobooks.Add(other);
                    await context.SaveChangesAsync();
                    row.AudiobookId = other.Id;
                    break;
                case "identity-state":
                    row.PreparePathIdentityReconciliation("Concurrent identity repair");
                    break;
                case "physical-identity":
                    row.ApplyPhysicalObjectIdentity("new-physical-evidence", DateTime.UtcNow);
                    break;
            }
            await context.SaveChangesAsync();
        }, () => publicationMatches);
        audiobook = fixture.Audiobook;
        file = fixture.File;
        using (fixture.Lease)
        {
            // When the refresh attempts to commit the extracted result.
            var service = _provider.GetRequiredService<IAudiobookFileService>();
            if (mutation == "cancellation")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    service.RefreshMetadataAsync(audiobook, file.Id, fixture.Lease, cancellation.Token));
            }
            else
            {
                Assert.False(await service.RefreshMetadataAsync(audiobook, file.Id, fixture.Lease));
            }

            // Then the newer state is preserved and old metadata remains unchanged.
            var persisted = await ReloadFileAsync(file.Id);
            Assert.Null(persisted.DurationSeconds);
            Assert.Null(persisted.Format);
            Assert.Null(persisted.SampleRate);
            Assert.Equal("existing-codec", persisted.Codec);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("legacy-physical-evidence")]
    public async Task RefreshMetadataAsync_SqliteReload_PreservesExistingPhysicalEvidence(string? physicalIdentity)
    {
        var fixture = await CreateFixtureAsync(physicalIdentity);
        using (fixture.Lease)
        await using (var connection = new SqliteConnection("Data Source=:memory:"))
        {
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connection).Options;
            await using var context = new ListenArrDbContext(options);
            await context.Database.EnsureCreatedAsync();
            context.Audiobooks.Add(fixture.Audiobook);
            context.AudiobookFiles.Add(fixture.File);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            var reloaded = await context.AudiobookFiles.SingleAsync();
            if (physicalIdentity != null)
            {
                Assert.Equal(DateTimeKind.Unspecified, reloaded.PhysicalIdentityObservedAtUtc!.Value.Kind);
            }
            var repository = new EfAudiobookFileRepository(context);
            var service = ActivatorUtilities.CreateInstance<AudiobookFileService>(_provider, repository);

            Assert.True(await service.RefreshMetadataAsync(fixture.Audiobook, reloaded.Id, fixture.Lease));

            context.ChangeTracker.Clear();
            var persisted = await context.AudiobookFiles.SingleAsync();
            Assert.Equal(222, persisted.DurationSeconds);
            Assert.Equal("refreshed-format", persisted.Format);
            Assert.Equal("existing-codec", persisted.Codec);
            Assert.Equal(64000, persisted.Bitrate);
            Assert.Equal(fixture.File.CapturePathState(), persisted.CapturePathState());
            Assert.Equal(fixture.File.PhysicalObjectIdentity, persisted.PhysicalObjectIdentity);
            Assert.Equal(fixture.File.PhysicalIdentityObservedAtUtc, persisted.PhysicalIdentityObservedAtUtc);
        }
    }

    [Theory]
    [InlineData("path")]
    [InlineData("base-path")]
    [InlineData("identity-state")]
    [InlineData("physical-identity")]
    [InlineData("deleted")]
    public async Task RefreshMetadataAsync_SqliteConcurrentChange_RejectsStaleSnapshot(string mutation)
    {
        DbContextOptions<ListenArrDbContext>? options = null;
        var fixture = await CreateFixtureAsync(null, async () =>
        {
            await using var competing = new ListenArrDbContext(options!);
            var row = await competing.AudiobookFiles.SingleAsync();
            switch (mutation)
            {
                case "path":
                    row.Path += ".changed.m4b";
                    break;
                case "base-path":
                    (await competing.Audiobooks.SingleAsync()).BasePath += "-changed";
                    break;
                case "identity-state":
                    row.PreparePathIdentityReconciliation("Concurrent repair");
                    break;
                case "physical-identity":
                    row.ApplyPhysicalObjectIdentity("new-evidence", DateTime.UtcNow);
                    break;
                case "deleted":
                    competing.AudiobookFiles.Remove(row);
                    break;
            }
            await competing.SaveChangesAsync();
        });
        using (fixture.Lease)
        await using (var connection = new SqliteConnection("Data Source=:memory:"))
        {
            await connection.OpenAsync();
            options = new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connection).Options;
            await using var context = new ListenArrDbContext(options);
            await context.Database.EnsureCreatedAsync();
            context.Audiobooks.Add(fixture.Audiobook);
            context.AudiobookFiles.Add(fixture.File);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            var service = ActivatorUtilities.CreateInstance<AudiobookFileService>(
                _provider, new EfAudiobookFileRepository(context));

            Assert.False(await service.RefreshMetadataAsync(fixture.Audiobook, fixture.File.Id, fixture.Lease));

            context.ChangeTracker.Clear();
            var persisted = await context.AudiobookFiles.SingleOrDefaultAsync();
            if (mutation == "deleted")
            {
                Assert.Null(persisted);
            }
            else
            {
                Assert.NotNull(persisted);
                Assert.Null(persisted.DurationSeconds);
                Assert.Null(persisted.Format);
                Assert.Null(persisted.SampleRate);
                Assert.Equal("existing-codec", persisted.Codec);
            }
        }
    }

    [LinuxFact]
    public async Task RefreshMetadataAsync_IdenticalContentReplacement_PreservesReplacementAndRejectsStaleRead()
    {
        string? path = null;
        string? displaced = null;
        var fixture = await CreateFixtureAsync(null, async () =>
        {
            displaced = path + ".original";
            File.Move(path!, displaced);
            await File.WriteAllTextAsync(path!, "metadata test audio");
        });
        path = fixture.Lease.PublicPath;
        fixture.Lease.Dispose();
        using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            Path.GetDirectoryName(path)!, createMissing: false);
        using var lease = PinnedAudiobookFileRegistrationLease.CreatePinnedPathOnly(
            parent.OpenExistingFileForStableRead(Path.GetFileName(path)), path);

        Assert.False(await _provider.GetRequiredService<IAudiobookFileService>()
            .RefreshMetadataAsync(fixture.Audiobook, fixture.File.Id, lease));

        Assert.Equal("metadata test audio", await File.ReadAllTextAsync(path));
        Assert.Equal("metadata test audio", await File.ReadAllTextAsync(displaced!));
        Assert.Null((await ReloadFileAsync(fixture.File.Id)).DurationSeconds);
        Assert.Null((await ReloadFileAsync(fixture.File.Id)).PhysicalObjectIdentity);
    }

    private async Task<(Audiobook Audiobook, AudiobookFile File, IAudiobookFileRegistrationLease Lease)>
        CreateFixtureAsync(string? physicalIdentity, Func<Task>? duringExtraction = null, Func<bool>? publicationMatches = null)
    {
        var metadataService = new Mock<IMetadataService>(MockBehavior.Strict);
        metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
            .Returns(async () =>
            {
                if (duringExtraction != null)
                {
                    await duringExtraction();
                }
                return new AudioMetadata
                {
                    Duration = TimeSpan.FromSeconds(222),
                    Format = "refreshed-format",
                    SampleRate = 48000
                };
            });
        Init(builder => builder.WithSingleton(metadataService.Object));
        var path = await FileService.GetFileAsync(
            FileService.GetTempDirectory("metadata-only-refresh"), "book.m4b", "metadata test audio");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Metadata Only Refresh").WithBasePath(Path.GetDirectoryName(path)!).Build());
        var identity = await _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>()
            .ResolveAsync(audiobook, path);
        Assert.Equal(PathIdentityState.Valid, identity.State);
        var file = new AudiobookFileBuilder().WithAudiobook(audiobook).WithPath(path).Build();
        file.ApplyPathIdentity(path, identity);
        file.Codec = "existing-codec";
        file.Bitrate = 64000;
        file.Channels = 2;
        if (physicalIdentity != null)
        {
            file.ApplyPhysicalObjectIdentity(physicalIdentity, DateTime.UtcNow);
        }
        file = await _audiobookFileRepository.AddAsync(file);
        var lease = new Mock<IAudiobookFileRegistrationLease>(MockBehavior.Strict);
        lease.SetupGet(value => value.PublicPath).Returns(path);
        lease.SetupGet(value => value.MetadataPath).Returns(path);
        lease.SetupGet(value => value.HasDurablePhysicalObjectIdentity).Returns(false);
        lease.Setup(value => value.MatchesCurrentPublication()).Returns(() => publicationMatches?.Invoke() ?? true);
        lease.Setup(value => value.Dispose());
        return (audiobook, file, lease.Object);
    }

    private async Task<AudiobookFile> ReloadFileAsync(int id)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        return await context.AudiobookFiles.AsNoTracking().SingleAsync(file => file.Id == id);
    }
}
