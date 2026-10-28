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

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryAddWorkflow
    {
        /// <summary>The search started by the last add with AutoSearch, if any (lets tests await it).</summary>
        internal Task? AutoSearchTask { get; private set; }

        /// <summary>
        /// "Search for downloads immediately": once the book is committed, run the same search-and-download
        /// as the Wanted page's search button, so the current quality profile and result scoring apply.
        /// This is independent of Monitored, which only governs later automatic searches and upgrades:
        /// an unmonitored book is grabbed once and then left alone.
        /// It runs in the background so the add response does not wait on indexers.
        /// </summary>
        private void StartAutoSearchAfterAdd(Audiobook? audiobook, bool autoSearch)
        {
            AutoSearchTask = null;
            if (!autoSearch || audiobook == null || audiobook.Id <= 0)
            {
                return;
            }

            var audiobookId = audiobook.Id;
            var title = LogRedaction.SanitizeText(audiobook.Title);
            AutoSearchTask = Task.Run(async () =>
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
                    _logger.LogInformation("Searching for downloads right after adding '{Title}' (id {AudiobookId})", title, audiobookId);
                    var result = await downloadService.SearchAndDownloadAsync(audiobookId);
                    _logger.LogInformation(
                        "Search after adding '{Title}' (id {AudiobookId}): success={Success}, {Message}",
                        title, audiobookId, result.Success, LogRedaction.SanitizeText(result.Message));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Search after adding '{Title}' (id {AudiobookId}) failed", title, audiobookId);
                }
            });
        }
    }
}
