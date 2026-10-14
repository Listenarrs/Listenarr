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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Notifications.Progress
{
    /// <summary>
    /// Handles broadcasting search progress updates to connected realtime clients.
    /// </summary>
    public class SearchProgressReporter
    {
        private readonly IHubBroadcaster? _hubBroadcaster;
        private readonly ILogger<SearchProgressReporter> _logger;
        private readonly ISearchActivityTracker? _activityTracker;

        public SearchProgressReporter(
            IHubBroadcaster? hubBroadcaster,
            ILogger<SearchProgressReporter> logger,
            ISearchActivityTracker? activityTracker = null)
        {
            _hubBroadcaster = hubBroadcaster;
            _logger = logger;
            _activityTracker = activityTracker;
        }

        /// <summary>
        /// Broadcasts an interactive (user-initiated) search progress message to
        /// all connected realtime clients. These are scoped to the page that
        /// started the search (e.g. Add New) and are not recorded in the
        /// background-activity tracker.
        /// </summary>
        /// <param name="message">The progress message to broadcast</param>
        /// <param name="asin">Optional ASIN associated with this progress update</param>
        public async Task BroadcastAsync(string message, string? asin = null)
        {
            try
            {
                if (_hubBroadcaster != null)
                {
                    // Structured payload: include a type so clients can distinguish interactive vs automatic
                    await _hubBroadcaster.BroadcastAsync("SearchProgress", new { message, asin, type = "interactive" });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast SearchProgress: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Broadcasts a background automatic-search activity update and records it
        /// in the <see cref="ISearchActivityTracker"/> so freshly-loaded clients
        /// can hydrate the live indicator. Clients opt in to these via the
        /// <c>includeAutomatic</c> flag on their SearchProgress subscription.
        /// </summary>
        /// <param name="message">Human-readable status (e.g. "Searching 13 indexers for …").</param>
        /// <param name="stage">One of <c>searching</c>/<c>grabbed</c>/<c>no_results</c>/<c>idle</c>.</param>
        /// <param name="audiobookId">The book this event concerns, if any.</param>
        /// <param name="asin">Associated ASIN, if any.</param>
        /// <param name="addToFeed">
        /// Whether the event enters the recent-outcomes feed. Transient stages
        /// (searching/idle) pass false so the feed lists outcomes, not play-by-play.
        /// </param>
        public async Task BroadcastAutomaticAsync(
            string message,
            string stage,
            int? audiobookId = null,
            string? asin = null,
            bool addToFeed = true)
        {
            var evt = new SearchActivityEvent(message, stage, audiobookId, asin, DateTime.UtcNow);
            _activityTracker?.Record(evt, addToFeed);

            try
            {
                if (_hubBroadcaster != null)
                {
                    await _hubBroadcaster.BroadcastAsync(
                        "SearchProgress",
                        new { message, asin, audiobookId, type = "automatic", stage, timestamp = evt.Timestamp });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast automatic SearchProgress: {Message}", ex.Message);
            }
        }
    }
}
