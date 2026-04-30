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
namespace Listenarr.Domain.Models
{
    public class QueueStats
    {
        public int PendingJobs { get; set; }
        public int ProcessingJobs { get; set; }
        public int CompletedJobs { get; set; }
        public int FailedJobs { get; set; }
        public int RetryJobs { get; set; }
        public int TotalJobs { get; set; }
        public DateTime? OldestPendingJob { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
