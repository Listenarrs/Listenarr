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

            // When set, a by-client read holds after it has reached the database until released.
            public bool HoldReads;
            public readonly TaskCompletionSource ReadReachedDatabase = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource ReleaseReads = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<List<RemotePathMapping>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
            // When set, the first two by-id lookups each hold after reading the row until released.
            public bool HoldLookups;
            private int _lookups = -1;
            public readonly TaskCompletionSource[] LookupReachedDatabase = [NewSignal(), NewSignal()];
            public readonly TaskCompletionSource[] ReleaseLookup = [NewSignal(), NewSignal()];

            private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<RemotePathMapping?> GetByIdAsync(int id, CancellationToken ct = default)
            {
                var mapping = await inner.GetByIdAsync(id, ct);
                if (HoldLookups)
                {
                    var lookup = Interlocked.Increment(ref _lookups);
                    LookupReachedDatabase[lookup].TrySetResult();
                    await ReleaseLookup[lookup].Task;
                }
                return mapping;
            }
            public async Task<List<RemotePathMapping>> GetByClientIdAsync(string downloadClientId, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _byClientReads);
                var mappings = await inner.GetByClientIdAsync(downloadClientId, ct);
                if (HoldReads)
                {
                    ReadReachedDatabase.TrySetResult();
                    await ReleaseReads.Task;
                }
                return mappings;
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

        // The boundary of the cache: a row inserted behind the service stays invisible until the
        // entry expires, because only the service's own writers clear it.
        [Fact]
        public async Task GetPathMappingByClientAsync_DoesNotSeeAMappingWrittenBehindTheService()
        {
            Assert.Empty(await _service.GetPathMappingByClientAsync(_client));

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(_client)
                .WithRemotePath(FileUtils.GetAbsolutePath("downloads"))
                .WithLocalPath(FileUtils.GetAbsolutePath("library"))
                .Build());

            Assert.Empty(await _service.GetPathMappingByClientAsync(_client));
        }

        // A cold read has fetched the old set when a create saves and clears the key. The write
        // must wait for that fill, or the fill stores the old set after the clear.
        [Fact]
        public async Task CreateAsync_DuringAnInFlightColdRead_IsSeenByTheNextRead()
        {
            _counter.HoldReads = true;
            var inFlight = _service.GetPathMappingByClientAsync(_client);
            await _counter.ReadReachedDatabase.Task;

            var create = CreateMapping("downloads", "library");
            // Not awaited: the write is expected to queue behind the fill, so awaiting it first
            // would deadlock. The delay gives an unserialised write time to finish.
            await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(1)));
            _counter.HoldReads = false;
            _counter.ReleaseReads.SetResult();

            Assert.Empty(await inFlight);
            await create;
            Assert.Single(await _service.GetPathMappingByClientAsync(_client));
        }

        [Fact]
        public async Task UpdateAsync_MovingAMappingToAnotherClient_ClearsTheOldClientToo()
        {
            var otherClient = await CreateDownloadClientConfiguration();
            var mapping = await CreateMapping("downloads", "library");
            Assert.Single(await _service.GetPathMappingByClientAsync(_client));
            Assert.Empty(await _service.GetPathMappingByClientAsync(otherClient));

            mapping.DownloadClientId = otherClient.Id;
            await _service.UpdateAsync(mapping);

            Assert.Single(await _service.GetPathMappingByClientAsync(otherClient));
            Assert.Empty(await _service.GetPathMappingByClientAsync(_client));
        }

        private static RemotePathMapping MovedTo(RemotePathMapping mapping, DownloadClientConfiguration client) =>
            new()
            {
                Id = mapping.Id,
                DownloadClientId = client.Id,
                Name = mapping.Name,
                RemotePath = mapping.RemotePath,
                LocalPath = mapping.LocalPath,
            };

        // Two writers race on one mapping that the first moves from _client to `middle`. The second
        // writer looks the mapping up while the first is still between its own lookup and save,
        // and is held until a read has cached the mapping under `middle`. Whatever the second
        // writer then does, `middle` must not go on serving a mapping it no longer owns.
        private async Task<DownloadClientConfiguration> RaceAWriterAgainstAMove(
            RemotePathMapping mapping, Func<Task> secondWriter)
        {
            var middle = await CreateDownloadClientConfiguration();
            _counter.HoldLookups = true;
            var move = _service.UpdateAsync(MovedTo(mapping, middle));
            try
            {
                await _counter.LookupReachedDatabase[0].Task;

                var second = secondWriter();
                // Not awaited: a serialised writer queues behind the move before its lookup, so awaiting
                // its lookup would deadlock. The delay gives an unserialised lookup time to read the row.
                await Task.WhenAny(_counter.LookupReachedDatabase[1].Task, Task.Delay(TimeSpan.FromSeconds(1)));

                _counter.ReleaseLookup[0].SetResult();
                await move;
                Assert.Single(await _service.GetPathMappingByClientAsync(middle));

                _counter.ReleaseLookup[1].SetResult();
                await second;
                return middle;
            }
            finally
            {
                // A writer parked on a gate holds the process-wide writer lock, so a failed step must
                // still let it go or every later mapping write in the run waits forever.
                _counter.ReleaseLookup[0].TrySetResult();
                _counter.ReleaseLookup[1].TrySetResult();
            }
        }

        [Fact]
        public async Task UpdateAsync_OverlappingMovesFromOneClient_ClearTheIntermediateClient()
        {
            var last = await CreateDownloadClientConfiguration();
            var mapping = await CreateMapping("downloads", "library");

            var middle = await RaceAWriterAgainstAMove(mapping, () => _service.UpdateAsync(MovedTo(mapping, last)));

            Assert.Empty(await _service.GetPathMappingByClientAsync(middle));
            Assert.Single(await _service.GetPathMappingByClientAsync(last));
            Assert.Empty(await _service.GetPathMappingByClientAsync(_client));
        }

        [Fact]
        public async Task DeleteAsync_OverlappingAMoveFromOneClient_ClearsTheClientItMovedTo()
        {
            var mapping = await CreateMapping("downloads", "library");

            var middle = await RaceAWriterAgainstAMove(mapping, () => _service.DeleteAsync(mapping.Id));

            Assert.Empty(await _service.GetPathMappingByClientAsync(middle));
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
