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

namespace Listenarr.Application.Downloads.Contracts
{
    /// <summary>
    /// Runs one retention-cleanup cycle for terminal download-processing jobs.
    /// Kept separate from <see cref="IDownloadImportProcessor" /> so importing files
    /// and pruning old job rows stay separate worker responsibilities.
    /// </summary>
    public interface IDownloadProcessingJobCleanupProcessor
    {
        /// <summary>
        /// Deletes eligible terminal processing-job rows for the configured retention window.
        /// </summary>
        Task RunCycleAsync(CancellationToken cancellationToken = default);
    }
}
