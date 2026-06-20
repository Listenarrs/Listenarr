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

namespace Listenarr.Infrastructure.Realtime.Broadcasting
{
    public class SignalRHubBroadcaster : IHubBroadcaster
    {
        private readonly IHubContext<DownloadHub> _downloadHubContext;
        private readonly IHubContext<SettingsHub>? _settingsHubContext;
        private readonly ILogger<SignalRHubBroadcaster> _logger;

        public SignalRHubBroadcaster(
            IHubContext<DownloadHub> downloadHubContext,
            ILogger<SignalRHubBroadcaster> logger,
            IHubContext<SettingsHub>? settingsHubContext = null)
        {
            _downloadHubContext = downloadHubContext ?? throw new ArgumentNullException(nameof(downloadHubContext));
            _settingsHubContext = settingsHubContext;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task BroadcastQueueUpdateAsync(QueueSnapshot queueSnapshot)
        {
            try
            {
                // Primary, public API
                var clientProxy = _downloadHubContext.Clients.All;
                await clientProxy.SendAsync("QueueUpdate", queueSnapshot);

                // Some tests/mocks expect SendCoreAsync; call as a compatibility step
                try
                {
                    await clientProxy.SendCoreAsync("QueueUpdate", new object[] { queueSnapshot }, CancellationToken.None);
                }
                catch (Exception inner) when (inner is not OperationCanceledException && inner is not OutOfMemoryException && inner is not StackOverflowException)
                {
                    _logger.LogDebug(inner, "Direct SendCoreAsync for QueueUpdate failed (non-fatal)");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast QueueUpdate");
            }
        }

        public async Task BroadcastAsync(string eventName, object payload, CancellationToken cancellationToken = default)
        {
            await BroadcastAsync(RealtimeHubTarget.Downloads, eventName, payload, cancellationToken);
        }

        public async Task BroadcastAsync(RealtimeHubTarget target, string eventName, object payload, CancellationToken cancellationToken = default)
        {
            try
            {
                if (target == RealtimeHubTarget.Settings && _settingsHubContext is null)
                {
                    _logger.LogWarning("Cannot broadcast {EventName} to {HubTarget} because the settings hub context is not registered", eventName, target);
                    return;
                }

                var clientProxy = target switch
                {
                    RealtimeHubTarget.Downloads => _downloadHubContext.Clients.All,
                    RealtimeHubTarget.Settings => _settingsHubContext!.Clients.All,
                    _ => _downloadHubContext.Clients.All
                };

                await clientProxy.SendAsync(eventName, payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast {EventName} to {HubTarget}", eventName, target);
            }
        }
    }
}
