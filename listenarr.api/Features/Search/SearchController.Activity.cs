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

using Listenarr.Application.Notifications.Progress;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Search
{
    public partial class SearchController
    {
        /// <summary>
        /// Current background automatic-search activity plus a short rolling history
        /// of recent outcomes. Lets the live UI indicator (sidebar) hydrate
        /// immediately on load instead of waiting for the next SignalR event —
        /// which during an idle window could be hours away. Volatile,
        /// process-local ambient status; not an audit log (History persists grabs).
        /// </summary>
        [HttpGet("activity")]
        [ProducesResponseType(typeof(SearchActivityResponse), StatusCodes.Status200OK)]
        public ActionResult<SearchActivityResponse> GetSearchActivity()
        {
            if (_searchActivityTracker == null)
            {
                return Ok(new SearchActivityResponse { Current = null, Recent = new List<SearchActivityEventDto>() });
            }

            var (current, recent) = _searchActivityTracker.Snapshot();
            return Ok(new SearchActivityResponse
            {
                Current = current == null ? null : SearchActivityEventDto.From(current),
                Recent = recent.Select(SearchActivityEventDto.From).ToList(),
            });
        }

        public sealed class SearchActivityResponse
        {
            public SearchActivityEventDto? Current { get; set; }
            public List<SearchActivityEventDto> Recent { get; set; } = new();
        }

        public sealed class SearchActivityEventDto
        {
            public string Message { get; set; } = string.Empty;
            public string Stage { get; set; } = string.Empty;
            public int? AudiobookId { get; set; }
            public string? Asin { get; set; }
            public DateTime Timestamp { get; set; }

            public static SearchActivityEventDto From(SearchActivityEvent e) => new()
            {
                Message = e.Message,
                Stage = e.Stage,
                AudiobookId = e.AudiobookId,
                Asin = e.Asin,
                Timestamp = e.Timestamp,
            };
        }
    }
}
