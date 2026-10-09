using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Jobs;

[Trait("Name", "MetadataRescanWeakStorageTests")]
[Trait("Category", "Infrastructure")]
public sealed class MetadataRescanWeakStorageTests : BaseTests
{
    [NativeWeakStorageFact]
    public async Task RunCycleAsync_WeakStorage_RefreshesMetadataWithoutGrantingPhysicalIdentity()
    {
        // Given a real weak mounted filesystem and a path-only scan registration.
        var metadataService = new Mock<IMetadataService>();
        metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
            .ReturnsAsync(new AudioMetadata
            {
                Duration = TimeSpan.FromSeconds(222),
                Format = "m4b",
                SampleRate = 48000
            });
        Init(builder => builder.WithSingleton(metadataService.Object));
        var mountPath = Environment.GetEnvironmentVariable(
            NativeStorageIdentityFactAttribute.PathEnvironmentVariable)!;
        var folder = Path.Join(mountPath, "metadata-rescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, "book.m4b");
        try
        {
            await File.WriteAllTextAsync(path, "original metadata source");
            Assert.Throws<PlatformNotSupportedException>(() =>
                PinnedAudiobookFileRegistrationLease.Open(path));
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Weak Metadata Rescan")
                .WithBasePath(folder)
                .Build());
            var identity = await _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>()
                .ResolveAsync(audiobook, path);
            Assert.Equal(PathIdentityState.Valid, identity.State);
            var pending = new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(path)
                .Build();
            pending.ApplyPathIdentity(path, identity);
            pending.ClearPhysicalObjectIdentity();
            var file = await _audiobookFileRepository.AddAsync(pending);
            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);

            // When the real processor runs after path-only registration.
            await processor.RunCycleAsync(CancellationToken.None);

            // Then only metadata changes; the read cannot enroll a physical generation.
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.AudiobookFiles.SingleAsync(row => row.Id == file.Id);
            Assert.Equal(222, persisted.DurationSeconds);
            Assert.Equal("m4b", persisted.Format);
            Assert.Equal(48000, persisted.SampleRate);
            Assert.Equal(file.PathOwnershipKey, persisted.PathOwnershipKey);
            Assert.Equal(file.PathIdentityLookupKey, persisted.PathIdentityLookupKey);
            Assert.Null(persisted.PhysicalObjectIdentity);
            Assert.Null(persisted.PhysicalIdentityObservedAtUtc);
            Assert.Equal(file.PhysicalIdentityVersion, persisted.PhysicalIdentityVersion);
            metadataService.Verify(service => service.ExtractFileMetadataAsync(
                It.Is<MetadataFileSource>(source => source.ReadPath.StartsWith("/proc/"))), Times.Once);
            Assert.Equal("original metadata source", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(folder);
        }
    }
}
