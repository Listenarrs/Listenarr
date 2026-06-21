/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed class EfMoveQueuePersistence(IDbContextFactory<ListenArrDbContext> dbFactory)
    : IMoveQueuePersistence
{
    public async Task<MoveJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.MoveJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == id, cancellationToken);
    }

    public async Task<MoveJob?> GetActiveByKeyAsync(
        string deduplicationKey,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.MoveJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                job => job.ActiveDeduplicationKey == deduplicationKey,
                cancellationToken);
    }

    public async Task AddAsync(MoveJob job, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.MoveJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateStatusAsync(
        Guid id,
        string status,
        string? error,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.MoveJobs.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (job == null)
        {
            return;
        }

        job.Status = status;
        job.Error = error;
        job.UpdatedAt = updatedAt.UtcDateTime;
        job.ActiveDeduplicationKey = IsActive(status) ? job.ActiveDeduplicationKey : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsActive(string status) =>
        string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Processing", StringComparison.OrdinalIgnoreCase);
}
