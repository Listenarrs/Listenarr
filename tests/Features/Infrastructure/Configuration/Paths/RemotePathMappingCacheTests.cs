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
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Configuration.Paths
{
    [Trait("Name", "RemotePathMappingCacheTests")]
    [Trait("Category", "RemotePathMapping")]
    public class RemotePathMappingCacheTests : BaseTests
    {
        // Counts per-client reads against the real repository, so the numbers are what a poll costs.
        private sealed class CountingRepository(IRemotePathMappingRepository inner) : IRemotePathMappingRepository
        {
            private int _byClientReads;
            public int ByClientReads => _byClientReads;

            public Task<List<RemotePathMapping>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
            public Task<RemotePathMapping?> GetByIdAsync(int id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
            public Task<List<RemotePathMapping>> GetByClientIdAsync(string downloadClientId, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _byClientReads);
                return inner.GetByClientIdAsync(downloadClientId, ct);
            }
            public Task<RemotePathMapping> SaveAsync(RemotePathMapping mapping, CancellationToken ct = default) => inner.SaveAsync(mapping, ct);
            public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        }

        private CountingRepository _counter = null!;
        private RemotePathMappingService _service = null!;
        private DownloadClientConfiguration _client = null!;

        public override async Task InitializeAsync()
        {
            _client = await CreateDownloadClientConfiguration();
            _counter = new CountingRepository(new EfRemotePathMappingRepository(
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>()));
            _service = new RemotePathMappingService(
                _counter,
                NullLogger<RemotePathMappingService>.Instance,
                _provider.GetRequiredService<IMemoryCache>());
        }

        private Task<RemotePathMapping> CreateMapping(string remote, string local) =>
            _service.CreateAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(_client)
                .WithRemotePath(FileUtils.GetAbsolutePath(remote))
                .WithLocalPath(FileUtils.GetAbsolutePath(local))
                .Build());

        // The mock adapter's queue has two items with 3 and 5 source files, so an uncached poll
        // translates 12 paths and reads the mappings 12 times.
        [Fact]
        public async Task GetQueueAsync_TwoItemPoll_ReadsTheClientsMappingsOnce()
        {
            var client = new DownloadClientConfigurationBuilder().WithType("mock").Build();
            await _service.CreateAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(FileUtils.GetAbsolutePath("downloads"))
                .WithLocalPath(FileUtils.GetAbsolutePath("mnt", "wdelements", "downloads"))
                .Build());
            var gateway = new DownloadClientGateway(
                _service,
                _provider.GetRequiredService<IDownloadClientAdapterFactory>(),
                _provider.GetRequiredService<IFileSystem>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                NullLogger<DownloadClientGateway>.Instance);

            Assert.Equal(2, (await gateway.GetQueueAsync(client)).Count);
            Assert.Equal(1, _counter.ByClientReads);

            await gateway.GetQueueAsync(client);
            Assert.Equal(1, _counter.ByClientReads);
        }

        [Fact]
        public async Task GetPathMappingByClientAsync_ConcurrentColdReads_ShareOneQuery()
        {
            await CreateMapping("downloads", "library");

            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => _service.GetPathMappingByClientAsync(_client)));

            Assert.All(results, result => Assert.Single(result));
            Assert.Equal(1, _counter.ByClientReads);
        }

        [Fact]
        public async Task WritesThroughTheService_AreSeenByTheNextRead()
        {
            Assert.Empty(await _service.GetPathMappingByClientAsync(_client));

            var created = await CreateMapping("downloads", "library");
            Assert.Single(await _service.GetPathMappingByClientAsync(_client));

            Assert.True(await _service.DeleteAsync(created.Id));
            Assert.Empty(await _service.GetPathMappingByClientAsync(_client));
        }

        [Fact]
        public async Task GetPathMappingByClientAsync_HandsEachCallerItsOwnListInTheRepositoryOrder()
        {
            await CreateMapping("downloads", "library");
            await CreateMapping(Path.Combine("downloads", "audiobooks"), Path.Combine("library", "audiobooks"));

            var first = await _service.GetPathMappingByClientAsync(_client);
            var expectedOrder = first.Select(m => m.Id).ToList();
            first.Clear();

            var second = await _service.GetPathMappingByClientAsync(_client);

            Assert.Equal(expectedOrder, second.Select(m => m.Id));
            Assert.Equal(1, _counter.ByClientReads);
        }
    }
}
