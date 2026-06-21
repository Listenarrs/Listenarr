/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

public sealed class EfMoveQueuePersistenceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), "listenarr-tests", $"move-dedupe-{Guid.NewGuid():N}.db");
    private IDbContextFactory<ListenArrDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new TestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ActiveDeduplicationKey_IsUniqueUntilTerminalStatus()
    {
        var persistence = new EfMoveQueuePersistence(_factory);
        var first = CreateJob("42:/LIBRARY/BOOK");
        var duplicate = CreateJob("42:/LIBRARY/BOOK");

        await persistence.AddAsync(first);
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => persistence.AddAsync(duplicate));

        await persistence.UpdateStatusAsync(
            first.Id,
            "Completed",
            null,
            DateTimeOffset.UtcNow);
        await persistence.AddAsync(duplicate);

        Assert.Equal(duplicate.Id, (await persistence.GetActiveByKeyAsync("42:/LIBRARY/BOOK"))?.Id);
    }

    private static MoveJob CreateJob(string key) => new()
    {
        AudiobookId = 42,
        RequestedPath = "/library/book",
        Status = "Queued",
        ActiveDeduplicationKey = key
    };

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
