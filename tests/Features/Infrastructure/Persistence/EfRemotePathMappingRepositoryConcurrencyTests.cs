/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Data.Common;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "EfRemotePathMappingRepositoryConcurrencyTests")]
[Trait("Category", "RemotePathMapping")]
public sealed class EfRemotePathMappingRepositoryConcurrencyTests : IAsyncLifetime
{
    private const string ClientId = "client-under-poll";

    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), "listenarr-tests", $"rpm-concurrency-{Guid.NewGuid():N}.db");
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _connectionString = $"Data Source={_databasePath};Pooling=False";

        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.RemotePathMappings.Add(new RemotePathMapping
        {
            DownloadClientId = ClientId,
            RemotePath = FileUtils.GetAbsolutePath("downloads"),
            LocalPath = FileUtils.GetAbsolutePath("imports"),
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Method", "GetByClientIdAsync")]
    public async Task GetByClientIdAsync_TrulyOverlappingReads_EachUseTheirOwnContext()
    {
        // Download polling fans out concurrent path-mapping reads for one client. A barrier
        // command interceptor holds each read's query until a second read's query has also
        // begun, so the two are provably in flight at the same instant (asserted via
        // MaxObservedConcurrency). The factory-based repository gives each read its own
        // context/connection, so the overlap is safe; a single shared scoped context would
        // throw "A second operation was started on this context instance before a previous
        // operation completed." This test therefore fails if the per-operation
        // IDbContextFactory fix is reverted, rather than passing vacuously the way an
        // InMemory Task.WhenAll burst does (reads there complete as they are enumerated).
        const int concurrency = 2;
        using var overlap = new OverlappingReadBarrier(concurrency, TimeSpan.FromSeconds(30));
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(overlap)
            .Options;
        var repository = new EfRemotePathMappingRepository(new SingleOptionDbContextFactory(options));

        var reads = Enumerable
            .Range(0, concurrency)
            .Select(_ => Task.Run(() => repository.GetByClientIdAsync(ClientId)))
            .ToArray();

        var results = await Task.WhenAll(reads);

        Assert.Equal(concurrency, overlap.MaxObservedConcurrency);
        Assert.All(results, mappings => Assert.Single(mappings));
    }

    private sealed class SingleOptionDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);
    }

    // Holds each intercepted RemotePathMappings read until `participants` of them have begun,
    // guaranteeing the reads genuinely overlap instead of completing one at a time. Non-target
    // commands (connection pragmas, etc.) pass straight through so they do not skew the count.
    private sealed class OverlappingReadBarrier(int participants, TimeSpan timeout)
        : DbCommandInterceptor, IDisposable
    {
        private readonly Barrier _barrier = new(participants);
        private readonly Lock _gate = new();
        private int _inFlight;

        public int MaxObservedConcurrency { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Rendezvous(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() => Rendezvous(command), cancellationToken);
            return result;
        }

        private void Rendezvous(DbCommand command)
        {
            if (!command.CommandText.Contains("RemotePathMappings", StringComparison.Ordinal))
            {
                return;
            }

            lock (_gate)
            {
                _inFlight++;
                if (_inFlight > MaxObservedConcurrency)
                {
                    MaxObservedConcurrency = _inFlight;
                }
            }

            try
            {
                _barrier.SignalAndWait(timeout);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }

        public void Dispose() => _barrier.Dispose();
    }
}
