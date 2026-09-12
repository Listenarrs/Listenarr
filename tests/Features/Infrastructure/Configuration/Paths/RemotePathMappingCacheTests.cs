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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Configuration.Paths;

[Trait("Name", "RemotePathMappingCacheTests")]
[Trait("Category", "Unit")]
public sealed class RemotePathMappingCacheTests : BaseTests
{
    // The download queue poller polls several clients at once from one scope, so every client's
    // mapping lookup lands on the same scoped DbContext. Counting the queries is the way to say
    // whether the steady-state poll touches the database at all; asserting on the EF concurrency
    // exception would mean racing it.
    private sealed class CountingRepository : IRemotePathMappingRepository
    {
        private readonly object _gate = new();
        private readonly List<RemotePathMapping> _rows = [];
        private int _inFlight;
        private int _nextId = 1;

        public int QueryCount { get; private set; }
        public int MaxConcurrentQueries { get; private set; }

        public void Seed(string downloadClientId, string remotePath, string localPath)
        {
            lock (_gate)
            {
                _rows.Add(new RemotePathMapping
                {
                    Id = _nextId++,
                    DownloadClientId = downloadClientId,
                    RemotePath = remotePath,
                    LocalPath = localPath
                });
            }
        }

        public async Task<List<RemotePathMapping>> GetByClientIdAsync(
            string downloadClientId,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                QueryCount++;
                _inFlight++;
                MaxConcurrentQueries = Math.Max(MaxConcurrentQueries, _inFlight);
            }

            try
            {
                // Wide enough that concurrent callers genuinely overlap if they each query.
                await Task.Delay(30, ct);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }

            lock (_gate)
            {
                return [.. _rows.Where(row => row.DownloadClientId == downloadClientId)];
            }
        }

        public Task<List<RemotePathMapping>> GetAllAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult<List<RemotePathMapping>>([.. _rows]);
            }
        }

        public Task<RemotePathMapping?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_rows.FirstOrDefault(row => row.Id == id));
            }
        }

        public Task<RemotePathMapping> SaveAsync(RemotePathMapping mapping, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var existing = _rows.FirstOrDefault(row => row.Id == mapping.Id && mapping.Id != 0);
                if (existing != null)
                {
                    _rows.Remove(existing);
                }
                else
                {
                    mapping.Id = _nextId++;
                }

                _rows.Add(mapping);
                return Task.FromResult(mapping);
            }
        }

        public Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var existing = _rows.FirstOrDefault(row => row.Id == id);
                if (existing == null)
                {
                    return Task.FromResult(false);
                }

                _rows.Remove(existing);
                return Task.FromResult(true);
            }
        }
    }

    // Built by hand rather than resolved from the provider: the point of these tests is to count
    // the repository calls the cache does or does not make, which needs a repository that records
    // them and a cache instance this test owns.
    private readonly CountingRepository _repository = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly RemotePathMappingService _service;

    public RemotePathMappingCacheTests()
    {
        _service = new RemotePathMappingService(
            _repository,
            NullLogger<RemotePathMappingService>.Instance,
            _cache);
    }

    public override async Task DisposeAsync()
    {
        _cache.Dispose();
        await base.DisposeAsync();
    }

    private static DownloadClientConfiguration Client(string id) => new()
    {
        Id = id,
        Name = id,
        Type = "qBittorrent"
    };

    // The discriminating one. Remove the cache.Set in GetPathMappingByClientAsync and the eight
    // concurrent callers each issue their own query, so QueryCount reads nine instead of one and
    // MaxConcurrentQueries reads more than one.
    [Fact]
    public async Task GetPathMappingByClientAsync_ConcurrentPolls_IssueNoQueryOnceWarm()
    {
        var client = Client("client-1");
        _repository.Seed(client.Id, "/downloads", "/media/downloads");

        var warm = await _service.GetPathMappingByClientAsync(client);
        Assert.Single(warm);
        Assert.Equal(1, _repository.QueryCount);

        var polls = Enumerable.Range(0, 8)
            .Select(_ => _service.GetPathMappingByClientAsync(client));
        var results = await Task.WhenAll(polls);

        Assert.Equal(1, _repository.QueryCount);
        Assert.Equal(1, _repository.MaxConcurrentQueries);
        Assert.All(results, result => Assert.Equal("/media/downloads", Assert.Single(result).LocalPath));
    }

    // A single cache key for every client would serve one client's mappings to another, which is
    // worse than the query it saves.
    [Fact]
    public async Task GetPathMappingByClientAsync_KeepsEachClientSeparate()
    {
        var first = Client("client-1");
        var second = Client("client-2");
        _repository.Seed(first.Id, "/downloads/one", "/media/one");
        _repository.Seed(second.Id, "/downloads/two", "/media/two");

        Assert.Equal("/media/one", Assert.Single(await _service.GetPathMappingByClientAsync(first)).LocalPath);
        Assert.Equal("/media/two", Assert.Single(await _service.GetPathMappingByClientAsync(second)).LocalPath);
        Assert.Equal(2, _repository.QueryCount);

        Assert.Equal("/media/one", Assert.Single(await _service.GetPathMappingByClientAsync(first)).LocalPath);
        Assert.Equal("/media/two", Assert.Single(await _service.GetPathMappingByClientAsync(second)).LocalPath);
        Assert.Equal(2, _repository.QueryCount);
    }

    // Caching a set the user can edit is only safe while every writer drops it. The three writers
    // already removed this key before anything populated it, so these pin the pairing.
    [Fact]
    public async Task CreateAsync_DropsTheCachedMappingsForThatClient()
    {
        var client = Client("client-1");
        _repository.Seed(client.Id, "/downloads", "/media/downloads");
        Assert.Single(await _service.GetPathMappingByClientAsync(client));

        await _service.CreateAsync(new RemotePathMapping
        {
            DownloadClientId = client.Id,
            RemotePath = "/downloads/second",
            LocalPath = "/media/second"
        });

        var afterCreate = await _service.GetPathMappingByClientAsync(client);
        Assert.Equal(2, afterCreate.Count);
        Assert.Equal(2, _repository.QueryCount);
    }

    [Fact]
    public async Task DeleteAsync_DropsTheCachedMappingsForThatClient()
    {
        var client = Client("client-1");
        _repository.Seed(client.Id, "/downloads", "/media/downloads");
        var only = Assert.Single(await _service.GetPathMappingByClientAsync(client));

        Assert.True(await _service.DeleteAsync(only.Id));

        Assert.Empty(await _service.GetPathMappingByClientAsync(client));
        Assert.Equal(2, _repository.QueryCount);
    }

    // The cached array is the service's own. Handing it out would let one caller's edit reach the
    // next poll, which is the kind of fault a cache is expected not to introduce.
    [Fact]
    public async Task GetPathMappingByClientAsync_DoesNotShareTheListItHandsOut()
    {
        var client = Client("client-1");
        _repository.Seed(client.Id, "/downloads", "/media/downloads");

        var first = await _service.GetPathMappingByClientAsync(client);
        first.Clear();

        Assert.Single(await _service.GetPathMappingByClientAsync(client));
        Assert.Equal(1, _repository.QueryCount);
    }
}
