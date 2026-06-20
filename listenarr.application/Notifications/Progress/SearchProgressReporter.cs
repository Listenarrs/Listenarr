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

        public SearchProgressReporter(IHubBroadcaster? hubBroadcaster, ILogger<SearchProgressReporter> logger)
        {
            _hubBroadcaster = hubBroadcaster;
            _logger = logger;
        }

        /// <summary>
        /// Broadcasts a search progress message to all connected realtime clients.
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
    }
}
