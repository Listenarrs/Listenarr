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

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Notification
{
    /// <summary>
    /// SignalR hub for real-time settings updates
    /// </summary>
    public class SettingsHub : Hub
    {
        private readonly ILogger<SettingsHub> _logger;

        public SettingsHub(ILogger<SettingsHub> logger)
        {
            _logger = logger;
        }

        // Track connected clients for debugging/health checks
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _connectedClients = new();

        public static IReadOnlyCollection<string> ConnectedClientIds => _connectedClients.Keys.ToArray();

        public override async Task OnConnectedAsync()
        {
            _connectedClients[Context.ConnectionId] = DateTime.UtcNow;
            _logger.LogInformation("Settings client connected: {ConnectionId}. ConnectedCount={Count}", Context.ConnectionId, _connectedClients.Count);

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _connectedClients.TryRemove(Context.ConnectionId, out _);
            _logger.LogInformation("Settings client disconnected: {ConnectionId}. ConnectedCount={Count}", Context.ConnectionId, _connectedClients.Count);

            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Client can request current settings
        /// </summary>
        public Task RequestSettings()
        {
            _logger.LogDebug("Client {ConnectionId} requested settings", Context.ConnectionId);
            return Task.CompletedTask;
        }
    }
}
