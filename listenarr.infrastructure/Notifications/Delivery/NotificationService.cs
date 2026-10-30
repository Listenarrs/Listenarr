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

        // Webhook dispatch goes through SendNotificationAsync (see NotificationService.Webhooks.cs),
        // driven by IBookLifecycleNotifier against the configurable trigger catalog. The former
        // OnDownloadImportedAsync/OnDownloadFailedAsync/SendSystemNotificationAsync shims filtered on
        // the hardcoded "Imported"/"Failed"/"System" names, which were never in that catalog (book-*)
        // and so never sent — removed as part of #909.

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
