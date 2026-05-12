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
namespace Listenarr.Api.Services
{
    public class ScanJob
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int AudiobookId { get; set; }
        public string? Path { get; set; }
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "Queued";
        public string? Error { get; set; }
        /// <summary>
        /// When true, the worker should re-extract metadata for already-tracked files
        /// and backfill any blank fields on the audiobook record.
        /// </summary>
        public bool ForceMetadataRefresh { get; set; }

        /// <summary>
        /// When true, the worker must skip the destructive "BasePath missing → delete tracked
        /// AudiobookFile rows" cleanup. Set by library-wide flows (e.g. metadata backfill)
        /// where one stale path should not cascade into data loss.
        /// </summary>
        public bool SkipMissingBasePathCleanup { get; set; }
    }

    public interface IScanQueueService
    {
        Task<Guid> EnqueueScanAsync(Audiobook audiobook, string? path = null, bool forceMetadataRefresh = false, bool skipMissingBasePathCleanup = false);
        Task<Guid?> RequeueScanAsync(Guid jobId);
        bool TryGetJob(Guid id, out ScanJob? job);
        void UpdateJobStatus(Guid id, string status, string? error = null, int? found = null, int? created = null);
    }
}
