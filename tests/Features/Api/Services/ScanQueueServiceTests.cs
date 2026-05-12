using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "ScanQueueServiceTests")]
    [Trait("Category", "ScanQueueService")]
    public class ScanQueueServiceTests
    {
        [Fact]
        public async Task EnqueueScanAsync_RoundTripsForceMetadataRefreshFlag()
        {
            var svc = new ScanQueueService(NullLogger<ScanQueueService>.Instance);
            var ab = new Audiobook { Id = 7, Title = "Test" };

            var jobId = await svc.EnqueueScanAsync(ab, path: null, forceMetadataRefresh: true);

            Assert.True(svc.TryGetJob(jobId, out var job));
            Assert.NotNull(job);
            Assert.True(job!.ForceMetadataRefresh);
        }

        [Fact]
        public async Task EnqueueScanAsync_NormalScan_DoesNotDedupeAgainstForceRefresh()
        {
            var svc = new ScanQueueService(NullLogger<ScanQueueService>.Instance);
            var ab = new Audiobook { Id = 7, Title = "Test" };

            var force = await svc.EnqueueScanAsync(ab, path: null, forceMetadataRefresh: true);
            var normal = await svc.EnqueueScanAsync(ab, path: null, forceMetadataRefresh: false);

            Assert.NotEqual(force, normal);

            Assert.True(svc.TryGetJob(force, out var forceJob));
            Assert.True(forceJob!.ForceMetadataRefresh);

            Assert.True(svc.TryGetJob(normal, out var normalJob));
            Assert.False(normalJob!.ForceMetadataRefresh);
        }

        [Fact]
        public async Task EnqueueScanAsync_DupeForceRefreshRequests_AreCollapsed()
        {
            var svc = new ScanQueueService(NullLogger<ScanQueueService>.Instance);
            var ab = new Audiobook { Id = 7, Title = "Test" };

            var first = await svc.EnqueueScanAsync(ab, path: null, forceMetadataRefresh: true);
            var second = await svc.EnqueueScanAsync(ab, path: null, forceMetadataRefresh: true);

            Assert.Equal(first, second);
        }

        [Fact]
        public async Task EnqueueScanAsync_RoundTripsSkipMissingBasePathCleanupFlag()
        {
            var svc = new ScanQueueService(NullLogger<ScanQueueService>.Instance);
            var ab = new Audiobook { Id = 7, Title = "Test" };

            var safe = await svc.EnqueueScanAsync(ab, path: null, forceMetadataRefresh: true, skipMissingBasePathCleanup: true);
            var destructive = await svc.EnqueueScanAsync(ab, path: null, forceMetadataRefresh: true, skipMissingBasePathCleanup: false);

            Assert.NotEqual(safe, destructive);

            Assert.True(svc.TryGetJob(safe, out var safeJob));
            Assert.True(safeJob!.SkipMissingBasePathCleanup);

            Assert.True(svc.TryGetJob(destructive, out var destructiveJob));
            Assert.False(destructiveJob!.SkipMissingBasePathCleanup);
        }
    }
}
