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
    /// <summary>
    /// Service for sending webhook notifications.
    /// Refactored to delegate payload construction and attachment handling to NotificationPayloadBuilder.
    /// Provides static compatibility shims used by tests.
    /// </summary>
    public partial class NotificationService : INotificationService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<NotificationService> _logger;
        private readonly IConfigurationService _configurationService;
        private readonly IRequestContextAccessor? _requestContextAccessor;
        private readonly INotificationPayloadBuilder _payloadBuilder;
        private readonly NotificationHttpSender _httpSender;

        public NotificationService(HttpClient httpClient, ILogger<NotificationService> logger, IConfigurationService configurationService, INotificationPayloadBuilder payloadBuilder, IRequestContextAccessor? requestContextAccessor = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configurationService = configurationService;
            _payloadBuilder = payloadBuilder ?? throw new ArgumentNullException(nameof(payloadBuilder));
            _requestContextAccessor = requestContextAccessor;
            _httpSender = new NotificationHttpSender(httpClient, httpClient, logger, AllowPrivateWebhookTargetsForCurrentRequest);
        }

        // INotificationService interface stubs — webhook dispatch goes through SendNotificationAsync;
        // these typed convenience methods delegate to the main webhook loop or no-op.
        public async Task OnDownloadImportedAsync(Download download)
        {
            try
            {
                var webhooks = await _configurationService.GetWebhookConfigurationsAsync();
                foreach (var wh in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Imported")))
                    await SendNotificationAsync("Imported", new { AudiobookTitle = download.Title, Timestamp = DateTime.UtcNow }, wh.Url, wh.Triggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "SendDownloadCompletedNotificationAsync failed for {Id}", download.Id);
            }
        }

        public async Task OnDownloadFailedAsync(Download download)
        {
            try
            {
                var webhooks = await _configurationService.GetWebhookConfigurationsAsync();
                foreach (var wh in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Failed")))
                    await SendNotificationAsync("Failed", new { AudiobookTitle = download.Title, Error = download.ErrorMessage, Timestamp = DateTime.UtcNow }, wh.Url, wh.Triggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "SendDownloadFailedNotificationAsync failed for {Id}", download.Id);
            }
        }

        public async Task SendSystemNotificationAsync(string title, string message)
        {
            try
            {
                var webhooks = await _configurationService.GetWebhookConfigurationsAsync();
                foreach (var wh in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("System")))
                    await SendNotificationAsync("System", new { Title = title, Message = message, Timestamp = DateTime.UtcNow }, wh.Url, wh.Triggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "SendSystemNotificationAsync failed");
            }
        }

        // Compatibility shims removed — callers/tests should use NotificationPayloadBuilder directly.

        private bool AllowPrivateWebhookTargetsForCurrentRequest()
        {
            var context = _requestContextAccessor?.Current;
            if (context == null)
            {
                return true;
            }

            return context.RemoteIpAddress == null
                   || SecurityRequestUtils.IsLoopback(context.RemoteIpAddress)
                   || context.IsAuthenticatedAdminOrApiKey;
        }

        private async Task<HttpResponseMessage> PostValidatedAsync(string url, HttpContent content, CancellationToken cancellationToken = default)
        {
            return await _httpSender.PostValidatedAsync(url, content, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendValidatedAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            return await _httpSender.SendValidatedAsync(request, cancellationToken);
        }
    }
}
