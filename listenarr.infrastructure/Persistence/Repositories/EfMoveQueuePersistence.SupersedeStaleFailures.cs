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
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class EfMoveQueuePersistence
{
    public async Task<IReadOnlyList<Guid>> SupersedeStaleFailuresAsync(
        int audiobookId,
        Guid completedJobId,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stale = await db.MoveJobs
            .Where(j => j.AudiobookId == audiobookId
                        && j.Id != completedJobId
                        && (j.Status == MoveJobStatus.NeedsAttention || j.Status == MoveJobStatus.Failed))
            .ToListAsync(cancellationToken);

        foreach (var job in stale)
        {
            // Superseded is the durable "overtaken by a later job" terminal
            // state; the original error stays readable behind the new reason.
            job.Status = MoveJobStatus.Superseded;
            job.Error = string.IsNullOrWhiteSpace(job.Error) ? error : $"{error} Original: {job.Error}";
            job.UpdatedAt = now.UtcDateTime;
            job.ActiveDeduplicationKey = null;
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return stale.Select(j => j.Id).ToList();
    }
}
