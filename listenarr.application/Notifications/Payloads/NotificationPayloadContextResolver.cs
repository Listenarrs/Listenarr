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
            var baseUrl = startup?.ApplicationUrl;

            if (string.IsNullOrWhiteSpace(baseUrl) && IsAbsoluteUrl(startup?.UrlBase))
            {
                // Before ApplicationUrl existed this was the only way to get images into a
                // notification, so keep honouring it rather than breaking those installations.
                logger.LogWarning(
                    "UrlBase is set to an absolute URL and is being used as the notification base. Move the value to ApplicationUrl: UrlBase is the path Listenarr is served under");
                baseUrl = startup?.UrlBase;
            }

            if (string.IsNullOrWhiteSpace(baseUrl) && requestContextAccessor?.Current != null)
            {
                var derived = NotificationPayloadBuilder.GetBaseUrlFromRequestContext(requestContextAccessor.Current);
                if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
            }

            if (validateImageBaseUrl &&
                !string.IsNullOrWhiteSpace(baseUrl) &&
                !IsAbsoluteUrl(baseUrl))
            {
                logger.LogWarning("ApplicationUrl is not an absolute URL: {BaseUrl} - notifications will not include images", LogRedaction.SanitizeUrl(baseUrl));
                baseUrl = null;
            }

            var apiVersion = ApiVersionUtils.ResolveApiVersion(requestContextAccessor?.Current?.Path, startup?.ApiVersion);
            return new NotificationPayloadContext(baseUrl, apiVersion);
        }

        private static bool IsAbsoluteUrl(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    public sealed record NotificationPayloadContext(string? BaseUrl, string ApiVersion);
}
