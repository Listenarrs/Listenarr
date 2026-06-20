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
using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Notifications.Payloads
{
    public static class NotificationPayloadContextResolver
    {
        public static async Task<NotificationPayloadContext> ResolveAsync(
            IConfigurationService configurationService,
            IRequestContextAccessor? requestContextAccessor,
            ILogger logger,
            bool validateImageBaseUrl = false)
        {
            var startup = await configurationService.GetStartupConfigAsync();
            var baseUrl = startup?.UrlBase;

            if (string.IsNullOrWhiteSpace(baseUrl) && requestContextAccessor?.Current != null)
            {
                var derived = NotificationPayloadBuilder.GetBaseUrlFromRequestContext(requestContextAccessor.Current);
                if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
            }

            if (validateImageBaseUrl &&
                !string.IsNullOrWhiteSpace(baseUrl) &&
                !(baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning("Invalid base URL configured: {BaseUrl} - notifications will not include images", LogRedaction.SanitizeUrl(baseUrl));
                baseUrl = null;
            }

            var apiVersion = ApiVersionUtils.ResolveApiVersion(requestContextAccessor?.Current?.Path, startup?.ApiVersion);
            return new NotificationPayloadContext(baseUrl, apiVersion);
        }
    }

    public sealed record NotificationPayloadContext(string? BaseUrl, string ApiVersion);
}
