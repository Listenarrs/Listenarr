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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Metadata
{
    /// <summary>
    /// On-demand sync from a connected Audiobookshelf instance: backfills
    /// <see cref="Audiobook.RequiredNarrator"/> for books Listenarr tracks but doesn't yet have
    /// a required narrator set for, using whatever narrator Audiobookshelf already knows for
    /// that same book (matched by ASIN, falling back to title+author). Never overwrites a
    /// RequiredNarrator the user already set.
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/audiobookshelf")]
    [Tags("Metadata")]
    public class AudiobookshelfController : ControllerBase
    {
        private readonly IAudiobookshelfService _audiobookshelfService;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly ILogger<AudiobookshelfController> _logger;

        public AudiobookshelfController(
            IAudiobookshelfService audiobookshelfService,
            IAudiobookRepository audiobookRepository,
            ILogger<AudiobookshelfController> logger)
        {
            _audiobookshelfService = audiobookshelfService;
            _audiobookRepository = audiobookRepository;
            _logger = logger;
        }

        /// <summary>
        /// Whether Audiobookshelf is currently configured (base URL + API key present).
        /// </summary>
        [HttpGet("status")]
        public async Task<ActionResult<object>> GetStatus()
        {
            var configured = await _audiobookshelfService.IsConfiguredAsync();
            return Ok(new { configured });
        }

        /// <summary>
        /// Fetch Audiobookshelf's library and backfill RequiredNarrator for Listenarr audiobooks
        /// that don't already have one set, matched by ASIN first, then by title+author.
        /// </summary>
        [HttpPost("sync")]
        public async Task<ActionResult<object>> Sync(CancellationToken cancellationToken)
        {
            if (!await _audiobookshelfService.IsConfiguredAsync())
            {
                return BadRequest(new { message = "Audiobookshelf is not configured" });
            }

            var shelfItems = await _audiobookshelfService.GetLibraryItemsAsync(cancellationToken);
            var byAsin = shelfItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Asin) && !string.IsNullOrWhiteSpace(i.NarratorName))
                .GroupBy(i => i.Asin!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var byTitleAuthor = shelfItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Title) && !string.IsNullOrWhiteSpace(i.NarratorName))
                .GroupBy(i => NormalizeKey(i.Title, i.AuthorName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var audiobooks = await _audiobookRepository.GetAllAsync();
            var checked_ = 0;
            var updated = 0;

            foreach (var audiobook in audiobooks)
            {
                if (!string.IsNullOrWhiteSpace(audiobook.RequiredNarrator))
                {
                    continue;
                }

                checked_++;

                AudiobookshelfLibraryItem? match = null;
                if (!string.IsNullOrWhiteSpace(audiobook.Asin) && byAsin.TryGetValue(audiobook.Asin.Trim(), out var asinMatch))
                {
                    match = asinMatch;
                }
                else
                {
                    var key = NormalizeKey(audiobook.Title, audiobook.Authors?.FirstOrDefault());
                    if (key.Length > 0 && byTitleAuthor.TryGetValue(key, out var titleMatch))
                    {
                        match = titleMatch;
                    }
                }

                if (match == null || string.IsNullOrWhiteSpace(match.NarratorName))
                {
                    continue;
                }

                audiobook.RequiredNarrator = match.NarratorName;
                var saved = await _audiobookRepository.UpdateAsync(audiobook);
                if (saved)
                {
                    updated++;
                    _logger.LogInformation(
                        "Set RequiredNarrator for audiobook {AudiobookId} ('{Title}') to '{Narrator}' from Audiobookshelf",
                        audiobook.Id, audiobook.Title, match.NarratorName);
                }
            }

            return Ok(new { shelfItemsFound = shelfItems.Count, audiobooksChecked = checked_, audiobooksUpdated = updated });
        }

        private static string NormalizeKey(string? title, string? author)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var t = title.Trim().ToLowerInvariant();
            var a = author?.Trim().ToLowerInvariant() ?? string.Empty;
            return $"{t}::{a}";
        }
    }
}
