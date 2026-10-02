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
        /// <summary>
        /// The environment variable the Discord bot already treats as the external URL. It is read
        /// here for the same reason and in the same position, so the two notification paths agree.
        /// </summary>
        internal const string PublicUrlVariable = "LISTENARR_PUBLIC_URL";

        public static async Task<NotificationPayloadContext> ResolveAsync(
            IConfigurationService configurationService,
            IRequestContextAccessor? requestContextAccessor,
            ILogger logger,
            bool validateImageBaseUrl = false,
            Func<string, string?>? environmentReader = null)
        {
            var startup = await configurationService.GetStartupConfigAsync();
            var readEnvironment = environmentReader ?? Environment.GetEnvironmentVariable;

            // The order is DiscordBotService.GetListenarrUrl()'s: the environment variable an
            // operator has probably already set wins, then whatever is configured, then the
            // request the notification is being sent from.
            var source = PublicUrlVariable;
            var baseUrl = TrimTrailingSlash(readEnvironment(PublicUrlVariable));

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                source = nameof(StartupConfig.ApplicationUrl);
                baseUrl = startup?.ApplicationUrl;
            }

            if (string.IsNullOrWhiteSpace(baseUrl) && IsAbsoluteUrl(startup?.UrlBase))
            {
                // Before ApplicationUrl existed this was the only way to get images into a
                // notification, so keep honouring it rather than breaking those installations.
                logger.LogWarning(
                    "UrlBase is set to an absolute URL and is being used as the notification base. Move the value to ApplicationUrl or set {PublicUrlVariable}: UrlBase is the path Listenarr is served under",
                    PublicUrlVariable);
                source = nameof(StartupConfig.UrlBase);
                baseUrl = startup?.UrlBase;
            }

            if (string.IsNullOrWhiteSpace(baseUrl) && requestContextAccessor?.Current != null)
            {
                var derived = NotificationPayloadBuilder.GetBaseUrlFromRequestContext(requestContextAccessor.Current);
                if (!string.IsNullOrWhiteSpace(derived))
                {
                    source = "The request context";
                    baseUrl = derived;
                }
            }

            if (validateImageBaseUrl &&
                !string.IsNullOrWhiteSpace(baseUrl) &&
                !IsAbsoluteUrl(baseUrl))
            {
                logger.LogWarning("{Source} is not an absolute URL: {BaseUrl} - notifications will not include images", source, LogRedaction.SanitizeUrl(baseUrl));
                baseUrl = null;
            }

            var apiVersion = ApiVersionUtils.ResolveApiVersion(requestContextAccessor?.Current?.Path, startup?.ApiVersion);
            return new NotificationPayloadContext(baseUrl, apiVersion);
        }

        private static string? TrimTrailingSlash(string? value) =>
            string.IsNullOrWhiteSpace(value) ? value : value.Trim().TrimEnd('/');

        private static bool IsAbsoluteUrl(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    public sealed record NotificationPayloadContext(string? BaseUrl, string ApiVersion);
}
