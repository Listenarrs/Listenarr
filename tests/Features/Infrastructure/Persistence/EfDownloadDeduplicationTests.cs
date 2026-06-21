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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

public sealed class EfDownloadDeduplicationTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), "listenarr-tests", $"download-dedupe-{Guid.NewGuid():N}.db");
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
    public async Task ActiveAudiobookKey_IsUniqueUntilDownloadBecomesTerminal()
    {
        var repository = new EfDownloadRepository(
            _factory,
            NullLogger<EfDownloadRepository>.Instance);
        var first = CreateDownload("first", 42);
        var duplicate = CreateDownload("duplicate", 42);

        await repository.AddAsync(first);
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => repository.AddAsync(duplicate));

        first.Status = DownloadStatus.Failed;
        await repository.UpdateAsync(first);
        await repository.AddAsync(duplicate);

        Assert.Null((await repository.GetByIdAsync(first.Id))?.ActiveAudiobookDeduplicationKey);
        Assert.Equal(42, (await repository.GetByIdAsync(duplicate.Id))?.ActiveAudiobookDeduplicationKey);
    }

    private static Download CreateDownload(string id, int audiobookId) => new()
    {
        Id = id,
        AudiobookId = audiobookId,
        Title = id,
        Status = DownloadStatus.Queued,
        Metadata = []
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
