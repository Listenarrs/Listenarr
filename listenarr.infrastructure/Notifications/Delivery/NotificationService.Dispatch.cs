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

namespace Listenarr.Infrastructure.Notifications.Delivery
{
    public partial class NotificationService : INotificationService
    {
        /// <summary>
        /// Sends a trigger to every notification target that subscribes to it: the legacy single
        /// webhook URL and each enabled entry in the webhook list the settings screen manages.
        /// </summary>
        /// <remarks>
        /// Call sites used to reach only one of those two sets, which is why a webhook added through
        /// the settings screen never fired for a library or download event. Anything that emits a
        /// notification should come through here so both sets are considered once. Each target is
        /// delivered in isolation, so one that times out or fails does not cost the rest their
        /// delivery.
        /// </remarks>
        public async Task SendNotificationAsync(string trigger, object data)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            if (settings == null)
            {
                return;
            }

            var legacyUrl = settings.WebhookUrl;
            var legacyDelivered = false;
            if (!string.IsNullOrWhiteSpace(legacyUrl)
                && NotificationTriggers.IsEnabled(settings.EnabledNotificationTriggers, trigger))
            {
                await SendToTargetAsync(trigger, data, legacyUrl, settings.EnabledNotificationTriggers);
                legacyDelivered = true;
            }

            foreach (var webhook in settings.Webhooks ?? [])
            {
                if (!webhook.IsEnabled
                    || string.IsNullOrWhiteSpace(webhook.Url)
                    || !NotificationTriggers.IsEnabled(webhook.Triggers, trigger))
                {
                    continue;
                }

                if (legacyDelivered && string.Equals(webhook.Url, legacyUrl, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await SendToTargetAsync(trigger, data, webhook.Url, webhook.Triggers);
            }
        }

        private async Task SendToTargetAsync(string trigger, object data, string url, List<string> enabledTriggers)
        {
            try
            {
                await SendNotificationAsync(trigger, data, url, enabledTriggers);
            }
            catch (OperationCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                // HttpClient reports its own timeout as a cancellation. Only that one is absorbed here;
                // a cancellation somebody asked for still stops the whole dispatch.
                _logger.LogWarning(ex, "Sending the {Trigger} notification to one target timed out; continuing with the remaining targets", trigger);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Sending the {Trigger} notification to one target failed; continuing with the remaining targets", trigger);
            }
        }

        private async Task SendNotificationSafelyAsync(string trigger, object data, string failureContext)
        {
            try
            {
                await SendNotificationAsync(trigger, data);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Sending the {Trigger} notification failed for {Context}", trigger, failureContext);
            }
        }
    }
}
