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
    /// A full queue read from one download client.
    /// </summary>
    /// <param name="Items">The rows the client returned that could be read.</param>
    /// <param name="IsComplete">
    /// False when the client reported items that were left out of <paramref name="Items"/>.
    /// The rows are still accurate, but absence from them no longer means absence from the
    /// client, so an incomplete snapshot must not drive orphan cleanup.
    /// </param>
    public sealed record DownloadClientQueueSnapshot(List<QueueItem> Items, bool IsComplete)
    {
        public static DownloadClientQueueSnapshot Complete(List<QueueItem> items) => new(items, IsComplete: true);
    }
}
