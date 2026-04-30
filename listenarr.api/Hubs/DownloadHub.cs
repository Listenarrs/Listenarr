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

namespace Listenarr.Api.Hubs
{
    /// <summary>
    /// SignalR hub for real-time download progress updates
    /// </summary>
    public class DownloadHub : Hub
    {
        private readonly ILogger<DownloadHub> _logger;
        private readonly DownloadPushService _pushService;
        public DownloadHub(ILogger<DownloadHub> logger, DownloadPushService pushService)
        {
            _logger = logger;
            _pushService = pushService;
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Client can request current downloads status
        /// </summary>
        public Task RequestDownloadsUpdate()
        {
            _logger.LogDebug("Client {ConnectionId} requested downloads update", Context.ConnectionId);
            // The background service will handle sending updates
            return Task.CompletedTask;
        }

        /// <summary>
        /// Client pushes a download update to the server. The server will broadcast
        /// to other clients and cache the push so the poller avoids re-broadcasting.
        /// </summary>
        public async Task PushDownloadUpdate(Download download)
        {
            _logger.LogDebug("Received PushDownloadUpdate from {ConnectionId} for download {DownloadId}", Context.ConnectionId, download?.Id);

            try
            {
                if (_pushService == null)
                {
                    _logger.LogWarning("DownloadPushService not available to handle push");
                    return;
                }

                if (download != null)
                {
                    await _pushService.HandlePushAsync(download);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error processing PushDownloadUpdate");
            }
        }
    }
}


