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

public sealed class EfDownloadProcessingJobDeduplicationTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), "listenarr-tests", $"import-dedupe-{Guid.NewGuid():N}.db");
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
    public async Task ActiveJobKey_IsUniqueUntilJobBecomesTerminal()
    {
        var repository = new EfDownloadProcessingJobRepository(_factory);
        var first = CreateJob("download-42");
        var duplicate = CreateJob("DOWNLOAD-42");

        await repository.AddAsync(first);
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => repository.AddAsync(duplicate));

        first.MarkAsCompleted();
        await repository.UpdateAsync(first);
        await repository.AddAsync(duplicate);

        Assert.Equal(
            duplicate.Id,
            (await repository.GetActiveByDownloadIdAsync("DOWNLOAD-42"))?.Id);
    }

    [Fact]
    public async Task DeleteCompletedBeforeAsync_WhenStatusesEmpty_DoesNothing()
    {
        var repository = new EfDownloadProcessingJobRepository(_factory);
        var oldCompleted = CreateJob("download-cleanup").MarkAsCompleted();
        oldCompleted.CompletedAt = DateTime.UtcNow.AddDays(-30);

        await repository.AddAsync(oldCompleted);

        var removed = await repository.DeleteCompletedBeforeAsync(
            Array.Empty<ProcessingJobStatus>(),
            DateTime.UtcNow);

        Assert.Equal(0, removed);
        Assert.NotNull(await repository.GetByIdAsync(oldCompleted.Id));
    }

    private static DownloadProcessingJob CreateJob(string downloadId) => new()
    {
        DownloadId = downloadId,
        Status = ProcessingJobStatus.Pending,
        JobType = ProcessingJobType.MoveOrCopyFile
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
