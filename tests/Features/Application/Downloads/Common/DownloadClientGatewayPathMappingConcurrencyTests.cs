/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Downloads.Common;

[Trait("Name", "DownloadClientGatewayPathMappingConcurrencyTests")]
[Trait("Category", "Unit")]
public sealed class DownloadClientGatewayPathMappingConcurrencyTests : BaseTests
{
    // IRemotePathMappingService is scoped and the ListenArrDbContext behind its repository is
    // scoped too. GetQueueAsync fans out over every queue item, and each item used to look up the
    // client's mappings for itself, so a queue of N items issued N concurrent queries against a
    // context that permits one at a time. Asserting on the EF exception would mean racing it, so
    // this counts the overlap directly.
    private sealed class OverlapRecordingMappingService : IRemotePathMappingService
    {
        private int _inFlight;
        public int MaxConcurrentLookups { get; private set; }
        public int LookupCount { get; private set; }

        public async Task<List<RemotePathMapping>> GetPathMappingByClientAsync(
            DownloadClientConfiguration client)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (this)
            {
                LookupCount++;
                if (now > MaxConcurrentLookups) MaxConcurrentLookups = now;
            }

            // A real query is not instantaneous; without this the overlap can go unobserved.
            await Task.Delay(20);

            Interlocked.Decrement(ref _inFlight);
            return [];
        }

        public string TranslatePath(
            IReadOnlyList<RemotePathMapping> mappings,
            DownloadClientConfiguration client,
            string remotePath) => remotePath;

        public async Task<string> TranslatePathAsync(
            DownloadClientConfiguration client,
            string remotePath)
        {
            var mappings = await GetPathMappingByClientAsync(client);
            return TranslatePath(mappings, client, remotePath);
        }

        public Task<List<RemotePathMapping>> GetAllAsync() =>
            Task.FromResult(new List<RemotePathMapping>());
        public Task<RemotePathMapping?> GetByIdAsync(int id) =>
            Task.FromResult<RemotePathMapping?>(null);
        public Task<RemotePathMapping> CreateAsync(RemotePathMapping mapping) =>
            Task.FromResult(mapping);
        public Task<RemotePathMapping> UpdateAsync(RemotePathMapping mapping) =>
            Task.FromResult(mapping);
        public Task<bool> DeleteAsync(int id) => Task.FromResult(true);
    }

    [Fact]
    public async Task GetQueueAsync_ResolvesClientMappingsOncePerBatch()
    {
        var mappingService = new OverlapRecordingMappingService();
        var client = new DownloadClientConfiguration
        {
            Id = "client-1",
            Name = "qbittorrent",
            Type = "qBittorrent"
        };

        var items = Enumerable.Range(0, 10)
            .Select(i => new QueueItem
            {
                Id = $"item-{i}",
                RemotePath = $"/remote/downloads/book-{i}",
                ContentPath = $"/remote/downloads/book-{i}/audio.m4b"
            })
            .ToList();

        var adapter = new Mock<IDownloadClientAdapter>();
        adapter.Setup(a => a.GetQueueAsync(client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
        var factory = new Mock<IDownloadClientAdapterFactory>();
        factory.Setup(f => f.GetByType(It.IsAny<string>())).Returns(adapter.Object);

        var gateway = new DownloadClientGateway(
            mappingService,
            factory.Object,
            new LocalFileSystem(),
            new FileSystemSemanticsResolver(),
            NullLogger<DownloadClientGateway>.Instance);

        await gateway.GetQueueAsync(client);

        Assert.Equal(1, mappingService.MaxConcurrentLookups);
        // Ten items, each carrying two translatable paths, resolved from one lookup.
        Assert.Equal(1, mappingService.LookupCount);
    }

    [Fact]
    public async Task GetQueueAsync_ResolvesOncePerBatch_WhenItemsCarrySourceFiles()
    {
        var mappingService = new OverlapRecordingMappingService();
        var client = new DownloadClientConfiguration
        {
            Id = "client-1",
            Name = "qbittorrent",
            Type = "qBittorrent"
        };

        // qBittorrent's queue mapper populates SourceFiles from the torrent's file list, so a
        // real queue item arrives with one entry per file rather than with the list empty.
        var items = Enumerable.Range(0, 10)
            .Select(i => new QueueItem
            {
                Id = $"item-{i}",
                RemotePath = $"/remote/downloads/book-{i}",
                ContentPath = $"/remote/downloads/book-{i}/audio.m4b",
                SourceFiles =
                [
                    $"/remote/downloads/book-{i}/01.m4b",
                    $"/remote/downloads/book-{i}/02.m4b",
                    $"/remote/downloads/book-{i}/03.m4b"
                ]
            })
            .ToList();

        var adapter = new Mock<IDownloadClientAdapter>();
        adapter.Setup(a => a.GetQueueAsync(client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
        var factory = new Mock<IDownloadClientAdapterFactory>();
        factory.Setup(f => f.GetByType(It.IsAny<string>())).Returns(adapter.Object);

        var gateway = new DownloadClientGateway(
            mappingService,
            factory.Object,
            new LocalFileSystem(),
            new FileSystemSemanticsResolver(),
            NullLogger<DownloadClientGateway>.Instance);

        await gateway.GetQueueAsync(client);

        Assert.Equal(1, mappingService.MaxConcurrentLookups);
        Assert.Equal(1, mappingService.LookupCount);
    }
}
