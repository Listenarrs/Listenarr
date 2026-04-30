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
    }

    public interface IScanQueueService
    {
        Task<Guid> EnqueueScanAsync(Audiobook audiobook, string? path = null);
        Task<Guid?> RequeueScanAsync(Guid jobId);
        bool TryGetJob(Guid id, out ScanJob? job);
        void UpdateJobStatus(Guid id, string status, string? error = null, int? found = null, int? created = null);
    }
}
