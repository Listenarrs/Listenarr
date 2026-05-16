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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    public interface IAuthorMonitoringService
    {
        Task<MonitoredAuthor?> GetMonitoredAuthorAsync(
            string name,
            string region,
            string language,
            CancellationToken cancellationToken = default);

        Task<MonitorAuthorOperationResult> MonitorAuthorAsync(
            MonitorAuthorRequest request,
            CancellationToken cancellationToken = default);

        Task<bool> UnmonitorAuthorAsync(int id, CancellationToken cancellationToken = default);

        Task<MonitorAuthorSyncResult> SyncAuthorAsync(int id, CancellationToken cancellationToken = default);

        Task<int> SyncDueAuthorsAsync(CancellationToken cancellationToken = default);
    }

    public sealed class MonitorAuthorRequest
    {
        public string Name { get; set; } = string.Empty;

        public string? Asin { get; set; }

        public string Region { get; set; } = "us";

        public string Language { get; set; } = "all";
    }

    public sealed class MonitorAuthorOperationResult
    {
        public MonitoredAuthor? MonitoredAuthor { get; set; }

        public MonitorAuthorSyncResult SyncResult { get; set; } = new();
    }

    public sealed class MonitorAuthorSyncResult
    {
        public int AddedCount { get; set; }

        public int ExistingCount { get; set; }

        public int FailedCount { get; set; }

        public bool Succeeded { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
