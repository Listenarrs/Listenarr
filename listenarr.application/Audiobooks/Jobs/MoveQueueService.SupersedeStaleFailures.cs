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
using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    /// <summary>
    /// A durably completed move proves the record's files are where the job
    /// put them, so earlier NeedsAttention/Failed jobs for the same audiobook
    /// describe a world that no longer exists. Left alone they keep the book
    /// in "needs attention", block new mutations through the recovery policy,
    /// and pile up as operator noise (live: 13 of 17 NeedsAttention rows had
    /// been overtaken by a later successful move).
    /// </summary>
    private async Task SupersedeStaleFailuresAsync(MoveJob completed, CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var retired = await _persistence.SupersedeStaleFailuresAsync(
                completed.AudiobookId,
                completed.Id,
                $"Superseded: a later move ({completed.Id}) for this audiobook completed on {now:u}.",
                now,
                cancellationToken);
            if (retired.Count > 0)
            {
                _logger.LogInformation(
                    "Move job {JobId} completed; superseded {Count} earlier failed/needs-attention job(s) for audiobook {AudiobookId}: {Ids}",
                    completed.Id, retired.Count, completed.AudiobookId, string.Join(", ", retired));
            }
        }
        catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
        {
            _logger.LogWarning(ex, "Failed to supersede stale move failures for audiobook {AudiobookId}", completed.AudiobookId);
        }
    }
}
