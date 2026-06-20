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

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// Stage 4: Query object for finding downloads in clients
    /// Used by hash retrieval retry logic
    /// </summary>
    public class DownloadClientItemQuery
    {
        /// <summary>
        /// Download ID (natural key: torrent hash or NZB ID)
        /// </summary>
        public string DownloadId { get; set; } = string.Empty;

        /// <summary>
        /// Title/name of the download for fallback matching
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Audiobook ID this download is for
        /// </summary>
        public Guid? AudiobookId { get; set; }

        /// <summary>
        /// When the download was first grabbed/added
        /// </summary>
        public DateTime AddedDate { get; set; }

        /// <summary>
        /// Download client this was sent to
        /// </summary>
        public string DownloadClient { get; set; } = string.Empty;

        /// <summary>
        /// Download client ID
        /// </summary>
        public string DownloadClientId { get; set; } = string.Empty;

        /// <summary>
        /// Protocol (Torrent or Usenet)
        /// </summary>
        public DownloadProtocol Protocol { get; set; }

        /// <summary>
        /// Number of times we've tried to retrieve the hash
        /// </summary>
        public int RetryCount { get; set; }

        /// <summary>
        /// When we last attempted retrieval
        /// </summary>
        public DateTime? LastRetry { get; set; }
    }
}
