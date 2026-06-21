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

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

public sealed class EfUnitOfWorkTests
{
    [Fact]
    public async Task CommitAsync_PersistsTrackedChangesOnce()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase($"unit-of-work-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ListenArrDbContext(options);
        var job = new MoveJob
        {
            AudiobookId = 42,
            RequestedPath = "library/book",
            Status = "Queued"
        };
        db.MoveJobs.Add(job);
        var unitOfWork = new EfUnitOfWork(db);

        await unitOfWork.CommitAsync();

        Assert.NotNull(await db.MoveJobs.FindAsync(job.Id));
    }
}
